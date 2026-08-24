using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using KitLugia.Core;
using KitLugia.GUI.Services;
using Microsoft.Win32;

// Resolve ambiguidade da Cor
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class TweaksPage : Page
    {
        private bool _isLoading = true;
        private int _selectedGpuIndex = -1;
        private string? _selectedGpuRegPath;
        private readonly SolidColorBrush _colorActive = new SolidColorBrush(Color.FromRgb(108, 203, 95));
        private readonly SolidColorBrush _colorDefault = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private readonly SolidColorBrush _colorSlideActive = new SolidColorBrush(Color.FromRgb(255, 170, 0)); // Amarelo Escuro para SLIDE

        public TweaksPage()
        {
            InitializeComponent();

            this.Loaded += (s, e) => { _isPageLoaded = true; _ = LoadCurrentStatus(); };
            this.Unloaded += TweaksPage_Unloaded;
        }

        private bool _isPageLoaded;


        public void Cleanup()
        {
            this.Unloaded -= TweaksPage_Unloaded;


            this.DataContext = null;



        }

        private void TweaksPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isPageLoaded = false;
            Cleanup();
        }

        private async Task LoadCurrentStatus()
        {
            await Task.Run(() =>
            {
                // ========== LER TODOS OS REGISTROS NA THREAD DE BACKGROUND ==========
                bool gamesOptimized = SystemTweaks.IsGamingOptimized();
                bool mpoDisabled = SystemTweaks.IsMpoDisabled();
                bool vbsEnabledInSystem = SystemTweaks.IsVbsEnabled();
                bool bingDisabled = SystemTweaks.IsBingDisabled();
                bool memoryUsageEnabled = SystemTweaks.IsMemoryUsageEnabled();
                bool timerOptimized = SystemTweaks.IsTimerResolutionOptimized();
                bool shutdownOptimized = SystemTweaks.IsFastShutdownEnabled();
                bool slideInput = SystemTweaks.IsInputLatencyOptimized();
                bool slideUsb = SystemTweaks.IsUsbPowerSavingDisabled();
                bool slideGaming = SystemTweaks.IsGamingLatencyOptimized();
                bool pciePowerDisabled = SystemTweaks.IsPcieLinkStatePowerManagementDisabled();
                bool timeoutDisabled = SystemTweaks.IsHardDiskDisplayTimeoutDisabled();

                bool smartScreenSystemDisabled = IsSmartScreenSystemDisabled();
                bool smartScreenExplorerDisabled = IsSmartScreenExplorerDisabled();

                bool backgroundApps = SystemTweaks.IsBackgroundAppsDisabled();
                bool ndu = SystemTweaks.IsNDUDisabled();
                bool serviceStartup = SystemTweaks.IsServiceStartupOptimized();
                bool noAutoReboot = SystemTweaks.IsNoAutoRebootEnabled();
                bool diagnosticServices = SystemTweaks.IsDiagnosticServicesDisabled();
                bool powerThrottling = SystemTweaks.IsPowerThrottlingDisabled();
                bool gdiScaling = SystemTweaks.IsGdiScalingDisabled();

                bool l2CacheSet = SystemTweaks.IsSecondLevelDataCacheSet();
                bool rmCacheLocSet = SystemTweaks.IsRmCacheLocSet();
                bool nagleDisabled = SystemTweaks.IsNagleAlgorithmDisabled();
                bool coreParkingDisabled = SystemTweaks.IsCoreParkingDisabled();
                var cpuInfo = SystemTweaks.GetCpuInfo();
                var cacheVal = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache", 0);
                int cacheKb = cacheVal != null ? Convert.ToInt32(cacheVal) : 0;

                var gpuNames = SystemTweaks.GetAllGpuNames();
                string firstGpuRegPath = gpuNames.Count > 0 ? SystemTweaks.FindGpuRegistryPathByDescription(gpuNames[0]) : null;

                bool frameQueue = SystemTweaks.IsFrameQueueModeSet();
                bool preemption = SystemTweaks.IsPreemptionEnabled();
                bool gpuIdle = SystemTweaks.IsGpuIdleDisabled();
                bool powerLatency = SystemTweaks.IsGpuPowerLatencySet();

                bool hags = SystemTweaks.IsHagsEnabled();
                bool tdr = SystemTweaks.IsTdrDelayIncreased();
                bool ioLock = SystemTweaks.IsIoPageLockLimitSet();
                bool inputQueue = SystemTweaks.IsInputQueueSizeIncreased();

                bool startupDelay = SystemTweaks.IsStartupDelayOptimized();
                bool fastStartup = SystemTweaks.IsFastStartupDisabled();
                bool bootMenu = SystemTweaks.IsBootMenuTimeoutZero();
                bool numLock = SystemTweaks.IsNumLockOnStartupEnabled();

                bool waitToKillService = SystemTweaks.IsWaitToKillServiceOptimized();
                bool quickAppKill = SystemTweaks.IsQuickAppKillApplied();
                bool clearPageFile = SystemTweaks.IsClearPageFileDisabled();
                bool verboseStatus = SystemTweaks.IsVerboseStatusEnabled();

                bool menuDelay = SystemTweaks.IsMenuShowDelayOptimized();
                bool visualFX = SystemTweaks.IsVisualFXOptimized();
                int explorerLaunchTo = SystemTweaks.GetExplorerLaunchToIndex();
                bool iconCache = SystemTweaks.IsIconCacheIncreased();
                bool pca = SystemTweaks.IsPCADisabled();

                // ========== ATUALIZAR UI NA THREAD PRINCIPAL ==========
                Dispatcher.Invoke(() =>
                {
                    if (!_isPageLoaded) return;
                    _isLoading = true;

                    ChkMaintainTweaks.IsChecked = TweakRegistry.IsMaintained();

                    ChkGameMode.IsChecked = gamesOptimized;
                    UpdateLabel(StatusGame, gamesOptimized, "Prioridade Alta", "Padrão");

                    ChkMPO.IsChecked = mpoDisabled;
                    UpdateLabel(StatusMPO, mpoDisabled, "Corrigido (OFF)", "Padrão (ON)");

                    ChkVBS.IsChecked = !vbsEnabledInSystem;
                    StatusVBS.Text = vbsEnabledInSystem ? "Padrão (Seguro)" : "⚡ Max FPS";
                    StatusVBS.Foreground = vbsEnabledInSystem ? _colorDefault : _colorActive;

                    ChkBing.IsChecked = bingDisabled;
                    UpdateLabel(StatusBing, bingDisabled, "Limpo", "Padrão");

                    ChkMemoryUsage.IsChecked = memoryUsageEnabled;
                    UpdateLabel(StatusMemoryUsage, memoryUsageEnabled, "Otimizado", "Padrão");

                    ChkTimer.IsChecked = timerOptimized;
                    UpdateLabel(StatusTimer, timerOptimized, "Latência Mínima", "Padrão");

                    ChkShutdown.IsChecked = shutdownOptimized;
                    UpdateLabel(StatusShutdown, shutdownOptimized, "⚡ Turbo Boot", "Padrão");

                    // SmartScreen
                    UpdateLabel(StatusSmartScreenSystem, smartScreenSystemDisabled, "Desativado", "Ativo");
                    ChkSmartScreenSystem.IsChecked = smartScreenSystemDisabled;

                    UpdateLabel(StatusSmartScreenExplorer, smartScreenExplorerDisabled, "Desativado", "Ativo");
                    ChkSmartScreenExplorer.IsChecked = smartScreenExplorerDisabled;

                    // Telemetria e relatórios
                    bool diagTrackOff = SystemTweaks.IsServiceDisabled("DiagTrack");
                    UpdateLabel(StatusDiagTrackSvc, diagTrackOff, "Desativado", "Ativo");
                    ChkDiagTrackSvc.IsChecked = diagTrackOff;

                    bool wapPushOff = SystemTweaks.IsServiceDisabled("dmwappushservice");
                    UpdateLabel(StatusDmwappushSvc, wapPushOff, "Desativado", "Ativo");
                    ChkDmwappushSvc.IsChecked = wapPushOff;

                    bool werOff = SystemTweaks.IsServiceDisabled("WerSvc");
                    UpdateLabel(StatusWerSvc, werOff, "Desativado", "Ativo");
                    ChkWerSvc.IsChecked = werOff;

                    bool pcaOff = SystemTweaks.IsServiceDisabled("PcaSvc");
                    UpdateLabel(StatusPcaSvc, pcaOff, "Desativado", "Ativo");
                    ChkPcaSvc.IsChecked = pcaOff;

                    bool telTasksOff = SystemTweaks.AreTelemetryTasksDisabled();
                    UpdateLabel(StatusTelemetryTasks, telTasksOff, "Desativadas", "Ativas");
                    ChkTelemetryTasks.IsChecked = telTasksOff;

                    ChkBackgroundApps.IsChecked = backgroundApps;
                    UpdateLabel(StatusBackgroundApps, backgroundApps, "Desativado", "Padrão");

                    ChkNDU.IsChecked = ndu;
                    UpdateLabel(StatusNDU, ndu, "Desativado", "Padrão");

                    ChkServiceStartup.IsChecked = serviceStartup;
                    UpdateLabel(StatusServiceStartup, serviceStartup, "Otimizado", "Padrão");

                    ChkNoAutoReboot.IsChecked = noAutoReboot;
                    UpdateLabel(StatusNoAutoReboot, noAutoReboot, "Ativado", "Padrão");

                    ChkDiagnosticServices.IsChecked = diagnosticServices;
                    UpdateLabel(StatusDiagnosticServices, diagnosticServices, "Desativado", "Padrão");

                    ChkPowerThrottling.IsChecked = powerThrottling;
                    UpdateLabel(StatusPowerThrottling, powerThrottling, "Desativado", "Padrão");

                    ChkGdiScaling.IsChecked = gdiScaling;
                    UpdateLabel(StatusGdiScaling, gdiScaling, "Desativado", "Padrão");

                    ChkSlideInput.IsChecked = slideInput;
                    UpdateSlideLabel(StatusSlideInput, slideInput, "Nível Máximo", "Padrão");

                    ChkSlideUsb.IsChecked = slideUsb;
                    UpdateSlideLabel(StatusSlideUsb, slideUsb, "Desativado", "Padrão");

                    ChkSlideGaming.IsChecked = slideGaming;
                    UpdateSlideLabel(StatusSlideGaming, slideGaming, "Extremo (GameDVR OFF)", "Padrão");

                    ChkPciePower.IsChecked = pciePowerDisabled;
                    UpdateSlideLabel(StatusPciePower, pciePowerDisabled, "Desativado (Off)", "Padrão");

                    ChkTimeout.IsChecked = timeoutDisabled;
                    UpdateSlideLabel(StatusTimeout, timeoutDisabled, "Desativado (0)", "Padrão");

                    // Hardware & Rede
                    ChkL2Cache.IsChecked = l2CacheSet;
                    UpdateLabel(StatusL2Cache, l2CacheSet, "Aplicado", "Padrão");

                    ChkRmCacheLoc.IsChecked = rmCacheLocSet;
                    UpdateLabel(StatusRmCacheLoc, rmCacheLocSet, "Aplicado", "Padrão");
                    InfoRmCacheLoc.Text = rmCacheLocSet
                        ? $"Aplicado: {Environment.ProcessorCount} núcleos lógicos (NVIDIA)."
                        : "Configurado com o numero de nucleos logicos.";

                    string cacheStr = cacheKb > 0 ? $"{cacheKb} KB" : "Não definido";
                    InfoCpu.Text = $"CPU: {cpuInfo.Name}  |  L2: {cpuInfo.L2CacheKb} KB  |  L3: {cpuInfo.L3CacheKb} KB  |  Registry: {cacheStr}";

                    ChkNagle.IsChecked = nagleDisabled;
                    UpdateLabel(StatusNagle, nagleDisabled, "Aplicado", "Padrão");

                    ChkCoreParking.IsChecked = coreParkingDisabled;
                    UpdateLabel(StatusCoreParking, coreParkingDisabled, "Aplicado", "Padrão");

                    // GPU & Scheduling
                    ChkGpuSelector.Items.Clear();
                    foreach (var gn in gpuNames)
                        ChkGpuSelector.Items.Add(gn);
                    if (ChkGpuSelector.Items.Count > 0)
                    {
                        ChkGpuSelector.SelectedIndex = 0;
                        _selectedGpuIndex = 0;
                        _selectedGpuRegPath = firstGpuRegPath;
                    }

                    BuildVramRows();

                    ChkFrameQueue.IsChecked = frameQueue;
                    UpdateLabel(StatusFrameQueue, frameQueue, "Low Latency (0)", "Padrão (1)");
                    InfoFrameQueue.Text = frameQueue ? "FrameQueueMode=0 (Low Latency)" : "FrameQueueMode=1 (Padrão Windows)";

                    ChkPreemption.IsChecked = preemption;
                    UpdateLabel(StatusPreemption, preemption, "Ativado", "Padrão");

                    ChkGpuIdle.IsChecked = gpuIdle;
                    UpdateLabel(StatusGpuIdle, gpuIdle, "Desativado", "Ativo");

                    ChkPowerLatency.IsChecked = powerLatency;
                    UpdateLabel(StatusPowerLatency, powerLatency, "Latência (1)", "Padrão (0)");

                    // Hardware & Rede — novos
                    ChkHags.IsChecked = hags;
                    UpdateLabel(StatusHags, hags, "Ativado (2)", "Padrão");

                    ChkTdr.IsChecked = tdr;
                    UpdateLabel(StatusTdr, tdr, "10s", "Padrão (2s)");

                    ChkIoLock.IsChecked = ioLock;
                    UpdateLabel(StatusIoLock, ioLock, "8192 KB", "Padrão");

                    ChkInputQueue.IsChecked = inputQueue;
                    UpdateLabel(StatusInputQueue, inputQueue, "200", "Padrão (100)");

                    // Novos hardware-aware tweaks
                    bool powerMizer = SystemTweaks.IsNvidiaPowerMizerMaxPerformance();
                    ChkPowerMizer.IsChecked = powerMizer;
                    UpdateLabel(StatusPowerMizer, powerMizer, "Max Perf", "Padrão");

                    bool nvmeLatency = SystemTweaks.IsNvMeLatencyOptimized();
                    ChkNvMe.IsChecked = nvmeLatency;
                    UpdateLabel(StatusNvMe, nvmeLatency, "Otimizado", "Padrão");
                    var nvmeDrives = SystemTweaks.DetectNvMeDrives();
                    InfoNvMe.Text = nvmeDrives.Count > 0
                        ? $"NVMe: {string.Join(", ", nvmeDrives.Take(2))} ({nvmeDrives.Count} detectado(s))"
                        : "Nenhum drive NVMe detectado";
                    ChkNvMe.IsEnabled = nvmeDrives.Count > 0;
                    StatusNvMe.Text = nvmeDrives.Count > 0 ? (nvmeLatency ? "Otimizado" : "Padrão") : "Sem NVMe";

                    bool dpcLatency = SystemTweaks.IsGpuDpcLatencyLow();
                    ChkDpcLatency.IsChecked = dpcLatency;
                    UpdateLabel(StatusDpcLatency, dpcLatency, "IRQ8=1", "Padrão");

                    bool memPriority = SystemTweaks.IsMemoryPrioritizationDse();
                    ChkMemPriority.IsChecked = memPriority;
                    UpdateLabel(StatusMemPriority, memPriority, "Em RAM", "Padrão");

                    // Startup
                    ChkStartupDelay.IsChecked = startupDelay;
                    UpdateLabel(StatusStartupDelay, startupDelay, "Removido", "Padrão");

                    ChkFastStartup.IsChecked = fastStartup;
                    UpdateLabel(StatusFastStartup, fastStartup, "Desativado", "Ativo");

                    ChkBootMenu.IsChecked = bootMenu;
                    UpdateLabel(StatusBootMenu, bootMenu, "0s", "Padrão (30s)");

                    ChkNumLock.IsChecked = numLock;
                    UpdateLabel(StatusNumLock, numLock, "Ativado", "Desativado");

                    // Shutdown
                    ChkWaitToKillService.IsChecked = waitToKillService;
                    UpdateLabel(StatusWaitToKillService, waitToKillService, "2s", "Padrão (5s)");

                    ChkQuickAppKill.IsChecked = quickAppKill;
                    UpdateLabel(StatusQuickAppKill, quickAppKill, "Ativado", "Padrão");

                    ChkClearPageFile.IsChecked = clearPageFile;
                    UpdateLabel(StatusClearPageFile, clearPageFile, "Desativado", "Padrão");

                    ChkVerboseStatus.IsChecked = verboseStatus;
                    UpdateLabel(StatusVerboseStatus, verboseStatus, "Detalhado", "Oculto");

                    // Interface & Explorer
                    bool menuDelay = SystemTweaks.IsMenuShowDelayOptimized();
                    bool visualFX = SystemTweaks.IsVisualFXOptimized();
                    int explorerLaunchTo = SystemTweaks.GetExplorerLaunchToIndex();
                    bool iconCache = SystemTweaks.IsIconCacheIncreased();
                    bool pca = SystemTweaks.IsPCADisabled();

                    ChkMenuDelay.IsChecked = menuDelay;
                    UpdateLabel(StatusMenuDelay, menuDelay, "0ms", "Padrão (400ms)");

                    ChkVisualFX.IsChecked = visualFX;
                    UpdateLabel(StatusVisualFX, visualFX, "Máximo", "Padrão");

                    // Populate Explorer LaunchTo ComboBox
                    ChkExplorerLaunchTo.Items.Clear();
                    ChkExplorerLaunchTo.Items.Add("Acesso Rápido");
                    ChkExplorerLaunchTo.Items.Add("Este Computador");
                    ChkExplorerLaunchTo.Items.Add("Downloads");
                    ChkExplorerLaunchTo.SelectedIndex = explorerLaunchTo >= 0 && explorerLaunchTo <= 2 ? explorerLaunchTo : 0;

                    ChkIconCache.IsChecked = iconCache;
                    UpdateLabel(StatusIconCache, iconCache, "8192 KB", "Padrão");

                    ChkPCA.IsChecked = pca;
                    UpdateLabel(StatusPCA, pca, "Desativado", "Ativo");

                    _isLoading = false;
                });
            });
        }

        private void UpdateLabel(TextBlock label, bool isActive, string textActive, string textInactive)
        {
            label.Text = isActive ? textActive : textInactive;
            label.Foreground = isActive ? _colorActive : _colorDefault;
        }

        private void UpdateSlideLabel(TextBlock label, bool isActive, string textActive, string textInactive)
        {
            label.Text = isActive ? textActive : textInactive;
            label.Foreground = isActive ? _colorSlideActive : _colorDefault;
        }

        // --- CLIQUES ---

        private async void ChkSlideInput_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkSlideInput.IsChecked == true;
                UpdateSlideLabel(StatusSlideInput, targetActive, "Aplicando...", "Revertendo...");

                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeInputLatency();
                    else SystemTweaks.RevertInputLatency();
                });

                UpdateSlideLabel(StatusSlideInput, targetActive, "Nível Máximo", "Padrão");

                RecordTweak("SlideInput", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("REINÍCIO NECESSÁRIO", "As mudanças na latência de input exigem reiniciar o computador.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChkSlideInput_Click: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async void ChkSlideUsb_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkSlideUsb.IsChecked == true;
                UpdateSlideLabel(StatusSlideUsb, targetActive, "Aplicando...", "Revertendo...");

                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableUsbPowerSaving();
                    else SystemTweaks.RevertUsbPowerSaving();
                });

                UpdateSlideLabel(StatusSlideUsb, targetActive, "Desativado", "Padrão");

                RecordTweak("SlideUsb", targetActive);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChkSlideUsb_Click: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async void ChkSlideGaming_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkSlideGaming.IsChecked == true;
                UpdateSlideLabel(StatusSlideGaming, targetActive, "Aplicando...", "Revertendo...");

                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeGamingLatency();
                    else SystemTweaks.RevertGamingLatency();
                });

                UpdateSlideLabel(StatusSlideGaming, targetActive, "Extremo (DWM/GameDVR OFF)", "Padrão");

                RecordTweak("SlideGaming", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("REINÍCIO NECESSÁRIO", "As alterações estruturais do Thread e GameDVR exigem reiniciar o computador.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChkSlideGaming_Click: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async void ChkPciePower_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkPciePower.IsChecked == true;
                UpdateSlideLabel(StatusPciePower, targetActive, "Aplicando...", "Revertendo...");

                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisablePcieLinkStatePowerManagement();
                    else SystemTweaks.EnablePcieLinkStatePowerManagement();
                });

                UpdateSlideLabel(StatusPciePower, targetActive, "Desativado (Off)", "Padrão");

                RecordTweak("PciePower", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("PCIe POWER", "Link State Power Management alterado. Ganho de FPS varia entre sistemas.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChkPciePower_Click: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async void ChkTimeout_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkTimeout.IsChecked == true;
                UpdateSlideLabel(StatusTimeout, targetActive, "Aplicando...", "Revertendo...");

                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableHardDiskDisplayTimeout();
                    else SystemTweaks.EnableHardDiskDisplayTimeout();
                });

                UpdateSlideLabel(StatusTimeout, targetActive, "Desativado (0)", "Padrão");

                RecordTweak("Timeout", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("TIMEOUT", "Timeout de disco e tela alterado. Não desligarão durante uso.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChkTimeout_Click: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void ChkGameMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            if (ChkGameMode.IsChecked == true)
            {
                SystemTweaks.ApplyGamingOptimizations();
                UpdateLabel(StatusGame, true, "Prioridade Alta", "Padrão");

                RecordTweak("GameMode", true);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("MODO JOGO", "Prioridade de jogo definida para Alta.");
            }
            else
            {
                SystemTweaks.RevertGamingOptimizations();
                UpdateLabel(StatusGame, false, "Prioridade Alta", "Padrão");

                RecordTweak("GameMode", false);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("MODO JOGO", "Prioridade de jogo restaurada para padrão.");
            }
        }

        private void ChkMPO_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var result = SystemTweaks.ToggleMpo();

            bool nowActive = ChkMPO.IsChecked == true;
            UpdateLabel(StatusMPO, nowActive, "Corrigido (OFF)", "Padrão (ON)");

                RecordTweak("MPO", nowActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", $"{result.Message}\nO Windows precisa ser reiniciado para aplicar.");
        }

        private void ChkVBS_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var result = SystemTweaks.ToggleVbs();

            bool isOptimizationActive = ChkVBS.IsChecked == true;
            if (isOptimizationActive)
            {
                StatusVBS.Text = "⚡ Max FPS (Ao Reiniciar)";
                StatusVBS.Foreground = _colorActive;
            }
            else
            {
                StatusVBS.Text = "Padrão (Seguro)";
                StatusVBS.Foreground = _colorDefault;


            RecordTweak("VBS", isOptimizationActive);
            }

            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", result.Message + "\nO Windows requer REINICIALIZAÇÃO para mudar este recurso de segurança.");
        }

        private void ChkBing_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (ChkBing.IsChecked == true)
            {
                SystemTweaks.ApplyBingTweak();
                UpdateLabel(StatusBing, true, "Limpo", "Padrão");
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("PESQUISA OTIMIZADA", "Sugestões do Bing na busca foram desativadas.");
            }
            else
            {
                SystemTweaks.RevertRegistryValue(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");
                UpdateLabel(StatusBing, false, "Padrão", "Limpo");
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("PESQUISA RESTAURADA", "Sugestões do Bing na busca foram reativadas.");
            }
        }

        private void ChkMemoryUsage_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var result = SystemTweaks.ToggleMemoryUsage();

            bool nowActive = ChkMemoryUsage.IsChecked == true;
            UpdateLabel(StatusMemoryUsage, nowActive, "Otimizado", "Padrão");

                RecordTweak("MemoryUsage", nowActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", $"{result.Message}\nO Windows precisa ser reiniciado para aplicar.");
        }

        private void ChkTimer_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var result = SystemTweaks.ToggleTimerResolution();

            bool nowActive = ChkTimer.IsChecked == true;
            UpdateLabel(StatusTimer, nowActive, "Latência Mínima", "Padrão");

                RecordTweak("TimerResolution", nowActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", $"{result.Message}\nO Windows precisa ser reiniciado para aplicar as mudanças de Timer.");
        }

        private void ChkShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            SystemTweaks.ToggleFastShutdown();

            bool nowActive = ChkShutdown.IsChecked == true;
            UpdateLabel(StatusShutdown, nowActive, "⚡ Turbo Boot", "Padrão");

                RecordTweak("FastShutdown", nowActive);
            // Update Tray if exists
            var tray = (Application.Current.MainWindow as MainWindow)?.TrayService;
            if (tray != null) { tray.TurboShutdownEnabled = nowActive; tray.SaveSettings(); }
        }

        private void ChkBackgroundApps_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkBackgroundApps.IsChecked == true;
            if (targetActive)
            {
                SystemTweaks.DisableBackgroundApps();
            }
            else
            {
                SystemTweaks.EnableBackgroundApps();
            }

            UpdateLabel(StatusBackgroundApps, targetActive, "Desativado", "Padrão");

            RecordTweak("BackgroundApps", targetActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess("APPS EM SEGUNDO PLANO", targetActive ? "Apps em segundo plano desabilitados via GPEDIT." : "Apps em segundo plano habilitados.");
        }

        private void ChkNDU_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkNDU.IsChecked == true;
            if (targetActive)
            {
                SystemTweaks.DisableNDU();
            }
            else
            {
                SystemTweaks.EnableNDU();
            }

            UpdateLabel(StatusNDU, targetActive, "Desativado", "Padrão");

            RecordTweak("NDU", targetActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess("SERVIÇO NDU", targetActive ? "Serviço NDU desabilitado (fix memory leak)." : "Serviço NDU habilitado.");
        }

        private void ChkServiceStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkServiceStartup.IsChecked == true;
            if (targetActive)
            {
                SystemTweaks.OptimizeServiceStartup();
            }
            else
            {
                SystemTweaks.RevertServiceStartup();
            }

            UpdateLabel(StatusServiceStartup, targetActive, "Otimizado", "Padrão");

            RecordTweak("ServiceStartup", targetActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess("STARTUP DE SERVIÇOS", targetActive ? "Serviços não essenciais definidos para Manual (reduz processos de inicialização)." : "Serviços revertidos para Automatic.");
        }

        private void ChkNoAutoReboot_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkNoAutoReboot.IsChecked == true;
            if (targetActive)
            {
                SystemTweaks.EnableNoAutoReboot();
            }
            else
            {
                SystemTweaks.DisableNoAutoReboot();
            }

            UpdateLabel(StatusNoAutoReboot, targetActive, "Ativado", "Padrão");

            RecordTweak("NoAutoReboot", targetActive);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess("REINÍCIO AUTOMÁTICO", targetActive ? "Reinício automático impedido quando usuário está logado." : "Reinício automático habilitado.");
        }

        private void ChkDiagnosticServices_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkDiagnosticServices.IsChecked == true;
            bool success;
            if (targetActive)
            {
                success = SystemTweaks.DisableDiagnosticServices();
            }
            else
            {
                success = SystemTweaks.EnableDiagnosticServices();
            }

            if (success)
            {
                UpdateLabel(StatusDiagnosticServices, targetActive, "Desativado", "Padrão");

                RecordTweak("DiagnosticServices", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("SERVIÇOS DE DIAGNÓSTICO", targetActive
                        ? "Diagnósticos desabilitados (DPS, WdiServiceHost, WdiSystemHost)."
                        : "Diagnósticos habilitados (DPS=Auto, WdiHost=Demand, WdiSysHost=Demand).");
            }
            else
            {
                ChkDiagnosticServices.IsChecked = !targetActive;
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowError("SERVIÇOS DE DIAGNÓSTICO",
                        "Falha ao alterar serviços de diagnóstico. Execute como administrador.");
            }
        }

        private void ChkPowerThrottling_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkPowerThrottling.IsChecked == true;
            SystemTweaks.DisablePowerThrottling();

            if (targetActive)
            {
                var result = SystemTweaks.DisablePowerThrottling();
                if (result.Success)
                {
                    UpdateLabel(StatusPowerThrottling, true, "Desativado", "Padrão");

                    RecordTweak("PowerThrottling", true);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("POWER THROTTLING", "Power Throttling desativado. CPU rodará em performance máxima.");
                }
                else
                {
                    ChkPowerThrottling.IsChecked = false;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("POWER THROTTLING", result.Message);
                }
            }
            else
            {
                var result = SystemTweaks.EnablePowerThrottling();
                if (result.Success)
                {
                    UpdateLabel(StatusPowerThrottling, false, "Desativado", "Padrão");

                    RecordTweak("PowerThrottling", false);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowInfo("POWER THROTTLING", "Power Throttling restaurado para padrão Windows.");
                }
                else
                {
                    ChkPowerThrottling.IsChecked = true;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("POWER THROTTLING", result.Message);
                }
            }
        }

        private void ChkGdiScaling_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;

            bool targetActive = ChkGdiScaling.IsChecked == true;
            if (targetActive)
            {
                var result = SystemTweaks.DisableGdiScaling();
                if (result.Success)
                {
                    UpdateLabel(StatusGdiScaling, true, "Desativado", "Padrão");

                    RecordTweak("GdiScaling", true);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("GDI SCALING", "GDI Scaling desativado. Aplicativos legados sem scaling automático.");
                }
                else
                {
                    ChkGdiScaling.IsChecked = false;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("GDI SCALING", result.Message);
                }
            }
            else
            {
                var result = SystemTweaks.EnableGdiScaling();
                if (result.Success)
                {
                    UpdateLabel(StatusGdiScaling, false, "Desativado", "Padrão");

                    RecordTweak("GdiScaling", false);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowInfo("GDI SCALING", "GDI Scaling restaurado para o padrão Windows.");
                }
                else
                {
                    ChkGdiScaling.IsChecked = true;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("GDI SCALING", result.Message);
                }
            }
        }

        // --- SMARTCREEN — SCANNING MULTI-SOURCE ---

        private static int ReadRegDword(string keyPath, string valueName, int defaultValue = 0)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) return defaultValue;
                var val = key.GetValue(valueName);
                return val is int i ? i : defaultValue;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return defaultValue; }
        }

        private static int ReadRegDwordHive(RegistryHive hive, string subKey, string valueName, int defaultValue = 0)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(subKey);
                if (key == null) return defaultValue;
                var val = key.GetValue(valueName);
                return val is int i ? i : defaultValue;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return defaultValue; }
        }

        private static string? ReadRegString(string keyPath, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) return null;
                var val = key.GetValue(valueName);
                return val?.ToString();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        private static string? ReadRegStringHive(RegistryHive hive, string subKey, string valueName)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(subKey);
                if (key == null) return null;
                var val = key.GetValue(valueName);
                return val?.ToString();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        internal static bool IsSmartScreenSystemDisabled()
        {
            int votes = 0, total = 0;

            // 1. HKLM System EnableSmartScreen (DWORD) — controle principal
            total++;
            if (ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 1) == 0)
                votes++;

            // 2. HKLM Explorer SmartScreenEnabled (String) — Win11 + Explorer
            total++;
            if (ReadRegString(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "SmartScreenEnabled") == "Off")
                votes++;

            // 3. HKCU AppHost EnableWebContentEvaluation (DWORD) — Store Apps
            total++;
            if (ReadRegDwordHive(RegistryHive.CurrentUser,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost",
                    "EnableWebContentEvaluation", 1) == 0)
                votes++;

            // 4. HKLM Edge PhishingFilter EnabledV9 (DWORD) — Microsoft Edge
            total++;
            if (ReadRegDword(@"SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter", "EnabledV9", 1) == 0)
                votes++;

            // 5. HKCU Attachments SaveZoneInformation (DWORD) — >= 2 evita bloqueio por zona
            total++;
            int zoneInfo = ReadRegDwordHive(RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments",
                "SaveZoneInformation", 2);
            if (zoneInfo >= 2)
                votes++;

            // 6. HKLM Defender SpynetReporting (DWORD) — 0 = desliga nuvem (reduz SmartScreen)
            total++;
            if (ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SpynetReporting", 1) == 0)
                votes++;

            return votes > total / 2;
        }

        internal static bool IsSmartScreenExplorerDisabled()
        {
            int votes = 0, total = 0;

            // 1. HKLM Explorer SmartScreenEnabled (String) — controle direto
            total++;
            if (ReadRegString(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "SmartScreenEnabled") == "Off")
                votes++;

            // 2. HKLM System EnableSmartScreen (DWORD) — se sistema desligado, Explorer também
            total++;
            if (ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 1) == 0)
                votes++;

            // 3. HKCU Attachments SaveZoneInformation (DWORD) — >= 2 = não salva zona = sem bloqueio
            total++;
            int zoneInfo = ReadRegDwordHive(RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments",
                "SaveZoneInformation", 2);
            if (zoneInfo >= 2)
                votes++;

            // 4. HKLM Attachments ScanWithAntiVirus (DWORD) — 3 = desliga verificação
            total++;
            if (ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Attachments",
                    "ScanWithAntiVirus", 1) == 3)
                votes++;

            return votes > total / 2;
        }

        private async void ChkSmartScreenSystem_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkSmartScreenSystem.IsChecked == true;
            ChkSmartScreenSystem.IsEnabled = false;
            await Task.Run(() =>
            {
                if (disable)
                {
                    // Desabilitar em todas as camadas
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableSmartScreen", 0, RegistryValueKind.DWord);
                    using (var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                        expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
                    using (var appKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost"))
                        appKey?.SetValue("EnableWebContentEvaluation", 0, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter",
                        "EnabledV9", 0, RegistryValueKind.DWord);
                    using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                        attKey?.SetValue("SaveZoneInformation", 2, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Spynet",
                        "SpynetReporting", 0, RegistryValueKind.DWord);
                }
                else
                {
                    // Reativar (remover restrições ou restaurar padrão)
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                        "EnableSmartScreen", 1, RegistryValueKind.DWord);
                    using (var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                        try { expKey?.DeleteValue("SmartScreenEnabled", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    using (var appKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost"))
                        appKey?.SetValue("EnableWebContentEvaluation", 1, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter",
                        "EnabledV9", 1, RegistryValueKind.DWord);
                    using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                        try { attKey?.DeleteValue("SaveZoneInformation", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    using (var defKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet"))
                        try { defKey?.DeleteValue("SpynetReporting", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            });
            ChkSmartScreenSystem.IsEnabled = true;
            UpdateLabel(StatusSmartScreenSystem, disable, "Desativado", "Ativo");

            RecordTweak("SmartScreenSystem", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("SmartScreen", disable
                    ? "Filtro SmartScreen desativado em todas as camadas. Recomenda-se manter um antivírus ativo."
                    : "SmartScreen reativado em todas as camadas.");
        }

        private async void ChkSmartScreenExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkSmartScreenExplorer.IsChecked == true;
            ChkSmartScreenExplorer.IsEnabled = false;
            await Task.Run(() =>
            {
                if (disable)
                {
                    using var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer");
                    expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
                    using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                    {
                        attKey?.SetValue("SaveZoneInformation", 2, RegistryValueKind.DWord);
                        attKey?.SetValue("ScanWithAntiVirus", 3, RegistryValueKind.DWord);
                    }
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Attachments",
                        "ScanWithAntiVirus", 3, RegistryValueKind.DWord);
                }
                else
                {
                    using var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer");
                    expKey?.DeleteValue("SmartScreenEnabled", false);
                    using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                    {
                        try { attKey?.DeleteValue("SaveZoneInformation", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        try { attKey?.DeleteValue("ScanWithAntiVirus", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                    using (var attKey2 = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                        try { attKey2?.DeleteValue("ScanWithAntiVirus", false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            });
            ChkSmartScreenExplorer.IsEnabled = true;
            UpdateLabel(StatusSmartScreenExplorer, disable, "Desativado", "Ativo");

            RecordTweak("SmartScreenExplorer", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("SmartScreen Explorer", disable
                    ? "Bloqueio de arquivos do Explorer desativado em múltiplas camadas. Você poderá abrir qualquer arquivo sem restrições."
                    : "Proteção do Explorer reativada.");
        }

        // --- TELEMETRIA E RELATÓRIOS ---

        private void SaveTelemetryPreference(string property, bool value)
        {
            try
            {
                var tray = (Application.Current.MainWindow as MainWindow)?.TrayService;
                if (tray == null) return;
                switch (property)
                {
                    case "DiagTrack": tray.DiagTrackSvcDisabled = value; break;
                    case "Dmwappush": tray.DmwappushSvcDisabled = value; break;
                    case "WerSvc": tray.WerSvcDisabled = value; break;
                    case "PcaSvc": tray.PcaSvcDisabled = value; break;
                    case "TelemetryTasks": tray.TelemetryTasksDisabled = value; break;
                }
                tray.SaveSettings();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private async void ChkDiagTrackSvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkDiagTrackSvc.IsChecked == true;
            ChkDiagTrackSvc.IsEnabled = false;
            await Task.Run(() => SystemTweaks.SetServiceStartup("DiagTrack", disable));
            ChkDiagTrackSvc.IsEnabled = true;
            UpdateLabel(StatusDiagTrackSvc, disable, "Desativado", "Ativo");

            RecordTweak("DiagTrack", disable);
            SaveTelemetryPreference("DiagTrack", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("Telemetria (DiagTrack)", disable
                    ? "Serviço DiagTrack desativado. A preferência será reaplicada a cada inicialização."
                    : "Serviço DiagTrack restaurado.");
        }

        private async void ChkDmwappushSvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkDmwappushSvc.IsChecked == true;
            ChkDmwappushSvc.IsEnabled = false;
            await Task.Run(() => SystemTweaks.SetServiceStartup("dmwappushservice", disable));
            ChkDmwappushSvc.IsEnabled = true;
            UpdateLabel(StatusDmwappushSvc, disable, "Desativado", "Ativo");

            RecordTweak("Dmwappush", disable);
            SaveTelemetryPreference("Dmwappush", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("WAP Push (dmwappushservice)", disable
                    ? "Serviço WAP Push desativado. A preferência será reaplicada a cada inicialização."
                    : "Serviço WAP Push restaurado.");
        }

        private async void ChkWerSvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkWerSvc.IsChecked == true;
            ChkWerSvc.IsEnabled = false;
            await Task.Run(() => SystemTweaks.SetServiceStartup("WerSvc", disable));
            ChkWerSvc.IsEnabled = true;
            UpdateLabel(StatusWerSvc, disable, "Desativado", "Ativo");

            RecordTweak("WerSvc", disable);
            SaveTelemetryPreference("WerSvc", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("Relatório de Erros (WerSvc)", disable
                    ? "Serviço WerSvc desativado. A preferência será reaplicada a cada inicialização."
                    : "Serviço WerSvc restaurado.");
        }

        private async void ChkPcaSvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkPcaSvc.IsChecked == true;
            ChkPcaSvc.IsEnabled = false;
            await Task.Run(() => SystemTweaks.SetServiceStartup("PcaSvc", disable));
            ChkPcaSvc.IsEnabled = true;
            UpdateLabel(StatusPcaSvc, disable, "Desativado", "Ativo");

            RecordTweak("PcaSvc", disable);
            SaveTelemetryPreference("PcaSvc", disable);
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("Compatibilidade (PcaSvc)", disable
                    ? "Serviço PcaSvc desativado. A preferência será reaplicada a cada inicialização."
                    : "Serviço PcaSvc restaurado.");
        }

        private async void ChkTelemetryTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkTelemetryTasks.IsChecked == true;
            ChkTelemetryTasks.IsEnabled = false;
            await Task.Run(() => SystemTweaks.ApplyTelemetryScheduledTasks(disable));
            ChkTelemetryTasks.IsEnabled = true;
            bool nowOff = SystemTweaks.AreTelemetryTasksDisabled();
            UpdateLabel(StatusTelemetryTasks, nowOff, "Desativadas", "Ativas");

            RecordTweak("TelemetryTasks", nowOff);
            SaveTelemetryPreference("TelemetryTasks", nowOff);
            if (nowOff != disable)
            {
                ChkTelemetryTasks.IsChecked = nowOff;
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("Tarefas de Telemetria", "Algumas tarefas nao puderam ser alteradas (provavelmente protegidas).");
            }
            else if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("Tarefas de Telemetria", disable
                    ? "Tarefas de telemetria desativadas (Application Experience + CEIP). A preferência será reaplicada a cada inicialização."
                    : "Tarefas de telemetria reativadas.");
        }

        // --- HARDWARE & REDE ---

        private void BuildVramRows()
        {
            VramContainer.Children.Clear();
            var gpus = SystemTweaks.GetAllGpuInfo();
            double totalRam = SystemUtils.GetTotalSystemRamGB();
            int recommended = SystemTweaks.GetRecommendedVramMb(totalRam);

            if (gpus.Count == 0)
            {
                VramContainer.Children.Add(new TextBlock
                {
                    Text = "Nenhuma GPU detectada.",
                    Foreground = _colorDefault,
                    Margin = new Thickness(20),
                    FontSize = 13
                });
                VramSeparator.Visibility = Visibility.Collapsed;
                return;
            }

            for (int i = 0; i < gpus.Count; i++)
            {
                var gpu = gpus[i];
                int vramMb = 0;
                if (!string.IsNullOrEmpty(gpu.RegPath))
                {
                    var val = Registry.GetValue(gpu.RegPath, "DedicatedSegmentSize", 0);
                    vramMb = val != null ? Convert.ToInt32(val) : 0;
                }
                bool isApplied = vramMb > 0;
                string displayName = gpu.Name.Length > 50 ? gpu.Name[..50] + "..." : gpu.Name;
                bool hasRegPath = !string.IsNullOrEmpty(gpu.RegPath);

                var grid = new Grid { Margin = new Thickness(20) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Info button
                var infoBtn = new System.Windows.Controls.Button
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Width = 30,
                    Height = 30,
                    Cursor = System.Windows.Input.Cursors.Help,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Content = new TextBlock
                    {
                        Text = "\u2139\uFE0F",
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromRgb(170, 136, 255)),
                        Opacity = 0.6
                    }
                };
                ToolTipService.SetInitialShowDelay(infoBtn, 0);
                ToolTipService.SetShowDuration(infoBtn, 20000);
                infoBtn.ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = hasRegPath
                        ? $"Aumenta a VRAM dedicada (DedicatedSegmentSize) no registro para esta GPU.{Environment.NewLine}Caminho: {gpu.RegPath}{Environment.NewLine}Recomendado: {recommended} MB baseado em {totalRam:F1} GB de RAM.{Environment.NewLine}Útil para GPUs integradas e jogos que reportam pouca VRAM."
                        : $"Caminho não encontrado — será criado ao ativar (força criação igual ao Dashboard).{Environment.NewLine}GPU: {gpu.Name}{Environment.NewLine}Recomendado: {recommended} MB."
                };
                Grid.SetColumn(infoBtn, 0);
                grid.Children.Add(infoBtn);

                // GPU name
                var stack = new StackPanel();
                var nameBlock = new TextBlock
                {
                    Text = displayName,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = System.Windows.Media.Brushes.White
                };
                stack.Children.Add(nameBlock);

                // VRAM info line - sempre visível, laranja vira aviso mas não bloqueia
                string statusPart = isApplied ? $"{vramMb} MB" : "Padr\u00E3o";
                string regPart = hasRegPath ? "" : " (será criado ao ativar)";
                var vramLine = new TextBlock
                {
                    Text = $"VRAM: {statusPart}  |  RAM: {totalRam:F1} GB  |  Recomendado: {recommended} MB{regPart}",
                    FontSize = 11,
                    Foreground = hasRegPath
                        ? (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#A0A0A0")
                        : new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(vramLine);
                Grid.SetColumn(stack, 1);
                grid.Children.Add(stack);

                // Status badge
                var border = new Border
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(8, 3, 8, 3),
                    CornerRadius = new CornerRadius(4),
                    Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#0A2838"),
                    BorderBrush = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#1A6B80"),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 15, 0)
                };
                var statusLabel = new TextBlock
                {
                    Text = isApplied ? "Aplicado" : "Padr\u00E3o",
                    Foreground = isApplied ? _colorActive : _colorDefault,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                };
                border.Child = statusLabel;
                Grid.SetColumn(border, 2);
                grid.Children.Add(border);

                // Toggle sempre habilitado - força criação do caminho se não existir (igual Dashboard)
                var toggle = new System.Windows.Controls.CheckBox
                {
                    Style = (System.Windows.Style)FindResource("ToggleSwitchStyle"),
                    IsChecked = isApplied,
                    Tag = hasRegPath ? gpu.RegPath! : $"CREATE:{gpu.Name}",
                    IsEnabled = true,
                    Margin = new Thickness(0, 20, 20, 20),
                    Opacity = 1.0
                };
                toggle.Click += VramToggle_Click;
                Grid.SetColumn(toggle, 3);
                grid.Children.Add(toggle);

                VramContainer.Children.Add(grid);

                if (i < gpus.Count - 1)
                {
                    VramContainer.Children.Add(new Separator
                    {
                        Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#1A5568"),
                        Opacity = 0.4,
                        Margin = new Thickness(20, 0, 20, 0)
                    });
                }
            }

            VramSeparator.Visibility = gpus.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void VramToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is System.Windows.Controls.CheckBox toggle && toggle.Tag is string regPath)
            {
                _isLoading = true;
                try
                {
                    bool targetActive = toggle.IsChecked == true;
                    string gpuName = "";

                    // Extrai o nome da GPU do StackPanel na mesma linha
                    if (toggle.Parent is Grid parentGrid && parentGrid.Children.Count > 1
                        && parentGrid.Children[1] is StackPanel sp && sp.Children.Count > 0
                        && sp.Children[0] is TextBlock nameTb)
                    {
                        gpuName = nameTb.Text.TrimEnd('.');
                    }

                    await Task.Run(() =>
                    {
                        string effectivePath = regPath;
                        // Força criação se caminho não existia (CREATE:GPU Name)
                        if (effectivePath.StartsWith("CREATE:", StringComparison.Ordinal))
                        {
                            string gpuDesc = effectivePath.Substring(7);
                            effectivePath = SystemTweaks.FindGpuRegistryPathByDescription(gpuDesc) ?? "";
                            if (string.IsNullOrEmpty(effectivePath))
                            {
                                // Cria subchave nova em 0000-0009 livre (força criação igual Dashboard)
                                string baseGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
                                string basePath = $@"SYSTEM\CurrentControlSet\Control\Class\{baseGuid}";
                                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(basePath);
                                string? freeSlot = null;
                                if (baseKey != null)
                                {
                                    for (int i = 0; i < 16; i++)
                                    {
                                        string slot = i.ToString("D4");
                                        if (baseKey.OpenSubKey(slot) == null) { freeSlot = slot; break; }
                                    }
                                }
                                freeSlot ??= "0000";
                                effectivePath = $@"HKEY_LOCAL_MACHINE\{basePath}\{freeSlot}";
                                try
                                {
                                    using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"{basePath}\{freeSlot}", true);
                                    k?.SetValue("DriverDesc", gpuDesc, Microsoft.Win32.RegistryValueKind.String);
                                }
                                catch { }
                            }
                            // Atualiza tag para próximos toggles
                            toggle.Dispatcher.Invoke(() => toggle.Tag = effectivePath);
                        }

                        if (targetActive)
                        {
                            if (!string.IsNullOrEmpty(effectivePath))
                            {
                                double totalRam = SystemUtils.GetTotalSystemRamGB();
                                int sizeToSet = SystemTweaks.GetRecommendedVramMb(totalRam);
                                SystemTweaks.ApplyGpuVramTweak(effectivePath, sizeToSet);
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(effectivePath) && !effectivePath.StartsWith("CREATE:"))
                                SystemTweaks.RevertRegistryValue(effectivePath, "DedicatedSegmentSize");
                        }
                    });

                    // Atualiza a UI inline sem recriar nada
                    if (toggle.Parent is Grid pg && pg.Children.Count > 2)
                    {
                        if (pg.Children[2] is Border b && b.Child is TextBlock st)
                        {
                            st.Text = targetActive ? "Aplicado" : "Padr\u00E3o";
                            st.Foreground = targetActive ? _colorActive : _colorDefault;
                        }
                        if (pg.Children[1] is StackPanel s && s.Children.Count > 1 && s.Children[1] is TextBlock vl)
                        {
                            double totalRam = SystemUtils.GetTotalSystemRamGB();
                            int recommended = SystemTweaks.GetRecommendedVramMb(totalRam);
                            string vramStr = targetActive ? $"{recommended} MB" : "Padr\u00E3o";
                            vl.Text = $"VRAM: {vramStr}  |  RAM: {totalRam:F1} GB  |  Recomendado: {recommended} MB";
                        }
                    }

                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowInfo("VRAM", targetActive
                            ? $"VRAM ajustada em {gpuName}"
                            : $"VRAM removida de {gpuName}");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                finally { _isLoading = false; }
            }
        }

        private async void ChkFrameQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkFrameQueue.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyLowLatencyFrameQueue();
                    else SystemTweaks.RevertFrameQueue();
                });
                UpdateLabel(StatusFrameQueue, targetActive, "Low Latency (0)", "Padrão (1)");

                RecordTweak("FrameQueue", targetActive);
                InfoFrameQueue.Text = targetActive ? "FrameQueueMode=0 (Low Latency)" : "FrameQueueMode=1 (Padrão Windows)";
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "FrameQueueMode: Low Latency" : "FrameQueueMode: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkPreemption_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkPreemption.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.EnableGpuPreemption();
                    else SystemTweaks.RevertGpuPreemption();
                });
                UpdateLabel(StatusPreemption, targetActive, "Ativado", "Padrão");

                RecordTweak("Preemption", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "Preempção de GPU ativada" : "Preempção de GPU: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkGpuIdle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkGpuIdle.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableGpuIdleSchedule();
                    else SystemTweaks.RevertGpuIdleSchedule();
                });
                UpdateLabel(StatusGpuIdle, targetActive, "Desativado", "Ativo");

                RecordTweak("GpuIdle", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "GPU Idle desativado" : "GPU Idle: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkPowerLatency_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkPowerLatency.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.SetGpuPowerLatency();
                    else SystemTweaks.RevertGpuPowerLatency();
                });
                UpdateLabel(StatusPowerLatency, targetActive, "Latência (1)", "Padrão (0)");

                RecordTweak("PowerLatency", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "GPU Power Latency: Priorizar Latência" : "GPU Power Latency: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private void ChkGpuSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            _selectedGpuIndex = ChkGpuSelector.SelectedIndex;
            var names = SystemTweaks.GetAllGpuNames();
            if (_selectedGpuIndex >= 0 && _selectedGpuIndex < names.Count)
                _selectedGpuRegPath = SystemTweaks.FindGpuRegistryPathByDescription(names[_selectedGpuIndex]);
            else
                _selectedGpuRegPath = null;
        }

        private async void ChkL2Cache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkL2Cache.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyAutoCacheTweak();
                    else SystemTweaks.RevertRegistryValue(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache");
                });
                UpdateLabel(StatusL2Cache, targetActive, "Aplicado", "Padrão");

                RecordTweak("L2Cache", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("CACHE CPU", targetActive ? "Cache L2/L3 configurado conforme sua CPU." : "Cache L2/L3 restaurado para padrão.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkRmCacheLoc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkRmCacheLoc.IsChecked == true;
                var result = await Task.Run(() => SystemTweaks.ToggleRmCacheLocTweak());
                if (!result.Success)
                {
                    ChkRmCacheLoc.IsChecked = !targetActive;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("RMCACHELOC", result.Message);
                }
                else
                {
                    UpdateLabel(StatusRmCacheLoc, targetActive, "Aplicado", "Padrão");

                RecordTweak("RmCacheLoc", targetActive);
                    InfoRmCacheLoc.Text = targetActive
                        ? $"Aplicado: {Environment.ProcessorCount} núcleos lógicos (NVIDIA)."
                        : "Configurado com o numero de nucleos logicos.";
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowInfo("RMCACHELOC", result.Message);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkNagle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkNagle.IsChecked == true;
                var result = await Task.Run(() => targetActive
                    ? SystemTweaks.DisableNagleAlgorithm()
                    : SystemTweaks.RevertNagleAlgorithm());
                if (!result.Success)
                {
                    ChkNagle.IsChecked = !targetActive;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("NAGLE", result.Message);
                }
                else
                {
                    UpdateLabel(StatusNagle, targetActive, "Aplicado", "Padrão");

                    RecordTweak("Nagle", targetActive);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowInfo("NAGLE", result.Message);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkCoreParking_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkCoreParking.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableCoreParking();
                    else
                    {
                        string[] keys = {
                            @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583",
                            @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\ea4be0c1-7c65-46f8-8c17-f298766665d9"
                        };
                        foreach (var k in keys)
                        {
                            SystemTweaks.RevertRegistryValue(k, "ValueMax");
                            SystemTweaks.RevertRegistryValue(k, "ValueMin");
                        }
                    }
                });
                UpdateLabel(StatusCoreParking, targetActive, "Aplicado", "Padrão");

                RecordTweak("CoreParking", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("CORE PARKING", targetActive ? "Core Parking desativado. Todos os núcleos permanecem ativos." : "Core Parking restaurado para padrão.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkHags_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkHags.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.EnableHags();
                    else SystemTweaks.RevertHags();
                });
                UpdateLabel(StatusHags, targetActive, "Ativado (2)", "Padrão");

                RecordTweak("Hags", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "HAGS ativado (HwSchMode=2)" : "HAGS: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkTdr_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkTdr.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.IncreaseTdrDelay();
                    else SystemTweaks.RevertTdrDelay();
                });
                UpdateLabel(StatusTdr, targetActive, "10s", "Padrão (2s)");

                RecordTweak("Tdr", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPU", targetActive ? "TDR Delay aumentado para 10s" : "TDR Delay: Padrão (2s)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkIoLock_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkIoLock.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.SetIoPageLockLimit();
                    else SystemTweaks.RevertIoPageLockLimit();
                });
                UpdateLabel(StatusIoLock, targetActive, "8192 KB", "Padrão");

                RecordTweak("IoLock", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("MEMÓRIA", targetActive ? "IoPageLockLimit=8192 KB" : "IoPageLockLimit: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkInputQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkInputQueue.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.IncreaseInputQueueSize();
                    else SystemTweaks.RevertInputQueueSize();
                });
                UpdateLabel(StatusInputQueue, targetActive, "200", "Padrão (100)");

                RecordTweak("InputQueue", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("INPUT", targetActive ? "Fila de input aumentada para 200" : "Fila de input: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // --- NVIDIA PowerMizer ---
        private async void ChkPowerMizer_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkPowerMizer.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyNvidiaPowerMizerMaxPerformance();
                    else SystemTweaks.RevertNvidiaPowerMizer();
                });
                bool newState = await Task.Run(() => SystemTweaks.IsNvidiaPowerMizerMaxPerformance());
                UpdateLabel(StatusPowerMizer, newState, "Max Perf", "Padrão");
                ChkPowerMizer.IsChecked = newState;
                RecordTweak("PowerMizer", newState);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("NVIDIA", newState ? "PowerMizer: Máximo desempenho (reinício necessário)" : "PowerMizer restaurado");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // --- NVMe Latency ---
        private async void ChkNvMe_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkNvMe.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyNvMeLatencyTweaks();
                    else SystemTweaks.RevertNvMeLatencyTweaks();
                });
                bool newState = await Task.Run(() => SystemTweaks.IsNvMeLatencyOptimized());
                UpdateLabel(StatusNvMe, newState, "Otimizado", "Padrão");
                ChkNvMe.IsChecked = newState;
                RecordTweak("NvMe", newState);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("NVME", newState ? "NVMe latency otimizado (D3Handoff=1)" : "NVMe restaurado");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // --- GPU DPC Latency ---
        private async void ChkDpcLatency_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkDpcLatency.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyGpuDpcLatencyTweaks();
                    else SystemTweaks.RevertGpuDpcLatencyTweaks();
                });
                bool newState = await Task.Run(() => SystemTweaks.IsGpuDpcLatencyLow());
                UpdateLabel(StatusDpcLatency, newState, "IRQ8=1", "Padrão");
                ChkDpcLatency.IsChecked = newState;
                RecordTweak("DpcLatency", newState);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("DPC LATENCY", newState ? "IRQ8 Priority=1 aplicado" : "DPC Latency restaurado");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // --- Memory Prioritization ---
        private async void ChkMemPriority_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkMemPriority.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyMemoryPrioritizationDse();
                    else SystemTweaks.RevertMemoryPrioritizationDse();
                });
                bool newState = await Task.Run(() => SystemTweaks.IsMemoryPrioritizationDse());
                UpdateLabel(StatusMemPriority, newState, "Em RAM", "Padrão");
                ChkMemPriority.IsChecked = newState;
                RecordTweak("MemPriority", newState);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("MEMORIA", newState ? "Kernel mantido em RAM (reinício necessário)" : "Memory Prioritization restaurado");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkStartupDelay_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkStartupDelay.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeStartupDelay();
                    else SystemTweaks.RevertStartupDelay();
                });
                UpdateLabel(StatusStartupDelay, targetActive, "Removido", "Padrão");

                RecordTweak("StartupDelay", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("STARTUP", targetActive ? "Startup delay removido (0ms)" : "Startup delay: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkFastStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkFastStartup.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableFastStartup();
                    else SystemTweaks.EnableFastStartup();
                });
                UpdateLabel(StatusFastStartup, targetActive, "Desativado", "Ativo");

                RecordTweak("FastStartup", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("STARTUP", targetActive ? "Fast Startup desativado (desligamento completo)" : "Fast Startup: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkBootMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkBootMenu.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.SetBootMenuTimeoutZero();
                    else SystemTweaks.RevertBootMenuTimeout();
                });
                UpdateLabel(StatusBootMenu, targetActive, "0s", "Padrão (30s)");

                RecordTweak("BootMenu", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("STARTUP", targetActive ? "Boot menu timeout: 0s" : "Boot menu timeout: 30s");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkNumLock_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkNumLock.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.EnableNumLockOnStartup();
                    else SystemTweaks.DisableNumLockOnStartup();
                });
                UpdateLabel(StatusNumLock, targetActive, "Ativado", "Desativado");

                RecordTweak("NumLock", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("STARTUP", targetActive ? "NumLock ativado na inicialização" : "NumLock desativado na inicialização");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkWaitToKillService_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkWaitToKillService.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeWaitToKillService();
                    else SystemTweaks.RevertWaitToKillService();
                });
                UpdateLabel(StatusWaitToKillService, targetActive, "2s", "Padrão (5s)");

                RecordTweak("WaitToKillService", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("SHUTDOWN", targetActive ? "WaitToKillService: 2s" : "WaitToKillService: Padrão (5s)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkQuickAppKill_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkQuickAppKill.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.ApplyQuickAppKill();
                    else SystemTweaks.RevertQuickAppKill();
                });
                UpdateLabel(StatusQuickAppKill, targetActive, "Ativado", "Padrão");

                RecordTweak("QuickAppKill", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("SHUTDOWN", targetActive ? "Quick App Kill: Ativado" : "Quick App Kill: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkClearPageFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkClearPageFile.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisableClearPageFile();
                    else SystemTweaks.EnableClearPageFile();
                });
                UpdateLabel(StatusClearPageFile, targetActive, "Desativado", "Padrão");

                RecordTweak("ClearPageFile", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("SHUTDOWN", targetActive ? "Limpeza de PageFile: Desativada" : "Limpeza de PageFile: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkVerboseStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkVerboseStatus.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.EnableVerboseStatus();
                    else SystemTweaks.DisableVerboseStatus();
                });
                UpdateLabel(StatusVerboseStatus, targetActive, "Detalhado", "Oculto");

                RecordTweak("VerboseStatus", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("SHUTDOWN", targetActive ? "VerboseStatus: Ativado (mensagens detalhadas)" : "VerboseStatus: Oculto");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkMenuDelay_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkMenuDelay.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeMenuShowDelay();
                    else SystemTweaks.RevertMenuShowDelay();
                });
                UpdateLabel(StatusMenuDelay, targetActive, "0ms", "Padrão (400ms)");

                RecordTweak("MenuDelay", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("INTERFACE", targetActive ? "MenuShowDelay: 0ms (instantâneo)" : "MenuShowDelay: Padrão (400ms)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkVisualFX_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkVisualFX.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.OptimizeVisualFX();
                    else SystemTweaks.RevertVisualFX();
                });
                UpdateLabel(StatusVisualFX, targetActive, "Máximo", "Padrão");

                RecordTweak("VisualFX", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("INTERFACE", targetActive ? "Animações visuais: Desativadas" : "Animações visuais: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkExplorerLaunchTo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                int value = ChkExplorerLaunchTo.SelectedIndex;
                await Task.Run(() => SystemTweaks.SetExplorerLaunchTo(value));
                var label = value == 0 ? "Acesso Rápido" : value == 1 ? "Este Computador" : "Downloads";
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("EXPLORER", $"Explorer abre em: {label}");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkIconCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkIconCache.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.IncreaseIconCache();
                    else SystemTweaks.ResetIconCache();
                });
                UpdateLabel(StatusIconCache, targetActive, "8192 KB", "Padrão");

                RecordTweak("IconCache", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("EXPLORER", targetActive ? "Cache de ícones: 8192 KB" : "Cache de ícones: Padrão");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkPCA_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool targetActive = ChkPCA.IsChecked == true;
                await Task.Run(() =>
                {
                    if (targetActive) SystemTweaks.DisablePCA();
                    else SystemTweaks.EnablePCA();
                });
                UpdateLabel(StatusPCA, targetActive, "Desativado", "Ativo");

                RecordTweak("PCA", targetActive);
                RecordTweak("PCA", targetActive);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("SISTEMA", targetActive ? "PCA: Desativado" : "PCA: Ativo (padrão)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // ── Manter Tweaks Apos Reboot ──────────────────────────────────

        private void ChkMaintainTweaks_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = ChkMaintainTweaks.IsChecked == true;
            TweakRegistry.SetMaintained(enabled);
            if (enabled)
            {
                TweakRegistry.SaveAllTweakStates();
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("TWEAKS", "Estados dos tweaks salvos. Serão verificados e reaplicados a cada inicialização.");
            }
            else
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("TWEAKS", "Manutenção automática desativada. Tweaks não serão reaplicados no boot.");
            }
        }

        /// <summary>
        /// Records the current state of a tweak for persistence.
        /// Call at the end of each tweak Click handler when maintenance is enabled.
        /// </summary>
        private void RecordTweak(string name, bool active)
        {
            if (TweakRegistry.IsMaintained())
                TweakRegistry.SaveTweakState(name, active);
        }
    }
}
