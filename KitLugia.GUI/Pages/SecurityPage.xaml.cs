using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using KitLugia.GUI.Services;
using Microsoft.Win32;

using CheckBox = System.Windows.Controls.CheckBox;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    public partial class SecurityPage : Page
    {
        private bool _isLoading = true;

        public SecurityPage()
        {
            InitializeComponent();
            this.Unloaded += SecurityPage_Unloaded;
            this.Loaded += SecurityPage_Loaded;
        }

        public void Cleanup()
        {
            this.Unloaded -= SecurityPage_Unloaded;
            this.Loaded -= SecurityPage_Loaded;
            this.DataContext = null;
        }

        private void SecurityPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        private async void SecurityPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadStateAsync();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadStateAsync();
        }

        private async Task LoadStateAsync()
        {
            _isLoading = true;
            TxtSecurityScore.Text = "Calculando...";

            await Task.Run(() =>
            {
                bool defenderDisabled = SystemTweaks.IsMSDefenderDisabled();
                bool vbsEnabled = SystemTweaks.IsVbsEnabled();
                bool telemetryDisabled = IsTelemetryDisabled();
                bool activityHistoryDisabled = IsActivityHistoryDisabled();
                bool adIdDisabled = IsAdIdDisabled();
                bool locationDisabled = IsLocationDisabled();
                bool remoteRegistryDisabled = IsRemoteRegistryDisabled();
                bool rdpDisabled = IsRdpDisabled();
                bool firewallEnabled = IsFirewallEnabled();
                bool realtimeDisabled = IsRealtimeProtectionDisabled();
                bool smartScreenEnabled = IsSmartScreenEnabled();
                int uacLevel = GetUACLevel();

                Dispatcher.Invoke(() =>
                {
                    // Defender
                    ChkDefender.IsChecked = !defenderDisabled;
                    TxtDefenderStatus.Text = defenderDisabled ? "⚠️ Desativado — sistema vulnerável" : "✅ Ativo e protegendo";
                    TxtDefenderStatus.Foreground = defenderDisabled
                        ? new SolidColorBrush(Color.FromRgb(255, 80, 80))
                        : new SolidColorBrush(Color.FromRgb(76, 175, 80));

                    // Realtime
                    ChkRealtimeProtection.IsChecked = !realtimeDisabled;

                    // VBS — checkbox "ativo" = VBS habilitado
                    ChkVBS.IsChecked = vbsEnabled;

                    // SmartScreen
                    ChkSmartScreen.IsChecked = smartScreenEnabled;

                    // Firewall
                    ChkFirewall.IsChecked = firewallEnabled;
                    TxtFirewallStatus.Text = firewallEnabled ? "✅ Ativo" : "⚠️ Desativado";
                    TxtFirewallStatus.Foreground = firewallEnabled
                        ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
                        : new SolidColorBrush(Color.FromRgb(255, 80, 80));

                    // Remote Registry — checkbox "ativo" = serviço desativado (mais seguro)
                    ChkRemoteRegistry.IsChecked = remoteRegistryDisabled;

                    // RDP — checkbox "ativo" = RDP desativado (mais seguro)
                    ChkRDP.IsChecked = rdpDisabled;

                    // Privacidade
                    ChkTelemetry.IsChecked = telemetryDisabled;
                    ChkActivityHistory.IsChecked = activityHistoryDisabled;
                    ChkAdID.IsChecked = adIdDisabled;
                    ChkLocation.IsChecked = locationDisabled;

                    // UAC
                    TxtUACLevel.Text = $"Nível atual: {GetUACLevelName(uacLevel)}";
                    SetUACComboSelection(uacLevel);

                    // Score
                    int score = CalculateSecurityScore(
                        !defenderDisabled, !realtimeDisabled, vbsEnabled, smartScreenEnabled,
                        firewallEnabled, remoteRegistryDisabled, rdpDisabled,
                        telemetryDisabled, adIdDisabled, uacLevel >= 1);
                    TxtSecurityScore.Text = $"{score}/100 — {GetScoreLabel(score)}";
                    TxtSecurityScore.Foreground = score >= 80
                        ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
                        : score >= 50
                            ? new SolidColorBrush(Color.FromRgb(255, 165, 0))
                            : new SolidColorBrush(Color.FromRgb(255, 80, 80));

                    _isLoading = false;
                });
            });
        }

        // ─── Toggle Handlers ────────────────────────────────────────────────────

        private async void ChkDefender_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkDefender.IsChecked == true;
            ChkDefender.IsEnabled = false;

            await Task.Run(() =>
            {
                if (enable) SystemTweaks.EnableMSDefender();
                else SystemTweaks.DisableMSDefender();
            });

            ChkDefender.IsEnabled = true;
            ShowNotification(enable ? "✅ Defender Ativado" : "⚠️ Defender Desativado",
                enable ? "Windows Defender foi reativado." : "Windows Defender foi desativado. Seu PC está vulnerável.");
            _ = LoadStateAsync();
        }

        private async void ChkRealtimeProtection_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkRealtimeProtection.IsChecked == true;
            ChkRealtimeProtection.IsEnabled = false;

            await Task.Run(() =>
            {
                string keyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection";
                Registry.SetValue(keyPath, "DisableRealtimeMonitoring", enable ? 0 : 1, RegistryValueKind.DWord);
            });

            ChkRealtimeProtection.IsEnabled = true;
            ShowNotification(enable ? "✅ Proteção em Tempo Real Ativada" : "⚠️ Proteção em Tempo Real Desativada", "");
        }

        private async void ChkVBS_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkVBS.IsChecked == true;
            ChkVBS.IsEnabled = false;

            await Task.Run(() =>
            {
                var result = SystemTweaks.ToggleVbs();
                Logger.Log($"VBS toggle: {result.Message}");
            });

            ChkVBS.IsEnabled = true;
            ShowNotification("🔄 VBS Alterado", "Reinicie o computador para aplicar a mudança.");
        }

        private async void ChkSmartScreen_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkSmartScreen.IsChecked == true;
            ChkSmartScreen.IsEnabled = false;

            await Task.Run(() =>
            {
                // SmartScreen para apps e arquivos
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                    "EnableSmartScreen", enable ? 1 : 0, RegistryValueKind.DWord);
                // SmartScreen do Explorer (Win11)
                if (enable)
                {
                    using var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer");
                    if (expKey != null)
                        try { expKey.DeleteValue("SmartScreenEnabled"); } catch { }
                }
                else
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                        "SmartScreenEnabled", "Off", RegistryValueKind.String);
                }
                // SmartScreen para Edge
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter",
                    "EnabledV9", enable ? 1 : 0, RegistryValueKind.DWord);
            });

            ChkSmartScreen.IsEnabled = true;
            ShowNotification(enable ? "✅ SmartScreen Ativado" : "⚠️ SmartScreen Desativado", "");
        }

        private async void ChkFirewall_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkFirewall.IsChecked == true;
            ChkFirewall.IsEnabled = false;

            await Task.Run(() =>
            {
                string action = enable ? "on" : "off";
                SystemUtils.RunExternalProcess("netsh", $"advfirewall set allprofiles state {action}", hidden: true);
            });

            ChkFirewall.IsEnabled = true;
            ShowNotification(enable ? "✅ Firewall Ativado" : "⚠️ Firewall Desativado", "");
            _ = LoadStateAsync();
        }

        private async void ChkRemoteRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkRemoteRegistry.IsChecked == true; // checked = desativado (mais seguro)
            ChkRemoteRegistry.IsEnabled = false;

            await Task.Run(() =>
            {
                string action = disable ? "disabled" : "auto";
                SystemUtils.RunExternalProcess("sc", $"config RemoteRegistry start= {action}", hidden: true);
                if (disable)
                    SystemUtils.RunExternalProcess("sc", "stop RemoteRegistry", hidden: true);
            });

            ChkRemoteRegistry.IsEnabled = true;
            ShowNotification(disable ? "✅ Registro Remoto Desativado" : "⚠️ Registro Remoto Ativado", "");
        }

        private async void ChkRDP_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkRDP.IsChecked == true; // checked = desativado (mais seguro)
            ChkRDP.IsEnabled = false;

            await Task.Run(() =>
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server",
                    "fDenyTSConnections", disable ? 1 : 0, RegistryValueKind.DWord);
            });

            ChkRDP.IsEnabled = true;
            ShowNotification(disable ? "✅ RDP Desativado" : "⚠️ RDP Ativado", "");
        }

        private async void ChkTelemetry_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkTelemetry.IsChecked == true;
            ChkTelemetry.IsEnabled = false;

            await Task.Run(() =>
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowTelemetry", disable ? 0 : 3, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
                    "AllowTelemetry", disable ? 0 : 3, RegistryValueKind.DWord);
            });

            ChkTelemetry.IsEnabled = true;
            ShowNotification(disable ? "✅ Telemetria Desativada" : "ℹ️ Telemetria Ativada", "");
        }

        private async void ChkActivityHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkActivityHistory.IsChecked == true;
            ChkActivityHistory.IsEnabled = false;

            await Task.Run(() =>
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                    "PublishUserActivities", disable ? 0 : 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                    "UploadUserActivities", disable ? 0 : 1, RegistryValueKind.DWord);
            });

            ChkActivityHistory.IsEnabled = true;
            ShowNotification(disable ? "✅ Histórico de Atividades Desativado" : "ℹ️ Histórico de Atividades Ativado", "");
        }

        private async void ChkAdID_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkAdID.IsChecked == true;
            ChkAdID.IsEnabled = false;

            await Task.Run(() =>
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", disable ? 0 : 1, RegistryValueKind.DWord);
            });

            ChkAdID.IsEnabled = true;
            ShowNotification(disable ? "✅ ID de Publicidade Desativado" : "ℹ️ ID de Publicidade Ativado", "");
        }

        private async void ChkLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool disable = ChkLocation.IsChecked == true;
            ChkLocation.IsEnabled = false;

            await Task.Run(() =>
            {
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                    "DisableLocation", disable ? 1 : 0, RegistryValueKind.DWord);
            });

            ChkLocation.IsEnabled = true;
            ShowNotification(disable ? "✅ Localização Desativada" : "ℹ️ Localização Ativada", "");
        }

        private async void CmbUACLevel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (CmbUACLevel.SelectedItem is not ComboBoxItem item) return;
            if (!int.TryParse(item.Tag?.ToString(), out int level)) return;

            await Task.Run(() => SetUACLevel(level));
            ShowNotification("🔑 UAC Atualizado", $"Nível: {item.Content}. Reinicie para aplicar.");
        }

        // ─── Ações Rápidas ───────────────────────────────────────────────────────

        private async void BtnMaxSecurity_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            if (!await mw.ShowConfirmationDialog(
                "Isso ativará TODAS as proteções:\n• Windows Defender\n• Firewall\n• SmartScreen\n• VBS\n• UAC Máximo\n• Desativa Telemetria, RDP e Registro Remoto\n\nAlgumas mudanças requerem reinicialização. Continuar?"))
                return;

            string taskId = Guid.NewGuid().ToString();
            BackgroundTaskTracker.Instance.RegisterTask(taskId, "Máxima Segurança", "Security");
            _isLoading = true;
            await Task.Run(() =>
            {
                SystemTweaks.EnableMSDefender();
                SystemUtils.RunExternalProcess("netsh", "advfirewall set allprofiles state on", hidden: true);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 1, RegistryValueKind.DWord);
                using (var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                    try { expKey?.DeleteValue("SmartScreenEnabled"); } catch { }
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1, RegistryValueKind.DWord);
                SystemUtils.RunExternalProcess("sc", "config RemoteRegistry start= disabled", hidden: true);
                SystemUtils.RunExternalProcess("sc", "stop RemoteRegistry", hidden: true);
                SetUACLevel(2);
            });

            BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            mw.ShowSuccess("🛡️ Máxima Segurança Aplicada", "Todas as proteções foram ativadas. Reinicie o PC para aplicar VBS.");
            await LoadStateAsync();
        }

        private async void BtnGamingMode_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            if (!await mw.ShowConfirmationDialog(
                "Modo Gaming desativa VBS e Telemetria para melhor performance.\n⚠️ Isso reduz a segurança do sistema.\n\nContinuar?"))
                return;

            string taskId = Guid.NewGuid().ToString();
            BackgroundTaskTracker.Instance.RegisterTask(taskId, "Modo Gaming", "Security");
            _isLoading = true;
            await Task.Run(() =>
            {
                // Desativa VBS para ganho de FPS
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard",
                    "EnableVirtualizationBasedSecurity", 0, RegistryValueKind.DWord);
                // Desativa telemetria
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                    "AllowTelemetry", 0, RegistryValueKind.DWord);
                // Desativa ID de publicidade
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                    "Enabled", 0, RegistryValueKind.DWord);
            });

            BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            mw.ShowSuccess("🎮 Modo Gaming Aplicado", "VBS e Telemetria desativados. Reinicie para aplicar VBS.");
            await LoadStateAsync();
        }

        private async void BtnRestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            if (!await mw.ShowConfirmationDialog("Isso restaurará todas as configurações de segurança para o padrão do Windows. Continuar?"))
                return;

            string taskId = Guid.NewGuid().ToString();
            BackgroundTaskTracker.Instance.RegisterTask(taskId, "Restaurar Padrões", "Security");
            _isLoading = true;
            await Task.Run(() =>
            {
                SystemTweaks.EnableMSDefender();
                SystemUtils.RunExternalProcess("netsh", "advfirewall set allprofiles state on", hidden: true);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, RegistryValueKind.DWord);
                SetUACLevel(1);
            });

            BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            mw.ShowSuccess("↩️ Padrões Restaurados", "Configurações de segurança restauradas para o padrão do Windows.");
            await LoadStateAsync();
        }

        // ─── Leitura de Estado ───────────────────────────────────────────────────

        private static bool IsTelemetryDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { return false; }
        }

        private static bool IsActivityHistoryDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 1);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { return false; }
        }

        private static bool IsAdIdDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { return false; }
        }

        private static bool IsLocationDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 0);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return false; }
        }

        private static bool IsRemoteRegistryDisabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RemoteRegistry");
                var start = key?.GetValue("Start");
                return start != null && Convert.ToInt32(start) == 4; // 4 = Disabled
            }
            catch { return false; }
        }

        private static bool IsRdpDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections", 1);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return true; }
        }

        private static bool IsFirewallEnabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile", "EnableFirewall", 1);
                return val == null || Convert.ToInt32(val) == 1;
            }
            catch { return true; }
        }

        private static bool IsRealtimeProtectionDisabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring", 0);
                return val != null && Convert.ToInt32(val) == 1;
            }
            catch { return false; }
        }

        private static bool IsSmartScreenEnabled()
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 1);
                bool systemEnabled = val == null || Convert.ToInt32(val) == 1;
                if (!systemEnabled) return false;
                var explorerVal = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer", "SmartScreenEnabled", null);
                if (explorerVal is string s && s.Equals("Off", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
            catch { return true; }
        }

        private static int GetUACLevel()
        {
            try
            {
                var consent = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 5);
                int level = consent != null ? Convert.ToInt32(consent) : 5;
                return level switch
                {
                    5 => 2,  // Sempre notificar
                    3 => 1,  // Notificar mudanças de apps
                    2 => 0,  // Notificar sem escurecer
                    0 => -1, // Nunca notificar
                    _ => 1
                };
            }
            catch { return 1; }
        }

        private static void SetUACLevel(int level)
        {
            try
            {
                int consentValue = level switch
                {
                    2 => 5,   // Sempre notificar
                    1 => 3,   // Notificar mudanças de apps
                    0 => 2,   // Notificar sem escurecer
                    -1 => 0,  // Nunca notificar
                    _ => 3
                };
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
                    "ConsentPromptBehaviorAdmin", consentValue, RegistryValueKind.DWord);
            }
            catch { }
        }

        private static string GetUACLevelName(int level) => level switch
        {
            2 => "Sempre Notificar (Máximo)",
            1 => "Notificar Mudanças de Apps",
            0 => "Notificar Sem Escurecer",
            -1 => "Nunca Notificar (Desativado)",
            _ => "Desconhecido"
        };

        private void SetUACComboSelection(int level)
        {
            foreach (ComboBoxItem item in CmbUACLevel.Items)
            {
                if (item.Tag?.ToString() == level.ToString())
                {
                    CmbUACLevel.SelectedItem = item;
                    return;
                }
            }
            CmbUACLevel.SelectedIndex = 1; // Padrão
        }

        private static int CalculateSecurityScore(
            bool defenderOn, bool realtimeOn, bool vbsOn, bool smartScreenOn,
            bool firewallOn, bool remoteRegDisabled, bool rdpDisabled,
            bool telemetryDisabled, bool adIdDisabled, bool uacEnabled)
        {
            int score = 0;
            if (defenderOn) score += 20;
            if (realtimeOn) score += 15;
            if (firewallOn) score += 15;
            if (vbsOn) score += 10;
            if (smartScreenOn) score += 10;
            if (remoteRegDisabled) score += 8;
            if (rdpDisabled) score += 7;
            if (telemetryDisabled) score += 5;
            if (adIdDisabled) score += 5;
            if (uacEnabled) score += 5;
            return Math.Min(score, 100);
        }

        private static string GetScoreLabel(int score) => score switch
        {
            >= 90 => "Excelente 🛡️",
            >= 70 => "Bom ✅",
            >= 50 => "Moderado ⚠️",
            _ => "Vulnerável ❌"
        };

        private void ShowNotification(string title, string message)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                if (!string.IsNullOrEmpty(message))
                    mw.ShowInfo(title, message);
                else
                    mw.ShowSuccess(title, "Configuração aplicada.");
            }
        }
    }
}
