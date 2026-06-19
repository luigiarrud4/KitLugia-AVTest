using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KitLugia.Core;

// === RESOLUÇÃO DE AMBIGUIDADES ===
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

#pragma warning disable CS4014

namespace KitLugia.GUI.Pages
{
    public partial class IntegrityPage : Page
    {
        private bool _isBusy = false;
        private CancellationTokenSource? _scanCts;
        private List<ScannableTweak>? _allTweaks;
        private List<ScannableTweak>? _cachedFilteredTweaks;
        private bool _isLoaded = false;

        public IntegrityPage()
        {
            InitializeComponent();
            UpdateUiState(false);
            this.Unloaded += IntegrityPage_Unloaded;
            RunScan();
        }

        public void Cleanup()
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = null;
            _isBusy = false;
            _isLoaded = false;
            this.Unloaded -= IntegrityPage_Unloaded;

            if (ItemsList != null)
            {
                ItemsList.ItemsSource = null;
                ItemsList.Items.Clear();
            }

            _allTweaks = null;
            _cachedFilteredTweaks = null;
            this.DataContext = null;
        }

        private void IntegrityPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task RunScan()
        {
            if (_isBusy) return;
            _isBusy = true;

            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Verificando Integridade do Sistema", "Integrity");
            bool success = true;
            string message = "Verificação de integridade concluída";

            try
            {
                UpdateUiState(isLoading: true);
                ShowLoadingOverlay(true);
                if (TxtScore != null) TxtScore.Text = "...";

                // Executa scan em background thread
                var tweaks = await Task.Run(() => Guardian.GetHarmfulTweaksWithStatus(), token);

                if (token.IsCancellationRequested) return;

                // Armazena todos os tweaks para filtragem
                _allTweaks = tweaks;

                // Calcula score ignorando opcionais
                var nonOptionalTweaks = _allTweaks.Where(t => !t.IsOptional).ToList();
                var badItems = nonOptionalTweaks.Where(t => t.Status == TweakStatus.MODIFIED).ToList();
                int total = nonOptionalTweaks.Count;
                int score = total > 0 ? 100 - (int)Math.Ceiling(100.0 * badItems.Count / total) : 100;

                if (TxtScore != null) TxtScore.Text = score + "%";
                UpdateScoreColor(score);

                if (BtnFixAll != null && BtnRescan != null)
                {
                    if (score == 100)
                    {
                        BtnFixAll.Visibility = Visibility.Collapsed;
                        BtnRescan.Margin = new Thickness(0, 0, 0, 0);
                    }
                    else
                    {
                        BtnFixAll.Visibility = Visibility.Visible;
                        BtnRescan.Margin = new Thickness(15, 0, 0, 0);
                    }
                }

                // Carrega todos os itens de uma vez com cache
                var allFiltered = ApplyFilters(_allTweaks);
                _cachedFilteredTweaks = allFiltered;
                
                if (ItemsList != null)
                {
                    ItemsList.ItemsSource = _cachedFilteredTweaks;
                }

                _isLoaded = true;
                UpdateEmptyState();
            }
            catch (Exception ex)
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowError("ERRO NO SCAN", ex.Message);

                success = false;
                message = ex.Message;
            }
            finally
            {
                _isBusy = false;
                ShowLoadingOverlay(false);
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, success, message);
                UpdateUiState(isLoading: false);
            }
        }

        /// <summary>
        /// Aplica filtro por texto e categoria selecionada
        /// </summary>
        private List<ScannableTweak> ApplyFilters(List<ScannableTweak> tweaks)
        {
            if (tweaks == null) return new List<ScannableTweak>();

            var query = SearchBox?.Text?.Trim() ?? string.Empty;
            var filtered = tweaks.AsEnumerable();

            // Filtro por texto
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(t =>
                    t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Category.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // Filtro por categoria no ComboBox
            if (CategoryFilter?.SelectedItem is ComboBoxItem cbi)
            {
                string tag = cbi.Tag?.ToString() ?? "ALL";
                switch (tag)
                {
                    case "SEGURANCA":
                        filtered = filtered.Where(t => t.Category.Contains("Segurança") || t.Category.Contains("Defesa"));
                        break;
                    case "DEFESA":
                        filtered = filtered.Where(t => t.Category.Contains("Defesa") || t.Category.Contains("Antivírus"));
                        break;
                    case "PROTEGIDO":
                        filtered = filtered.Where(t => t.Status == TweakStatus.OK);
                        break;
                    case "MODIFICADO":
                        filtered = filtered.Where(t => t.Status == TweakStatus.MODIFIED && !t.IsOptional);
                        break;
                    case "NAO_ENCONTRADO":
                        filtered = filtered.Where(t => t.Status == TweakStatus.NOT_FOUND);
                        break;
                    case "OPCIONAL":
                        filtered = filtered.Where(t => t.IsOptional);
                        break;
                    case "DESEMPENHO":
                        filtered = filtered.Where(t => t.Category.Contains("Desempenho") || 
                                                       t.Category.Contains("Performance") || 
                                                       t.Category.Contains("Estabilidade") || 
                                                       t.Category.Contains("Saúde do Disco"));
                        break;
                }
            }

            return filtered.ToList();
        }

        private void UpdateUiState(bool isLoading)
        {
            if (BtnRescan != null) BtnRescan.IsEnabled = !isLoading;
            if (BtnFixAll != null) BtnFixAll.IsEnabled = !isLoading;
            if (ItemsList != null) ItemsList.IsEnabled = !isLoading;
            if (SearchBox != null) SearchBox.IsEnabled = !isLoading;
            if (CategoryFilter != null) CategoryFilter.IsEnabled = !isLoading;

            if (isLoading && BtnFixAll != null && BtnFixAll.Visibility == Visibility.Visible)
                BtnFixAll.Content = "⏳ PROCESSANDO...";
            else if (BtnFixAll != null)
                BtnFixAll.Content = "🛡️ RESTAURAR TODOS (PADRÃO SEGURO)";
        }

        private void ShowLoadingOverlay(bool show)
        {
            if (LoadingOverlay != null) LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (ProgressBarContainer != null) ProgressBarContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateEmptyState()
        {
            if (EmptyState == null || ItemsList == null) return;
            var items = ItemsList.ItemsSource as IList;
            EmptyState.Visibility = (items == null || items.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateScoreColor(int score)
        {
            if (BorderScore != null && TxtScore != null)
            {
                if (score == 100)
                {
                    var green = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    BorderScore.BorderBrush = green;
                    TxtScore.Foreground = green;
                }
                else if (score > 60)
                {
                    var gold = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                    BorderScore.BorderBrush = gold;
                    TxtScore.Foreground = gold;
                }
                else
                {
                    var red = new SolidColorBrush(Color.FromRgb(196, 43, 28));
                    BorderScore.BorderBrush = red;
                    TxtScore.Foreground = red;
                }
            }
        }

        private void BtnRescan_Click(object sender, RoutedEventArgs e)
        {
            RunScan();
        }

        private void BtnInfo_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string description)
            {
                if (string.IsNullOrEmpty(description)) description = "Sem descrição disponível.";
                MessageBox.Show(description, "Detalhes de Segurança", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void BtnToggleItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (sender is Button btn && btn.Tag is ScannableTweak tweak)
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null) return;

                if (tweak.Status == TweakStatus.OK)
                {
                    bool confirm = await mainWindow.ShowConfirmationDialog(
                        $"⚠️ PERIGO: Desativar '{tweak.Name}' reduz a segurança.\nTem certeza?");

                    if (!confirm) return;
                }

                _isBusy = true;
                btn.IsEnabled = false;
                btn.Content = "⏳";

                try
                {
                    var originalStatus = tweak.Status;
                    var result = await Task.Run(() => Guardian.ToggleTweak(tweak));
                    
                    await Task.Delay(2000);

                    var verification = await Task.Run(() => Guardian.GetHarmfulTweaksWithStatus());
                    _allTweaks = verification;
                    var currentTweak = verification.FirstOrDefault(t => t.Name == tweak.Name);
                    
                    if (result.Success && currentTweak != null)
                    {
                        if (currentTweak.Status == TweakStatus.OK && originalStatus == TweakStatus.MODIFIED)
                            mainWindow.ShowSuccess("SUCESSO", "Item restaurado com sucesso.");
                        else if (currentTweak.Status == TweakStatus.MODIFIED && originalStatus == TweakStatus.OK)
                            mainWindow.ShowInfo("ATENÇÃO", "Item modificado (Personalizado).");
                        else if (currentTweak.Status == TweakStatus.NOT_FOUND)
                            mainWindow.ShowError("FALHA", "Item não encontrado no sistema.");
                        else
                            mainWindow.ShowError("FALHA", "A alteração não foi aplicada corretamente.");
                    }
                    else
                    {
                        mainWindow.ShowError("FALHA", result.Message ?? "Erro ao processar a solicitação.");
                    }

                    RefreshFromAllTweaks();
                }
                catch (Exception ex)
                {
                    mainWindow.ShowError("ERRO CRÍTICO", ex.Message);
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private async void BtnFixAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (Application.Current.MainWindow is MainWindow mw)
            {
                bool confirm = await mw.ShowConfirmationDialog(
                    "RESTAURAÇÃO TOTAL DE INTEGRIDADE\n\n" +
                    "Isso corrigirá TODAS as vulnerabilidades detectadas.\nContinuar?");

                if (!confirm) return;

                _isBusy = true;
                UpdateUiState(isLoading: true);
                ShowLoadingOverlay(true);

                mw.ShowInfo("INICIANDO", "Analisando e corrigindo itens...");

                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Corrigindo Vulnerabilidades", "Integrity");

                int fixedCount = 0;
                int errorCount = 0;
                var failedTweaks = new List<string>();

                await Task.Run(async () =>
                {
                    var currentTweaks = Guardian.GetHarmfulTweaksWithStatus();
                    var badTweaks = currentTweaks
                        .Where(t => t.Status == TweakStatus.MODIFIED && !t.IsOptional)
                        .ToList();

                    foreach (var t in badTweaks)
                    {
                        try
                        {
                            var res = Guardian.ToggleTweak(t);
                            if (res.Success) 
                                fixedCount++;
                            else 
                            {
                                errorCount++;
                                failedTweaks.Add($"{t.Name}: {res.Message}");
                            }
                        }
                        catch (Exception ex) 
                        { 
                            errorCount++;
                            failedTweaks.Add($"{t.Name}: {ex.Message}");
                        }
                        await Task.Delay(150);
                    }
                    await Task.Delay(800);
                });

                string resultMessage;
                if (errorCount == 0)
                    resultMessage = $"{fixedCount} itens corrigidos com sucesso";
                else
                    resultMessage = $"{fixedCount} corrigidos, {errorCount} falharam. Falhas: {string.Join("; ", failedTweaks)}";

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, errorCount == 0, resultMessage);

                if (errorCount == 0)
                    mw.ShowSuccess("CONCLUÍDO", $"{fixedCount} itens foram corrigidos com sucesso.");
                else
                    mw.ShowInfo("FINALIZADO", $"{fixedCount} corrigidos. {errorCount} falharam.");

                _allTweaks = await Task.Run(() => Guardian.GetHarmfulTweaksWithStatus());
                RefreshFromAllTweaks();

                _isBusy = false;
                ShowLoadingOverlay(false);
            }
        }

        #region Search & Filter

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoaded || _allTweaks == null) return;

            string query = SearchBox?.Text?.Trim() ?? string.Empty;
            if (BtnClearSearch != null)
                BtnClearSearch.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

            ApplyFiltersAndUpdateList();
        }

        private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded || _allTweaks == null) return;
            ApplyFiltersAndUpdateList();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBox != null) SearchBox.Text = string.Empty;
        }

        private void ApplyFiltersAndUpdateList()
        {
            if (_allTweaks == null) return;

            var filtered = ApplyFilters(_allTweaks);
            _cachedFilteredTweaks = filtered;

            if (ItemsList != null)
            {
                ItemsList.ItemsSource = _cachedFilteredTweaks;
            }

            UpdateEmptyState();
        }

        private void RefreshFromAllTweaks()
        {
            if (_allTweaks == null) return;

            var nonOptionalTweaks = _allTweaks.Where(t => !t.IsOptional).ToList();
            var badItems = nonOptionalTweaks.Where(t => t.Status == TweakStatus.MODIFIED).ToList();
            int total = nonOptionalTweaks.Count;
            int score = total > 0 ? 100 - (int)Math.Ceiling(100.0 * badItems.Count / total) : 100;

            if (TxtScore != null) TxtScore.Text = score + "%";
            UpdateScoreColor(score);

            if (BtnFixAll != null && BtnRescan != null)
            {
                if (score == 100)
                {
                    BtnFixAll.Visibility = Visibility.Collapsed;
                    BtnRescan.Margin = new Thickness(0, 0, 0, 0);
                }
                else
                {
                    BtnFixAll.Visibility = Visibility.Visible;
                    BtnRescan.Margin = new Thickness(15, 0, 0, 0);
                }
            }

            ApplyFiltersAndUpdateList();
        }

        #endregion

    }
}