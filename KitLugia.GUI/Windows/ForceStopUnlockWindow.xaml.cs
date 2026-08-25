using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Windows
{
    public partial class ForceStopUnlockWindow : Window
    {
        private List<BlockingProcessInfo> _currentResults = new();
        private bool _isAnalyzing;

        public ForceStopUnlockWindow()
        {
            InitializeComponent();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TxtPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool hasPath = !string.IsNullOrWhiteSpace(TxtPath?.Text);
            BtnAnalyze.IsEnabled = hasPath && !_isAnalyzing;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Try folder first, then file
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Selecionar arquivo bloqueado",
                    Filter = "Todos os arquivos (*.*)|*.*",
                    CheckFileExists = false
                };

                if (ofd.ShowDialog() == true)
                {
                    TxtPath.Text = ofd.FileName;
                }
            }
            catch
            {
                // Fallback: folder browser
                try
                {
                    var dialog = new Microsoft.Win32.OpenFolderDialog
                    {
                        Title = "Selecionar pasta bloqueada"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        TxtPath.Text = dialog.FolderName;
                    }
                }
                catch { }
            }
        }

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_isAnalyzing) return;

            string path = TxtPath.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            _isAnalyzing = true;
            BtnAnalyze.IsEnabled = false;
            BtnUnlock.IsEnabled = false;

            ShowProgress(true, "🔍 Analisando processos bloqueadores...");
            TxtBottomStatus.Text = "Analisando...";

            try
            {
                var results = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));

                _currentResults = results;

                if (results.Count == 0)
                {
                    ShowResults(false);
                    TxtStatus.Text = "✅ Nenhum processo bloqueador encontrado.\nO arquivo/pasta está livre para uso.";
                    TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    StatusOverlay.Visibility = Visibility.Visible;
                    ResultsPanel.Visibility = Visibility.Collapsed;
                    ActionBar.Visibility = Visibility.Collapsed;
                    TxtBottomStatus.Text = "Nenhum bloqueio detectado";
                }
                else
                {
                    ShowResults(true);
                    ProcessList.ItemsSource = _currentResults;
                    TxtSummary.Text = $"Encontrados: {results.Count} processo(s) bloqueando este caminho";
                    TxtMethodInfo.Text = GetMethodInfo(results);
                    TxtBottomStatus.Text = $"{results.Count} bloqueio(s) encontrado(s) — selecione e clique em Liberar";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"❌ Erro durante a análise:\n{ex.Message}";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                StatusOverlay.Visibility = Visibility.Visible;
                ResultsPanel.Visibility = Visibility.Collapsed;
                ActionBar.Visibility = Visibility.Collapsed;
                TxtBottomStatus.Text = "Erro na análise";
            }
            finally
            {
                _isAnalyzing = false;
                BtnAnalyze.IsEnabled = true;
                ShowProgress(false, "");
            }
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_currentResults.Count == 0) return;

            var selected = _currentResults.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtBottomStatus.Text = "Nenhum processo selecionado para liberar";
                return;
            }

            // Confirm for system-like processes
            var systemLike = selected.Where(r =>
                r.ProcessName.Contains("svchost", StringComparison.OrdinalIgnoreCase) ||
                r.ProcessName.Contains("dwm", StringComparison.OrdinalIgnoreCase) ||
                r.ProcessName.Contains("explorer", StringComparison.OrdinalIgnoreCase));

            if (systemLike.Any())
            {
                var names = string.Join(", ", systemLike.Select(s => s.ProcessName).Distinct());
                var confirm = MessageBox.Show(
                    $"⚠️ ATENÇÃO: Você está prestes a finalizar processos do sistema:\n\n{names}\n\n" +
                    "Isso pode fechar janelas, reiniciar serviços ou causar instabilidade.\n\nContinuar?",
                    "Force Stop Unlock",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes) return;
            }

            BtnUnlockBottom.IsEnabled = false;
            ShowProgress(true, "⚡ Liberando processos...");
            TxtBottomStatus.Text = "Liberando...";

            try
            {
                string path = TxtPath.Text?.Trim() ?? "";
                var result = await Task.Run(() => ForceStopUnlockService.Unlock(path, selected));

                TxtResult.Text = result.Message;

                if (result.Success)
                {
                    TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    TxtBottomStatus.Text = $"✅ Concluído: {result.HandlesClosed} handle(s) liberado(s), {result.ProcessesKilled} processo(s) finalizado(s)";

                    // Re-analyze to verify
                    await Task.Delay(500);
                    await ReAnalyze();
                }
                else
                {
                    TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 150, 100));
                    TxtBottomStatus.Text = $"⚠️ {result.Message}";

                    if (result.Errors.Count > 0)
                    {
                        TxtResult.Text += "\n\nErros:\n" + string.Join("\n", result.Errors.Take(5));
                    }
                }
            }
            catch (Exception ex)
            {
                TxtResult.Text = $"❌ Erro: {ex.Message}";
                TxtResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100));
                TxtBottomStatus.Text = "Erro durante a liberação";
            }
            finally
            {
                BtnUnlockBottom.IsEnabled = true;
                ShowProgress(false, "");
            }
        }

        private async Task ReAnalyze()
        {
            string path = TxtPath.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var results = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));
                _currentResults = results;

                if (results.Count == 0)
                {
                    ShowResults(false);
                    TxtStatus.Text = "✅ Arquivo/pasta liberado com sucesso!\nNenhum processo bloqueador restante.";
                    TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    StatusOverlay.Visibility = Visibility.Visible;
                    ResultsPanel.Visibility = Visibility.Collapsed;
                    ActionBar.Visibility = Visibility.Collapsed;
                    TxtBottomStatus.Text = "✅ Liberado com sucesso";
                }
                else
                {
                    ProcessList.ItemsSource = _currentResults;
                    TxtSummary.Text = $"Ainda bloqueado: {results.Count} processo(s)";
                    TxtMethodInfo.Text = GetMethodInfo(results);
                    TxtBottomStatus.Text = $"⚠️ {results.Count} bloqueio(s) restante(s)";
                }
            }
            catch { }
        }

        private void ChkSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool selectAll = ChkSelectAll.IsChecked == true;
            foreach (var item in _currentResults)
                item.IsSelected = selectAll;
            ProcessList.ItemsSource = null;
            ProcessList.ItemsSource = _currentResults;
        }

        private void ShowProgress(bool show, string message)
        {
            ProgressBar.IsIndeterminate = show;
            ProgressBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show && !string.IsNullOrEmpty(message))
            {
                TxtStatus.Text = message;
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                StatusOverlay.Visibility = Visibility.Visible;
                ResultsPanel.Visibility = Visibility.Collapsed;
                ActionBar.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowResults(bool hasResults)
        {
            StatusOverlay.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
            ResultsPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            ActionBar.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            BtnUnlock.IsEnabled = hasResults;
        }

        private static string GetMethodInfo(List<BlockingProcessInfo> results)
        {
            var methods = results.Select(r => r.HandleType).Distinct().ToList();
            if (methods.Count == 0) return "";
            if (methods.Contains("Restart Manager"))
                return "🔧 Detectado via Restart Manager API";
            if (methods.Contains("File"))
                return "🔧 Detectado via Handle tool (Sysinternals)";
            return $"🔧 Métodos: {string.Join(", ", methods)}";
        }
    }
}
