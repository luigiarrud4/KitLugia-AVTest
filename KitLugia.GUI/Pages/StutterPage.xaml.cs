using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.Core;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MediaColor = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    public partial class StutterPage : Page
    {
        private StutterDetector? _detector;
        private DispatcherTimer? _uiTimer;

        public StutterPage()
        {
            InitializeComponent();
            this.Unloaded += StutterPage_Unloaded;
        }

        public void Cleanup()
        {
            _uiTimer?.Stop();
            _uiTimer = null;
            _detector?.Stop();
            _detector?.Dispose();
            _detector = null;
            this.Unloaded -= StutterPage_Unloaded;
            this.DataContext = null;
        }

        private void StutterPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void StutterPage_Loaded(object sender, RoutedEventArgs e)
        {
            _detector = new StutterDetector();
            _detector.StutterDetected += OnStutterDetected;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (_detector == null || !_detector.IsRunning) return;

            TxtCurrentLatency.Text = _detector.CurrentLatencyUs switch
            {
                < 100 => $"{_detector.CurrentLatencyUs:F0}",
                < 1000 => $"{_detector.CurrentLatencyUs:F0}",
                _ => $"{_detector.CurrentLatencyUs / 1000.0:F1}k"
            };

            TxtMaxLatency.Text = _detector.MaxLatencyUs switch
            {
                < 1000 => $"{_detector.MaxLatencyUs:F0}",
                _ => $"{_detector.MaxLatencyUs / 1000.0:F1}k"
            };

            TxtAvgLatency.Text = _detector.AverageLatencyUs switch
            {
                < 1000 => $"{_detector.AverageLatencyUs:F0}",
                _ => $"{_detector.AverageLatencyUs / 1000.0:F1}k"
            };

            TxtStuttersTotal.Text = _detector.TotalStutters.ToString();
            TxtStuttersMin.Text = _detector.StuttersPerMinute.ToString("F1");
            TxtTotalSamples.Text = _detector.TotalSamples > 1000
                ? $"{_detector.TotalSamples / 1000.0:F1}k"
                : _detector.TotalSamples.ToString();
            TxtDpcPercent.Text = _detector.DpcPercent.ToString("F1");
            TxtUptime.Text = $"⏱ {_detector.Uptime:hh\\:mm\\:ss}";
            UpdateMonitoredProcessStats();
            UpdateAudioStatus();

            double current = _detector.CurrentLatencyUs;
            var color = current switch
            {
                < 500 => MediaColor.FromRgb(136, 204, 136),
                < 2000 => MediaColor.FromRgb(255, 204, 68),
                < 10000 => MediaColor.FromRgb(255, 102, 68),
                _ => MediaColor.FromRgb(255, 34, 34)
            };
            TxtCurrentLatency.Foreground = new SolidColorBrush(color);
        }

        private void OnStutterDetected(StutterEvent stutter)
        {
            Dispatcher.Invoke(() =>
            {
                LstStutters.Items.Insert(0, stutter);
                TxtEmptyState.Visibility = Visibility.Collapsed;

                while (LstStutters.Items.Count > 500)
                    LstStutters.Items.RemoveAt(LstStutters.Items.Count - 1);
            });
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_detector == null) return;

            try
            {
                LstStutters.Items.Clear();
                TxtEmptyState.Visibility = Visibility.Visible;
                _detector.ClearHistory();
                _detector.Start();

                BtnStart.IsEnabled = false;
                BtnStop.IsEnabled = true;
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(68, 255, 68));
                TxtStatus.Text = "Monitorando...";
                TxtStatus.Foreground = new SolidColorBrush(MediaColor.FromRgb(136, 255, 136));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao iniciar: {ex.Message}", "Stutter Detector",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _detector?.Stop();
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(102, 102, 102));
            TxtStatus.Text = "Parado";
            TxtStatus.Foreground = new SolidColorBrush(MediaColor.FromRgb(136, 136, 136));
        }

        private void BtnMonitorProcess_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtProcessName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                ShowToast("Digite o nome do processo para monitorar.");
                return;
            }

            if (_detector == null) return;

            try
            {
                _detector.SetMonitoredProcess(name);
                BtnMonitorProcess.IsEnabled = false;
                BtnStopMonitor.IsEnabled = true;
                PanelMonitoredStats.Visibility = Visibility.Visible;
                TxtProcessName.IsEnabled = false;
                ShowToast($"Monitorando processo: {name}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro: {ex.Message}", "Stutter Detector",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnStopMonitor_Click(object sender, RoutedEventArgs e)
        {
            _detector?.ClearMonitoredProcess();
            BtnMonitorProcess.IsEnabled = true;
            BtnStopMonitor.IsEnabled = false;
            PanelMonitoredStats.Visibility = Visibility.Collapsed;
            TxtProcessName.IsEnabled = true;
            ShowToast("Monitoramento de processo interrompido.");
        }

        private void UpdateMonitoredProcessStats()
        {
            if (_detector == null) return;
            string monName = _detector.MonitoredProcessName;
            if (string.IsNullOrEmpty(monName) || PanelMonitoredStats.Visibility != Visibility.Visible) return;

            TxtMonProcCpu.Text = $"{_detector.MonitoredProcessCpu:F1}%";
            TxtMonProcMem.Text = $"{_detector.MonitoredProcessMb:F1} MB";
            TxtMonProcThreads.Text = _detector.MonitoredProcessThreads.ToString();
        }

        private void UpdateAudioStatus()
        {
            if (_detector == null) return;
            bool audioRunning = _detector.AudiodgRunning;
            PanelAudioStatus.Visibility = audioRunning ? Visibility.Visible : Visibility.Collapsed;
            if (audioRunning)
                TxtAudiodgCpu.Text = $"audiodg.exe  {_detector.AudiodgCpu:F1}% CPU";
        }

        private void BtnRecommend_Click(object sender, RoutedEventArgs e)
        {
            if (_detector == null) return;

            try
            {
                string[] recs = _detector.GetRecommendations();
                RecList.ItemsSource = recs;
                PanelRecommendations.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao gerar recomendacoes: {ex.Message}", "Stutter Detector",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string log = _detector?.ExportLog() ?? "Nenhum dado coletado.";
                Clipboard.SetText(log);
                ShowToast("✅ Log copiado para a área de transferência");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao copiar: {ex.Message}", "Stutter Detector",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            LstStutters.Items.Clear();
            TxtEmptyState.Visibility = Visibility.Visible;
            _detector?.ClearHistory();
        }

        private bool _autoTestRunning;
        private record TestPhase(double AvgLatencyUs, long Stutters, long Samples, string Label);

        private async void BtnAutoTest_Click(object sender, RoutedEventArgs e)
        {
            if (_detector == null || _autoTestRunning) return;

            if (!_detector.IsRunning)
            {
                ShowToast("Inicie o monitoramento antes do teste.");
                return;
            }

            _autoTestRunning = true;
            BtnAutoTest.IsEnabled = false;
            BtnAutoTest.Content = "⏳ Testando...";
            PanelRecommendations.Visibility = Visibility.Visible;

            try
            {
                var baseline = TakeSnapshot();
                FlipGameBoost(false);
                await Task.Delay(65_000);
                var semGb = TakeSnapshot();
                FlipGameBoost(true);
                await Task.Delay(65_000);
                var restaurado = TakeSnapshot();

                var lines = new[]
                {
                    "══════════ TESTE A/B — GAMEBOOST ══════════",
                    "",
                    $"📊 Baseline (GameBoost ON):",
                    $"   Média: {baseline.AvgLatencyUs:F0}µs | Stutters: {baseline.Stutters} | Amostras: {baseline.Samples}",
                    "",
                    $"📉 Sem GameBoost (65s):",
                    $"   Média: {semGb.AvgLatencyUs:F0}µs | Stutters: {semGb.Stutters} | Amostras: {semGb.Samples}",
                    "",
                    $"📈 Restaurado (65s):",
                    $"   Restaurado: {restaurado.AvgLatencyUs:F0}µs | Stutters: {restaurado.Stutters} | Amostras: {restaurado.Samples}",
                    "",
                    AnalisarTeste(baseline, semGb, restaurado),
                };

                RecList.ItemsSource = lines;
            }
            catch (Exception ex)
            {
                FlipGameBoost(true);
                var err = new[] { $"Erro no teste: {ex.Message}", "GameBoost foi restaurado." };
                RecList.ItemsSource = err;
            }
            finally
            {
                _autoTestRunning = false;
                BtnAutoTest.IsEnabled = true;
                BtnAutoTest.Content = "  Teste A/B";
            }
        }

        private TestPhase TakeSnapshot()
        {
            if (_detector == null) return new(0, 0, 0, "");
            return new(_detector.AverageLatencyUs, _detector.TotalStutters, _detector.TotalSamples, "");
        }

        private void FlipGameBoost(bool enable)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow mw && mw.TrayService != null)
                {
                    mw.TrayService.GamePriorityEnabled = enable;
                    mw.TrayService.SaveSettings();
                    mw.TrayService.LoadSettings();
                    ShowToast(enable ? "GameBoost ativado" : "GameBoost desativado");
                }
            }
            catch { }
        }

        private static string AnalisarTeste(TestPhase baseline, TestPhase semGb, TestPhase restaurado)
        {
            if (baseline.AvgLatencyUs <= 0) return "⚠️ Dados insuficientes na fase baseline.";

            double diff = semGb.AvgLatencyUs - baseline.AvgLatencyUs;
            string conclusao = Math.Abs(diff) switch
            {
                < 100 => "✅ Sem diferença significativa. O GameBoost não afeta a latência do sistema neste PC.",
                < 300 => $"📊 Diferença de {diff:F0}µs ({(diff / baseline.AvgLatencyUs * 100):F0}%). Pode ser variação normal — repita o teste para confirmar.",
                _ when diff < 0 => $"✅ GameBoost REDUZIU a latência em {Math.Abs(diff):F0}µs! Mantenha-o ativado.{(semGb.Stutters > baseline.Stutters ? " Também teve mais stutters sem ele." : "")}",
                _ => $"🔴 GameBoost AUMENTOU a latência em {diff:F0}µs ({(diff / baseline.AvgLatencyUs * 100):F0}%). Considere mantê-lo desativado para jogos."
            };

            return conclusao;
        }

        private void ShowToast(string message)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("Stutter Detector", message);
        }
    }
}
