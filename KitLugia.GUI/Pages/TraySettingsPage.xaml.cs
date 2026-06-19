using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.GUI.Services;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Linq;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Cursors = System.Windows.Input.Cursors;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace KitLugia.GUI.Pages
{
    public partial class TraySettingsPage : Page
    {
        private DispatcherTimer? _refreshTimer;
        private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "KitLugia";

        private bool _isCleaningNow;
        private bool _isRemovingTurboApp;
        private bool _isRestoringTurboApp;
        private KitLugia.GUI.Controls.ProcessPickerOverlay? _currentPicker;

        public TraySettingsPage()
        {
            InitializeComponent();
            LoadSettings();
            LoadProcessLimits();
            StartRamRefresh();


            this.Unloaded += TraySettingsPage_Unloaded;
        }


        public void Cleanup()
        {
            // Para o timer de refresh
            if (_refreshTimer != null)
            {
                _refreshTimer.Tick -= OnRefreshTimerTick;
                _refreshTimer.Stop();
            }
            _refreshTimer = null;
            this.Unloaded -= TraySettingsPage_Unloaded;


            this.DataContext = null;

            if (_currentPicker != null)
            {
                _currentPicker.ProcessSelected -= OnPickerProcessSelected;
                _currentPicker.OverlayClosed -= OnPickerOverlayClosed;
            }
        }

        private void TraySettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void LoadSettings()
        {
            var tray = GetTrayService();
            if (tray == null) return;

            ChkEnableTray.IsChecked = tray.IsTrayEnabled;
            ChkAutoClean.IsChecked = tray.AutoCleanEnabled;
            SliderThreshold.Value = tray.AutoCleanThresholdPercent;
            SliderInterval.Value = tray.MonitorIntervalSeconds;
            TxtThreshold.Text = $"{tray.AutoCleanThresholdPercent}%";
            TxtInterval.Text = FormatInterval((int)SliderInterval.Value);

            // Select active mode
            switch (tray.SelectedCleaningMode)
            {
                case MemoryOptimizer.CleaningMode.Leve: ModeLeve.IsChecked = true; break;
                case MemoryOptimizer.CleaningMode.Normal: ModeNormal.IsChecked = true; break;
                case MemoryOptimizer.CleaningMode.Alta: ModeAlta.IsChecked = true; break;
                case MemoryOptimizer.CleaningMode.Bruta: ModeBruta.IsChecked = true; break;
            }

            // Background Features
            ChkStandbyClean.IsChecked = tray.StandbyCleanEnabled;
            ChkTurboBoot.IsChecked = tray.TurboBootEnabled;
            ChkTurboShutdown.IsChecked = tray.TurboShutdownEnabled;
            ChkTrayIcon.IsChecked = tray.IsTrayEnabled;
            ChkCloseToTray.IsChecked = tray.CloseToTray;

            // Auto-Start - usa novo método que verifica o caminho
            try
            {
                ChkAutoStart.IsChecked = TrayIconService.IsAutoStartEnabled();
            }
            catch
            {
                ChkAutoStart.IsChecked = false;
            }

            LoadTurboApps();
        }

        private void LoadTurboApps()
        {
            try
            {
                var apps = StartupManager.GetStartupAppsWithDetails(true)
                    .Where(a => a.Location == "Turbo Boot (KitLugia)")
                    .ToList();

                ListTurboApps.ItemsSource = apps;
                TxtEmptyTurbo.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void OnRefreshTimerTick(object? s, EventArgs e)
        {
            RefreshRamDisplay();
            RefreshProcessLimitsStatus();
        }

        private void StartRamRefresh()
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
            RefreshRamDisplay();
        }

        /// <summary>
        /// Atualiza apenas os valores de RAM atual na lista de limites — sem recriar os rows.
        /// Chamado a cada 2s pelo timer para manter os números atualizados.
        /// </summary>
        private void RefreshProcessLimitsStatus()
        {
            var tray = GetTrayService();
            if (tray == null) return;

            var limits = tray.GetProcessRamLimits().ToList();
            if (limits.Count == 0) return;

            foreach (Border row in ProcessLimitsPanel.Children.OfType<Border>())
            {
                if (row.Child is not Grid grid) continue;
                if (grid.Children.Count == 0) continue;
                if (grid.Children[0] is not StackPanel nameStack) continue;
                if (nameStack.Children.Count < 2) continue;
                if (nameStack.Children[0] is not TextBlock nameTb) continue;

                // O DisplayName pode ser capitalizado — converte para lowercase para buscar
                string processName = nameTb.Text.ToLowerInvariant();
                var limit = limits.FirstOrDefault(l =>
                    l.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
                if (limit == null) continue;

                if (nameStack.Children[1] is not TextBlock statusTb) continue;

                bool exceeded = limit.LastKnownMB > limit.LimitMB;
                statusTb.Text = limit.LastKnownMB > 0
                    ? $"Atual: {limit.LastKnownMB} MB" +
                      $"{(exceeded ? " ⚠️ excedido" : " ✓")}" +
                      $"{(limit.IsForeground ? " 🎯 em foco (pausado)" : "")}"
                    : "Processo não está rodando";

                statusTb.Foreground = exceeded
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(255, 85, 85))
                    : limit.IsForeground
                        ? new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(255, 215, 0))
                        : new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(102, 102, 102));
            }
        }

        private void RefreshRamDisplay()
        {
            try
            {
                var mem = GetMemoryStats();
                TxtPercentMain.Text = $"{mem.Percent}%";
                TxtTotalRam.Text = $"{mem.TotalGB:F1} GB";
                TxtUsedRam.Text = $"{mem.UsedGB:F1} GB";
                TxtFreeRam.Text = $"{mem.FreeGB:F1} GB";

                // Color coding
                if (mem.Percent >= 90) TxtPercentMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 53, 69));
                else if (mem.Percent >= 70) TxtPercentMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7));
                else TxtPercentMain.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));

                // Label
                if (mem.Percent >= 90) TxtStatusText.Text = "ESTADO: CRÍTICO";
                else if (mem.Percent >= 70) TxtStatusText.Text = "ESTADO: ALTO";
                else TxtStatusText.Text = "ESTADO: NORMAL";

                TxtStatusText.Foreground = TxtPercentMain.Foreground;
            }
            catch { }
        }

        // --- EVENT HANDLERS ---

        private async void BtnCleanNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaningNow) return;
            _isCleaningNow = true;
            try
            {
                var tray = GetTrayService();
                if (tray == null) return;

                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Limpando RAM ({tray.SelectedCleaningMode})", "TraySettings");

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    await mw.ExecuteWithLoadingAsync($"Limpando RAM (Modo: {tray.SelectedCleaningMode})...", () =>
                    {
                        var memBefore = GetMemoryStats();
                        var result = MemoryOptimizer.Optimize(tray.SelectedCleaningMode);
                        var memAfter = GetMemoryStats();

                        int freedPercent = memBefore.Percent - memAfter.Percent;
                        double freedGB = memAfter.FreeGB - memBefore.FreeGB;

                        // Atualizar UI na thread principal
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            TxtFreedLast.Text = $"Última limpeza: {DateTime.Now:HH:mm} ({(freedGB > 0 ? freedGB : 0):F2} GB)";
                            RefreshRamDisplay();
                            mw.ShowSuccess("Otimizado", result.Message);
                        });

                        return result;
                    });

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Limpeza de RAM concluída");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BtnCleanNow_Click] Error: {ex.Message}");
            }
            finally
            {
                _isCleaningNow = false;
            }
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray == null || !(sender is System.Windows.Controls.RadioButton rb)) return;

            // Usa o Name do RadioButton para determinar o modo
            string modeStr = "Normal";
            if (rb.Name == "ModeLeve") modeStr = "Leve";
            else if (rb.Name == "ModeNormal") modeStr = "Normal";
            else if (rb.Name == "ModeAlta") modeStr = "Alta";
            else if (rb.Name == "ModeBruta") modeStr = "Bruta";

            Enum.TryParse(modeStr, out MemoryOptimizer.CleaningMode mode);
            tray.SelectedCleaningMode = mode;
            tray.SaveSettings();
        }

        private void ChkAutoClean_Click(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.AutoCleanEnabled = ChkAutoClean.IsChecked == true;
                tray.SaveSettings();
            }
        }

        private void ChkEnableTray_Click(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.SetTrayEnabled(ChkEnableTray.IsChecked == true);
                tray.SaveSettings();
            }
        }

        private void ChkBackgroundFeature_Click(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray == null || !(sender is System.Windows.Controls.CheckBox cb)) return;

            if (cb == ChkStandbyClean) tray.StandbyCleanEnabled = cb.IsChecked == true;
            else if (cb == ChkTurboBoot) tray.TurboBootEnabled = cb.IsChecked == true;
            else if (cb == ChkTurboShutdown) tray.TurboShutdownEnabled = cb.IsChecked == true;
            else if (cb == ChkTrayIcon) tray.IsTrayEnabled = cb.IsChecked == true;
            else if (cb == ChkCloseToTray) tray.CloseToTray = cb.IsChecked == true;

            tray.SaveSettings();
        }

        private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool enable = ChkAutoStart.IsChecked == true;
                

                TrayIconService.SetAutoStart(enable);
                
                // Verificar se funcionou atualizando o estado
                ChkAutoStart.IsChecked = TrayIconService.IsAutoStartEnabled();
            }
            catch { }
        }

        private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtThreshold == null) return;
            int val = (int)SliderThreshold.Value;
            TxtThreshold.Text = $"{val}%";
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.AutoCleanThresholdPercent = val;
                tray.SaveSettings();
            }
        }

        private void SliderInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtInterval == null) return;
            int val = (int)SliderInterval.Value;
            TxtInterval.Text = FormatInterval(val);
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.MonitorIntervalSeconds = val;
                tray.SaveSettings();
            }
        }

        private async void BtnRemoveTurboApp_Click(object sender, RoutedEventArgs e)
        {
            if (_isRemovingTurboApp) return;
            _isRemovingTurboApp = true;
            try
            {
                if (sender is System.Windows.Controls.Button btn && btn.Tag is string appName && Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog($"Remover '{appName}' do Turbo Boot?")) return;

                    var result = StartupManager.RemoveFromKitLugia(appName);
                    if (result.Success)
                    {
                        mw.ShowSuccess("TURBO BOOT", result.Message);
                        LoadTurboApps();
                    }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BtnRemoveTurboApp_Click] Error: {ex.Message}");
            }
            finally
            {
                _isRemovingTurboApp = false;
            }
        }

        private void BtnConfigureAutoClean_Click(object sender, RoutedEventArgs e)
        {
            // 🖱️ Abrir página de configurações avançada de limpeza automática via NavigateToPage
            if (Application.Current.MainWindow is MainWindow mw)
            {
                var advancedPage = new AdvancedRamCleanSettingsPage();
                mw.NavigateToPage("AdvancedRamCleanSettings");
            }
        }

        private async void BtnRestoreTurboApp_Click(object sender, RoutedEventArgs e)
        {
            if (_isRestoringTurboApp) return;
            _isRestoringTurboApp = true;
            try
            {
                if (sender is System.Windows.Controls.Button btn && btn.Tag is string appName && Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog($"Restaurar '{appName}' para a inicialização padrão?")) return;

                    var result = await Task.Run(() => StartupManager.RestoreToNormal(appName));
                    if (result.Success)
                    {
                        mw.ShowSuccess("RESTAURAR", result.Message);
                        LoadTurboApps();
                    }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BtnRestoreTurboApp_Click] Error: {ex.Message}");
            }
            finally
            {
                _isRestoringTurboApp = false;
            }
        }

        // --- HELPERS ---

        private static TrayIconService? GetTrayService()
        {
            if (Application.Current.MainWindow is MainWindow mw)
                return mw.TrayService;
            return null;
        }

        private static string FormatInterval(int seconds)
        {
            if (seconds < 60) return $"{seconds}s";
            return $"{seconds / 60}m {seconds % 60}s";
        }

        private struct MemoryInfo
        {
            public int Percent;
            public double TotalGB;
            public double UsedGB;
            public double FreeGB;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private MemoryInfo GetMemoryStats()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(m);
            GlobalMemoryStatusEx(ref m);

            return new MemoryInfo
            {
                Percent = (int)m.dwMemoryLoad,
                TotalGB = m.ullTotalPhys / (1024.0 * 1024.0 * 1024.0),
                FreeGB = m.ullAvailPhys / (1024.0 * 1024.0 * 1024.0),
                UsedGB = (m.ullTotalPhys - m.ullAvailPhys) / (1024.0 * 1024.0 * 1024.0)
            };
        }

        // ═══════════════════════════════════════════════════════════════
        // LIMITADOR DE RAM POR PROCESSO
        // ═══════════════════════════════════════════════════════════════

        private void LoadProcessLimits()
        {
            var tray = GetTrayService();
            if (tray == null) return;

            // Carrega o intervalo configurado
            if (TxtRamLimiterInterval != null)
                TxtRamLimiterInterval.Text = tray.RamLimiterIntervalMs.ToString();

            var limits = tray.GetProcessRamLimits().ToList();

            ProcessLimitsPanel.Children.Clear();
            TxtNoLimits.Visibility = limits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var limit in limits)
                ProcessLimitsPanel.Children.Add(BuildLimitRow(limit));
        }

        private void TxtRamLimiterInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray == null) return;

            if (int.TryParse(TxtRamLimiterInterval.Text?.Trim(), out int ms) && ms >= 500 && ms <= 30000)
            {
                tray.RamLimiterIntervalMs = ms;
                tray.SaveSettings();
                ShowNotification("✅ Intervalo atualizado", $"RAM Limiter verificará a cada {ms}ms");
            }
            else
            {
                // Valor inválido — restaura o atual
                TxtRamLimiterInterval.Text = tray.RamLimiterIntervalMs.ToString();
                ShowNotification("⚠️ Valor inválido", "Digite entre 500 e 30000 ms");
            }
        }

        private Border BuildLimitRow(TrayIconService.ProcessRamLimit limit)
        {
            var row = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(26, 26, 26)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Nome + status
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock
            {
                Text = char.ToUpper(limit.ProcessName[0]) + limit.ProcessName.Substring(1),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            });

            string statusText = limit.LastKnownMB > 0
                ? $"Atual: {limit.LastKnownMB} MB" +
                  $"{(limit.LastKnownMB > limit.LimitMB ? " ⚠️ excedido" : " ✓")}" +
                  $"{(limit.IsForeground ? " 🎯 em foco (pausado)" : "")}"
                : "Processo não está rodando";

            var statusColor = limit.LastKnownMB > limit.LimitMB
                ? System.Windows.Media.Color.FromRgb(255, 85, 85)
                : limit.IsForeground
                    ? System.Windows.Media.Color.FromRgb(255, 215, 0)
                    : System.Windows.Media.Color.FromRgb(102, 102, 102);

            nameStack.Children.Add(new TextBlock
            {
                Text = statusText,
                Foreground = new System.Windows.Media.SolidColorBrush(statusColor),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(nameStack, 0);
            grid.Children.Add(nameStack);

            // Limite editável inline
            var limitBox = new TextBox
            {
                Text = limit.LimitMB.ToString(),
                Width = 60,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(37, 37, 37)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 215, 0)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(68, 68, 68)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 3, 5, 3),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Tag = limit.ProcessName
            };
            limitBox.LostFocus += LimitBox_LostFocus;
            Grid.SetColumn(limitBox, 1);
            grid.Children.Add(limitBox);

            grid.Children.Add(new TextBlock
            {
                Text = "MB",
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(102, 102, 102)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            Grid.SetColumn(grid.Children[^1], 2);

            // Toggle on/off
            var toggle = new CheckBox
            {
                IsChecked = limit.Enabled,
                Style = (Style)FindResource("TrayToggle"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = limit.ProcessName
            };
            toggle.Click += ChkProcessLimitEnabled_Click;
            Grid.SetColumn(toggle, 3);
            grid.Children.Add(toggle);

            // Botão remover
            var removeBtn = new Button
            {
                Content = "✕",
                Width = 24,
                Height = 24,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(211, 47, 47)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = limit.ProcessName,
                VerticalAlignment = VerticalAlignment.Center
            };
            removeBtn.Click += BtnRemoveProcessLimit_Click;
            Grid.SetColumn(removeBtn, 4);
            grid.Children.Add(removeBtn);

            row.Child = grid;
            return row;
        }

        private void LimitBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box && box.Tag is string processName)
            {
                var tray = GetTrayService();
                if (long.TryParse(box.Text, out long newLimit) && newLimit >= 50)
                {
                    var existing = tray?.GetProcessRamLimits().FirstOrDefault(l => l.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
                    tray?.SetProcessRamLimit(processName, newLimit, existing?.Enabled ?? true);
                }
                else
                {
                    var existing = tray?.GetProcessRamLimits().FirstOrDefault(l => l.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
                    box.Text = existing?.LimitMB.ToString() ?? "500";
                }
            }
        }

        // Abre o seletor de processos — centralizado na tela via MainWindow OverlayContainer
        private void BtnOpenProcessPicker_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            var overlayContainer = mw.FindName("OverlayContainer") as System.Windows.Controls.Grid;
            if (overlayContainer == null) return;

            // Remove overlay anterior se existir
            var existing = overlayContainer.Children.OfType<KitLugia.GUI.Controls.ProcessPickerOverlay>().FirstOrDefault();
            if (existing != null)
                overlayContainer.Children.Remove(existing);

            var picker = new KitLugia.GUI.Controls.ProcessPickerOverlay();
            picker.ProcessSelected += OnPickerProcessSelected;
            picker.OverlayClosed += OnPickerOverlayClosed;
            _currentPicker = picker;

            overlayContainer.Children.Add(picker);
            overlayContainer.Visibility = Visibility.Visible;
            picker.Open();
        }

        private void OnPickerProcessSelected(string name, long limitMB)
        {
            if (_currentPicker == null) return;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;
            var overlayContainer = mw.FindName("OverlayContainer") as System.Windows.Controls.Grid;
            if (overlayContainer != null)
            {
                overlayContainer.Children.Remove(_currentPicker);
                overlayContainer.Visibility = Visibility.Collapsed;
            }
            OnProcessSelected(name, limitMB);
            _currentPicker = null;
        }

        private void OnPickerOverlayClosed()
        {
            if (_currentPicker == null) return;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;
            var overlayContainer = mw.FindName("OverlayContainer") as System.Windows.Controls.Grid;
            if (overlayContainer != null)
            {
                overlayContainer.Children.Remove(_currentPicker);
                overlayContainer.Visibility = Visibility.Collapsed;
            }
            _currentPicker = null;
        }

        // Adicionar por arquivo .exe diretamente (sem abrir o picker)
        private void BtnAddByFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecione o executável do processo",
                Filter = "Executáveis (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() != true) return;

            string processName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)
                .ToLowerInvariant();

            var tray = GetTrayService();
            if (tray == null) return;

            tray.SetProcessRamLimit(processName, 500);
            LoadProcessLimits();
            ShowNotification($"✅ Processo adicionado", $"{processName} → 500 MB (ajuste conforme necessário)");
        }

        private void OnProcessSelected(string processName, long limitMB)
        {
            var tray = GetTrayService();
            if (tray == null) return;

            tray.SetProcessRamLimit(processName, limitMB);
            LoadProcessLimits();
            ShowNotification($"✅ Limite configurado", $"{processName} → {limitMB} MB");
        }

        private void BtnAddProcessLimit_Click(object sender, RoutedEventArgs e)
        {
            // Mantido para compatibilidade — abre o picker
            BtnOpenProcessPicker_Click(sender, e);
        }

        private void BtnRemoveProcessLimit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? name = btn.Tag?.ToString();
            if (string.IsNullOrEmpty(name)) return;

            var tray = GetTrayService();
            tray?.RemoveProcessRamLimit(name);
            LoadProcessLimits();
        }

        private void ChkProcessLimitEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox chk) return;
            string? name = chk.Tag?.ToString();
            if (string.IsNullOrEmpty(name)) return;

            var tray = GetTrayService();
            if (tray == null) return;

            var limits = tray.GetProcessRamLimits();
            var limit = limits.FirstOrDefault(l =>
                l.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (limit != null)
                tray.SetProcessRamLimit(name, limit.LimitMB, chk.IsChecked == true);
        }

        private void ShowNotification(string title, string message)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo(title, message);
        }
    }

    /// <summary>
    /// ViewModel para exibir limites de RAM na lista.
    /// </summary>
    public class ProcessLimitViewModel
    {
        private readonly TrayIconService.ProcessRamLimit _limit;

        public ProcessLimitViewModel(TrayIconService.ProcessRamLimit limit)
        {
            _limit = limit;
        }

        public string ProcessName => _limit.ProcessName;
        public bool Enabled => _limit.Enabled;
        public string LimitText => $"{_limit.LimitMB} MB";
        public string StatusText => _limit.LastKnownMB > 0
            ? $"Atual: {_limit.LastKnownMB} MB{(_limit.LastKnownMB > _limit.LimitMB ? " ⚠️ excedido" : "")}"
            : "Aguardando monitoramento...";
        public string StatusColor => _limit.LastKnownMB > _limit.LimitMB ? "#FF5555" : "#888888";
    }
}
