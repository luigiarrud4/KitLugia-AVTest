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
                BuildFilterOptions();
                UpdatePathCardState();

                // Score + botões + banner (fórmula única em RecalculateScore)
                RecalculateScore();

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

            // Filtro do ComboBox. Cada opcao e' uma FACETA do proprio catalogo:
            // estado real do check ou uma categoria que EXISTE (as opcoes de
            // categoria sao montadas em BuildFilterOptions a partir dos dados).
            // Antes eram tags fixas ("SEGURANCA", "DESEMPENHO") que testavam
            // Contains num texto qualquer: "Desempenho" nao incluia
            // "Performance Degradada"/"Memoria" e "Seguranca" deixava de fora
            // "Seguranca de Rede" era o unico acerto por acaso; pior, opcionais
            // eram escondidos de um filtro e mostrados no outro.
            string tag = (CategoryFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
            if (tag.StartsWith("CAT::", StringComparison.Ordinal))
            {
                string categoria = tag.Substring(5);
                filtered = filtered.Where(t => string.Equals(t.Category, categoria, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                switch (tag)
                {
                    case "PROTEGIDO":
                        // Protegido = so nao-opcionais com Status=OK. Opcionais vao
                        // para o filtro "Opcional", nunca para "Protegido".
                        filtered = filtered.Where(t => t.Status == TweakStatus.OK && !t.IsOptional);
                        break;
                    case "MODIFICADO":
                        // Só não-opcionais MODIFIED. Os opcionais ficam SEMPRE no
                        // filtro "Opcional" (badge fixa "◇ OPCIONAL"), então um
                        // item opcional modificado não pode aparecer aqui — nem
                        // sumir dos dois filtros.
                        filtered = filtered.Where(t => t.Status == TweakStatus.MODIFIED && !t.IsOptional);
                        break;
                    case "PENDING":
                        filtered = filtered.Where(t => t.Status == TweakStatus.PENDING);
                        break;
                    case "NAO_VERIFICAVEL":
                        filtered = filtered.Where(t => t.Status == TweakStatus.NOT_FOUND || t.Status == TweakStatus.ERROR);
                        break;
                    case "OPCIONAL":
                        // Opcional = TODOS os itens com IsOptional=true (badge fixa
                        // "◇ OPCIONAL"), com ou sem otimização aplicada. Assim o
                        // item opcional modificado NÃO desaparece: ele continua
                        // no filtro Opcional e o botão mostra REVERTER.
                        filtered = filtered.Where(t => t.IsOptional);
                        break;
                    case "SEGURANCA":
                        // "Segurança" na imagem: agrupa as categorias de defesa do sistema.
                        filtered = filtered.Where(t =>
                            t.Category != null &&
                            (t.Category.Contains("Segurança", StringComparison.OrdinalIgnoreCase) ||
                             t.Category.Contains("Security", StringComparison.OrdinalIgnoreCase)));
                        break;
                    case "DESEMPENHO":
                        // "Desempenho" na imagem: categoria de performance (inclui variantes).
                        filtered = filtered.Where(t =>
                            t.Category != null &&
                            (t.Category.Equals("Desempenho", StringComparison.OrdinalIgnoreCase) ||
                             t.Category.Contains("Performance", StringComparison.OrdinalIgnoreCase) ||
                             t.Category.Contains("Memória", StringComparison.OrdinalIgnoreCase)));
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

        /// <summary>Nome curto de um check para o banner (sem cortar o sentido).</summary>
        private static string PendingShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return name.Length <= 46 ? name : name.Substring(0, 45).TrimEnd() + "…";
        }

        /// <summary>
        /// Aviso amarelo de correções aplicadas que só entram em vigor depois de
        /// reiniciar. Sem ele o usuário corrigiria algo, veria o item sumir da lista
        /// "Modificado" e não saberia que ainda falta reiniciar — ou pior,
        /// acreditaria que já está protegido quando o hypervisor ainda não leu a
        /// configuração.
        /// </summary>
        private void UpdatePendingBanner(IReadOnlyList<ScannableTweak> tweaks)
        {
            if (PendingBanner == null || TxtPendingBanner == null) return;

            var pendentes = tweaks.Where(t => t.Status == TweakStatus.PENDING).ToList();
            var naoConfirmados = tweaks.Where(t => !string.IsNullOrEmpty(t.UnconfirmedReason)).ToList();

            if (pendentes.Count == 0 && naoConfirmados.Count == 0)
            {
                PendingBanner.Visibility = Visibility.Collapsed;
                return;
            }

            var partes = new List<string>();
            if (pendentes.Count > 0)
            {
                var nomes = string.Join(", ", pendentes.Take(3).Select(t => PendingShortName(t.Name)));
                partes.Add(pendentes.Count == 1
                    ? $"⏳ 1 correção foi aplicada mas só vale depois de reiniciar: {nomes}. Reinicie para o check ser confirmado."
                    : $"⏳ {pendentes.Count} correções foram aplicadas mas só valem depois de reiniciar: {nomes}{(pendentes.Count > 3 ? "…" : "")}. Reinicie para os checks serem confirmados.");
            }
            if (naoConfirmados.Count > 0)
            {
                var nomes = string.Join(", ", naoConfirmados.Take(2).Select(t => PendingShortName(t.Name)));
                partes.Add($"❔ {naoConfirmados.Count} check(s) não puderam ser confirmados após o reinício (não é falha nem proteção): {nomes}{(naoConfirmados.Count > 2 ? "…" : "")}.");
            }

            TxtPendingBanner.Text = string.Join("  ", partes);
            PendingBanner.Visibility = Visibility.Visible;

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
            if (sender is Button btn && btn.Tag is ScannableTweak tweak)
            {
                string description = tweak.Description;
                if (string.IsNullOrEmpty(description)) description = "Sem descrição disponível.";
                MessageBox.Show(description, "Detalhes de Segurança", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (sender is Button btn2 && btn2.Tag is string description2)
            {
                if (string.IsNullOrEmpty(description2)) description2 = "Sem descrição disponível.";
                MessageBox.Show(description2, "Detalhes de Segurança", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    // Texto do aviso depende do tipo: em item OPCIONAL o botão
                    // "APLICAR" vai ativar a otimização (ex.: desativar o Spooler),
                    // o que é escolha do usuário — não uma "redução de segurança".
                    string aviso = tweak.IsOptional
                        ? $"⚙️ OPCIONAL: CORRIGIR (aplicar otimização) em '{tweak.Name}'?\n{tweak.Description}\n\nVocê pode reverter a qualquer momento."
                        : $"⚠️ PERIGO: Desativar '{tweak.Name}' reduz a segurança.\nTem certeza?";

                    bool confirm = await mainWindow.ShowConfirmationDialog(aviso);
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
                    "Isso corrigirá TODAS as vulnerabilidades detectadas (itens MODIFICADOS).\n" +
                    "Itens OPCIONAIS são mantidos como estão.\nContinuar?");

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
            // Repopular as opcoes dispara SelectionChanged: sem esta guarda a lista
            // era filtrada no meio da remontagem (com a selecao ainda vazia).
            if (_rebuildingFilters) return;
            if (!_isLoaded || _allTweaks == null) return;
            ApplyFiltersAndUpdateList();
        }

        private bool _rebuildingFilters;

        /// <summary>
        /// Monta as opcoes do filtro como uma LISTA SIMPLES e DIRETA, exatamente
        /// como mostra a referencia da UI:
        ///
        ///   📋 Todas as Categorias   🛡️ Segurança   ✅ Protegido   ⚠️ Modificado
        ///   ⚙️ Opcional              ⚡ Desempenho
        ///
        /// Antes o combo montava categorias fixas inventadas ("Seguranca",
        /// "Desempenho") que testavam Contains num texto qualquer — nao pegava
        /// "Performance Degradada"/"Memoria" e as categorias reais do catalogo
        /// nunca apareciam. Agora sao as 6 opcoes da referencia, cada uma com
        /// a contagem real do catalogo. OPCIONAL e MODIFICADO sao facetas
        /// mutuamente exclusivas (um item opcional alterado aparece so em
        /// "Modificado", nunca nos dois).
        /// </summary>
        private void BuildFilterOptions()
        {
            if (CategoryFilter == null || _allTweaks == null) return;

            _rebuildingFilters = true;
            try
            {
                string atual = (CategoryFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";
                var visiveis = _allTweaks.Where(t => !t.IsPathItem).ToList();

                CategoryFilter.Items.Clear();
                void Add(string tag, string texto) =>
                    CategoryFilter.Items.Add(new ComboBoxItem { Tag = tag, Content = texto });

                Add("ALL", $"📋 Todas as Categorias ({visiveis.Count})");
                Add("SEGURANÇA", $"🛡️ Segurança ({visiveis.Count(t => t.Category != null &&
                    (t.Category.Contains("Segurança", StringComparison.OrdinalIgnoreCase) ||
                     t.Category.Contains("Security", StringComparison.OrdinalIgnoreCase))) })");
                Add("PROTEGIDO", $"✅ Protegido ({visiveis.Count(t => t.Status == TweakStatus.OK && !t.IsOptional)})");
                Add("MODIFICADO", $"⚠️ Modificado ({visiveis.Count(t => t.Status == TweakStatus.MODIFIED && !t.IsOptional)})");
                Add("OPCIONAL", $"⚙️ Opcional ({visiveis.Count(t => t.IsOptional)})");
                Add("DESEMPENHO", $"⚡ Desempenho ({visiveis.Count(t => t.Category != null &&
                    (t.Category.Equals("Desempenho", StringComparison.OrdinalIgnoreCase) ||
                     t.Category.Contains("Performance", StringComparison.OrdinalIgnoreCase) ||
                     t.Category.Contains("Memória", StringComparison.OrdinalIgnoreCase))) })");

                CategoryFilter.SelectedItem =
                    CategoryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag?.ToString() ?? "") == atual)
                    ?? CategoryFilter.Items.OfType<ComboBoxItem>().FirstOrDefault();
            }
            finally
            {
                _rebuildingFilters = false;
            }
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

            BuildFilterOptions();   // refresca contagens do combo (Protegido/Modificado/Opcional/Segurança)
            RecalculateScore();

            UpdatePathCardState();
            ApplyFiltersAndUpdateList();
        }

        /// <summary>
        /// Calcula o score e atualiza score + botões + banner de pendências.
        ///
        /// POR QUE ESTE MÉTODO EXISTE: o cálculo estava DUPLICADO em dois lugares
        /// (o do carregamento e o pós-toggle) e só um deles foi atualizado quando
        /// os PENDING entraram. Resultado: depois de "corrigir" um item que exige
        /// reinício, o score voltava a contar o item como vulnerável enquanto o
        /// banner dizia "aguardando reinício" — a tela se contradizendo. Uma
        /// fórmula só, chamada de todo canto.
        /// </summary>
        private void RecalculateScore()
        {
            if (_allTweaks == null) return;

            var nonOptionalTweaks = _allTweaks.Where(t => !t.IsOptional).ToList();

            // PENDING (corrigido, aguardando reinício) sai do
            // DENOMINADOR: o score mede o que está comprovadamente vulnerável
            // agora. Contar um item pendente como "ruim" puniria o usuário por
            // algo que ele acabou de corrigir; contá-lo como "ok" seria dizer
            // que já está protegido, o que ainda não se sabe.
            var countableTweaks = nonOptionalTweaks
                .Where(t => t.Status != TweakStatus.PENDING)
                .ToList();
            var badCountable = countableTweaks.Where(t => t.Status == TweakStatus.MODIFIED).ToList();
            int total = countableTweaks.Count;
            int score = total > 0 ? 100 - (int)Math.Ceiling(100.0 * badCountable.Count / total) : 100;

            if (TxtScore != null) TxtScore.Text = score + "%";
            UpdateScoreColor(score);
            UpdatePendingBanner(_allTweaks);

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