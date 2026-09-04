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
                UpdatePathCardState();

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
            // Itens de PATH ficam consolidados no card unico acima da lista
            // (nao poluem a lista com varios botoes "corrigir" separados).
            var filtered = tweaks.Where(t => !t.IsPathItem).AsEnumerable();

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

            // Animação controlada por código: só existe enquanto o loading está visível.
            // Antes era um Storyboard "Forever" no Loaded que continuava consumindo CPU
            // mesmo com a barra Collapsed (invisível ≠ parado em WPF).
            if (ProgressBarFill != null)
            {
                if (show)
                {
                    var sweep = new DoubleAnimation
                    {
                        From = 0,
                        To = ProgressBarContainer?.ActualWidth ?? 300,
                        Duration = TimeSpan.FromMilliseconds(1100),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    ProgressBarFill.BeginAnimation(WidthProperty, sweep, HandoffBehavior.SnapshotAndReplace);
                }
                else
                {
                    ProgressBarFill.BeginAnimation(WidthProperty, null); // para e libera a animação
                }
            }
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

        private void BtnPathExplore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new KitLugia.GUI.Windows.PathExplorerWindow
                {
                    Owner = System.Windows.Window.GetWindow(this)
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não foi possível abrir o Explorador de PATH: {ex.Message}",
                    "Explorador de PATH", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                    // ToggleTweak já re-verifica o item via CheckTweak - SEM re-scan completo
                    // de 2s+ (a otimização do scan removeu os 28 processos bcdedit e os
                    // 94 ServiceControllers; o re-scan ficava ~10-20s à toa por toggle).
                    var currentTweak = tweak;

                    if (result.Success)
                    {
                        if (currentTweak.Status != originalStatus)
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
                            mainWindow.ShowInfo("INFO", result.Message ?? "Alteração aplicada.");
                        }
                    }
                    else
                    {
                        mainWindow.ShowError("FALHA", result.Message ?? "Erro ao processar a solicitação.");
                    }
                }
                catch (Exception ex)
                {
                    mainWindow.ShowError("ERRO CRÍTICO", ex.Message);
                }
                finally
                {
                    // SEMPRE reconstruir a lista no fim — inclusive em falha — para o botão
                    // da linha não ficar preso em "⏳" (desabilitado) até reentrar na página.
                    _isBusy = false;
                    try { RefreshFromAllTweaks(); } catch { }
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

                try
                {
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
                            // Delay mínimo entre serviços (dependências) - sem os antigos 150ms+800ms
                            await Task.Delay(25);
                        }
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
                }
                catch (Exception ex)
                {
                    // NUNCA deixar a página congelada: mesmo se o re-scan final falhar,
                    // tudo já foi corrigido — destrava a UI e mostra o erro (não trava).
                    Logger.Log($"[INTEGRITY] FixAll: erro pós-correção: {ex}");
                    try { Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message); } catch { }
                    try { mw.ShowError("FALHA NA RESTAURAÇÃO", ex.Message); } catch { }
                }
                finally
                {
                    // SEMPRE reabilita a lista/botões e restaura o texto do botão.
                    // Antes: UpdateUiState(true) no início + só _isBusy=false no fim =
                    // página congelada (IsEnabled=false) se qualquer passo final falhasse.
                    _isBusy = false;
                    UpdateUiState(isLoading: false);
                    ShowLoadingOverlay(false);
                }
            }
        }

        #region Search & Filter

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoaded || _allTweaks == null) return;

            string query = SearchBox?.Text?.Trim() ?? string.Empty;
            if (BtnClearSearch != null)
                BtnClearSearch.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

            // FIX PERF: digitar disparava re-filtragem + re-criação da lista a CADA tecla.
            // Debounce de 250ms agrupa digitação rápida numa única filtragem.
            if (_searchDebounce == null)
            {
                _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _searchDebounce.Tick += (_, __) =>
                {
                    _searchDebounce.Stop();
                    ApplyFiltersAndUpdateList();
                };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
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

            UpdatePathCardState();
            ApplyFiltersAndUpdateList();
        }

        #endregion

        #region PATH (card unico)

        /// <summary>
        /// Reflete o estado consolidado dos itens de PATH (IsPathItem) no card unico.
        /// Quando OK o card fica CALMO (badge verde + botao neutro discreto); quando ha
        /// pendencia o botao vira o destaque de acao (estilo CORRIGIR).
        /// </summary>
        private void UpdatePathCardState()
        {
            if (_allTweaks == null)
            {
                SetPathButtonNeutral();
                return;
            }
            if (PathStatusBadge == null || TxtPathStatus == null || TxtPathDetail == null) return;

            var modified = _allTweaks.Where(t => t.IsPathItem && t.Status == TweakStatus.MODIFIED).ToList();

            if (modified.Count == 0)
            {
                PathStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x15, 0x4C, 0xAF, 0x50));
                TxtPathStatus.Text = "✅ PATH OK";
                TxtPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                TxtPathDetail.Text = "PATH do sistema e do usuário corretos. Pode clicar para re-aplicar ou adicionar programas instalados que estejam fora do PATH (winget, git, node, dotnet, npm, 7-Zip, cargo...).";
                SetPathButtonNeutral();
                return;
            }

            string ShortDesc(string name)
            {
                if (name.Contains("Incompleto")) return "caminhos essenciais ausentes";
                if (name.Contains("Duplicadas")) return "entradas duplicadas";
                if (name.Contains("Inexistentes")) return "pastas inexistentes";
                if (name.Contains("Lixo")) return "lixo de desenvolvimento";
                if (name.Contains("Vulnerável") || name.Contains("Hijacking")) return "ordem vulnerável";
                if (name.Contains("Corrompida")) return "variável corrompida";
                return name;
            }

            PathStatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x25, 0xC4, 0x2B, 0x1C));
            TxtPathStatus.Text = "⚠️ REQUER CORREÇÃO";
            TxtPathStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6F, 0x61));
            TxtPathDetail.Text = "Pendências: " + string.Join(" · ", modified.Select(t => ShortDesc(t.Name)))
                + ". Um clique em CORRIGIR resolve tudo de uma vez (sem abrir o ➕).";
            SetPathButtonAction();
        }

        private void SetPathButtonNeutral()
        {
            if (BtnPathRepairAll == null) return;
            BtnPathRepairAll.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            BtnPathRepairAll.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
            BtnPathRepairAll.Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5));
            BtnPathRepairAll.Content = "⟳ Reaplicar / Adicionar ausentes";
        }

        private void SetPathButtonAction()
        {
            if (BtnPathRepairAll == null) return;
            var accent = AccentBrush();
            BtnPathRepairAll.Background = accent;
            BtnPathRepairAll.BorderBrush = accent;
            BtnPathRepairAll.Foreground = new SolidColorBrush(Colors.Black);
            BtnPathRepairAll.Content = "🛠️ CORRIGIR PATH (ADICIONA AUSENTES)";
        }

        private System.Windows.Media.Brush? _accentCache;
        private System.Windows.Media.Brush AccentBrush()
        {
            if (_accentCache != null) return _accentCache;
            try
            {
                if (Application.Current?.TryFindResource("AccentColor") is System.Windows.Media.Brush b)
                {
                    _accentCache = b;
                    return b;
                }
            }
            catch { }
            _accentCache = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00));
            return _accentCache;
        }

        private async void BtnPathRepairAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (Application.Current.MainWindow is not MainWindow mw) return;

            _isBusy = true;
            if (BtnPathRepairAll != null)
            {
                BtnPathRepairAll.IsEnabled = false;
                BtnPathRepairAll.Content = "⏳ CORRIGINDO PATH...";
            }

            try
            {
                var result = await Task.Run(() => Guardian.RepairAllPathsOnce());

                // Re-verifica os status (sem scan completo de 2s+)
                _allTweaks = await Task.Run(() => Guardian.GetHarmfulTweaksWithStatus());
                RefreshFromAllTweaks();

                if (result.Changed)
                {
                    string summary = result.Summary;
                    if (summary.Length > 600) summary = summary.Substring(0, 600) + "...";
                    mw.ShowSuccess("PATH CORRIGIDO", summary);
                }
                else
                {
                    mw.ShowInfo("PATH OK", result.Summary);
                }
            }
            catch (Exception ex)
            {
                mw.ShowError("ERRO AO CORRIGIR PATH", ex.Message);
            }
            finally
            {
                if (BtnPathRepairAll != null) BtnPathRepairAll.IsEnabled = true;
                _isBusy = false;
                // Restaura o estado real do card (neutro se OK, acao se ainda houver pendencia)
                if (_allTweaks != null) UpdatePathCardState();
            }
        }

        #endregion

    }
}