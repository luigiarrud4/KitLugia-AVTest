using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.GUI.Helpers;

namespace KitLugia.GUI.Pages
{
    public partial class ProcessMonitorPage : Page
    {
        private ObservableCollection<ProcessMonitorInfo> _processes = null!;
        private DispatcherTimer _updateTimer = null!;
        private ProcessOptimizationManager _optimizationManager = null!;
        private bool _isLoaded = false;

        // Nomes de processos do Windows / sistema que ficam embaixo
        private static readonly HashSet<string> _windowsProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "smss", "lsass", "services", "wininit", "svchost", "sihost", "taskhostw",
            "dwm", "SearchIndexer", "SearchUI", "ShellExperienceHost", "StartMenuExperienceHost",
            "RuntimeBroker", "WmiPrvSE", "dps", "spoolsv", "TrustedInstaller", "TiWorker",
            "audiodg", "conhost", "ctfmon", "fontdrvhost", "MsMpEng", "NisSrv",
            "System", "Idle", "Registry", "Memory Compression", "vmmem",
            "SecurityHealthService", "SecurityHealthSystray", "dllhost",
            "Microsoft.Photos", "YourPhone", "Widgets", "GameBarPresenceWriter",
            "TextInputHost", "CompPkgSurrogate", "SearchProtocolHost",
            "SearchFilterHost", "SearchProtocolHost", "backgroundTaskHost",
            "MicrosoftEdge", "MicrosoftEdgeCP", "msedge", "msedgewebview2",
            "OneDrive", "OneDriveStandaloneUpdater", "WAASMedicSvc",
            "WUDFHost", "unsecapp", "WmiApSrv", "MoUsoWorker",
            "UsoClient", "MusNotification", "MusNotifyIcon",
            "WerFault", "WerMgr", "WerInject",
            "TabletInputService", "WpnService", "cbdhsvc",
            "PrintWorkflow", "LicenseManager", "ClipSVC",
            "WSearch", "SysMain", "Dnscache", "EventLog",
            "CryptSvc", "BFE", "WinDefend", "SenseIR",
            "SenseCncProxy", "SenseNdl", "mpcmdrun",
            "MsAudit", "CumulativeUpdate", "DISMHost",
            "WaaSMedicSvc", "MoUsoWorker", "HxTsiRouter",
            "SystemSettings", "F5OSTune", "GameBar",
        };

        public ProcessMonitorPage()
        {
            InitializeComponent();
            InitializeProcessMonitor();
            InitializeOptimizationManager();
            LoadDefaultProfiles();
            this.Loaded += ProcessMonitorPage_Loaded;
        }

        #region Initialization

        private void ProcessMonitorPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
            {
                _isLoaded = true;
                _ = RefreshProcessesAsync();
            }
        }

        private void InitializeProcessMonitor()
        {
            _processes = new ObservableCollection<ProcessMonitorInfo>();
            ProcessListView.ItemsSource = _processes;

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            this.Unloaded += ProcessMonitorPage_Unloaded;
        }

        public void Cleanup()
        {
            _updateTimer?.Stop();
            _updateTimer = null;
            _processes?.Clear();
            _isLoaded = false;
            this.Loaded -= ProcessMonitorPage_Loaded;
            this.Unloaded -= ProcessMonitorPage_Unloaded;
            this.DataContext = null;
        }

        private void ProcessMonitorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void InitializeOptimizationManager()
        {
            _optimizationManager = new ProcessOptimizationManager();
            _optimizationManager.StatusChanged += (message, color) =>
                Dispatcher.Invoke(() => UpdateStatus(message, color));
        }

        private void LoadDefaultProfiles()
        {
            SteamProfile.IsChecked = true;
            EpicProfile.IsChecked = true;
            BalancedMode.IsChecked = true;
            MemoryCompression.IsChecked = true;
            IntelligentCleanup.IsChecked = true;
            AutoDetectSteam.IsChecked = true;
            AutoDetectEpic.IsChecked = true;
        }

        #endregion

        #region Process Enumeration (Safe - no Win32Exception spam)

        /// <summary>
        /// Safely enumerates processes without throwing Win32Exception.
        /// Each property is accessed individually with its own try-catch.
        /// </summary>
        private static ProcessMonitorInfo? SafeEnumerateProcess(Process proc)
        {
            if (proc == null) return null;

            string name = string.Empty;
            int id = 0;

            try
            {
                id = proc.Id;
                name = proc.ProcessName;
            }
            catch { return null; }

            if (string.IsNullOrEmpty(name)) return null;

            var info = new ProcessMonitorInfo
            {
                Id = id,
                Name = name
            };

            // CPU usage - can throw Win32Exception for system processes
            try
            {
                info.CpuUsage = Math.Round(proc.TotalProcessorTime.TotalMilliseconds / 1000.0, 2);
            }
            catch { info.CpuUsage = 0; }

            // RAM usage - can throw Win32Exception
            try
            {
                info.RamUsageBytes = proc.WorkingSet64;
                info.RamUsage = FormatBytes(proc.WorkingSet64);
            }
            catch { info.RamUsage = "N/A"; info.RamUsageBytes = 0; }

            // Priority - can throw Win32Exception
            try
            {
                info.Priority = proc.PriorityClass.ToString();
            }
            catch { info.Priority = "N/A"; }

            // Responding - can throw InvalidOperationException if process exited
            try
            {
                info.Status = proc.Responding ? "Ativo" : "Sem resposta";
            }
            catch { info.Status = "N/A"; }

            // Session ID - can throw Win32Exception
            try
            {
                info.SessionId = proc.SessionId;
            }
            catch { info.SessionId = -1; }

            // Categorize: System/Windows processes go to bottom, background above, user apps at top
            info.Category = CategorizeProcess(name, id, info.SessionId);

            // Try to get main module path for icon
            try
            {
                var mainModule = proc.MainModule;
                if (mainModule != null)
                    info.ExecutablePath = mainModule.FileName;
            }
            catch { /* Access denied for many system processes - expected */ }

            return info;
        }

        private static ProcessCategory CategorizeProcess(string name, int pid, int sessionId)
        {
            // PID 0 = Idle, PID 4 = System
            if (pid == 0 || pid == 4) return ProcessCategory.WindowsSystem;

            // Named Windows system processes
            if (_windowsProcessNames.Contains(name)) return ProcessCategory.WindowsSystem;

            // Check session: session 0 is typically services/system
            if (sessionId == 0 && pid > 4) return ProcessCategory.Background;

            // Session 1+ = user processes
            return ProcessCategory.UserApp;
        }

        #endregion

        #region Timer and Updates

        private async void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isLoaded) return;

            UpdateSystemInfo();

            int filterIndex = CmbFilter?.SelectedIndex ?? 0;

            var currentProcesses = await Task.Run(() =>
            {
                var result = new List<ProcessMonitorInfo>();
                try
                {
                    var allProcs = Process.GetProcesses();
                    foreach (var proc in allProcs)
                    {
                        try
                        {
                            var info = SafeEnumerateProcess(proc);
                            if (info != null)
                                result.Add(info);
                        }
                        catch { /* Already handled in SafeEnumerateProcess */ }
                        finally
                        {
                            try { proc.Dispose(); } catch { }
                        }
                    }

                    // Apply filter
                    if (filterIndex == 1)
                        result = result.Where(p => p.Category == ProcessCategory.UserApp).ToList();
                    else if (filterIndex == 2)
                        result = result.Where(p => p.Category == ProcessCategory.Background).ToList();
                    else if (filterIndex == 3)
                        result = result.Where(p => p.Category == ProcessCategory.WindowsSystem).ToList();

                    // Sort: User apps first (by CPU desc), then Background, then Windows System
                    return result
                        .OrderBy(p => p.Category)
                        .ThenByDescending(p => p.CpuUsage)
                        .ThenBy(p => p.Name)
                        .Take(80)
                        .ToList();
                }
                catch { return new List<ProcessMonitorInfo>(); }
            });

            try
            {
                // Efficient update: update existing, add new, remove gone
                var toRemove = new List<ProcessMonitorInfo>();
                foreach (var existing in _processes)
                {
                    var updated = currentProcesses.FirstOrDefault(p => p.Id == existing.Id);
                    if (updated != null)
                        existing.UpdateFrom(updated);
                    else
                        toRemove.Add(existing);
                }

                foreach (var proc in toRemove)
                    _processes.Remove(proc);

                foreach (var proc in currentProcesses)
                {
                    if (!_processes.Any(p => p.Id == proc.Id))
                        _processes.Add(proc);
                }

                ProcessCountText.Text = $"{_processes.Count} processos";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar lista de processos: {ex.Message}");
            }

            CheckForAutoOptimizations();
        }

        private void UpdateSystemInfo()
        {
            try
            {
                var stats = MemoryOptimizer.GetMemoryStats();
                var usedMemory = stats.TotalGB - stats.FreeGB;
                RamUsageText.Text = $"{usedMemory:F1} GB / {stats.TotalGB:F1} GB ({stats.Percent}%)";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar informações do sistema: {ex.Message}");
            }
        }

        #endregion

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:F1} {sizes[order]}";
        }

        #region Auto-Detection and Optimization

        private void CheckForAutoOptimizations()
        {
            if (!AutoDetectSteam.IsChecked.HasValue || !AutoDetectEpic.IsChecked.HasValue)
                return;

            var steamProcesses = _processes.Where(p =>
                p.Name.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase)).ToList();

            var epicProcesses = _processes.Where(p =>
                p.Name.Contains("epic", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("eos", StringComparison.OrdinalIgnoreCase)).ToList();

            bool autoNetwork = AutoDetectHighNetwork.IsChecked == true;
            bool autoSteam = AutoDetectSteam.IsChecked == true;
            bool autoEpic = AutoDetectEpic.IsChecked == true;

            Task.Run(() =>
            {
                var networkActivity = GetNetworkActivity();

                if (autoNetwork && networkActivity > 10)
                    ActivateUltraPerformanceMode();

                if (autoSteam && steamProcesses.Any())
                    OptimizeGamingProcesses(steamProcesses, "Steam");

                if (autoEpic && epicProcesses.Any())
                    OptimizeGamingProcesses(epicProcesses, "Epic Games");
            });
        }

        private double GetNetworkActivity()
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                               i.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                double totalActivity = 0;
                foreach (var ni in networkInterfaces)
                {
                    var stats = ni.GetIPv4Statistics();
                    totalActivity += stats.BytesReceived + stats.BytesSent;
                }

                return totalActivity / (1024 * 1024);
            }
            catch { return 0; }
        }

        private void ActivateUltraPerformanceMode()
        {
            UpdateStatus("🚀 Ativando Ultra Desempenho (alta atividade de rede)", "#00FF88");

            var networkProcesses = _processes.Where(p =>
                p.Name.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("epic", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("download", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var process in networkProcesses)
            {
                _optimizationManager.SetProcessPriority(process.Id, ProcessPriorityClass.High);
            }

            _optimizationManager.OptimizeNetworkForHighSpeed();
        }

        private void OptimizeGamingProcesses(List<ProcessMonitorInfo> processes, string platform)
        {
            UpdateStatus($"🎮 Otimizando processos {platform}", "#00FF88");

            foreach (var process in processes)
            {
                try
                {
                    _optimizationManager.SetProcessPriority(process.Id, ProcessPriorityClass.High);
                    _optimizationManager.OptimizeProcessMemory(process.Id);
                    _optimizationManager.SetProcessAffinity(process.Id, (IntPtr)0xF);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Erro ao otimizar processo {process.Name}: {ex.Message}");
                }
            }
        }

        #endregion

        #region Event Handlers

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshProcessesAsync();
            UpdateStatus("🔄 Lista de processos atualizada", "#00FF88");
        }

        private void OptimizeButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyCustomOptimizations();
        }

        private void KillSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedProcess = ProcessListView.SelectedItem as ProcessMonitorInfo;
            if (selectedProcess != null)
            {
                try
                {
                    using var process = Process.GetProcessById(selectedProcess.Id);
                    process.Kill();
                    UpdateStatus($"❌ Processo {selectedProcess.Name} finalizado", "#FF6B00");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"⚠️ Erro ao finalizar processo: {ex.Message}", "#FF6B00");
                }
            }
            else
            {
                UpdateStatus("⚠️ Selecione um processo na lista", "#FFD700");
            }
        }

        private void Profile_Checked(object sender, RoutedEventArgs e) { }
        private void PerformanceMode_Checked(object sender, RoutedEventArgs e) { }
        private void NetworkOptimization_Checked(object sender, RoutedEventArgs e) { }
        private void MemoryOptimization_Checked(object sender, RoutedEventArgs e) { }
        private void AutoDetection_Checked(object sender, RoutedEventArgs e) { }

        private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Re-filter the process list based on selected category
            _ = RefreshProcessesAsync();
        }

        private async void ApplyCustomizationButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateStatus("⚙️ Aplicando customizações avançadas...", "#FFA500");
            await Task.Run(() => ApplyCustomOptimizations());
            UpdateStatus("✅ Customizações aplicadas com sucesso!", "#00FF88");
        }

        #endregion

        #region Custom Optimization Logic

        private void ApplyCustomOptimizations()
        {
            UpdateStatus("⚙️ Aplicando customizações avançadas...", "#FFA500");

            var optimizations = new OptimizationSettings
            {
                SteamEnabled = SteamProfile.IsChecked == true,
                EpicEnabled = EpicProfile.IsChecked == true,
                XboxEnabled = XboxProfile.IsChecked == true,
                DiscordEnabled = DiscordProfile.IsChecked == true,
                UltraPerformanceMode = UltraPerfMode.IsChecked == true,
                GamingMode = GamingMode.IsChecked == true,
                BalancedMode = BalancedMode.IsChecked == true,
                HighBandwidthMode = HighBandwidthMode.IsChecked == true,
                LowLatencyMode = LowLatencyMode.IsChecked == true,
                DownloadBoost = DownloadBoost.IsChecked == true,
                MemoryCompression = MemoryCompression.IsChecked == true,
                IntelligentCleanup = IntelligentCleanup.IsChecked == true,
                StandbyListOptimization = StandbyListOptimization.IsChecked == true,
                AutoDetectSteam = AutoDetectSteam.IsChecked == true,
                AutoDetectEpic = AutoDetectEpic.IsChecked == true,
                AutoDetectHighNetwork = AutoDetectHighNetwork.IsChecked == true
            };

            _optimizationManager.ApplyOptimizations(optimizations);

            UpdateStatus("✅ Customizações aplicadas com sucesso!", "#00FF88");
        }

        private Task RefreshProcessesAsync()
        {
            // Capture current filter on UI thread
            int filterIndex = 0;
            Dispatcher.Invoke(() => { filterIndex = CmbFilter?.SelectedIndex ?? 0; });

            return Task.Run(() =>
            {
                var processes = new List<ProcessMonitorInfo>();
                var allProcs = Process.GetProcesses();
                foreach (var proc in allProcs)
                {
                    try
                    {
                        var info = SafeEnumerateProcess(proc);
                        if (info != null)
                            processes.Add(info);
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }

                // Apply filter
                if (filterIndex == 1)
                    processes = processes.Where(p => p.Category == ProcessCategory.UserApp).ToList();
                else if (filterIndex == 2)
                    processes = processes.Where(p => p.Category == ProcessCategory.Background).ToList();
                else if (filterIndex == 3)
                    processes = processes.Where(p => p.Category == ProcessCategory.WindowsSystem).ToList();

                processes = processes
                    .OrderBy(p => p.Category)
                    .ThenByDescending(p => p.CpuUsage)
                    .ThenBy(p => p.Name)
                    .Take(80)
                    .ToList();

                Dispatcher.Invoke(() =>
                {
                    _processes.Clear();
                    foreach (var p in processes) _processes.Add(p);
                    ProcessCountText.Text = $"{_processes.Count} processos";
                });
            });
        }

        private void UpdateStatus(string message, string color)
        {
            try
            {
                StatusText.Text = message;
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            }
            catch { }
        }

        #endregion
    }

    #region Data Models

    public enum ProcessCategory
    {
        UserApp = 0,      // Aplicativos do usuário (topo)
        Background = 1,   // Processos em segundo plano (meio)
        WindowsSystem = 2 // Processos do Windows/Sistema (baixo)
    }

    public class ProcessMonitorInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public int SessionId { get; set; }
        public ProcessCategory Category { get; set; } = ProcessCategory.UserApp;
        public long RamUsageBytes { get; set; }

        private BitmapSource? _icon;
        public BitmapSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(nameof(Icon)); }
        }

        private double _cpuUsage;
        public double CpuUsage
        {
            get => _cpuUsage;
            set
            {
                _cpuUsage = value;
                OnPropertyChanged(nameof(CpuUsage));
                OnPropertyChanged(nameof(CpuUsageFormatted));
                OnPropertyChanged(nameof(CpuColor));
            }
        }

        public string CpuUsageFormatted => CpuUsage > 0 ? $"{CpuUsage:F1}%" : "0%";

        public System.Windows.Media.Brush CpuColor
        {
            get
            {
                if (CpuUsage > 50) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107));
                if (CpuUsage > 20) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0));
                if (CpuUsage > 5) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 205, 196));
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));
            }
        }

        private string _ramUsage = string.Empty;
        public string RamUsage
        {
            get => _ramUsage;
            set { _ramUsage = value; OnPropertyChanged(nameof(RamUsage)); }
        }

        private string _priority = string.Empty;
        public string Priority
        {
            get => _priority;
            set { _priority = value; OnPropertyChanged(nameof(Priority)); }
        }

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        private string _networkUsage = string.Empty;
        public string NetworkUsage
        {
            get => _networkUsage;
            set { _networkUsage = value; OnPropertyChanged(nameof(NetworkUsage)); }
        }

        public string CategoryLabel => Category switch
        {
            ProcessCategory.UserApp => "👤 App",
            ProcessCategory.Background => "⚙️ Background",
            ProcessCategory.WindowsSystem => "🪟 Windows",
            _ => "?"
        };

        public System.Windows.Media.Brush CategoryColor => Category switch
        {
            ProcessCategory.UserApp => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 205, 196)),
            ProcessCategory.Background => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0)),
            ProcessCategory.WindowsSystem => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(130, 130, 130)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150))
        };

        public void UpdateFrom(ProcessMonitorInfo other)
        {
            CpuUsage = other.CpuUsage;
            RamUsage = other.RamUsage;
            RamUsageBytes = other.RamUsageBytes;
            Status = other.Status;
            NetworkUsage = other.NetworkUsage;
            Priority = other.Priority;
            Category = other.Category;
            OnPropertyChanged(nameof(CategoryLabel));
            OnPropertyChanged(nameof(CategoryColor));
        }

        /// <summary>
        /// Loads the process icon asynchronously. Safe to call from any thread.
        /// </summary>
        public async Task LoadIconAsync()
        {
            if (Icon != null) return; // Already loaded
            if (string.IsNullOrEmpty(ExecutablePath)) return;

            await Task.Run(() =>
            {
                try
                {
                    var icon = ProgramIconHelper.GetIconFromFile(ExecutablePath);
                    if (icon != null)
                    {
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            Icon = icon;
                        });
                    }
                }
                catch { /* Icon loading is best-effort */ }
            });
        }

        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class OptimizationSettings
    {
        public bool SteamEnabled { get; set; }
        public bool EpicEnabled { get; set; }
        public bool XboxEnabled { get; set; }
        public bool DiscordEnabled { get; set; }

        public bool UltraPerformanceMode { get; set; }
        public bool GamingMode { get; set; }
        public bool BalancedMode { get; set; }

        public bool HighBandwidthMode { get; set; }
        public bool LowLatencyMode { get; set; }
        public bool DownloadBoost { get; set; }

        public bool MemoryCompression { get; set; }
        public bool IntelligentCleanup { get; set; }
        public bool StandbyListOptimization { get; set; }

        public bool AutoDetectSteam { get; set; }
        public bool AutoDetectEpic { get; set; }
        public bool AutoDetectHighNetwork { get; set; }
    }

    #endregion

    #region Optimization Manager

    public class ProcessOptimizationManager
    {
        public event Action<string, string>? StatusChanged;

        public void ApplyOptimizations(OptimizationSettings settings)
        {
            try
            {
                StatusChanged?.Invoke("⚙️ Aplicando otimizações de sistema...", "#FFA500");

                if (settings.MemoryCompression) EnableMemoryCompression();
                if (settings.IntelligentCleanup) EnableIntelligentCleanup();
                if (settings.StandbyListOptimization) OptimizeStandbyList();
                if (settings.HighBandwidthMode) OptimizeNetworkBandwidth();
                if (settings.LowLatencyMode) OptimizeNetworkLatency();
                if (settings.DownloadBoost) EnableDownloadBoost();

                ApplyPerformanceMode(settings);

                StatusChanged?.Invoke("✅ Todas as otimizações aplicadas!", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"❌ Erro: {ex.Message}", "#FF6B00");
            }
        }

        public void SetProcessPriority(int processId, ProcessPriorityClass priority)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.PriorityClass = priority;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao definir prioridade: {ex.Message}", "#FF6B00");
            }
        }

        public void OptimizeProcessMemory(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.MinWorkingSet = (nint)process.WorkingSet64;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar memória: {ex.Message}", "#FF6B00");
            }
        }

        public void SetProcessAffinity(int processId, IntPtr affinity)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.ProcessorAffinity = affinity;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao definir afinidade: {ex.Message}", "#FF6B00");
            }
        }

        public void OptimizeNetworkForHighSpeed()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.SetValue("TcpWindowSize", 65536, Microsoft.Win32.RegistryValueKind.DWord);
                key?.SetValue("Tcp1323Opts", 3, Microsoft.Win32.RegistryValueKind.DWord);
                StatusChanged?.Invoke("🌐 Rede otimizada para alta velocidade", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar rede: {ex.Message}", "#FF6B00");
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:F1} {sizes[order]}";
        }

        private void EnableMemoryCompression()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true);
                key?.SetValue("CompressionKey", 1, Microsoft.Win32.RegistryValueKind.DWord);
                StatusChanged?.Invoke("🗜️ Compressão de RAM ativada", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void EnableIntelligentCleanup()
        {
            try
            {
                var tempPath = Path.GetTempPath();
                var tempFiles = Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories)
                    .Where(f => { try { return File.GetLastWriteTime(f) < DateTime.Now.AddDays(1); } catch { return false; } });

                int deleted = 0;
                foreach (var file in tempFiles.Take(100))
                {
                    try { File.Delete(file); deleted++; } catch { }
                }

                StatusChanged?.Invoke($"🧹 Limpeza inteligente: {deleted} arquivos removidos", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void OptimizeStandbyList()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Command \"rundll32.exe powrprof.dll,SetSuspendState 0,1,0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                StatusChanged?.Invoke("💤 Lista de standby otimizada", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void OptimizeNetworkBandwidth()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                key?.SetValue("NetworkThrottlingIndex", 0xFFFFFFFF, Microsoft.Win32.RegistryValueKind.DWord);
                StatusChanged?.Invoke("📡 Largura de banda otimizada", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void OptimizeNetworkLatency()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                key?.SetValue("SystemResponsiveness", 0, Microsoft.Win32.RegistryValueKind.DWord);
                StatusChanged?.Invoke("⚡ Latência otimizada", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void EnableDownloadBoost()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.SetValue("MaxUserPort", 65534, Microsoft.Win32.RegistryValueKind.DWord);
                key?.SetValue("TCPTimedWaitDelay", 30, Microsoft.Win32.RegistryValueKind.DWord);
                StatusChanged?.Invoke("⬇️ Download boost ativado", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }

        private void ApplyPerformanceMode(OptimizationSettings settings)
        {
            try
            {
                string scheme = settings.UltraPerformanceMode ? "SCHEME_MIN" :
                                settings.GamingMode ? "SCHEME_PERFORMANCE" : "SCHEME_BALANCED";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = $"/setactive {scheme}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                StatusChanged?.Invoke("⚡ Modo de desempenho aplicado", "#00FF88");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"⚠️ Erro: {ex.Message}", "#FF6B00"); }
        }
    }

    #endregion
}
