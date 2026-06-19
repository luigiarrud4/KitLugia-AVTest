using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.GUI.Services;

using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class OptimizationPage : Page
    {
        private bool _isLoading = true;
        private bool _isOptimizationOperation;

        public OptimizationPage()
        {
            InitializeComponent();
            this.Unloaded += OptimizationPage_Unloaded;
            this.Loaded += OptimizationPage_Loaded;
        }

        public void Cleanup()
        {
            this.Unloaded -= OptimizationPage_Unloaded;
            this.Loaded -= OptimizationPage_Loaded;
            this.DataContext = null;
        }

        private void OptimizationPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        private async void OptimizationPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                await LoadStateAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("OptimizationPage_Loaded", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        private async Task LoadStateAsync()
        {
            _isLoading = true;

            await Task.Run(() =>
            {
                // RAM
                var memInfo = MemoryOptimizer.GetMemoryStats();
                double ramPercent = memInfo.Percent;
                double ramUsedGB = memInfo.UsedGB;

                // CPU
                double cpuLoad = GetCpuLoad();

                // Uptime
                TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

                // Auto-clean settings
                var trayService = GetTrayService();
                bool autoCleanEnabled = trayService?.AutoCleanEnabled ?? false;
                int threshold = trayService?.AutoCleanThresholdPercent ?? 80;
                int interval = trayService?.MonitorIntervalSeconds ?? 30;

                // Power plans
                var plans = Toolbox.GetAllPowerPlans();
                var activePlan = Toolbox.GetActivePowerPlan();

                // Startup
                bool startWithWindows = TrayIconService.IsAutoStartEnabled();
                bool turboBoot = SystemTweaks.IsTurboBootEnabled();
                bool turboShutdown = SystemTweaks.IsFastShutdownEnabled();

                Dispatcher.Invoke(() =>
                {
                    // Status cards
                    TxtRamUsage.Text = $"{ramPercent:F0}%";
                    TxtCpuUsage.Text = $"{cpuLoad:F0}%";
                    TxtAutoCleanStatus.Text = autoCleanEnabled ? "ON" : "OFF";
                    TxtUptime.Text = uptime.TotalDays >= 1
                        ? $"{(int)uptime.TotalDays}d {uptime.Hours}h"
                        : $"{uptime.Hours}h {uptime.Minutes}m";

                    // Auto-clean
                    ChkAutoClean.IsChecked = autoCleanEnabled;
                    SliderThreshold.Value = threshold;
                    TxtThresholdValue.Text = $"{threshold}%";
                    SliderInterval.Value = interval;
                    TxtIntervalValue.Text = $"{interval} segundos";

                    // Power plans
                    CmbPowerPlan.Items.Clear();
                    foreach (var plan in plans)
                    {
                        var item = new ComboBoxItem
                        {
                            Content = plan.Name,
                            Tag = plan.Guid
                        };
                        CmbPowerPlan.Items.Add(item);
                        if (plan.Guid.Equals(activePlan.Guid, StringComparison.OrdinalIgnoreCase))
                            CmbPowerPlan.SelectedItem = item;
                    }

                    // Startup
                    ChkStartWithWindows.IsChecked = startWithWindows;
                    ChkTurboBoot.IsChecked = turboBoot;
                    ChkTurboShutdown.IsChecked = turboShutdown;

                    _isLoading = false;
                });
            });
        }

        // "?"?"🧹 Auto-Clean "?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?

        private void ChkAutoClean_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkAutoClean.IsChecked == true;
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.AutoCleanEnabled = enable;
                tray.SaveSettings();
                TxtAutoCleanStatus.Text = enable ? "ON" : "OFF";
            }
        }

        private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading || TxtThresholdValue == null) return;
            int value = (int)e.NewValue;
            TxtThresholdValue.Text = $"{value}%";
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.AutoCleanThresholdPercent = value;
                tray.SaveSettings();
            }
        }

        private void SliderInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isLoading || TxtIntervalValue == null) return;
            int value = (int)e.NewValue;
            TxtIntervalValue.Text = $"{value} segundos";
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.MonitorIntervalSeconds = value;
                tray.SaveSettings();
            }
        }

        // "?"?"⚡ Power Plans "?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?

        private void CmbPowerPlan_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (CmbPowerPlan.SelectedItem is not ComboBoxItem item) return;
            string? guid = item.Tag?.ToString();
            if (string.IsNullOrEmpty(guid)) return;

            _ = Task.Run(() => Toolbox.SetActivePowerPlan(guid));
            ShowResult($"✅. Plano de energia alterado para: {item.Content}");
        }

        private void BtnRefreshPowerPlans_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadStateAsync();
        }

        private async void BtnUltimatePower_Click(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                var result = await Task.Run(() => Toolbox.UnlockAndActivateUltimatePerformance());
                ShowResult(result.Success ? $"✅. {result.Message}" : $"✅ {result.Message}");
                if (result.Success) _ = LoadStateAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnUltimatePower_Click", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        // "?"?"🚀 Startup "?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?

        private async void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                if (_isLoading) return;
                bool enable = ChkStartWithWindows.IsChecked == true;
                ChkStartWithWindows.IsEnabled = false;

                await Task.Run(() => TrayIconService.SetAutoStart(enable));

                // Verifica estado real
                bool actual = TrayIconService.IsAutoStartEnabled();
                ChkStartWithWindows.IsChecked = actual;
                ChkStartWithWindows.IsEnabled = true;
                ShowResult(actual ? "✅. Inicialização com Windows ativada." : "❌️ Inicialização com Windows desativada.");
            }
            catch (Exception ex)
            {
                Logger.LogError("ChkStartWithWindows_Click", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        private void ChkTurboBoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            bool enable = ChkTurboBoot.IsChecked == true;
            SystemTweaks.ToggleTurboBoot(enable);
            var tray = GetTrayService();
            if (tray != null) { tray.TurboBootEnabled = enable; tray.SaveSettings(); }
            ShowResult(enable ? "✅. Turbo Boot ativado." : "❌️ Turbo Boot desativado.");
        }

        private void ChkTurboShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            SystemTweaks.ToggleFastShutdown();
            bool actual = SystemTweaks.IsFastShutdownEnabled();
            ChkTurboShutdown.IsChecked = actual;
            var tray = GetTrayService();
            if (tray != null) { tray.TurboShutdownEnabled = actual; tray.SaveSettings(); }
            ShowResult(actual ? "✅. Turbo Shutdown ativado." : "❌️ Turbo Shutdown desativado.");
        }

        // "?"?"🔧 Tarefas de Manutenção "?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?

        private async void BtnQuickCleanTemp_Click(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                ShowResult("Y Limpando arquivos temporários...");
                var result = await Task.Run(() => Toolbox.CleanTemporaryFiles());
                ShowResult($"✅. Temporários: {result.TotalBytesFreed / 1024 / 1024:N1} MB liberados.");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnQuickCleanTemp_Click", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        private async void BtnOptimizeRam_Click(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                ShowResult("Y' Otimizando RAM...");
                await Task.Run(() => MemoryOptimizer.Optimize(MemoryOptimizer.CleaningMode.Normal));
                var memInfo = MemoryOptimizer.GetMemoryStats();
                ShowResult($"✅. RAM otimizada. Uso atual: {memInfo.Percent:F0}% ({memInfo.UsedGB:F1} GB).");
                TxtRamUsage.Text = $"{memInfo.Percent:F0}%";
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnOptimizeRam_Click", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        private void BtnCheckDisk_Click(object sender, RoutedEventArgs e)
        {
            ShowResult("Agendando verificação de disco para próxima reinicialização...");
            _ = Task.Run(() => Toolbox.ScheduleDiskCheck("C:"));
            ShowResult("✅. CHKDSK agendado para C: na próxima reinicialização.");
        }

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            if (_isOptimizationOperation) return;
            _isOptimizationOperation = true;
            try
            {
                ShowResult("YO Limpando cache DNS...");
                var result = await Task.Run(() => Toolbox.FlushDnsCache());
                ShowResult($"✅. {result.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnFlushDns_Click", ex.Message);
            }
            finally
            {
                _isOptimizationOperation = false;
            }
        }

        // "?"?"🛠️ Helpers "?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?"?

        private static TrayIconService? GetTrayService()
        {
            if (Application.Current.MainWindow is MainWindow mw)
                return mw.TrayService;
            return null;
        }

        private static double GetCpuLoad()
        {
            try
            {
                using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                counter.NextValue(); // Primeira leitura sempre retorna 0
                System.Threading.Thread.Sleep(100);
                return counter.NextValue();
            }
            catch { return 0; }
        }

        private void ShowResult(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtActionResult.Text = message;
            });
        }
    }
}
