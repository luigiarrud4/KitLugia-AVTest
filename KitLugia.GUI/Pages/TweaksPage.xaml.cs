using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using KitLugia.Core;
using Microsoft.Win32;

// Resolve ambiguidade da Cor
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class TweaksPage : Page
    {
        private bool _isLoading = true;
        private readonly SolidColorBrush _colorActive = new SolidColorBrush(Color.FromRgb(108, 203, 95));
        private readonly SolidColorBrush _colorDefault = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private readonly SolidColorBrush _colorSlideActive = new SolidColorBrush(Color.FromRgb(255, 170, 0)); // Amarelo Escuro para SLIDE

        public TweaksPage()
        {
            InitializeComponent();
            _ = LoadCurrentStatus();

            this.Unloaded += TweaksPage_Unloaded;
        }


        public void Cleanup()
        {
            this.Unloaded -= TweaksPage_Unloaded;


            this.DataContext = null;



        }

        private void TweaksPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task LoadCurrentStatus()
        {
            await Task.Run(() =>
            {
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

                Dispatcher.Invoke(() =>
                {
                    _isLoading = true; // Pausa eventos durante carga

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

                    // SmartScreen — varredura multi-camada
                    bool smartScreenSystemDisabled = IsSmartScreenSystemDisabled();
                    UpdateLabel(StatusSmartScreenSystem, smartScreenSystemDisabled, "Desativado", "Ativo");
                    ChkSmartScreenSystem.IsChecked = smartScreenSystemDisabled;

                    bool smartScreenExplorerDisabled = IsSmartScreenExplorerDisabled();
                    UpdateLabel(StatusSmartScreenExplorer, smartScreenExplorerDisabled, "Desativado", "Ativo");
                    ChkSmartScreenExplorer.IsChecked = smartScreenExplorerDisabled;

                    ChkBackgroundApps.IsChecked = SystemTweaks.IsBackgroundAppsDisabled();
                    UpdateLabel(StatusBackgroundApps, ChkBackgroundApps.IsChecked == true, "Desativado", "Padrão");

                    ChkNDU.IsChecked = SystemTweaks.IsNDUDisabled();
                    UpdateLabel(StatusNDU, ChkNDU.IsChecked == true, "Desativado", "Padrão");

                    ChkServiceStartup.IsChecked = SystemTweaks.IsServiceStartupOptimized();
                    UpdateLabel(StatusServiceStartup, ChkServiceStartup.IsChecked == true, "Otimizado", "Padrão");

                    ChkNoAutoReboot.IsChecked = SystemTweaks.IsNoAutoRebootEnabled();
                    UpdateLabel(StatusNoAutoReboot, ChkNoAutoReboot.IsChecked == true, "Ativado", "Padrão");

                    ChkDiagnosticServices.IsChecked = SystemTweaks.IsDiagnosticServicesDisabled();
                    UpdateLabel(StatusDiagnosticServices, ChkDiagnosticServices.IsChecked == true, "Desativado", "Padrão");

                    ChkPowerThrottling.IsChecked = SystemTweaks.IsPowerThrottlingDisabled();
                    UpdateLabel(StatusPowerThrottling, ChkPowerThrottling.IsChecked == true, "Desativado", "Padrão");

                    ChkGdiScaling.IsChecked = SystemTweaks.IsGdiScalingDisabled();
                    UpdateLabel(StatusGdiScaling, ChkGdiScaling.IsChecked == true, "Desativado", "Padrão");

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
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("MODO JOGO", "Prioridade de jogo definida para Alta.");
            }
            else
            {
                SystemTweaks.RevertGamingOptimizations();
                UpdateLabel(StatusGame, false, "Prioridade Alta", "Padrão");
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

            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", $"{result.Message}\nO Windows precisa ser reiniciado para aplicar.");
        }

        private void ChkTimer_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var result = SystemTweaks.ToggleTimerResolution();

            bool nowActive = ChkTimer.IsChecked == true;
            UpdateLabel(StatusTimer, nowActive, "Latência Mínima", "Padrão");

            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("REINÍCIO NECESSÁRIO", $"{result.Message}\nO Windows precisa ser reiniciado para aplicar as mudanças de Timer.");
        }

        private void ChkShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            SystemTweaks.ToggleFastShutdown();

            bool nowActive = ChkShutdown.IsChecked == true;
            UpdateLabel(StatusShutdown, nowActive, "⚡ Turbo Boot", "Padrão");

            // Update Tray if exists
            var tray = (Application.Current.MainWindow as MainWindow)?.TrayService;
            if (tray != null) tray.TurboShutdownEnabled = nowActive;
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
            catch { return defaultValue; }
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
            catch { return defaultValue; }
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
            catch { return null; }
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
            catch { return null; }
        }

        private static bool IsSmartScreenSystemDisabled()
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

        private static bool IsSmartScreenExplorerDisabled()
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
                        try { expKey?.DeleteValue("SmartScreenEnabled", false); } catch { }
                    using (var appKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost"))
                        appKey?.SetValue("EnableWebContentEvaluation", 1, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter",
                        "EnabledV9", 1, RegistryValueKind.DWord);
                    using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                        try { attKey?.DeleteValue("SaveZoneInformation", false); } catch { }
                    using (var defKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet"))
                        try { defKey?.DeleteValue("SpynetReporting", false); } catch { }
                }
            });
            ChkSmartScreenSystem.IsEnabled = true;
            UpdateLabel(StatusSmartScreenSystem, disable, "Desativado", "Ativo");
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
                        try { attKey?.DeleteValue("SaveZoneInformation", false); } catch { }
                        try { attKey?.DeleteValue("ScanWithAntiVirus", false); } catch { }
                    }
                    using (var attKey2 = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                        try { attKey2?.DeleteValue("ScanWithAntiVirus", false); } catch { }
                }
            });
            ChkSmartScreenExplorer.IsEnabled = true;
            UpdateLabel(StatusSmartScreenExplorer, disable, "Desativado", "Ativo");
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("SmartScreen Explorer", disable
                    ? "Bloqueio de arquivos do Explorer desativado em múltiplas camadas. Você poderá abrir qualquer arquivo sem restrições."
                    : "Proteção do Explorer reativada.");
        }
    }
}
