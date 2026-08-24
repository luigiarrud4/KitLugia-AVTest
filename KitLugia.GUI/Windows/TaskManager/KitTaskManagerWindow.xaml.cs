using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MenuItem = System.Windows.Controls.MenuItem;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KitLugia.Core;
using KitLugia.Core.TaskManager;
using KitLugia.GUI.Helpers;

namespace KitLugia.GUI.Windows.TaskManager
{
    public partial class KitTaskManagerWindow : Window
    {
        // ══════════════════════════════════════════════
        //  STATE
        // ══════════════════════════════════════════════
        private List<ProcessRow> _allRows = new();
        private List<ProcessRow> _filteredRows = new();
        private Dictionary<int, TimeSpan> _prevCpu = new();
        private DateTime _prevTime = DateTime.UtcNow;
        private readonly object _lock = new();
        private bool _isRefreshing;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private CancellationTokenSource? _refreshCts;

        // Performance counters
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _memAvailable;
        private long _totalMemBytes;

        // Icon cache: path → BitmapSource
        private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _iconLock = new();
        private static BitmapSource? _genericIcon;

        // Search
        private DispatcherTimer? _searchDebounce;
        private string _lastSearchQuery = "";

        // Refresh
        private DispatcherTimer? _refreshTimer;
        private int _refreshSeconds = 1;

        // Network cache
        private Dictionary<uint, int> _networkConnections = new();

        // Per-process network speed cache: pid → (readBytes, writeBytes, timestamp)
        private readonly Dictionary<int, (double readBytes, double writeBytes, DateTime time)> _netSpeedCache = new();

        // Disk read+write perf counter
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;

        // Performance graphs (histórico por dispositivo — ver região PERFORMANCE TAB)
        private DispatcherTimer? _graphTimer;

        // Process I/O cache (from last refresh cycle)
        private Dictionary<int, ProcessIoHelper.ProcessIoRate> _ioCache = new();

        // Mini CPU graph for detail panel
        private readonly Queue<float> _miniCpuHistory = new(31);

        // Services & Startup
        private List<ServiceInfo> _allServices = new();
        private List<StartupAppDetails> _allStartupApps = new();

        // Sorting (Win11: usuário escolhe e mantém)
        private string _currentSortColumn = "CpuValue";
        private ListSortDirection _currentSortDirection = ListSortDirection.Descending;
        private readonly ObservableCollection<ProcessRow> _groupedLive = new();
        private CollectionViewSource? _groupedCvs;
        private bool _cvsInitialized = false;
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("kernel32.dll")]
        private static extern void GetPhysicallyInstalledSystemMemory(out long totalMemoryInKb);

        // ══════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════
        public KitTaskManagerWindow()
        {
            InitializeComponent();

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += SearchDebounce_Tick;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_refreshSeconds) };
            _refreshTimer.Tick += async (_, __) => await RefreshAsync();

            _graphTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _graphTimer.Tick += (_, __) => UpdatePerformanceGraphs();

            _genericIcon = ProgramIconHelper.GetGenericIcon();

            Loaded += async (_, __) =>
            {
                // Inicializa CollectionViewSource live uma única vez (evita recriar e perder SortDescriptions)
                _groupedCvs = new CollectionViewSource { Source = _groupedLive };
                _groupedCvs.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
                DgProcesses.ItemsSource = _groupedCvs.View;
                _cvsInitialized = true;
                // Marca CPU como ordenação inicial (setinha ↓)
                foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                var cpuCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == "CpuValue");
                if (cpuCol != null) cpuCol.SortDirection = ListSortDirection.Descending;
                ApplySorting();

                InitCounters();
                await RefreshAsync();
                _refreshTimer.Start();
                StartResourceMonitor();
                _graphTimer.Start();
                await BuildPerfDevicesAsync();
                await LoadServicesAsync();
                await LoadStartupAppsAsync();
            };

            Closing += (_, __) =>
            {
                try { _refreshCts?.Cancel(); } catch { }
                _refreshTimer?.Stop();
                _graphTimer?.Stop();
                _searchDebounce?.Stop();
                DisposeCounters();
                ProcessIoHelper.ResetAll();
                try { _refreshGate.Dispose(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                GpuMonitor.Shutdown();
            };
        }

        // ══════════════════════════════════════════════
        //  COUNTERS
        // ══════════════════════════════════════════════
        private void InitCounters()
        {
            try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuCounter.NextValue(); } catch { }
            try { _memAvailable = new PerformanceCounter("Memory", "Available MBytes"); } catch { }
            try { _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total"); _diskReadCounter.NextValue(); } catch { }
            try { _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total"); _diskWriteCounter.NextValue(); } catch { }
            _totalMemBytes = GetTotalPhysicalMemory();
        }

        private void DisposeCounters()
        {
            try { _cpuCounter?.Dispose(); } catch { }
            try { _memAvailable?.Dispose(); } catch { }
            try { _diskReadCounter?.Dispose(); } catch { }
            try { _diskWriteCounter?.Dispose(); } catch { }
        }

        private void StartResourceMonitor()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, __) =>
            {
                try
                {
                    float cpu = 0;
                    try { cpu = _cpuCounter?.NextValue() ?? 0; } catch { }
                    float availMemMb = 0;
                    try { availMemMb = _memAvailable?.NextValue() ?? 0; } catch { }
                    long totalMemMb = _totalMemBytes / (1024 * 1024);
                    float memPct = totalMemMb > 0 ? (1f - availMemMb / totalMemMb) * 100f : 0;

                    TxtCpuUsage.Text = $"{cpu:F0}%";
                    TxtCpuUsage.Foreground = GetHeatColor(cpu, 80, 95);
                    TxtMemUsage.Text = $"{memPct:F0}%";
                    TxtMemUsage.Foreground = GetHeatColor(memPct, 70, 90);
                }
                catch { }
            };
            timer.Start();
        }

        private static long GetTotalPhysicalMemory()
        {
            try { GetPhysicallyInstalledSystemMemory(out long kb); return kb * 1024; }
            catch { return 0; }
        }

        // ══════════════════════════════════════════════
        //  DRAG / SEARCH BAR
        // ══════════════════════════════════════════════
        private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { }
        }

        private void SearchBar_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void SearchBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TxtSearch.Focus();
            e.Handled = true;
        }

        private void WindowControls_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // ══════════════════════════════════════════════
        //  WINDOW CONTROLS
        // ══════════════════════════════════════════════
        private void BtnToggleMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ══════════════════════════════════════════════
        //  TAB SWITCHING
        // ══════════════════════════════════════════════
        private void SwitchTab(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;

            // Hide all tabs
            TabProcesses.Visibility = Visibility.Collapsed;
            TabPerformance.Visibility = Visibility.Collapsed;
            TabServices.Visibility = Visibility.Collapsed;
            TabStartup.Visibility = Visibility.Collapsed;

            // Reset all sidebar buttons to inactive
            ResetSidebarButton(BtnTabProcesses);
            ResetSidebarButton(BtnTabPerformance);
            ResetSidebarButton(BtnTabServices);
            ResetSidebarButton(BtnTabStartup);

            // Activate selected tab + sidebar button
            switch (tag)
            {
                case "Processes":
                    TabProcesses.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabProcesses);
                    break;
                case "Performance":
                    TabPerformance.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabPerformance);
                    break;
                case "Services":
                    TabServices.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabServices);
                    break;
                case "Startup":
                    TabStartup.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabStartup);
                    break;
            }
        }

        private static readonly SolidColorBrush _goldBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)));
        private static readonly SolidColorBrush _grayBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly SolidColorBrush _bgActive = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28)));
        private static readonly SolidColorBrush _bgInactive = Freeze(new SolidColorBrush(Colors.Transparent));

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            if (!b.IsFrozen) b.Freeze();
            return b;
        }

        private static void ResetSidebarButton(Button btn)
        {
            if (btn.Template == null) return;
            // Background
            if (btn.Template.FindName("bdr", btn) is System.Windows.Controls.Border bdr)
                bdr.Background = _bgInactive;
            // Indicator
            if (btn.Template.FindName("Indicator", btn) is System.Windows.Controls.Border ind)
                ind.Visibility = Visibility.Collapsed;
            // Icon
            if (btn.Template.FindName("Icon", btn) is TextBlock icon)
                icon.Foreground = _grayBrush;
            // Label
            if (btn.Template.FindName("Label", btn) is TextBlock label)
                label.Foreground = _grayBrush;
        }

        private static void ActivateSidebarButton(Button btn)
        {
            if (btn.Template == null) return;
            // Background
            if (btn.Template.FindName("bdr", btn) is System.Windows.Controls.Border bdr)
                bdr.Background = _bgActive;
            // Indicator
            if (btn.Template.FindName("Indicator", btn) is System.Windows.Controls.Border ind)
                ind.Visibility = Visibility.Visible;
            // Icon
            if (btn.Template.FindName("Icon", btn) is TextBlock icon)
                icon.Foreground = _goldBrush;
            // Label
            if (btn.Template.FindName("Label", btn) is TextBlock label)
                label.Foreground = _goldBrush;
        }

        // ══════════════════════════════════════════════
        //  SEARCH
        // ══════════════════════════════════════════════
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounce?.Stop();
            _searchDebounce?.Start();
        }

        private void SearchDebounce_Tick(object? sender, EventArgs e)
        {
            _searchDebounce?.Stop();
            var query = TxtSearch.Text?.Trim() ?? "";
            if (query == _lastSearchQuery) return;
            _lastSearchQuery = query;
            ApplyFilter(query);
            // Barra de busca GLOBAL: aplica o mesmo filtro nas outras abas
            ApplyServiceFilter(GetServiceFilter());
            ApplyStartupFilter();
        }

        private string GetServiceFilter() =>
            (CmbServiceFilter?.SelectedItem as ComboBoxItem)?.Content as string ?? "Todos";

        // ══════════════════════════════════════════════
        //  REFRESH INTERVAL
        // ══════════════════════════════════════════════
        private void CmbRefreshInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRefreshInterval.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                if (int.TryParse(text.Replace("s", ""), out int sec))
                {
                    _refreshSeconds = sec;
                    if (_refreshTimer != null)
                        _refreshTimer.Interval = TimeSpan.FromSeconds(sec);
                }
            }
        }

        // ══════════════════════════════════════════════
        //  PROCESS REFRESH — zero-freeze pipeline (native batch + single-flight + off-UI)
        // ══════════════════════════════════════════════
        private async Task RefreshAsync()
        {
            // Single-flight gate: evita sobreposição de refreshes quando UI dispara rápido
            if (!await _refreshGate.WaitAsync(0)) return;
            _isRefreshing = true;
            var sw = Stopwatch.StartNew();
            var now = DateTime.UtcNow;
            var deltaMs = (now - _prevTime).TotalMilliseconds;
            _prevTime = now;
            int cores = Environment.ProcessorCount;
            var cts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshCts, cts);
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }
            var token = cts.Token;
            try
            {
                // Network + GPU em paralelo, 100% off-UI (antes GPU bloqueava UI na 1a chamada)
                var netTask = Task.Run(() =>
                {
                    try { return NetworkTrafficMonitor.GetActiveTcpConnectionsPerPid(); }
                    catch { return new Dictionary<uint, int>(); }
                }, token);
                var gpuTask = Task.Run(() =>
                {
                    try { return (float)GpuMonitor.GetTotalGpuUtilization(); }
                    catch { return -1f; }
                }, token);

                await Task.WhenAll(netTask, gpuTask);
                if (token.IsCancellationRequested) return;
                var netConnections = netTask.Result;
                float gpuTotal = gpuTask.Result;
                _networkConnections = netConnections;
                _lastGpuPct = gpuTotal; // alimenta gráfico GPU na aba Desempenho

                // UI update rápido (não bloqueia) — agenda no dispatcher mas não espera
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtGpuUsage.Text = gpuTotal >= 0 ? $"{gpuTotal:F0}%" : "N/A";
                    TxtGpuUsage.Foreground = gpuTotal >= 0 ? GetHeatColor(gpuTotal, 70, 90) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
                    TxtStatus.Text = "Atualizando processos...";
                }), DispatcherPriority.Background);

                var rows = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var result = new List<ProcessRow>(320);
                    Process[] processes;
                    try { processes = Process.GetProcesses(); }
                    catch { return result; }

                    // Fast path: enumeração nativa (Rust) entrega parentPid + handleCount sem WMI
                    var fastMap = SafeProcessHelper.TryEnumerateFast();
                    Dictionary<int, int>? parentPidDict = null;
                    if (fastMap == null)
                    {
                        // Fallback WMI só quando nativo indisponível — 1 query em vez de 300
                        parentPidDict = new Dictionary<int, int>(processes.Length);
                        try
                        {
                            using var searcher = new System.Management.ManagementObjectSearcher(
                                "SELECT ProcessId, ParentProcessId FROM Win32_Process");
                            using var wmiResults = searcher.Get();
                            foreach (System.Management.ManagementObject obj in wmiResults)
                            {
                                try
                                {
                                    int pid = Convert.ToInt32(obj["ProcessId"]);
                                    int ppid = Convert.ToInt32(obj["ParentProcessId"]);
                                    parentPidDict[pid] = ppid;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    // Batch de paths: 1 buffer reutilizado para todos os PIDs (evita 300 allocs)
                    var pidList = processes.Select(pr => pr.Id).ToList();
                    var pathMap = SafeProcessHelper.GetProcessPathsBatch(pidList);

                    foreach (var p in processes)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            bool exited = false;
                            try { exited = p.HasExited; } catch { exited = true; }
                            if (exited) { try { p.Dispose(); } catch { } continue; }

                            string pName;
                            try { pName = p.ProcessName; } catch { try { p.Dispose(); } catch { } continue; }
                            int pid = p.Id;

                            string path = pathMap.TryGetValue(pid, out var cached) ? cached : "";
                            bool hasWindow = false;
                            try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                            int threads = 0;
                            try { threads = p.Threads.Count; } catch { }
                            int handles = 0;
                            if (fastMap != null && fastMap.TryGetValue(pid, out var fm))
                                handles = (int)fm.HandleCount;
                            else
                                try { handles = p.HandleCount; } catch { }

                            string group;
                            if (hasWindow) group = "Aplicativos";
                            else if (IsWindowsProcess(pName, path)) group = "Processos do Windows";
                            else group = "Processos em segundo plano";

                            string badge = "";
                            bool isProtected = false;
                            if (pid < 100) { badge = "SYSTEM"; isProtected = true; }
                            else if (group == "Processos do Windows" && pid > 4) { badge = "WIN"; isProtected = true; }

                            double cpuVal = 0;
                            string cpu = "0%";
                            try
                            {
                                var cur = p.TotalProcessorTime;
                                if (_prevCpu.TryGetValue(pid, out var prev) && deltaMs > 50)
                                {
                                    var diff = (cur - prev).TotalMilliseconds;
                                    cpuVal = Math.Clamp(diff / deltaMs / cores * 100.0, 0, 100);
                                    cpu = $"{cpuVal:F1}%";
                                }
                                _prevCpu[pid] = cur;
                            }
                            catch { }

                            long ramBytes = 0;
                            string ramMb = "0 MB";
                            try { ramBytes = p.WorkingSet64; ramMb = $"{ramBytes / 1024 / 1024} MB"; } catch { }
                            double ramVal = ramBytes / 1024.0 / 1024.0;

                            var io = ProcessIoHelper.SampleProcessIo(pid);
                            double ioBytes = io.ReadBytesPerSec + io.WriteBytesPerSec;
                            string disk = FormatBytesSpeed(ioBytes);

                            int netConns = netConnections.TryGetValue((uint)pid, out int c) ? c : 0;
                            double netBytesPerSec = netConns > 0 ? ioBytes : 0;
                            string net = FormatBytesSpeed(netBytesPerSec);

                            string gpu = gpuTotal >= 0 ? "—" : "N/A";

                            int parentPid = 0;
                            if (fastMap != null)
                            {
                                if (fastMap.TryGetValue(pid, out var f)) parentPid = (int)f.ParentPid;
                            }
                            else if (parentPidDict != null && parentPidDict.TryGetValue(pid, out int pp)) parentPid = pp;

                            // Ícone instantâneo do cache — evita linhas em branco e "piscada"
                            // a cada refresh (linhas são recriadas, mas o cache persiste).
                            var iconNow = GetCachedIcon(path, pName);

                            result.Add(new ProcessRow
                            {
                                Name = pName,
                                DisplayName = pName,
                                Pid = pid,
                                Cpu = cpu,
                                CpuValue = cpuVal,
                                RamMB = ramMb,
                                RamValue = ramVal,
                                Handles = handles.ToString(),
                                Threads = threads.ToString(),
                                Group = group,
                                Status = hasWindow ? "Executando" : (group == "Processos do Windows" ? "Serviço" : "Segundo plano"),
                                Disk = disk,
                                DiskBytesPerSec = ioBytes,
                                Network = net,
                                NetworkConnections = netConns,
                                NetBytesPerSec = netBytesPerSec,
                                Gpu = gpu,
                                Path = path,
                                ParentPid = parentPid,
                                ProtectedBadge = badge,
                                IsProtected = isProtected,
                                ProcessIcon = iconNow,
                                IconPath = string.IsNullOrEmpty(iconNow == null ? "" : path) ? "" : path,
                            });
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }

                    var alive = new HashSet<int>(result.Select(r => r.Pid));
                    ProcessIoHelper.CleanupStaleSnapshots(alive);
                    foreach (var k in _prevCpu.Keys.Where(k => !alive.Contains(k)).ToList()) _prevCpu.Remove(k);
                    return result;
                }, token);

                if (token.IsCancellationRequested) return;
                lock (_lock) { _allRows = rows; }

                // Ícones: carrega incremental e atualiza via INotify (sem recriar ItemsSource)
                _ = LoadIconsIncrementalAsync(rows.Where(r => string.IsNullOrEmpty(r.IconPath) && !string.IsNullOrEmpty(r.Path)).Take(24).ToList());

                ApplyFilter(_lastSearchQuery);

                sw.Stop();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TxtStatus.Text = $"{rows.Count} processos em {sw.ElapsedMilliseconds}ms";
                    double totalDisk = rows.Sum(r => r.DiskBytesPerSec);
                    TxtDiskUsage.Text = FormatBytesSpeed(totalDisk);
                    TxtDiskUsage.Foreground = GetHeatColor(totalDisk > 0 ? (float)(totalDisk / 1024 / 1024) : 0, 50, 200);
                    double totalNet = rows.Sum(r => r.NetBytesPerSec);
                    TxtNetUsage.Text = FormatBytesSpeed(totalNet);
                    TxtNetUsage.Foreground = totalNet > 0 ? GetHeatColor((float)(totalNet / 1024 / 1024), 5, 50) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
                }), DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] RefreshAsync: {ex.Message}"); } catch { } }
            finally { _isRefreshing = false; _refreshGate.Release(); }
        }

        // ══════════════════════════════════════════════
        //  ICON LOADING — incremental, sem re-criar ItemsSource (INotify atualiza ícone na linha)
        // ══════════════════════════════════════════════
        /// <summary>
        /// Lê SOMENTE do cache (thread-safe). Nunca extrai ícone aqui — extração custa ms
        /// e é feita por LoadIconsIncrementalAsync. Resolve também fallback System32 pelo nome.
        /// </summary>
        private BitmapSource? GetCachedIcon(string path, string name)
        {
            try
            {
                lock (_iconLock)
                {
                    if (!string.IsNullOrEmpty(path) && _iconCache.TryGetValue(path, out var byPath))
                        return byPath ?? _genericIcon;
                    if (_iconCache.TryGetValue(name, out var byName))
                        return byName ?? _genericIcon;
                }
                // Fallback System32 conhecido (dwm, conhost, etc.) já em cache?
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                {
                    string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string cand = Path.Combine(sys, name + ".exe");
                    lock (_iconLock)
                    {
                        if (_iconCache.TryGetValue(cand, out var bySys)) return bySys ?? _genericIcon;
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task LoadIconsAsync(List<ProcessRow> rows) => await LoadIconsIncrementalAsync(rows);

        private static string ExtractPackageName(string path)
        {
            try
            {
                var parts = path.Split('\\');
                int idx = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx + 1 < parts.Length) return parts[idx + 1];
            }
            catch { }
            return "";
        }

        private async Task LoadIconsIncrementalAsync(List<ProcessRow> rows)
        {
            if (rows.Count == 0) return;
            await Task.Run(() =>
            {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = 4 };
                Parallel.ForEach(rows, opts, row =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(row.Path))
                        {
                            // Tenta resolver via System32 pelo nome (ex: dwm -> System32\dwm.exe)
                            try
                            {
                                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                                string cand = Path.Combine(sys, row.Name + ".exe");
                                if (File.Exists(cand))
                                {
                                    var ic2 = ProgramIconHelper.GetIconFromFile(cand);
                                    if (ic2 != null)
                                    {
                                        lock (_iconLock) { _iconCache[row.Name] = ic2; }
                                        Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = ic2; row.IconPath = cand; }), DispatcherPriority.Background);
                                        return;
                                    }
                                }
                            }
                            catch { }
                            Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = _genericIcon; row.IconPath = ""; }), DispatcherPriority.Background);
                            return;
                        }
                        lock (_iconLock) { if (_iconCache.ContainsKey(row.Path)) { var cached = _iconCache[row.Path] ?? _genericIcon; Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = cached; row.IconPath = row.Path; }), DispatcherPriority.Background); return; } }
                        BitmapSource? icon = null;
                        // UWP: tenta AppIconHelper via manifest (evita SHGetFileInfo falhar por permissão)
                        if (row.Path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                string pkg = ExtractPackageName(row.Path);
                                if (!string.IsNullOrEmpty(pkg))
                                    icon = AppIconHelper.GetAppIcon(pkg, 32);
                            }
                            catch { }
                        }
                        if (icon == null)
                            icon = ProgramIconHelper.GetIconFromFile(row.Path);
                        lock (_iconLock)
                        {
                            _iconCache[row.Path] = icon;
                            // Cache também pela chave de nome: agrupamentos e processos sem
                            // caminho resolvem o ícone instantaneamente no próximo refresh.
                            if (icon != null && !string.IsNullOrEmpty(row.Name))
                                _iconCache[row.Name] = icon;
                        }
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            row.ProcessIcon = icon ?? _genericIcon;
                            row.IconPath = row.Path;
                        }), DispatcherPriority.Background);
                    }
                    catch { }
                });
            });
            // Sem ApplyFilter — ProcessIcon notifica via INotify, linha virtualizada atualiza sozinha
        }

        // ══════════════════════════════════════════════
        //  PROCESS CLASSIFICATION
        // ══════════════════════════════════════════════
        private static readonly HashSet<string> WindowsProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg", "conhost", "csrss", "ctfmon", "dwm", "lsass", "services",
            "smss", "svchost", "wininit", "winlogon", "FontdrvHost",
            "sihost", "RuntimeBroker", "ShellExperienceHost", "SearchUI",
            "TextInputHost", "StartMenuExperienceHost", "ApplicationFrameHost",
            "backgroundTaskHost", "dllhost", "taskhostw", "WerFault",
            "WmiPrvSE", "SearchProtocolHost", "SearchFilterHost",
            "spoolsv", "WUDFHost", "msdtc", "TrustedInstaller",
            "TiWorker", "WaaSMedicSvc", "UsoSvc", "WaaSMedicAgent",
            "SearchIndexer", "SearchApp", "SecHealthUI",
            "wsqmcons", "cbdhsvc", "csrss", "win32k",
            "Memory Compression", "Registry", "Security Health Service",
            "System", "Idle", "[System Process]",
        };

        private static bool IsWindowsProcess(string name, string path)
        {
            if (WindowsProcessNames.Contains(name)) return true;
            if (!string.IsNullOrEmpty(path))
            {
                var lower = path.ToLowerInvariant();
                if (lower.Contains(@"\windows\system32\") || lower.Contains(@"\windows\syswow64\") ||
                    lower.Contains(@"\windows\winsxs\") || lower.Contains(@"\program files\windowsapps\"))
                    return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════
        //  PARENT PID (batched — single WMI query for all processes)
        // ══════════════════════════════════════════════
        private static Dictionary<int, int> GetBatchParentPids(List<ProcessRow> rows)
        {
            var result = new Dictionary<int, int>();
            if (rows.Count == 0) return result;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId FROM Win32_Process");
                using var results = searcher.Get();
                foreach (System.Management.ManagementObject obj in results)
                {
                    try
                    {
                        int pid = Convert.ToInt32(obj["ProcessId"]);
                        int parentPid = Convert.ToInt32(obj["ParentProcessId"]);
                        result[pid] = parentPid;
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // ══════════════════════════════════════════════
        //  FILTER + GROUPING
        // ══════════════════════════════════════════════
        // Win11: clique no cabeçalho preserva ordenação
        private void DgProcesses_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var col = e.Column;
            string prop = col.SortMemberPath;
            if (string.IsNullOrEmpty(prop)) return;
            if (_currentSortColumn == prop)
                _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _currentSortColumn = prop;
                // Métricas numéricas começam do maior
                _currentSortDirection = (prop == "DisplayName" || prop == "Status" || prop == "Name") ? ListSortDirection.Ascending : ListSortDirection.Descending;
            }
            foreach (var c in DgProcesses.Columns) c.SortDirection = null;
            col.SortDirection = _currentSortDirection;
            ApplySorting();
        }

        private void ApplySorting()
        {
            // IMPORTANTE: a ordenação NÃO usa view.SortDescriptions — ela reordenaria a
            // CollectionView agrupada e jogaria as linhas-filho para longe do pai ao
            // expandir (bug "filhos espalhados"). A ordem real é aplicada manualmente
            // em ApplyFilter (Rank do grupo + métrica), preservando pai→filhos juntos.
            // Aqui só atualizamos as setinhas das colunas.
            try
            {
                foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                var activeCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == _currentSortColumn);
                if (activeCol != null) activeCol.SortDirection = _currentSortDirection;
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  EXPAND/COLLAPSE — estilo Gerenciador de Tarefas:
        //  os processos-filhos aparecem SEMPRE imediatamente abaixo do pai.
        // ══════════════════════════════════════════════
        private void ToggleExpand(ProcessRow row)
        {
            if (row == null || row.IsChild || row.RawChildren.Count == 0) return;

            bool expand = !row.IsExpanded;
            row.IsExpanded = expand;
            string key = row.GroupKey;
            if (expand) _expandedGroups.Add(key); else _expandedGroups.Remove(key);

            // Sem SortDescriptions na view, a ordem da ObservableCollection é
            // preservada dentro do grupo — inserir logo após o pai funciona.
            int idx = _groupedLive.IndexOf(row);
            if (idx < 0) return;
            if (expand)
            {
                for (int i = 0; i < row.RawChildren.Count; i++)
                {
                    var child = row.RawChildren[i];
                    child.IsChild = true;
                    _groupedLive.Insert(idx + 1 + i, child);
                }
                var needIcons = row.RawChildren.Where(c => c.ProcessIcon == null).ToList();
                if (needIcons.Count > 0) _ = LoadIconsIncrementalAsync(needIcons);
            }
            else
            {
                int rem = idx + 1;
                while (rem < _groupedLive.Count && _groupedLive[rem].IsChild && _groupedLive[rem].GroupKey == key)
                    _groupedLive.RemoveAt(rem);
            }
            try { DgProcesses.UpdateLayout(); } catch { }
        }

        private void ExpandIcon_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ProcessRow row)
            {
                e.Handled = true;
                ToggleExpand(row);
            }
        }

private void ApplyFilter(string query)
        {
            List<ProcessRow> rows;
            lock (_lock) { rows = _allRows.ToList(); }

            _ = Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(query))
                {
                    var advancedMatch = Regex.Match(query, @"^(\w+):(.+)$");
                    if (advancedMatch.Success)
                    {
                        string field = advancedMatch.Groups[1].Value.ToLowerInvariant();
                        string value = advancedMatch.Groups[2].Value;
                        rows = field switch
                        {
                            "name" => rows.Where(r => r.Name.Contains(value, StringComparison.OrdinalIgnoreCase)).ToList(),
                            "pid" when int.TryParse(value, out int pid) => rows.Where(r => r.Pid == pid).ToList(),
                            "cpu" when value.StartsWith(">") && double.TryParse(value[1..], out double cpuGt) => rows.Where(r => r.CpuValue > cpuGt).ToList(),
                            "cpu" when value.StartsWith("<") && double.TryParse(value[1..], out double cpuLt) => rows.Where(r => r.CpuValue < cpuLt).ToList(),
                            "ram" when value.StartsWith(">") && double.TryParse(value[1..], out double ramGt) => rows.Where(r => r.RamValue > ramGt).ToList(),
                            "ram" when value.StartsWith("<") && double.TryParse(value[1..], out double ramLt) => rows.Where(r => r.RamValue < ramLt).ToList(),
                            _ => rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().Contains(query) || (r.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList()
                        };
                    }
                    else
                    {
                        rows = rows.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().Contains(query) || (r.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                    }
                }

                // Agrupa por Name+Group (mesma lógica Win11) — RawChildren guarda os PIDs individuais para expansão
                var grouped = rows.GroupBy(r => new { r.Name, r.Group }).Select(g =>
                {
                    var first = g.First();
                    var count = g.Count();
                    var totalRam = g.Sum(r => r.RamValue);
                    var totalCpu = g.Sum(r => r.CpuValue);
                    var members = g.ToList();
                    string gkey = $"{first.Name}|{first.Group}";
                    // Ícone do grupo: primeiro membro que já tenha ícone resolvido
                    // (evita grupo sem ícone quando o 1º processo ainda não carregou).
                    var groupIcon = first.ProcessIcon ?? members.Select(m => m.ProcessIcon).FirstOrDefault(i => i != null);
                    var row = new ProcessRow
                    {
                        Name = first.Name,
                        DisplayName = count > 1 ? $"{first.Name} ({count})" : first.Name,
                        Pid = first.Pid,
                        Cpu = count > 1 ? $"{totalCpu:F1}%" : first.Cpu,
                        CpuValue = totalCpu,
                        RamMB = totalRam > 1024 ? $"{totalRam / 1024:F1} GB" : $"{totalRam:F0} MB",
                        RamValue = totalRam,
                        Handles = first.Handles,
                        Threads = first.Threads,
                        Group = first.Group,
                        Status = first.Status,
                        Disk = first.Disk,
                        DiskBytesPerSec = first.DiskBytesPerSec,
                        Network = first.Network,
                        NetworkConnections = first.NetworkConnections,
                        NetBytesPerSec = g.Sum(r => r.NetBytesPerSec),
                        Gpu = first.Gpu,
                        Path = first.Path,
                        ParentPid = first.ParentPid,
                        ProtectedBadge = first.ProtectedBadge,
                        IsProtected = first.IsProtected,
                        ChildCount = count,
                        ProcessIcon = groupIcon,
                        IconPath = first.IconPath,
                        IsExpanded = _expandedGroups.Contains(gkey),
                    };
                    if (count > 1)
                    {
                        row.RawChildren = members.Select(m => new ProcessRow
                        {
                            Name = m.Name,
                            DisplayName = $"{m.Name} — PID {m.Pid}",
                            Pid = m.Pid,
                            Cpu = m.Cpu,
                            CpuValue = m.CpuValue,
                            RamMB = m.RamMB,
                            RamValue = m.RamValue,
                            Handles = m.Handles,
                            Threads = m.Threads,
                            Group = m.Group,
                            Status = m.Status,
                            Disk = m.Disk,
                            DiskBytesPerSec = m.DiskBytesPerSec,
                            Network = m.Network,
                            NetworkConnections = m.NetworkConnections,
                            NetBytesPerSec = m.NetBytesPerSec,
                            Gpu = m.Gpu,
                            Path = m.Path,
                            ParentPid = m.ParentPid,
                            ChildCount = 1,
                            IsChild = true,
                            ProcessIcon = m.ProcessIcon,
                            IconPath = m.IconPath,
                        }).OrderByDescending(c => c.CpuValue).ThenByDescending(c => c.RamValue).ToList();
                    }
                    return row;
                }).ToList();

                _filteredRows = grouped;
                int total = rows.Count;
                int apps = rows.Count(r => r.Group == "Aplicativos");
                int bg = rows.Count(r => r.Group == "Processos em segundo plano");
                int win = rows.Count(r => r.Group == "Processos do Windows");

                Dispatcher.InvokeAsync(() =>
                {
                    int selectedPid = -1;
                    if (DgProcesses.SelectedItem is ProcessRow sel) selectedPid = sel.Pid;
                    double savedOffset = 0;
                    ScrollViewer? sv = null;
                    try { sv = FindVisualChild<ScrollViewer>(DgProcesses); if (sv != null) savedOffset = sv.VerticalOffset; } catch { }

                    if (_cvsInitialized && _groupedCvs != null)
                    {
                        var childs = _groupedLive.Where(r => r.IsChild).ToList();
                        foreach (var ch in childs) _groupedLive.Remove(ch);
                        int Rank(string g) => g == "Aplicativos" ? 0 : g == "Processos em segundo plano" ? 1 : 2;
                        grouped = (_currentSortColumn switch
                        {
                            "CpuValue" => _currentSortDirection == ListSortDirection.Ascending ? grouped.OrderBy(r => Rank(r.Group)).ThenBy(r => r.CpuValue).ThenBy(r => r.DisplayName) : grouped.OrderBy(r => Rank(r.Group)).ThenByDescending(r => r.CpuValue).ThenBy(r => r.DisplayName),
                            "RamValue" => _currentSortDirection == ListSortDirection.Ascending ? grouped.OrderBy(r => Rank(r.Group)).ThenBy(r => r.RamValue) : grouped.OrderBy(r => Rank(r.Group)).ThenByDescending(r => r.RamValue),
                            "DisplayName" or "Name" => _currentSortDirection == ListSortDirection.Ascending ? grouped.OrderBy(r => Rank(r.Group)).ThenBy(r => r.DisplayName) : grouped.OrderBy(r => Rank(r.Group)).ThenByDescending(r => r.DisplayName),
                            "Status" => _currentSortDirection == ListSortDirection.Ascending ? grouped.OrderBy(r => Rank(r.Group)).ThenBy(r => r.Status) : grouped.OrderBy(r => Rank(r.Group)).ThenByDescending(r => r.Status),
                            _ => grouped.OrderBy(r => Rank(r.Group)).ThenByDescending(r => r.CpuValue)
                        }).ToList();
                        _filteredRows = grouped;
                        var existingDict = _groupedLive.ToDictionary(r => r.GroupKey);
                        var freshDict = grouped.ToDictionary(r => r.GroupKey);
                        for (int i = _groupedLive.Count - 1; i >= 0; i--) if (!freshDict.ContainsKey(_groupedLive[i].GroupKey)) _groupedLive.RemoveAt(i);
                        foreach (var fresh in grouped)
                        {
                            if (existingDict.TryGetValue(fresh.GroupKey, out var ex)) ex.UpdateFrom(fresh);
                            else _groupedLive.Add(fresh);
                        }
                        for (int i = 0; i < grouped.Count; i++)
                        {
                            var desired = grouped[i];
                            int cur = -1;
                            for (int j = 0; j < _groupedLive.Count; j++) if (_groupedLive[j].GroupKey == desired.GroupKey) { cur = j; break; }
                            if (cur >= 0 && cur != i) _groupedLive.Move(cur, i);
                        }
                        var expanded = _groupedLive.Where(r => !r.IsChild && r.IsExpanded && r.RawChildren.Count > 0).ToList();
                        foreach (var parent in expanded.AsEnumerable().Reverse())
                        {
                            int pIdx = _groupedLive.IndexOf(parent);
                            if (pIdx < 0) continue;
                            for (int c = parent.RawChildren.Count - 1; c >= 0; c--)
                            {
                                var child = parent.RawChildren[c];
                                child.IsChild = true;
                                _groupedLive.Insert(pIdx + 1, child);
                            }
                        }
                        var needIcons = expanded.SelectMany(pr => pr.RawChildren).Where(c => c.ProcessIcon == null).ToList();
                        if (needIcons.Count > 0) _ = LoadIconsIncrementalAsync(needIcons);
                        TxtProcessCount.Text = $"— {total} processos ({apps} apps, {bg} segundo plano, {win} Windows)";
                        if (selectedPid >= 0)
                        {
                            var prev = existingDict.Values.FirstOrDefault(r => r.Pid == selectedPid);
                            string? prevKey = prev?.GroupKey;
                            var match = _groupedLive.FirstOrDefault(r => r.Pid == selectedPid) ?? (prevKey != null ? _groupedLive.FirstOrDefault(r => r.GroupKey == prevKey) : null);
                            if (match != null) DgProcesses.SelectedItem = match;
                            else { DgProcesses.SelectedItem = null; DetailName.Text = "Nenhum processo selecionado"; DetailPid.Text = ""; DetailPidValue.Text = "—"; DetailStatus.Text = "—"; DetailUser.Text = "—"; DetailStartTime.Text = "—"; DetailUptime.Text = "—"; DetailHandles.Text = "—"; DetailThreads.Text = "—"; DetailDisk.Text = "—"; DetailNet.Text = "—"; DetailCpu.Text = "—"; DetailRam.Text = "—"; }
                        }
                        foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                        var activeCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == _currentSortColumn);
                        if (activeCol != null) activeCol.SortDirection = _currentSortDirection;
                        if (sv != null) Dispatcher.BeginInvoke(new Action(() => { try { sv.ScrollToVerticalOffset(savedOffset); } catch { } }), DispatcherPriority.Loaded);
                    }
                    else
                    {
                        // Fallback (não deveria ocorrer após Loaded)
                        var cvs = new CollectionViewSource { Source = grouped };
                        cvs.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
                        DgProcesses.ItemsSource = cvs.View;
                        TxtProcessCount.Text = $"— {total} processos ({apps} apps, {bg} segundo plano, {win} Windows)";
                    }
                }, DispatcherPriority.DataBind);
            });
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        // ══════════════════════════════════════════════
        //  KEYBOARD SHORTCUTS
        // ══════════════════════════════════════════════
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; return; }
            if (e.Key == Key.Delete) { Kill(false); e.Handled = true; return; }
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            { TxtSearch?.Focus(); TxtSearch?.SelectAll(); e.Handled = true; return; }
            // Enter numa linha agrupada expande/recolhe (acessibilidade de teclado)
            if (e.Key == Key.Enter && SelectedRow is ProcessRow pr && !pr.IsChild && pr.RawChildren.Count > 0
                && !ReferenceEquals(Keyboard.FocusedElement, TxtSearch))
            { ToggleExpand(pr); e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(TxtSearch?.Text)) { TxtSearch.Text = ""; e.Handled = true; }
                else Close();
                return;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && DgProcesses?.SelectedItem != null) { Kill(false); e.Handled = true; }
        }

        // ══════════════════════════════════════════════
        //  SELECTION + DETAIL PANEL
        // ══════════════════════════════════════════════
        private ProcessRow? SelectedRow => DgProcesses.SelectedItem as ProcessRow;

        private void BtnCloseDetail_Click(object sender, RoutedEventArgs e)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailSplitter.Visibility = Visibility.Collapsed;
            // Set column width to 0 so splitter area collapses
            if (DetailPanel.Parent is Grid parentGrid)
            {
                var col = parentGrid.ColumnDefinitions[2];
                col.Width = new GridLength(0);
                col.MinWidth = 0;
            }
        }

        private CancellationTokenSource? _detailCts;

        private void DgProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;

            // Re-show detail panel if it was closed
            if (DetailPanel.Visibility != Visibility.Visible)
            {
                DetailPanel.Visibility = Visibility.Visible;
                DetailSplitter.Visibility = Visibility.Visible;
                if (DetailPanel.Parent is Grid pg)
                {
                    var col = pg.ColumnDefinitions[2];
                    col.Width = new GridLength(340);
                    col.MinWidth = 0;
                    col.MaxWidth = 500;
                }
            }

            // Icon + campos instantâneos (zero bloqueios)
            lock (_iconLock)
            {
                if (_iconCache.TryGetValue(row.Path ?? "", out var icon) && icon != null)
                    DetailIcon.Source = icon;
                else if (_genericIcon != null)
                    DetailIcon.Source = _genericIcon;
            }

            DetailName.Text = row.Name;
            DetailPid.Text = $"PID: {row.Pid}";
            DetailPidValue.Text = row.Pid.ToString();
            DetailStatus.Text = row.Status;
            DetailCpu.Text = row.Cpu;
            DetailCpu.Foreground = GetHeatColor((float)row.CpuValue, 50, 80);
            DetailRam.Text = row.RamMB;
            DetailRam.Foreground = GetHeatColor((float)row.RamValue, 2048, 8192);
            DetailHandles.Text = row.Handles;
            DetailThreads.Text = row.Threads;
            DetailDisk.Text = row.Disk;
            DetailNet.Text = row.Network;
            DetailPath.Text = row.Path ?? "(desconhecido)";

            // Placeholders enquanto carrega off-UI
            DetailUser.Text = "…";
            DetailStartTime.Text = "…";
            DetailUptime.Text = "…";
            // Prioridade — tenta ler rápida do cache, sem WMI
            try { CmbPriority.SelectedIndex = 3; } catch { }

            // Cancela fetch anterior e dispara novo em background (não trava hover)
            try { _detailCts?.Cancel(); } catch { }
            _detailCts = new CancellationTokenSource();
            var pid = row.Pid;
            var ct = _detailCts.Token;
            _ = Task.Run(() =>
            {
                string user = "—";
                string start = "—";
                string uptime = "—";
                string prioTag = "Normal";
                try
                {
                    // User nativo ultra-rápido (<0.1ms) — sem WMI, sem storm ao navegar com setas
                    try { user = SafeProcessHelper.GetProcessUserFast(pid); } catch { }
                    // StartTime e Priority ainda precisam do handle, mas fora da UI
                    using var proc = Process.GetProcessById(pid);
                    if (proc != null && !proc.HasExited)
                    {
                        try
                        {
                            var st = proc.StartTime;
                            start = st.ToString("dd/MM/yyyy HH:mm");
                            uptime = (DateTime.Now - st).ToString(@"d\.hh\:mm\:ss");
                        }
                        catch { }
                        try
                        {
                            prioTag = proc.PriorityClass switch
                            {
                                ProcessPriorityClass.RealTime => "RealTime",
                                ProcessPriorityClass.High => "High",
                                ProcessPriorityClass.AboveNormal => "AboveNormal",
                                ProcessPriorityClass.Normal => "Normal",
                                ProcessPriorityClass.BelowNormal => "BelowNormal",
                                ProcessPriorityClass.Idle => "Idle",
                                _ => "Normal"
                            };
                        }
                        catch { }
                    }
                }
                catch { }
                if (ct.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SelectedRow?.Pid != pid) return; // seleção mudou
                    DetailUser.Text = user;
                    DetailStartTime.Text = start;
                    DetailUptime.Text = uptime;
                    foreach (ComboBoxItem item in CmbPriority.Items)
                    {
                        if (item.Tag?.ToString() == prioTag) { CmbPriority.SelectedItem = item; return; }
                    }
                    CmbPriority.SelectedIndex = 3;
                }), DispatcherPriority.Background);
            }, ct);
        }

        private static string GetProcessUser(Process proc)
        {
            try
            {
                if (proc == null || proc.HasExited) return "—";
                // Use WMI to get owner
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT Owner FROM Win32_Process WHERE ProcessId={proc.Id}");
                foreach (var obj in searcher.Get())
                {
                    var owner = obj["Owner"];
                    return owner?.ToString() ?? "—";
                }
            }
            catch { }
            return "—";
        }

        private void DgProcesses_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Estilo Gerenciador de Tarefas: duplo clique num app agrupado expande/recolhe
            // os processos abaixo dele; nos demais abre a pasta do executável.
            if (SelectedRow is ProcessRow row && !row.IsChild && row.RawChildren.Count > 0)
            {
                ToggleExpand(row);
                e.Handled = true;
                return;
            }
            MenuOpenFolder_Click(sender, e);
        }

        // ══════════════════════════════════════════════
        //  KILL ACTIONS (with instant removal)
        // ══════════════════════════════════════════════
        private void BtnKill_Click(object sender, RoutedEventArgs e) => Kill(false);
        private void MenuKill_Click(object sender, RoutedEventArgs e) => Kill(false);
        private void BtnKillTree_Click(object sender, RoutedEventArgs e) => Kill(true);
        private void MenuKillTree_Click(object sender, RoutedEventArgs e) => Kill(true);

        private void Kill(bool tree)
        {
            var row = SelectedRow;
            if (row == null) { TxtStatus.Text = "Selecione um processo primeiro."; return; }

            try
            {
                if (tree)
                {
                    KillTree(row.Pid);
                    TxtStatus.Text = $"❌ {row.Name} (PID {row.Pid}) + filhos finalizados.";
                }
                else
                {
                    try
                    {
                        using var p = Process.GetProcessById(row.Pid);
                        if (p == null || p.HasExited) { TxtStatus.Text = $"❌ {row.Name} (PID {row.Pid}) já finalizado."; return; }
                        if (!p.CloseMainWindow()) p.Kill(entireProcessTree: true);
                        TxtStatus.Text = $"❌ {row.Name} (PID {row.Pid}) finalizado.";
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Try Force Stop via our engine
                        try
                        {
                            string target = !string.IsNullOrEmpty(row.Path) && File.Exists(row.Path) ? row.Path : row.Name;
                            ForceStopUnlockService.Unlock(target, new List<BlockingProcessInfo>(), deleteTarget: false);
                            TxtStatus.Text = $"❌ {row.Name}: Force Stop aplicado.";
                        }
                        catch { TxtStatus.Text = $"{row.Name}: acesso negado. Use Force Stop."; }
                    }
                }
            }
            catch (InvalidOperationException) { TxtStatus.Text = $"{row.Name}: processo já encerrado."; }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao finalizar {row.Name}: {ex.Message}"; }

            // Instant removal from UI
            lock (_lock)
            {
                _allRows.RemoveAll(r => r.Pid == row.Pid);
            }
            ApplyFilter(_lastSearchQuery);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr a, string? n);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr job, uint code);

        private void KillTree(int pid)
        {
            try
            {
                var children = GetChildPids(pid);
                foreach (var c in children)
                    try { using var p = Process.GetProcessById(c); p.Kill(entireProcessTree: true); } catch { }
                using var root = Process.GetProcessById(pid);
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    try { AssignProcessToJobObject(job, root.Handle); } catch { }
                    TerminateJobObject(job, 1);
                    CloseHandle(job);
                }
                else root.Kill(entireProcessTree: true);
            }
            catch
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }

            // Also remove children from UI
            lock (_lock) { _allRows.RemoveAll(r => r.ParentPid == pid || r.Pid == pid); }
        }

        private List<int> GetChildPids(int parentPid)
        {
            var res = new List<int>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"Select ProcessId From Win32_Process Where ParentProcessId={parentPid}");
                foreach (var o in searcher.Get()) res.Add(Convert.ToInt32(o["ProcessId"]));
                foreach (var c in res.ToList()) res.AddRange(GetChildPids(c));
            }
            catch { }
            return res;
        }

        // ══════════════════════════════════════════════
        //  FORCE STOP
        // ══════════════════════════════════════════════
        private async void BtnForceStop_Click(object sender, RoutedEventArgs e) => await ForceStopSelectedAsync();
        private async void MenuForceStop_Click(object sender, RoutedEventArgs e) => await ForceStopSelectedAsync();

        private async Task ForceStopSelectedAsync()
        {
            var row = SelectedRow;
            if (row == null) { TxtStatus.Text = "Selecione um processo para Force Stop."; return; }

            TxtStatus.Text = $"Force Stop {row.Name}...";
            await Task.Run(() =>
            {
                try
                {
                    string target = !string.IsNullOrEmpty(row.Path) && (File.Exists(row.Path) || Directory.Exists(row.Path)) ? row.Path : row.Name;
                    var blocking = ForceStopUnlockService.FindBlockingProcesses(target);
                    if (blocking.Count == 0)
                    {
                        try { using var p = Process.GetProcessById(row.Pid); p.Kill(entireProcessTree: true); } catch { }
                    }
                    else ForceStopUnlockService.Unlock(target, blocking, deleteTarget: false);
                }
                catch (Exception ex) { Logger.Log($"[KIT TASK MANAGER] {row.Name}: {ex.Message}"); }
            });

            // Instant removal
            lock (_lock) { _allRows.RemoveAll(r => r.Pid == row.Pid); }
            ApplyFilter(_lastSearchQuery);
            TxtStatus.Text = $"❌ Force Stop {row.Name} concluído.";
        }

        // ══════════════════════════════════════════════
        //  CONTEXT MENU ACTIONS
        // ══════════════════════════════════════════════
        private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null || string.IsNullOrEmpty(r.Path)) return;
            try { Process.Start("explorer.exe", $"/select,\"{r.Path}\""); } catch { }
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null) return;
            try { Clipboard.SetText(r.Path ?? r.Name); TxtStatus.Text = "📋 Caminho copiado."; } catch { }
        }

        private void MenuCopyPid_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null) return;
            try { Clipboard.SetText(r.Pid.ToString()); TxtStatus.Text = "📋 PID copiado."; } catch { }
        }

        // ══════════════════════════════════════════════
        //  PROCESS ACTIONS (Detail Panel)
        // ══════════════════════════════════════════════
        private void CmbPriority_Changed(object sender, SelectionChangedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null || CmbPriority.SelectedItem is not ComboBoxItem item) return;

            try
            {
                var priority = item.Tag?.ToString() switch
                {
                    "RealTime" => ProcessPriorityClass.RealTime,
                    "High" => ProcessPriorityClass.High,
                    "AboveNormal" => ProcessPriorityClass.AboveNormal,
                    "Normal" => ProcessPriorityClass.Normal,
                    "BelowNormal" => ProcessPriorityClass.BelowNormal,
                    "Low" => ProcessPriorityClass.Idle,
                    "Idle" => ProcessPriorityClass.Idle,
                    _ => ProcessPriorityClass.Normal
                };
                using var proc = Process.GetProcessById(row.Pid);
                proc.PriorityClass = priority;
                TxtStatus.Text = $"✅ Prioridade de {row.Name} alterada para {priority}";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao alterar prioridade: {ex.Message}"; }
        }

        private void MenuPriority_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string tag)
            {
                foreach (ComboBoxItem item in CmbPriority.Items)
                {
                    if (item.Tag?.ToString() == tag) { CmbPriority.SelectedItem = item; break; }
                }
            }
        }

        private void BtnClearMemory_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;
            try
            {
                MemoryOptimizer.EmptyProcessWorkingSet(row.Pid);
                TxtStatus.Text = $"🧹 Memória de {row.Name} limpa.";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao limpar memória: {ex.Message}"; }
        }

        private void MenuSuspend_Click(object sender, RoutedEventArgs e) => SuspendResume(true);
        private void MenuResume_Click(object sender, RoutedEventArgs e) => SuspendResume(false);
        private void BtnSuspend_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;
            // Check if process is suspended by trying to resume
            SuspendResume(true);
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandleP(IntPtr handle);

        private const uint PROCESS_SUSPEND_RESUME = 0x0800;

        private void SuspendResume(bool suspend)
        {
            var row = SelectedRow;
            if (row == null) return;
            try
            {
                IntPtr hProcess = OpenProcess(PROCESS_SUSPEND_RESUME, false, row.Pid);
                if (hProcess == IntPtr.Zero) { TxtStatus.Text = "Acesso negado."; return; }
                try
                {
                    if (suspend) { NtSuspendProcess(hProcess); TxtStatus.Text = $"⏸ {row.Name} suspenso."; }
                    else { NtResumeProcess(hProcess); TxtStatus.Text = $"▶ {row.Name} retomado."; }
                }
                finally { CloseHandleP(hProcess); }
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro: {ex.Message}"; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

        private const int ProcessPowerThrottling = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        private void MenuEcoQoS_Click(object sender, RoutedEventArgs e) => BtnEcoQos_Click(sender, e);

        private void BtnEcoQos_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;
            try
            {
                using var proc = Process.GetProcessById(row.Pid);
                var state = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(state));
                try
                {
                    Marshal.StructureToPtr(state, ptr, false);
                    SetProcessInformation(proc.Handle, ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf(state));
                    TxtStatus.Text = $"🌱 EcoQoS ativado para {row.Name}.";
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao ativar EcoQoS: {ex.Message}"; }
        }

        // ══════════════════════════════════════════════
        //  EXPORT CSV
        // ══════════════════════════════════════════════
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = $"KitLugia_Processos_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (dialog.ShowDialog() != true) return;

                var sb = new StringBuilder();
                sb.AppendLine("Nome,PID,CPU%,RAM,Disco,Rede,GPU,Status,Grupo,Threads,Handles,Caminho");

                foreach (var r in _filteredRows)
                {
                    sb.AppendLine($"\"{r.Name}\",{r.Pid},{r.CpuValue:F1},\"{r.RamMB}\",\"{r.Disk}\",\"{r.Network}\",\"{r.Gpu}\",\"{r.Status}\",\"{r.Group}\",{r.Threads},{r.Handles},\"{r.Path}\"");
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                TxtStatus.Text = $"📄 Exportado {dialog.FileName}";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao exportar: {ex.Message}"; }
        }

        // ══════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════
        private static string FormatBytesSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
            return $"{bytesPerSec / (1024 * 1024 * 1024):F2} GB/s";
        }

        // Frozen brush cache — created once, shared across all calls (thread-safe, zero GC)
        private static readonly SolidColorBrush _brushRed = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x11, 0x23)));
        private static readonly SolidColorBrush _brushOrange = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00)));
        private static readonly SolidColorBrush _brushYellow = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00)));
        private static readonly SolidColorBrush _brushGreen = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)));
        private static readonly SolidColorBrush _brushGray = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly SolidColorBrush _brushTransparent = FreezeBrush(Brushes.Transparent);

        private static SolidColorBrush FreezeBrush(SolidColorBrush b)
        {
            if (!b.IsFrozen) b.Freeze();
            return b;
        }

        private static SolidColorBrush GetHeatColor(float value, float warnThreshold, float criticalThreshold)
        {
            if (value >= criticalThreshold) return _brushRed;
            if (value >= warnThreshold) return _brushOrange;
            if (value >= warnThreshold * 0.6f) return _brushYellow;
            return _brushGreen;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        // ══════════════════════════════════════════════
        //  PROCESS ROW MODEL
        // ══════════════════════════════════════════════
        public class ProcessRow : INotifyPropertyChanged
        {
            private BitmapSource? _processIcon;
            private string _displayName = "";
            private string _name = "";
            private int _pid;
            private string _cpu = "0%";
            private double _cpuValue;
            private string _ramMB = "";
            private double _ramValue;
            private string _handles = "0";
            private string _threads = "0";
            private string _group = "";
            private string _status = "";
            private string _disk = "—";
            private double _diskBytesPerSec;
            private string _network = "—";
            private int _networkConnections;
            private double _netBytesPerSec;
            private string _gpu = "—";
            private double _gpuValue;
            private string _path = "";
            private int _parentPid;
            private int _childCount = 1;
            private bool _isProtected;
            private string _protectedBadge = "";
            private string _iconPath = "";
            private bool _isExpanded;
            private List<ProcessRow> _rawChildren = new();
            private bool _isChild;

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            private bool Set<T>(ref T f, T v, string n) { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; Raise(n); return true; }

            public bool IsExpanded { get => _isExpanded; set { if (Set(ref _isExpanded, value, nameof(IsExpanded))) Raise(nameof(ExpandIcon)); } }
            public bool IsChild { get => _isChild; set { if (Set(ref _isChild, value, nameof(IsChild))) { Raise(nameof(NameMargin)); } } }
            public Thickness NameMargin => IsChild ? new Thickness(28, 0, 0, 0) : new Thickness(4, 0, 0, 0);
            public List<ProcessRow> RawChildren { get => _rawChildren; set { _rawChildren = value; Raise(nameof(RawChildren)); Raise(nameof(HasChildren)); } }
            public bool HasChildren => RawChildren != null && RawChildren.Count > 0;
            public string ExpandIcon => IsChild ? "" : (ChildCount > 1 ? (IsExpanded ? "▼" : "▶") : "");
            public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value, nameof(DisplayName))) Raise(nameof(ExpandIcon)); } }
            public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
            public int Pid { get => _pid; set => Set(ref _pid, value, nameof(Pid)); }
            public string Cpu { get => _cpu; set { if (Set(ref _cpu, value, nameof(Cpu))) Raise(nameof(CpuCellBackground)); } }
            public double CpuValue { get => _cpuValue; set { if (Set(ref _cpuValue, value, nameof(CpuValue))) { Raise(nameof(CpuCellBackground)); } } }
            public string RamMB { get => _ramMB; set { if (Set(ref _ramMB, value, nameof(RamMB))) Raise(nameof(RamCellBackground)); } }
            public double RamValue { get => _ramValue; set { if (Set(ref _ramValue, value, nameof(RamValue))) Raise(nameof(RamCellBackground)); } }
            public string Handles { get => _handles; set => Set(ref _handles, value, nameof(Handles)); }
            public string Threads { get => _threads; set => Set(ref _threads, value, nameof(Threads)); }
            public string Group { get => _group; set => Set(ref _group, value, nameof(Group)); }
            public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }
            public string Disk { get => _disk; set => Set(ref _disk, value, nameof(Disk)); }
            public double DiskBytesPerSec { get => _diskBytesPerSec; set => Set(ref _diskBytesPerSec, value, nameof(DiskBytesPerSec)); }
            public string Network { get => _network; set => Set(ref _network, value, nameof(Network)); }
            public int NetworkConnections { get => _networkConnections; set => Set(ref _networkConnections, value, nameof(NetworkConnections)); }
            public double NetBytesPerSec { get => _netBytesPerSec; set => Set(ref _netBytesPerSec, value, nameof(NetBytesPerSec)); }
            public string Gpu { get => _gpu; set => Set(ref _gpu, value, nameof(Gpu)); }
            public double GpuValue { get => _gpuValue; set => Set(ref _gpuValue, value, nameof(GpuValue)); }
            public string Path { get => _path; set => Set(ref _path, value, nameof(Path)); }
            public int ParentPid { get => _parentPid; set => Set(ref _parentPid, value, nameof(ParentPid)); }
            public int ChildCount { get => _childCount; set { if (Set(ref _childCount, value, nameof(ChildCount))) Raise(nameof(ExpandIcon)); } }
            public bool IsProtected { get => _isProtected; set => Set(ref _isProtected, value, nameof(IsProtected)); }
            public string ProtectedBadge { get => _protectedBadge; set { if (Set(ref _protectedBadge, value, nameof(ProtectedBadge))) Raise(nameof(IsProtectedBadgeVisible)); } }
            public Visibility IsProtectedBadgeVisible => string.IsNullOrEmpty(ProtectedBadge) ? Visibility.Collapsed : Visibility.Visible;
            public string IconPath { get => _iconPath; set => Set(ref _iconPath, value, nameof(IconPath)); }

            public BitmapSource? ProcessIcon
            {
                get => _processIcon;
                set { _processIcon = value; Raise(nameof(ProcessIcon)); }
            }

            // Chave estável para diff (Win11: agrupa por nome+grupo)
            public string GroupKey => $"{Name}|{Group}";

            // Atualiza in-place (sem recriar linha — mantém seleção/ordem)
            public void UpdateFrom(ProcessRow src)
            {
                DisplayName = src.DisplayName;
                Cpu = src.Cpu; CpuValue = src.CpuValue;
                RamMB = src.RamMB; RamValue = src.RamValue;
                Handles = src.Handles; Threads = src.Threads;
                Status = src.Status;
                Disk = src.Disk; DiskBytesPerSec = src.DiskBytesPerSec;
                Network = src.Network; NetworkConnections = src.NetworkConnections; NetBytesPerSec = src.NetBytesPerSec;
                Gpu = src.Gpu; GpuValue = src.GpuValue;
                Path = src.Path; ParentPid = src.ParentPid;
                ChildCount = src.ChildCount;
                IsProtected = src.IsProtected; ProtectedBadge = src.ProtectedBadge;
                if (src.ProcessIcon != null) ProcessIcon = src.ProcessIcon;
                IconPath = src.IconPath;
                // Filhos: atualiza lista (RowDetails mostra os PIDs individuais)
                RawChildren = src.RawChildren;
                // IsExpanded preserva o estado do usuário (não sobrescreve)
            }

            // Heatmap colors — Cpu 50/80, Ram sempre em MB (fix: >1GB ficava preto)
            public SolidColorBrush CpuCellBackground => GetCellBackground(CpuValue, 50, 80);
            public SolidColorBrush RamCellBackground => GetCellBackground(RamValue, 40, 80);

            private static readonly SolidColorBrush _cellRed = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(40, 0xE8, 0x11, 0x23)));
            private static readonly SolidColorBrush _cellOrange = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(30, 0xFF, 0x98, 0x00)));
            private static readonly SolidColorBrush _cellYellow = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(20, 0xFF, 0xD7, 0x00)));

            private static SolidColorBrush FreezeCellBrush(SolidColorBrush b)
            {
                if (!b.IsFrozen) b.Freeze();
                return b;
            }

            private static SolidColorBrush GetCellBackground(double value, double warn, double critical)
            {
                if (value >= critical) return _cellRed;
                if (value >= warn) return _cellOrange;
                if (value >= warn * 0.5) return _cellYellow;
                return Brushes.Transparent;
            }
        }
    }
}
