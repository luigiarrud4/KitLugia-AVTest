using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class ExmTweaksPage : Page
    {
        private bool _isLoading = true;

        private static int _sfcProgress;
        private static string _sfcStatus = "";
        private static bool _sfcRunning;
        private static CancellationTokenSource? _sfcCts;
        private static string _sfcLastOutput = "";

        private static int _dismProgress;
        private static string _dismStatus = "";
        private static bool _dismRunning;
        private static CancellationTokenSource? _dismCts;
        private static string _dismLastOutput = "";
        private readonly SolidColorBrush _colorActive = new(Color.FromRgb(108, 203, 95));
        private readonly SolidColorBrush _colorDefault = new(Color.FromRgb(150, 150, 150));

        public ExmTweaksPage()
        {
            InitializeComponent();
            _ = LoadCurrentStatus();
            this.Unloaded += (_, _) => Cleanup();
        }

        public void Cleanup()
        {
            this.DataContext = null;
        }

        private async Task LoadCurrentStatus()
        {
            await Task.Run(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    _isLoading = true;

                    SetToggle(ChkTcpInitialRto, StatusTcpInitialRto, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "InitialRto") == 2000);
                    SetToggle(ChkTcpNonBestEffort, StatusTcpNonBestEffort, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "NonBestEffortLimit") == 0);
                    SetToggle(ChkTcpDefaultTtl, StatusTcpDefaultTtl, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL") == 64);
                    SetToggle(ChkTcp1323, StatusTcp1323, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TCP1323Opts") == 1);
                    SetToggle(ChkTcpMaxDupAcks, StatusTcpMaxDupAcks, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxDupAcks") == 2);
                    SetToggle(ChkDisableWsd, StatusDisableWsd, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnableWsd") == 0);
                    SetToggle(ChkDisableMdns, StatusDisableMdns, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\mDNS", "Start", 3) == 4);
                    SetToggle(ChkDisableLltd, StatusDisableLltd, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\lltdsvc", "Start", 3) == 4);

                    SetToggle(ChkDisableBandwidthThrottle, StatusDisableBandwidthThrottle, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DisableBandwidthThrottle") == 1);
                    SetToggle(ChkIncreaseUserVa, StatusIncreaseUserVa, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SessionViewSize") == 0x20);
                    SetToggle(ChkDisableDynamicTick, StatusDisableDynamicTick, ReadBcdedit("disabledynamictick") == "Yes");
                    SetToggle(ChkTpmBootEntropy, StatusTpmBootEntropy, ReadBcdedit("tpmbootentropy") == "No");
                    SetToggle(ChkUsePhysicalDest, StatusUsePhysicalDest, ReadBcdedit("usephysicaldestination") == "No");
                    SetToggle(ChkUseLegacyApic, StatusUseLegacyApic, ReadBcdedit("uselegacyapic") == "No");

                    SetToggle(ChkDisableLicenseManager, StatusDisableLicenseManager, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\LicenseManager", "Start", 3) == 4);
                    SetToggle(ChkDisableWlidsvc, StatusDisableWlidsvc, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\wlidsvc", "Start", 3) == 4);
                    SetToggle(ChkDisableShpamsvc, StatusDisableShpamsvc, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\shpamsvc", "Start", 3) == 4);
                    SetToggle(ChkDisableTablet, StatusDisableTablet, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\TabletInputService", "Start", 3) == 4);

                    SetToggle(ChkDisableLockScreen, StatusDisableLockScreen, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen") == 1);
                    SetToggle(ChkDisableToast, StatusDisableToast, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableNotificationCenter") == 1);
                    SetToggle(ChkDisableClipboard, StatusDisableClipboard, ReadRegCuDword(@"SOFTWARE\Microsoft\Clipboard", "EnableClipboardHistory", 1) == 0);
                    SetToggle(ChkDisableAutoInstall, StatusDisableAutoInstall, ReadRegDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore\WindowsUpdate", "AutoDownload") == 2);
                    SetToggle(ChkDisableBiometrics, StatusDisableBiometrics, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\WbioSrvc", "Start", 3) == 4);
                    SetToggle(ChkDisableSensors, StatusDisableSensors, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\SensorService", "Start", 3) == 4);
                    SetToggle(ChkDisableVisualAnimations, StatusDisableVisualAnimations, ReadRegCuDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting") == 2);
                    SetToggle(ChkKillMenu, StatusKillMenu, ReadRegCuDword(@"Control Panel\Desktop", "AutoEndTasks") == 1);
                    SetToggle(ChkHideTaskView, StatusHideTaskView, ReadRegCuDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton") == 0);
                    SetToggle(ChkDisableMica, StatusDisableMica, ReadRegCuDword(@"SOFTWARE\Microsoft\Windows\Dwm", "MicaEnabled") == 0);
                    SetToggle(ChkDisableSnapAssist, StatusDisableSnapAssist, ReadRegCuDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableSnapAssist", 1) == 0);

                    SetToggle(ChkMouseAccel, StatusMouseAccel, ReadRegCuDword(@"Control Panel\Mouse", "MouseSpeed") == 0);
                    SetToggle(ChkKeyboard, StatusKeyboard, ReadRegCuDword(@"Control Panel\Keyboard", "KeyboardSpeed") == 31);
                    SetToggle(ChkCursorSuppression, StatusCursorSuppression, ReadRegCuCursorMask() == false);

                    SetToggle(ChkDisableMinidumps, StatusDisableMinidumps, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\CrashControl", "CrashDumpEnabled") == 0);
                    SetToggle(ChkDisableAutoRebootCrash, StatusDisableAutoRebootCrash, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot") == 0);
                    SetToggle(ChkZeroStartupDelay, StatusZeroStartupDelay, ReadRegDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec") == 0);
                    SetToggle(ChkDisableBootAnimation, StatusDisableBootAnimation, ReadRegDword(@"SOFTWARE\Microsoft\DWM", "EnableMachineBootAnimation") == 0);

                    UpdateLabel(StatusCleanWuCache, false);
                    UpdateLabel(StatusCleanRecent, false);
                    UpdateLabel(StatusCleanIeCache, false);
                    UpdateLabel(StatusCleanDumpsLogs, false);
                    UpdateLabel(StatusFlushNetworkCache, false);

                    SetToggle(ChkDisablePrefetch, StatusDisablePrefetch, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 3) == 0);
                    SetToggle(ChkLargeSystemCache, StatusLargeSystemCache, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargeSystemCache", 0) == 1);
                    SetToggle(ChkDisablePagingExecutive, StatusDisablePagingExecutive, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "DisablePagingExecutive", 0) == 1);
                    SetToggle(ChkDisableExceptionChain, StatusDisableExceptionChain, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "DisableExceptionChainValidation", 0) == 1);
                    SetToggle(ChkDisableCfg, StatusDisableCfg, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "EnableCfg", 1) == 0);
                    SetToggle(ChkDistributeTimers, StatusDistributeTimers, ReadRegDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "DistributeTimers", 0) == 1);

                    SetToggle(ChkBlockCortana, StatusBlockCortana, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 1) == 0);
                    SetToggle(ChkDisableActivityFeed, StatusDisableActivityFeed, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 1) == 0);
                    SetToggle(ChkDisableWindowsFeeds, StatusDisableWindowsFeeds, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 1) == 0);
                    SetToggle(ChkBlockAdvertisingId, StatusBlockAdvertisingId, ReadRegDword(@"Software\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 0) == 1);
                    SetToggle(ChkDisableConsumerFeatures, StatusDisableConsumerFeatures, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 0) == 1);
                    SetToggle(ChkDisableErrorReporting, StatusDisableErrorReporting, ReadRegDword(@"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 0) == 1);

                    SetToggle(ChkNetworkThrottlingIndex, StatusNetworkThrottlingIndex, ReadRegDword(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 10) == -1);

                    SetToggle(ChkDisableSpooler, StatusDisableSpooler, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\Spooler", "Start", 3) == 4);
                    SetToggle(ChkDisableBluetooth, StatusDisableBluetooth, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\BTAGService", "Start", 3) == 4 && ReadRegDword(@"SYSTEM\CurrentControlSet\Services\bthserv", "Start", 3) == 4);
                    SetToggle(ChkDisableMapsBroker, StatusDisableMapsBroker, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\MapsBroker", "Start", 3) == 4);
                    SetToggle(ChkDisableSysMain, StatusDisableSysMain, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\SysMain", "Start", 3) == 4);

                    SetToggle(ChkKeyboardDataQueue, StatusKeyboardDataQueue, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 0) == 100);
                    SetToggle(ChkMouseDataQueue, StatusMouseDataQueue, ReadRegDword(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 0) == 100);

                    if (_sfcRunning)
                    {
                        ProgressSfc.Visibility = System.Windows.Visibility.Visible;
                        ProgressSfc.IsIndeterminate = _sfcProgress == 0;
                        if (_sfcProgress > 0)
                            ProgressSfc.Value = _sfcProgress;
                        BtnSfcScan.IsEnabled = false;
                        BtnSfcScan.Content = _sfcProgress > 0 ? $"{_sfcProgress}%" : "Executando...";
                        UpdateTransientLabel(StatusSfcScan, true, "Escaneando...");
                    }
                    else if (!string.IsNullOrEmpty(_sfcStatus))
                    {
                        ProgressSfc.Visibility = System.Windows.Visibility.Collapsed;
                        bool ok = _sfcStatus is "OK" or "Reparado";
                        UpdateTransientLabel(StatusSfcScan, ok, ok ? _sfcStatus : "Falhou", "Parado");
                        BtnSfcScan.IsEnabled = true;
                        BtnSfcScan.Content = _sfcStatus;
                    }
                    if (_dismRunning)
                    {
                        ProgressDism.Visibility = System.Windows.Visibility.Visible;
                        ProgressDism.Value = _dismProgress;
                        BtnDism.IsEnabled = false;
                        BtnDism.Content = $"{_dismProgress}%";
                        UpdateTransientLabel(StatusDism, true, "Executando...");
                    }
                    else if (!string.IsNullOrEmpty(_dismStatus))
                    {
                        ProgressDism.Visibility = System.Windows.Visibility.Collapsed;
                        bool ok = _dismStatus == "Concluído";
                        UpdateTransientLabel(StatusDism, ok, ok ? "Concluído" : "Falhou", "Parado");
                        BtnDism.IsEnabled = true;
                        BtnDism.Content = _dismStatus;
                    }

                    _isLoading = false;
                });
            });
        }

        private void UpdateLabel(TextBlock label, bool isActive, string textActive = "Ativo", string textInactive = "Padrão")
        {
            label.Text = isActive ? textActive : textInactive;
            label.Foreground = isActive ? _colorActive : _colorDefault;
        }

        private void SetToggle(System.Windows.Controls.CheckBox chk, TextBlock status, bool isActive, string textActive = "Ativo", string textInactive = "Padrão")
        {
            chk.IsChecked = isActive;
            UpdateLabel(status, isActive, textActive, textInactive);
        }

        private static int ReadRegDword(string keyPath, string valueName, int defaultValue = 0)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(keyPath);
                if (key == null) return defaultValue;
                return Convert.ToInt32(key.GetValue(valueName, defaultValue));
            }
            catch { return defaultValue; }
        }

        private static int ReadRegCuDword(string keyPath, string valueName, int defaultValue = 0)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(keyPath);
                if (key == null) return defaultValue;
                return Convert.ToInt32(key.GetValue(valueName, defaultValue));
            }
            catch { return defaultValue; }
        }

        private static bool ReadRegCuCursorMask()
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"Control Panel\Desktop");
                if (key == null) return true;
                var mask = key.GetValue("UserPreferencesMask") as byte[];
                if (mask == null || mask.Length < 1) return true;
                return (mask[0] & 0x10) == 0;
            }
            catch { return false; }
        }

        private static string ReadBcdedit(string parameter)
        {
            try
            {
                var psi = new ProcessStartInfo("bcdedit", $"/enum {{current}}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(parameter, StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) return string.Join(" ", parts, 1, parts.Length - 1);
                    }
                }
                return "";
            }
            catch { return ""; }
        }

        private void ShowInfo(string title, string message)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo(title, message);
        }

        private void ShowSuccess(string title, string message)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess(title, message);
        }

        // --- REDE ---

        private void ChkTcpInitialRto_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTcpInitialRto.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "InitialRto", 2000, RegistryValueKind.DWord);
            else
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "InitialRto", 3000, RegistryValueKind.DWord);
            UpdateLabel(StatusTcpInitialRto, active);
            ShowInfo("TCP InitialRto", active ? "Definido para 2000ms (estabilidade)." : "Restaurado para 3000ms (padrão).");
        }

        private void ChkTcpNonBestEffort_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTcpNonBestEffort.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "NonBestEffortLimit", 0, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.DeleteValue("NonBestEffortLimit", false);
            }
            UpdateLabel(StatusTcpNonBestEffort, active);
            ShowInfo("QoS", active ? "NonBestEffortLimit=0 (QoS desligado)." : "Restaurado.");
        }

        private void ChkTcpDefaultTtl_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTcpDefaultTtl.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", 64, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.DeleteValue("DefaultTTL", false);
            }
            UpdateLabel(StatusTcpDefaultTtl, active);
            ShowInfo("TCP DefaultTTL", active ? "TTL definido para 64." : "Restaurado.");
        }

        private void ChkTcp1323_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTcp1323.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TCP1323Opts", 1, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.DeleteValue("TCP1323Opts", false);
            }
            UpdateLabel(StatusTcp1323, active);
            ShowInfo("TCP1323", active ? "Timestamps + Window Scaling ativados." : "Restaurado.");
        }

        private void ChkTcpMaxDupAcks_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTcpMaxDupAcks.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxDupAcks", 2, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                key?.DeleteValue("MaxDupAcks", false);
            }
            UpdateLabel(StatusTcpMaxDupAcks, active);
            ShowInfo("MaxDupAcks", active ? "Threshold de ACK duplicado=2." : "Restaurado.");
        }

        private void ChkDisableWsd_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableWsd.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "EnableWsd", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableWsd, active);
            ShowInfo("WSD", active ? "Web Services Discovery desligado." : "WSD ligado.");
        }

        private void ChkDisableMdns_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableMdns.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mDNS", "Start", active ? 4 : 3, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableMdns, active);
            ShowInfo("mDNS", active ? "mDNS desativado (Start=4)." : "mDNS manual (Start=3).");
        }

        private void ChkDisableLltd_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableLltd.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\lltdsvc", "Start", active ? 4 : 3, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableLltd, active);
            ShowInfo("LLTD", active ? "lltdsvc desativado (Start=4)." : "lltdsvc manual (Start=3).");
        }

        private void ChkDisableBandwidthThrottle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableBandwidthThrottle.IsChecked == true;
            if (active)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DisableBandwidthThrottle", 1, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", true);
                key?.DeleteValue("DisableBandwidthThrottle", false);
            }
            UpdateLabel(StatusDisableBandwidthThrottle, active);
            ShowInfo("Bandwidth Throttle", active ? "Limitação de banda desativada." : "Restaurado.");
        }

        private async void BtnFlushNetworkCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "Limpando...";
            UpdateLabel(StatusFlushNetworkCache, true, "Limpando...");
            await Task.Run(() =>
            {
                try
                {
                    foreach (var cmd in new[]
                    {
                        "netsh int ip delete arpcache",
                        "netsh int ip delete destinationcache",
                        "netsh int ip delete routecache",
                        "netsh int ip delete neighbors",
                        "ipconfig /flushdns",
                        "ipconfig /registerdns"
                    })
                    {
                        var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(5000);
                    }
                }
                catch { }
            });
            UpdateLabel(StatusFlushNetworkCache, true, "Cache Limpo!");
            btn.Content = "Feito!";
            ShowSuccess("Rede", "ARP, DNS, Route, Destination e Neighbors limpos com sucesso.");
        }

        // --- BCDEDIT ---

        private void ChkX2Apic_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkX2Apic.IsChecked == true;
            RunBcdedit($"x2apicpolicy {(active ? "Enable" : "Default")}");
            UpdateLabel(StatusX2Apic, active);
            ShowInfo("BCDEDIT", active ? "x2APIC habilitado (requer reinicialização)." : "x2APIC restaurado.");
        }

        private void ChkIncreaseUserVa_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkIncreaseUserVa.IsChecked == true;
            RunBcdedit(active ? "increaseuserva 3072" : "deletevalue increaseuserva");
            UpdateLabel(StatusIncreaseUserVa, active);
            ShowInfo("BCDEDIT", active ? "User VA aumentado para 3072 MB." : "User VA restaurado.");
        }

        private void ChkDisableDynamicTick_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableDynamicTick.IsChecked == true;
            RunBcdedit(active ? "disabledynamictick Yes" : "deletevalue disabledynamictick");
            UpdateLabel(StatusDisableDynamicTick, active);
            ShowInfo("BCDEDIT", active ? "Dynamic Tick desligado." : "Dynamic Tick restaurado.");
        }

        private void ChkTpmBootEntropy_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkTpmBootEntropy.IsChecked == true;
            RunBcdedit(active ? "tpmbootentropy No" : "deletevalue tpmbootentropy");
            UpdateLabel(StatusTpmBootEntropy, active);
            ShowInfo("BCDEDIT", active ? "TPM boot entropy desligado." : "TPM boot entropy restaurado.");
        }

        private void ChkUsePhysicalDest_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkUsePhysicalDest.IsChecked == true;
            RunBcdedit(active ? "usephysicaldestination No" : "deletevalue usephysicaldestination");
            UpdateLabel(StatusUsePhysicalDest, active);
            ShowInfo("BCDEDIT", active ? "Physical Destination=No." : "Restaurado.");
        }

        private void ChkUseLegacyApic_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkUseLegacyApic.IsChecked == true;
            RunBcdedit(active ? "uselegacyapic No" : "deletevalue uselegacyapic");
            UpdateLabel(StatusUseLegacyApic, active);
            ShowInfo("BCDEDIT", active ? "APIC moderno forçado." : "Restaurado.");
        }

        private static void RunBcdedit(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("bcdedit", $"/set {{current}} {arguments}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    Verb = "runas"
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { }
        }

        // --- SERVICOS ---

        private void SetServiceStart(string serviceName, int startValue)
        {
            Registry.SetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{serviceName}", "Start", startValue, RegistryValueKind.DWord);
        }

        private void ChkDisableLicenseManager_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableLicenseManager.IsChecked == true;
            SetServiceStart("LicenseManager", active ? 4 : 3);
            UpdateLabel(StatusDisableLicenseManager, active);
            ShowInfo("License Manager", active ? "Licenças da Store desativadas." : "Restaurado.");
        }

        private void ChkDisableWlidsvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableWlidsvc.IsChecked == true;
            SetServiceStart("wlidsvc", active ? 4 : 3);
            UpdateLabel(StatusDisableWlidsvc, active);
            ShowInfo("wlidsvc", active ? "Microsoft Account sign-in desligado." : "Restaurado.");
        }

        private void ChkDisableShpamsvc_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableShpamsvc.IsChecked == true;
            SetServiceStart("shpamsvc", active ? 4 : 3);
            UpdateLabel(StatusDisableShpamsvc, active);
            ShowInfo("shpamsvc", active ? "Shared PC Account desligado." : "Restaurado.");
        }

        private void ChkDisableTablet_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableTablet.IsChecked == true;
            SetServiceStart("TabletInputService", active ? 4 : 3);
            SetServiceStart("TouchKeyboard", active ? 4 : 3);
            UpdateLabel(StatusDisableTablet, active);
            ShowInfo("Tablet/Touch", active ? "Serviços tablet e touch desativados." : "Restaurado.");
        }

        // --- WINDOWS SETTINGS ---

        private void ChkDisableLockScreen_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableLockScreen.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Personalization");
            if (active)
                key.SetValue("NoLockScreen", 1, RegistryValueKind.DWord);
            else
                key.DeleteValue("NoLockScreen", false);
            UpdateLabel(StatusDisableLockScreen, active);
            ShowInfo("Lock Screen", active ? "Tela de bloqueio removida." : "Restaurada.");
        }

        private void ChkDisableToast_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableToast.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer");
            if (active)
                key.SetValue("DisableNotificationCenter", 1, RegistryValueKind.DWord);
            else
                key.DeleteValue("DisableNotificationCenter", false);
            UpdateLabel(StatusDisableToast, active);
            ShowInfo("Toast", active ? "Notificações toast desativadas." : "Restauradas.");
        }

        private void ChkDisableClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableClipboard.IsChecked == true;
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Clipboard", "EnableClipboardHistory", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableClipboard, active);
            ShowInfo("Clipboard", active ? "Histórico de clipboard desativado." : "Restaurado.");
        }

        private void ChkDisableAutoInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableAutoInstall.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore\WindowsUpdate", "AutoDownload", active ? 2 : 0, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableAutoInstall, active);
            ShowInfo("Store", active ? "Auto-instalação de apps desativada." : "Restaurada.");
        }

        private void ChkDisableBiometrics_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableBiometrics.IsChecked == true;
            SetServiceStart("WbioSrvc", active ? 4 : 3);
            UpdateLabel(StatusDisableBiometrics, active);
            ShowInfo("Biometria", active ? "Serviço biométrico desativado." : "Restaurado.");
        }

        private void ChkDisableSensors_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableSensors.IsChecked == true;
            SetServiceStart("SensorService", active ? 4 : 3);
            UpdateLabel(StatusDisableSensors, active);
            ShowInfo("Sensores", active ? "Serviço de sensores desativado." : "Restaurado.");
        }

        private void ChkDisableVisualAnimations_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableVisualAnimations.IsChecked == true;
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", active ? 2 : 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "DragFullWindows", active ? 0 : 1, RegistryValueKind.String);
            Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "SmoothScroll", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableVisualAnimations, active);
            ShowInfo("Visual", active ? "Composição, arrasto e scroll suave desligados." : "Restaurado.");
        }

        private void ChkKillMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkKillMenu.IsChecked == true;
            if (active)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", 1, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "HungAppTimeout", 1000, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout", 2000, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", 0, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", 2000, RegistryValueKind.String);
            }
            else
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "AutoEndTasks", 0, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "HungAppTimeout", 5000, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WaitToKillAppTimeout", 5000, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "MenuShowDelay", 400, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", 5000, RegistryValueKind.String);
            }
            UpdateLabel(StatusKillMenu, active);
            ShowInfo("Menu Kill", active ? "AutoEndTasks=1, HungApp 1s, WaitToKill 2s, Menu=0." : "Restaurado para padrão.");
        }

        private void ChkHideTaskView_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkHideTaskView.IsChecked == true;
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowTaskViewButton", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusHideTaskView, active);
            ShowInfo("Task View", active ? "Botão Task View escondido." : "Restaurado.");
        }

        private void ChkDisableMica_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableMica.IsChecked == true;
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\Dwm", "MicaEnabled", active ? 0 : 1, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableMica, active);
            ShowInfo("Mica", active ? "Efeitos Mica e transparência desligados." : "Restaurado.");
        }

        private void ChkDisableSnapAssist_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableSnapAssist.IsChecked == true;
            Registry.SetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "EnableSnapAssist", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableSnapAssist, active);
            ShowInfo("Snap Assist", active ? "Snap Layouts/Assist desativado." : "Restaurado.");
        }

        // --- MOUSE / TECLADO ---

        private void ChkMouseAccel_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkMouseAccel.IsChecked == true;
            if (active)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed", 0, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", 0, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", 0, RegistryValueKind.String);
            }
            else
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed", 1, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", 6, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", 10, RegistryValueKind.String);
            }
            UpdateLabel(StatusMouseAccel, active);
            ShowInfo("Mouse", active ? "Aceleração do mouse desativada." : "Restaurada.");
        }

        private void ChkKeyboard_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkKeyboard.IsChecked == true;
            if (active)
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardSpeed", 31, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardDelay", 0, RegistryValueKind.DWord);
            }
            else
            {
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardSpeed", 1, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Keyboard", "KeyboardDelay", 1, RegistryValueKind.DWord);
            }
            UpdateLabel(StatusKeyboard, active);
            ShowInfo("Teclado", active ? "KeyboardSpeed=31, KeyboardDelay=0." : "Restaurado.");
        }

        private void ChkCursorSuppression_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkCursorSuppression.IsChecked == true;
            if (active)
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
                var mask = (byte[])key.GetValue("UserPreferencesMask", new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 });
                if (mask.Length >= 1)
                    mask[0] = (byte)(mask[0] & ~0x10);
                key.SetValue("UserPreferencesMask", mask, RegistryValueKind.Binary);
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
                var mask = (byte[])key.GetValue("UserPreferencesMask", new byte[] { 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 });
                if (mask.Length >= 1)
                    mask[0] = (byte)(mask[0] | 0x10);
                key.SetValue("UserPreferencesMask", mask, RegistryValueKind.Binary);
            }
            UpdateLabel(StatusCursorSuppression, active);
            ShowInfo("Cursor", active ? "Supressão de cursor desativada (cursor não some ao digitar)." : "Restaurada.");
        }

        // --- BOOT / CRASH ---

        private void ChkDisableMinidumps_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableMinidumps.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", "CrashDumpEnabled", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableMinidumps, active);
            ShowInfo("Crash Dump", active ? "Minidumps desativados." : "Restaurado.");
        }

        private void ChkDisableAutoRebootCrash_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableAutoRebootCrash.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableAutoRebootCrash, active);
            ShowInfo("Auto Reboot", active ? "Auto-reboot pós-crash desativado." : "Restaurado.");
        }

        private void ChkZeroStartupDelay_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkZeroStartupDelay.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", active ? 0 : 100, RegistryValueKind.DWord);
            UpdateLabel(StatusZeroStartupDelay, active);
            ShowInfo("Startup", active ? "Delay de inicialização removido." : "Restaurado.");
        }

        private void ChkDisableBootAnimation_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableBootAnimation.IsChecked == true;
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\DWM", "EnableMachineBootAnimation", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableBootAnimation, active);
            ShowInfo("Boot", active ? "Animação de boot removida." : "Restaurado.");
        }

        // --- CLEANUP ---

        private void UpdateTransientLabel(TextBlock label, bool active, string activeText, string? inactiveText = null)
        {
            label.Text = active ? activeText : (inactiveText ?? "Parado");
            label.Foreground = active ? _colorActive : _colorDefault;
        }

        private async void BtnCleanWuCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "Limpando...";
            UpdateTransientLabel(StatusCleanWuCache, true, "Limpando...");
            await Task.Run(() =>
            {
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "SoftwareDistribution", "Download");
                    if (Directory.Exists(dir))
                    {
                        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                            TryDeleteFile(file);
                    }
                }
                catch { }
            });
            UpdateTransientLabel(StatusCleanWuCache, true, "Cache Limpo!");
            btn.Content = "Feito!";
            ShowSuccess("Windows Update", "Cache de updates limpo com sucesso.");
        }

        private async void BtnCleanRecent_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "Limpando...";
            UpdateTransientLabel(StatusCleanRecent, true, "Limpando...");
            await Task.Run(() =>
            {
                try
                {
                    var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                    if (Directory.Exists(recent))
                    {
                        foreach (var file in Directory.GetFiles(recent))
                            TryDeleteFile(file);
                    }
                }
                catch { }
            });
            UpdateTransientLabel(StatusCleanRecent, true, "Itens Limpos!");
            btn.Content = "Feito!";
            ShowSuccess("Recent Items", "Lista de arquivos recentes limpa.");
        }

        private async void BtnCleanIeCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "Limpando...";
            UpdateTransientLabel(StatusCleanIeCache, true, "Limpando...");
            await Task.Run(() =>
            {
                try
                {
                    var ieCache = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\INetCache");
                    if (Directory.Exists(ieCache))
                    {
                        foreach (var file in Directory.GetFiles(ieCache, "*", SearchOption.AllDirectories))
                            TryDeleteFile(file);
                    }
                }
                catch { }
            });
            UpdateTransientLabel(StatusCleanIeCache, true, "Cache Limpo!");
            btn.Content = "Feito!";
            ShowSuccess("IE/Edge Cache", "Cache do Internet Explorer/Edge limpo.");
        }

        private async void BtnCleanDumpsLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            var btn = (System.Windows.Controls.Button)sender;
            btn.IsEnabled = false;
            btn.Content = "Limpando...";
            UpdateTransientLabel(StatusCleanDumpsLogs, true, "Limpando...");
            await Task.Run(() =>
            {
                try
                {
                    var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    DeleteFilesInDir(Path.Combine(winDir, "Minidump"));
                    DeleteFilesInDir(Path.Combine(winDir, "Logs"));
                    var wer = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\WER");
                    if (Directory.Exists(wer))
                    {
                        foreach (var dir in Directory.GetDirectories(wer))
                            TryDeleteDirectory(dir);
                    }
                }
                catch { }
            });
            UpdateTransientLabel(StatusCleanDumpsLogs, true, "Limpos!");
            btn.Content = "Feito!";
            ShowSuccess("Limpeza", "Minidumps, Logs e WER reports removidos.");
        }

        private async void BtnSfcScan_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _sfcRunning || _dismRunning) return;
            var btn = (System.Windows.Controls.Button)sender;
            _sfcRunning = true;
            _sfcProgress = 0;
            _sfcStatus = "Preparando...";
            _sfcLastOutput = "";
            UpdateSfcUi(btn);

            _sfcCts = new CancellationTokenSource();
            var token = _sfcCts.Token;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Reparo Completo (DISM + SFC)", "ExmTweaks");
            Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Processo em andamento — não feche o KitLugia");

            // Aviso visível no status
            UpdateTransientLabel(StatusSfcScan, true, "⚠️ Processo em andamento — não feche o KitLugia");

            // Etapa 1: DISM RestoreHealth (sempre primeiro na ordem correta)
            Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Etapa 1/3: DISM RestoreHealth...");
            _dismLastOutput = "";
            bool dismOk = await RunDismWithProgress("RestoreHealth", btn, token, taskId);

            // Etapa 2: DISM Cleanup (só se RestoreHealth OK)
            if (dismOk && !token.IsCancellationRequested)
            {
                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Etapa 2/3: DISM Cleanup...");
                dismOk = await RunDismWithProgress("StartComponentCleanup /ResetBase", btn, token, taskId);
            }

            // Etapa 3: SFC (só se DISM OK)
            int sfcExitCode = -1;
            if (dismOk && !token.IsCancellationRequested)
            {
                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, "Etapa 3/3: SFC /scannow...");
                try { sfcExitCode = await RunSfcProcessAsync(btn, token, taskId); }
                catch { }
            }
            else if (!dismOk && !token.IsCancellationRequested)
            {
                _sfcLastOutput = "DISM falhou — execute DISM manualmente antes do SFC";
            }

            _sfcRunning = false;
            _sfcCts?.Dispose();
            _sfcCts = null;

            bool sfcOk = sfcExitCode == 0 || sfcExitCode == 1;
            bool allOk = dismOk && sfcOk;

            ProgressSfc.IsIndeterminate = false;
            ProgressSfc.Visibility = System.Windows.Visibility.Collapsed;

            string display = !string.IsNullOrEmpty(_sfcLastOutput) ? _sfcLastOutput : (allOk ? "Concluído" : "Falhou");
            string resultMsg;
            if (allOk)
            {
                resultMsg = "DISM + SFC concluídos. Sistema reparado.";
                ShowSuccess("Reparo Completo", resultMsg);
            }
            else if (dismOk)
            {
                resultMsg = $"DISM OK, SFC falhou (código {sfcExitCode})";
                ShowInfo("Reparo Completo", resultMsg);
            }
            else
            {
                resultMsg = "DISM falhou. Veja o status para detalhes.";
                ShowInfo("Reparo Completo", resultMsg);
            }

            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, allOk, resultMsg);
            UpdateTransientLabel(StatusSfcScan, allOk, display, "Parado");
            btn.IsEnabled = true;
            btn.Content = allOk ? "Concluído" : "Falhou";
            _sfcStatus = allOk ? "Concluído" : "Falhou";
        }

        private async Task<int> RunSfcProcessAsync(System.Windows.Controls.Button btn, CancellationToken token, string taskId)
        {
            try
            {
                return await RunProcessWithProgressAsync("sfc", "/scannow", btn, token, false, taskId);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return await RunProcessWithProgressAsync("cmd.exe", "/c sfc /scannow", btn, token, true, taskId);
            }
        }

        private async Task<int> RunProcessWithProgressAsync(string exe, string args, System.Windows.Controls.Button btn, CancellationToken token, bool isSfc, string taskId)
        {
            using var p = new Process();
            p.StartInfo.FileName = exe;
            p.StartInfo.Arguments = args;
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.EnableRaisingEvents = true;

            var progressRegex = new Regex(@"(\d+)%");
            int lastProgress = -1;

            DataReceivedEventHandler handler = (_, args2) =>
            {
                if (args2.Data != null && !token.IsCancellationRequested)
                {
                    // Captura a última linha de saída real do SFC
                    string line = args2.Data.Trim();
                    if (line.Length > 0 && !line.Contains('%'))
                        _sfcLastOutput = line.Length > 80 ? line[..80] + "..." : line;

                    var m = progressRegex.Match(args2.Data);
                    int prog;
                    if (m.Success && int.TryParse(m.Groups[1].Value, out prog))
                    {
                        if (prog != lastProgress)
                        {
                            lastProgress = prog;
                            if (isSfc)
                            {
                                _sfcProgress = prog;
                                _sfcStatus = $"{prog}%";
                                Dispatcher.Invoke(() => UpdateSfcProgress(btn, prog));
                            }
                            // ⬇️ NOVO: Reportar progresso ao tracker
                            Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, $"{prog}%");
                        }
                    }
                }
            };

            p.OutputDataReceived += handler;
            p.ErrorDataReceived += handler;

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            await p.WaitForExitAsync(token);
            return p.ExitCode;
        }

        private void UpdateSfcUi(System.Windows.Controls.Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "Executando...";
            UpdateTransientLabel(StatusSfcScan, true, "Escaneando...");
            ProgressSfc.Visibility = System.Windows.Visibility.Visible;
            ProgressSfc.IsIndeterminate = true;
        }

        private void UpdateSfcProgress(System.Windows.Controls.Button btn, int pct)
        {
            if (ProgressSfc.IsIndeterminate)
                ProgressSfc.IsIndeterminate = false;
            btn.Content = $"{pct}%";
            ProgressSfc.Value = pct;
        }

        private async void BtnDism_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _dismRunning || _sfcRunning) return;
            var btn = (System.Windows.Controls.Button)sender;
            _dismRunning = true;
            _dismProgress = 0;
            _dismStatus = "Iniciando...";
            _dismLastOutput = "";
            UpdateDismUi(btn);

            _dismCts = new CancellationTokenSource();
            var token = _dismCts.Token;

            // ⬇️ NOVO: Registrar no BackgroundTaskTracker
            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("DISM RestoreHealth + Cleanup", "ExmTweaks");

            bool dismOk = await RunDismWithProgress("RestoreHealth", btn, token, taskId);
            if (dismOk)
                dismOk = await RunDismWithProgress("StartComponentCleanup /ResetBase", btn, token, taskId);

            _dismRunning = false;
            _dismCts?.Dispose();
            _dismCts = null;

            ProgressDism.Visibility = System.Windows.Visibility.Collapsed;

            string display = !string.IsNullOrEmpty(_dismLastOutput) ? _dismLastOutput : (dismOk ? "Concluído" : "Falhou");
            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, dismOk, display);

            UpdateTransientLabel(StatusDism, dismOk, display, "Parado");
            btn.IsEnabled = true;
            btn.Content = dismOk ? "Concluído" : "Falhou";
            _dismStatus = dismOk ? "Concluído" : "Falhou";
            if (dismOk)
                ShowSuccess("DISM", "Reparo e limpeza WinSxS concluídos.");
            else
                ShowInfo("DISM", $"Falha: {_dismLastOutput}");
        }

        private async Task<bool> RunDismWithProgress(string args, System.Windows.Controls.Button btn, CancellationToken token, string taskId)
        {
            try
            {
                using var p = new Process();
                p.StartInfo.FileName = "dism";
                p.StartInfo.Arguments = $"/Online /Cleanup-Image /{args}";
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.EnableRaisingEvents = true;

                var dismRegex = new Regex(@"(\d+\.?\d*)%");
                int lastProgress = -1;

                DataReceivedEventHandler handler = (_, args2) =>
                {
                    if (args2.Data != null && !token.IsCancellationRequested)
                    {
                        // Captura a última linha de saída real
                        string line = args2.Data.Trim();
                        if (line.Length > 0 && !line.Contains('%'))
                            _dismLastOutput = line.Length > 80 ? line[..80] + "..." : line;

                        var m = dismRegex.Match(args2.Data);
                        double pct;
                        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out pct))
                        {
                            int prog = (int)Math.Round(pct);
                            if (prog != lastProgress)
                            {
                                lastProgress = prog;
                                _dismProgress = prog;
                                _dismStatus = $"{prog}%";
                                Dispatcher.Invoke(() => UpdateDismProgress(btn, prog));
                                // ⬇️ NOVO: Reportar progresso ao tracker
                                Services.BackgroundTaskTracker.Instance.UpdateTaskProgress(taskId, $"{prog}%");
                            }
                        }
                    }
                };

                p.OutputDataReceived += handler;
                p.ErrorDataReceived += handler;

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                await p.WaitForExitAsync(token);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private void UpdateDismUi(System.Windows.Controls.Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "0%";
            UpdateTransientLabel(StatusDism, true, "Executando...");
            ProgressDism.Visibility = System.Windows.Visibility.Visible;
            ProgressDism.IsIndeterminate = false;
            ProgressDism.Value = 0;
        }

        private void UpdateDismProgress(System.Windows.Controls.Button btn, int pct)
        {
            btn.Content = $"{pct}%";
            ProgressDism.Value = pct;
        }

        // --- SISTEMA E MEMÓRIA ---

        private void ChkDisablePrefetch_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisablePrefetch.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
            Registry.SetValue(path, "EnablePrefetcher", active ? 0 : 3, RegistryValueKind.DWord);
            Registry.SetValue(path, "EnableSuperfetch", active ? 0 : 3, RegistryValueKind.DWord);
            UpdateLabel(StatusDisablePrefetch, active);
            ShowInfo("Prefetch", active ? "Prefetch e Superfetch desativados (reduz I/O em SSD)." : "Restaurado para padrão (3).");
        }

        private void ChkLargeSystemCache_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkLargeSystemCache.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            Registry.SetValue(path, "LargeSystemCache", active ? 1 : 0, RegistryValueKind.DWord);
            UpdateLabel(StatusLargeSystemCache, active);
            ShowInfo("Large Cache", active ? "LargeSystemCache ativado (mais RAM para cache de arquivos)." : "Restaurado.");
        }

        private void ChkDisablePagingExecutive_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisablePagingExecutive.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            Registry.SetValue(path, "DisablePagingExecutive", active ? 1 : 0, RegistryValueKind.DWord);
            UpdateLabel(StatusDisablePagingExecutive, active);
            ShowInfo("Paging", active ? "DisablePagingExecutive=1 (kernel/drivers fixos na RAM)." : "Restaurado.");
        }

        private void ChkDisableExceptionChain_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableExceptionChain.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
            if (active)
                Registry.SetValue(path, "DisableExceptionChainValidation", 1, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, true);
                key?.DeleteValue("DisableExceptionChainValidation", false);
            }
            UpdateLabel(StatusDisableExceptionChain, active);
            ShowInfo("Exception Chain", active ? "Validação de cadeia de exceção desativada." : "Restaurado.");
        }

        private void ChkDisableCfg_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableCfg.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            Registry.SetValue(path, "EnableCfg", active ? 0 : 1, RegistryValueKind.DWord);
            UpdateLabel(StatusDisableCfg, active);
            ShowInfo("CFG", active ? "Control Flow Guard desativado (ganho FPS, menos segurança)." : "Restaurado.");
        }

        private void ChkDistributeTimers_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDistributeTimers.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
            Registry.SetValue(path, "DistributeTimers", active ? 1 : 0, RegistryValueKind.DWord);
            UpdateLabel(StatusDistributeTimers, active);
            ShowInfo("Timers", active ? "DistributeTimers=1 (timers distribuídos entre núcleos)." : "Restaurado.");
        }

        // --- PRIVACIDADE E TELEMETRIA ---

        private void ChkBlockCortana_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkBlockCortana.IsChecked == true;
            var search = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search");
            if (active)
            {
                search.SetValue("AllowCortana", 0, RegistryValueKind.DWord);
                search.SetValue("AllowCloudSearch", 0, RegistryValueKind.DWord);
                search.SetValue("AllowCortanaAboveLock", 0, RegistryValueKind.DWord);
                search.SetValue("AllowSearchToUseLocation", 0, RegistryValueKind.DWord);
                search.SetValue("ConnectedSearchUseWeb", 0, RegistryValueKind.DWord);
                search.SetValue("DisableWebSearch", 0, RegistryValueKind.DWord);
            }
            else
            {
                search.DeleteValue("AllowCortana", false);
                search.DeleteValue("AllowCloudSearch", false);
                search.DeleteValue("AllowCortanaAboveLock", false);
                search.DeleteValue("AllowSearchToUseLocation", false);
                search.DeleteValue("ConnectedSearchUseWeb", false);
                search.DeleteValue("DisableWebSearch", false);
            }
            UpdateLabel(StatusBlockCortana, active);
            ShowInfo("Cortana", active ? "Cortana bloqueada completamente via política de grupo." : "Restaurado.");
        }

        private void ChkDisableActivityFeed_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableActivityFeed.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
            if (active)
                key.SetValue("EnableActivityFeed", 0, RegistryValueKind.DWord);
            else
                key.DeleteValue("EnableActivityFeed", false);
            UpdateLabel(StatusDisableActivityFeed, active);
            ShowInfo("Activity Feed", active ? "Activity Feed desativado (histórico não é mais coletado)." : "Restaurado.");
        }

        private void ChkDisableWindowsFeeds_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableWindowsFeeds.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds");
            if (active)
                key.SetValue("EnableFeeds", 0, RegistryValueKind.DWord);
            else
                key.DeleteValue("EnableFeeds", false);
            UpdateLabel(StatusDisableWindowsFeeds, active);
            ShowInfo("Feeds", active ? "Windows Feeds (News & Interests) desativado." : "Restaurado.");
        }

        private void ChkBlockAdvertisingId_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkBlockAdvertisingId.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Policies\Microsoft\Windows\AdvertisingInfo");
            if (active)
                key.SetValue("DisabledByGroupPolicy", 1, RegistryValueKind.DWord);
            else
                key.DeleteValue("DisabledByGroupPolicy", false);
            UpdateLabel(StatusBlockAdvertisingId, active);
            ShowInfo("Advertising ID", active ? "Advertising ID bloqueado por política de grupo." : "Restaurado.");
        }

        private void ChkDisableConsumerFeatures_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableConsumerFeatures.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent");
            if (active)
            {
                key.SetValue("DisableWindowsConsumerFeatures", 1, RegistryValueKind.DWord);
                key.SetValue("DisableThirdPartySuggestions", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue("DisableWindowsConsumerFeatures", false);
                key.DeleteValue("DisableThirdPartySuggestions", false);
            }
            UpdateLabel(StatusDisableConsumerFeatures, active);
            ShowInfo("Consumer Features", active ? "Sugestões e promoções da Microsoft bloqueadas." : "Restaurado.");
        }

        private void ChkDisableErrorReporting_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableErrorReporting.IsChecked == true;
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting");
            if (active)
            {
                key.SetValue("Disabled", 1, RegistryValueKind.DWord);
                key.SetValue("DoReport", 0, RegistryValueKind.DWord);
                key.SetValue("LoggingDisabled", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue("Disabled", false);
                key.DeleteValue("DoReport", false);
                key.DeleteValue("LoggingDisabled", false);
            }
            UpdateLabel(StatusDisableErrorReporting, active);
            ShowInfo("WER", active ? "Error Reporting desativado (relatórios não são enviados)." : "Restaurado.");
        }

        // --- REDE AVANÇADA ---

        private void ChkNetworkThrottlingIndex_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkNetworkThrottlingIndex.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
            if (active)
            {
                Registry.SetValue(path, "NetworkThrottlingIndex", -1, RegistryValueKind.DWord);
                Registry.SetValue(path, "SystemResponsiveness", 0, RegistryValueKind.DWord);
            }
            else
            {
                Registry.SetValue(path, "NetworkThrottlingIndex", 10, RegistryValueKind.DWord);
                Registry.SetValue(path, "SystemResponsiveness", 20, RegistryValueKind.DWord);
            }
            UpdateLabel(StatusNetworkThrottlingIndex, active);
            ShowInfo("Network Throttle", active ? "NetworkThrottlingIndex=0xFFFFFFFF (sem limitação) e SystemResponsiveness=0." : "Restaurado.");
        }

        // --- SERVIÇOS DO SISTEMA ---

        private void ChkDisableSpooler_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableSpooler.IsChecked == true;
            SetServiceStart("Spooler", active ? 4 : 3);
            UpdateLabel(StatusDisableSpooler, active);
            ShowInfo("Spooler", active ? "Print Spooler desativado (economiza RAM)." : "Restaurado.");
        }

        private void ChkDisableBluetooth_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableBluetooth.IsChecked == true;
            int val = active ? 4 : 3;
            SetServiceStart("BTAGService", val);
            SetServiceStart("bthserv", val);
            SetServiceStart("BthAvctpSvc", val);
            SetServiceStart("BluetoothUserService", val);
            UpdateLabel(StatusDisableBluetooth, active);
            ShowInfo("Bluetooth", active ? "Serviços Bluetooth desativados." : "Restaurado.");
        }

        private void ChkDisableMapsBroker_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableMapsBroker.IsChecked == true;
            SetServiceStart("MapsBroker", active ? 4 : 3);
            UpdateLabel(StatusDisableMapsBroker, active);
            ShowInfo("MapsBroker", active ? "MapsBroker desativado (mapas offline não baixam)." : "Restaurado.");
        }

        private void ChkDisableSysMain_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkDisableSysMain.IsChecked == true;
            SetServiceStart("SysMain", active ? 4 : 3);
            UpdateLabel(StatusDisableSysMain, active);
            ShowInfo("SysMain", active ? "SysMain (Superfetch) desativado (recomendado para SSD)." : "Restaurado.");
        }

        // --- PERIFÉRICOS ---

        private void ChkKeyboardDataQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkKeyboardDataQueue.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters";
            if (active)
                Registry.SetValue(path, "KeyboardDataQueueSize", 100, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, true);
                key?.DeleteValue("KeyboardDataQueueSize", false);
            }
            UpdateLabel(StatusKeyboardDataQueue, active);
            ShowInfo("Keyboard Queue", active ? "Fila de dados do teclado=100 (menos input loss)." : "Restaurado.");
        }

        private void ChkMouseDataQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool active = ChkMouseDataQueue.IsChecked == true;
            var path = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters";
            if (active)
                Registry.SetValue(path, "MouseDataQueueSize", 100, RegistryValueKind.DWord);
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, true);
                key?.DeleteValue("MouseDataQueueSize", false);
            }
            UpdateLabel(StatusMouseDataQueue, active);
            ShowInfo("Mouse Queue", active ? "Fila de dados do mouse=100 (maior precisão)." : "Restaurado.");
        }

        // --- HELPERS ---

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, true); } catch { }
        }

        private static void DeleteFilesInDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                TryDeleteFile(file);
            foreach (var sub in Directory.GetDirectories(dir))
                TryDeleteDirectory(sub);
        }

    }
}
