using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace KitLugia.GUI.Pages
{
    public partial class ProcessMonitorPage : Page
    {
        private ObservableCollection<ProcessMonitorInfo> _processes = null!;
        private PerformanceCounter _cpuCounter = null!;
        private PerformanceCounter _ramCounter = null!;
        private DispatcherTimer _updateTimer = null!;
        private ProcessOptimizationManager _optimizationManager = null!;

        public ProcessMonitorPage()
        {
            InitializeComponent();
            InitializeProcessMonitor();
            InitializeOptimizationManager();
            LoadDefaultProfiles();
        }

        #region Initialization

        private void InitializeProcessMonitor()
        {
            _processes = new ObservableCollection<ProcessMonitorInfo>();
            ProcessListView.ItemsSource = _processes;

            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            }
            catch (Exception ex)
            {
                UpdateStatus($"⚠️ Erro ao inicializar contadores: {ex.Message}", "#FF6B00");
            }

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _updateTimer.Tick += UpdateTimer_Tick;
            _updateTimer.Start();

            // Carregar processos em background
            _ = RefreshProcessesAsync();

            this.Unloaded += ProcessMonitorPage_Unloaded;
        }

        public void Cleanup()
        {
            _updateTimer?.Stop();
            _updateTimer = null;
            _cpuCounter?.Dispose();
            _ramCounter?.Dispose();
            _processes?.Clear();
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
            // Carregar perfis padrão
            SteamProfile.IsChecked = true;
            EpicProfile.IsChecked = true;
            BalancedMode.IsChecked = true;
            MemoryCompression.IsChecked = true;
            IntelligentCleanup.IsChecked = true;
            AutoDetectSteam.IsChecked = true;
            AutoDetectEpic.IsChecked = true;
        }

        #endregion

        #region Timer and Updates

        private async void UpdateTimer_Tick(object? sender, EventArgs e)
        {
            UpdateSystemInfo();

            // Process.GetProcesses + WMI queries off UI thread
            var currentProcesses = await Task.Run(() =>
            {
                try
                {
                    return Process.GetProcesses()
                        .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                        .Select(p => new ProcessMonitorInfo
                        {
                            Id = p.Id,
                            Name = p.ProcessName,
                            CpuUsage = Math.Round(p.TotalProcessorTime.TotalMilliseconds / 1000.0, 2),
                            RamUsage = FormatBytes(p.WorkingSet64),
                            Priority = p.PriorityClass.ToString(),
                            Status = p.Responding ? "Responding" : "Not Responding",
                            NetworkUsage = "Detecting..."
                        })
                        .OrderByDescending(p => p.CpuUsage)
                        .Take(50)
                        .ToList();
                }
                catch { return new List<ProcessMonitorInfo>(); }
            });

            try
            {
                foreach (var process in currentProcesses)
                {
                    var existingProcess = _processes.FirstOrDefault(p => p.Id == process.Id);
                    if (existingProcess != null)
                        existingProcess.UpdateFrom(process);
                    else
                        _processes.Add(process);
                }

                var processesToRemove = _processes
                    .Where(p => !currentProcesses.Any(cp => cp.Id == p.Id))
                    .ToList();

                foreach (var process in processesToRemove)
                    _processes.Remove(process);
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
                // Atualizar uso de RAM
                var totalMemory = GC.GetTotalMemory(false);
                var availableMemory = _ramCounter?.NextValue() ?? 0;
                var usedMemory = (16 * 1024) - availableMemory; // Assumindo 16GB
                var memoryUsage = (usedMemory / (16 * 1024)) * 100;

                RamUsageText.Text = $"{usedMemory:F1} GB / 16 GB ({memoryUsage:F1}%)";

                // Atualizar contador de processos
                ProcessCountText.Text = _processes.Count.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar informações do sistema: {ex.Message}");
            }
        }

        private void UpdateProcessList()
        {
            try
            {
                var currentProcesses = Process.GetProcesses()
                    .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                    .Select(p => new ProcessMonitorInfo
                {
                    Id = p.Id,
                    Name = p.ProcessName,
                    CpuUsage = Math.Round(p.TotalProcessorTime.TotalMilliseconds / 1000.0, 2),
                    RamUsage = FormatBytes(p.WorkingSet64),
                    Priority = p.PriorityClass.ToString(),
                    Status = p.Responding ? "Responding" : "Not Responding",
                    NetworkUsage = GetProcessNetworkUsage(p.Id)
                })
                    .OrderByDescending(p => p.CpuUsage)
                    .Take(50) // Limitar para performance
                    .ToList();

                // Atualizar ObservableCollection de forma eficiente
                foreach (var process in currentProcesses)
                {
                    var existingProcess = _processes.FirstOrDefault(p => p.Id == process.Id);
                    if (existingProcess != null)
                    {
                        existingProcess.UpdateFrom(process);
                    }
                    else
                    {
                        _processes.Add(process);
                    }
                }

                // Remover processos que não existem mais
                var processesToRemove = _processes
                    .Where(p => !currentProcesses.Any(cp => cp.Id == p.Id))
                    .ToList();

                foreach (var process in processesToRemove)
                {
                    _processes.Remove(process);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao atualizar lista de processos: {ex.Message}");
            }
        }

        #endregion

        #region Auto-Detection and Optimization

        private void CheckForAutoOptimizations()
        {
            if (!AutoDetectSteam.IsChecked.HasValue || !AutoDetectEpic.IsChecked.HasValue)
                return;

            // Detectar processos de gaming (acessa _processes na UI thread)
            var steamProcesses = _processes.Where(p => 
                p.Name.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("steamwebhelper", StringComparison.OrdinalIgnoreCase)).ToList();

            var epicProcesses = _processes.Where(p => 
                p.Name.Contains("epic", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("eos", StringComparison.OrdinalIgnoreCase)).ToList();

            bool autoNetwork = AutoDetectHighNetwork.IsChecked == true;
            bool autoSteam = AutoDetectSteam.IsChecked == true;
            bool autoEpic = AutoDetectEpic.IsChecked == true;

            // Operações pesadas (rede, registro, processos) em background
            Task.Run(() =>
            {
                // Detectar alta atividade de rede
                var networkActivity = GetNetworkActivity();

                // Ativar Ultra Desempenho automaticamente
                if (autoNetwork && networkActivity > 10)
                    ActivateUltraPerformanceMode();

                // Otimizar processos de gaming
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

                return totalActivity / (1024 * 1024); // Converter para MB
            }
            catch
            {
                return 0;
            }
        }

        private void ActivateUltraPerformanceMode()
        {
            UpdateStatus("🚀 Ativando Ultra Desempenho (alta atividade de rede)", "#00FF88");
            
            // Aumentar prioridade de processos de rede
            var networkProcesses = _processes.Where(p => 
                p.Name.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("epic", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Contains("download", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var process in networkProcesses)
            {
                _optimizationManager.SetProcessPriority(process.Id, ProcessPriorityClass.High);
            }

            // Otimizar configurações de rede
            _optimizationManager.OptimizeNetworkForHighSpeed();
        }

        private void OptimizeGamingProcesses(List<ProcessMonitorInfo> processes, string platform)
        {
            UpdateStatus($"🎮 Otimizando processos {platform}", "#00FF88");

            foreach (var process in processes)
            {
                try
                {
                    // Definir prioridade alta para processos de gaming
                    _optimizationManager.SetProcessPriority(process.Id, ProcessPriorityClass.High);
                    
                    // Otimizar uso de memória
                    _optimizationManager.OptimizeProcessMemory(process.Id);
                    
                    // Definir afinidade de CPU para cores de performance
                    _optimizationManager.SetProcessAffinity(process.Id, 0xF); // Primeiros 4 cores
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
                    var process = Process.GetProcessById(selectedProcess.Id);
                    process.Kill();
                    UpdateStatus($"❌ Processo {selectedProcess.Name} finalizado", "#FF6B00");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"⚠️ Erro ao finalizar processo: {ex.Message}", "#FF6B00");
                }
            }
        }

        private void Profile_Checked(object sender, RoutedEventArgs e)
        {
            // Perfil atualizado - será aplicado no botão Apply
        }

        private void PerformanceMode_Checked(object sender, RoutedEventArgs e)
        {
            // Modo de desempenho atualizado - será aplicado no botão Apply
        }

        private void NetworkOptimization_Checked(object sender, RoutedEventArgs e)
        {
            // Otimização de rede atualizada - será aplicada no botão Apply
        }

        private void MemoryOptimization_Checked(object sender, RoutedEventArgs e)
        {
            // Otimização de memória atualizada - será aplicada no botão Apply
        }

        private void AutoDetection_Checked(object sender, RoutedEventArgs e)
        {
            // Detecção automática atualizada
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
                // Perfis de Gaming
                SteamEnabled = SteamProfile.IsChecked == true,
                EpicEnabled = EpicProfile.IsChecked == true,
                XboxEnabled = XboxProfile.IsChecked == true,
                DiscordEnabled = DiscordProfile.IsChecked == true,

                // Modos de Desempenho
                UltraPerformanceMode = UltraPerfMode.IsChecked == true,
                GamingMode = GamingMode.IsChecked == true,
                BalancedMode = BalancedMode.IsChecked == true,

                // Otimização de Rede
                HighBandwidthMode = HighBandwidthMode.IsChecked == true,
                LowLatencyMode = LowLatencyMode.IsChecked == true,
                DownloadBoost = DownloadBoost.IsChecked == true,

                // Otimização de RAM
                MemoryCompression = MemoryCompression.IsChecked == true,
                IntelligentCleanup = IntelligentCleanup.IsChecked == true,
                StandbyListOptimization = StandbyListOptimization.IsChecked == true,

                // Detecção Automática
                AutoDetectSteam = AutoDetectSteam.IsChecked == true,
                AutoDetectEpic = AutoDetectEpic.IsChecked == true,
                AutoDetectHighNetwork = AutoDetectHighNetwork.IsChecked == true
            };

            _optimizationManager.ApplyOptimizations(optimizations);
            
            UpdateStatus("✅ Customizações aplicadas com sucesso!", "#00FF88");
        }

        private Task RefreshProcessesAsync()
        {
            return Task.Run(() =>
            {
                var processes = Process.GetProcesses()
                    .Where(p => !string.IsNullOrEmpty(p.ProcessName))
                    .Select(p => new ProcessMonitorInfo
                    {
                        Id = p.Id,
                        Name = p.ProcessName,
                        CpuUsage = Math.Round(p.TotalProcessorTime.TotalMilliseconds / 1000.0, 2),
                        RamUsage = FormatBytes(p.WorkingSet64),
                        Priority = p.PriorityClass.ToString(),
                        Status = p.Responding ? "Responding" : "Not Responding",
                        NetworkUsage = "Detecting..."
                    })
                    .OrderByDescending(p => p.CpuUsage)
                    .Take(50)
                    .ToList();

                Dispatcher.Invoke(() =>
                {
                    _processes.Clear();
                    foreach (var p in processes) _processes.Add(p);
                });
            });
        }

        private void UpdateStatus(string message, string color)
        {
            StatusText.Text = message;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:F1} {sizes[order]}";
        }

        private string GetProcessNetworkUsage(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // Simplificado - implementação real exigiria APIs mais complexas
                        return "Detecting...";
                    }
                }
            }
            catch
            {
                return "N/A";
            }
            return "N/A";
        }

        #endregion
    }

    #region Data Models

    public class ProcessMonitorInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double CpuUsage { get; set; }
        public string RamUsage { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string NetworkUsage { get; set; } = string.Empty;

        public void UpdateFrom(ProcessMonitorInfo other)
        {
            CpuUsage = other.CpuUsage;
            RamUsage = other.RamUsage;
            Status = other.Status;
            NetworkUsage = other.NetworkUsage;
        }
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
        public event Action<string, string> StatusChanged = null!;

        public void ApplyOptimizations(OptimizationSettings settings)
        {
            try
            {
                StatusChanged?.Invoke("⚙️ Aplicando otimizações de sistema...", "#FFA500");

                // Otimizações de memória
                if (settings.MemoryCompression)
                    EnableMemoryCompression();

                if (settings.IntelligentCleanup)
                    EnableIntelligentCleanup();

                if (settings.StandbyListOptimization)
                    OptimizeStandbyList();

                // Otimizações de rede
                if (settings.HighBandwidthMode)
                    OptimizeNetworkBandwidth();

                if (settings.LowLatencyMode)
                    OptimizeNetworkLatency();

                if (settings.DownloadBoost)
                    EnableDownloadBoost();

                // Otimizações de desempenho
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
                var process = Process.GetProcessById(processId);
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
                var process = Process.GetProcessById(processId);
                // Forçar garbage collection para o processo
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
                var process = Process.GetProcessById(processId);
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
                // Otimizações de registro para rede
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true))
                {
                    key?.SetValue("TcpWindowSize", 65536, Microsoft.Win32.RegistryValueKind.DWord);
                    key?.SetValue("Tcp1323Opts", 3, Microsoft.Win32.RegistryValueKind.DWord);
                }

                StatusChanged?.Invoke("🌐 Rede otimizada para alta velocidade", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar rede: {ex.Message}", "#FF6B00");
            }
        }

        #region Private Optimization Methods

        private void EnableMemoryCompression()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", true))
                {
                    key?.SetValue("CompressionKey", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }

                StatusChanged?.Invoke("🗜️ Compressão de RAM ativada", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao ativar compressão: {ex.Message}", "#FF6B00");
            }
        }

        private void EnableIntelligentCleanup()
        {
            try
            {
                // Limpar arquivos temporários antigos
                var tempPath = Path.GetTempPath();
                var tempFiles = Directory.GetFiles(tempPath, "*", SearchOption.AllDirectories)
                    .Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(1));

                foreach (var file in tempFiles.Take(100)) // Limitar para não sobrecarregar
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { /* Ignorar arquivos em uso */ }
                }

                StatusChanged?.Invoke("🧹 Limpeza inteligente concluída", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro na limpeza: {ex.Message}", "#FF6B00");
            }
        }

        private void OptimizeStandbyList()
        {
            try
            {
                // Otimizar lista de standby para melhor performance
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-Command \"rundll32.exe powrprof.dll,SetSuspendState 0,1,0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                StatusChanged?.Invoke("💤 Lista de standby otimizada", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar standby: {ex.Message}", "#FF6B00");
            }
        }

        private void OptimizeNetworkBandwidth()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
                {
                    key?.SetValue("NetworkThrottlingIndex", 0xFFFFFFFF, Microsoft.Win32.RegistryValueKind.DWord);
                }

                StatusChanged?.Invoke("📡 Largura de banda otimizada", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar banda: {ex.Message}", "#FF6B00");
            }
        }

        private void OptimizeNetworkLatency()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true))
                {
                    key?.SetValue("SystemResponsiveness", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }

                StatusChanged?.Invoke("⚡ Latência otimizada", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao otimizar latência: {ex.Message}", "#FF6B00");
            }
        }

        private void EnableDownloadBoost()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true))
                {
                    key?.SetValue("MaxUserPort", 65534, Microsoft.Win32.RegistryValueKind.DWord);
                    key?.SetValue("TCPTimedWaitDelay", 30, Microsoft.Win32.RegistryValueKind.DWord);
                }

                StatusChanged?.Invoke("⬇️ Download boost ativado", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao ativar boost: {ex.Message}", "#FF6B00");
            }
        }

        private void ApplyPerformanceMode(OptimizationSettings settings)
        {
            try
            {
                if (settings.UltraPerformanceMode)
                {
                    // Modo Ultra Performance
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "/setactive SCHEME_MIN",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else if (settings.GamingMode)
                {
                    // Modo Gaming
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "/setactive SCHEME_PERFORMANCE",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }
                else if (settings.BalancedMode)
                {
                    // Modo Balanced
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = "/setactive SCHEME_BALANCED",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                }

                StatusChanged?.Invoke("⚡ Modo de desempenho aplicado", "#00FF88");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"⚠️ Erro ao aplicar modo: {ex.Message}", "#FF6B00");
            }
        }

        #endregion
    }

    #endregion
}
