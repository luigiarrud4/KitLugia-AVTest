using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KitLugia.Core;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;

namespace KitLugia.GUI.Pages
{
    public partial class GlobalSearchPage : Page
    {
        private string _currentQuery = "";
        private CancellationTokenSource? _cts;

        // Filtro por tipo (Tudo/Páginas/Tweaks/Ações/Serviços) + debounce da barra da página.
        private string _typeFilter = "all";
        private readonly DispatcherTimer _typingTimer;
        private bool _updatingBox;
        private bool _suppressChipEvent;
        private List<GlobalSearchResult> _allResults = new();

        public GlobalSearchPage(string query = "")
        {
            InitializeComponent();
            SearchProviders.EnsureRegistered();

            // Debounce curto: o índice em memória responde em <2 ms, mas rebindar a
            // lista a cada tecla perderia o scroll e atrapalharia a digitação.
            _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _typingTimer.Tick += (_, _) => { _typingTimer.Stop(); UpdateSearch(TxtPageSearch.Text); };

            UpdateSearch(query);
            this.Unloaded += GlobalSearchPage_Unloaded;
        }

        public void Cleanup()
        {
            _typingTimer?.Stop();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            this.Unloaded -= GlobalSearchPage_Unloaded;
            this.DataContext = null;
        }

        private void GlobalSearchPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        public void UpdateSearch(string query)
        {
            _currentQuery = query ?? string.Empty;

            // A barra lateral do MainWindow chama UpdateSearch a cada debounce: espelha o
            // termo na barra da própria página sem disparar o TextChanged dela.
            _updatingBox = true;
            if (!string.Equals(TxtPageSearch.Text, _currentQuery, StringComparison.Ordinal))
                TxtPageSearch.Text = _currentQuery;
            _updatingBox = false;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Busca sincrona no indice em memoria (<2ms p/ ~700 itens), SEM teto:
            // a lista e virtualizada (so materializa o visivel) e os estados vao
            // pelo cache TTL. Sem recriar a pagina, sem scan por tecla.
            _allResults = SearchEngine.Search(_currentQuery);

            TxtSearchTerm.Text = string.IsNullOrWhiteSpace(_currentQuery)
                ? "digite um termo"
                : $"“{_currentQuery}”";

            UpdateChipCounts();
            ApplyTypeFilter();

            LoadToggleStates(token);
        }

        /// <summary>Contagem por tipo nos chips (Tudo/Páginas/Tweaks/Ações/Serviços).</summary>
        private void UpdateChipCounts()
        {
            int pages = _allResults.Count(r => r.Type == SearchResultType.Navigation);
            int tweaks = _allResults.Count(r => r.Type == SearchResultType.Tweak);
            int actions = _allResults.Count(r => r.Type == SearchResultType.Action);
            int services = _allResults.Count(r => r.Type == SearchResultType.Service);

            ChipAll.Content = $"Tudo ({_allResults.Count})";
            ChipPages.Content = $"Páginas ({pages})";
            ChipTweaks.Content = $"Tweaks ({tweaks})";
            ChipActions.Content = $"Ações ({actions})";
            ChipServices.Content = $"Serviços ({services})";
        }

        private List<GlobalSearchResult> FilterResults() => _typeFilter switch
        {
            "nav" => _allResults.Where(r => r.Type == SearchResultType.Navigation).ToList(),
            "tweak" => _allResults.Where(r => r.Type == SearchResultType.Tweak).ToList(),
            "action" => _allResults.Where(r => r.Type == SearchResultType.Action).ToList(),
            "service" => _allResults.Where(r => r.Type == SearchResultType.Service).ToList(),
            _ => _allResults
        };

        private void ApplyTypeFilter()
        {
            var filtered = FilterResults();

            // O tipo escolhido nao tem itens para o termo novo: volta para "Tudo" em vez
            // de deixar a pagina vazia sem explicação.
            if (_typeFilter != "all" && filtered.Count == 0 && _allResults.Count > 0)
            {
                _typeFilter = "all";
                _suppressChipEvent = true;
                ChipAll.IsChecked = true;
                _suppressChipEvent = false;
                filtered = _allResults;
            }

            ListResults.ItemsSource = null;
            ListResults.ItemsSource = filtered;

            bool hasResults = filtered.Count > 0;
            ListResults.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            PanelNoResults.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;

            TxtResultCount.Text = filtered.Count == _allResults.Count
                ? $"{filtered.Count} itens"
                : $"{filtered.Count} de {_allResults.Count} itens";

            if (hasResults) return;

            bool emptyQuery = string.IsNullOrWhiteSpace(_currentQuery);
            TxtNoResultsTitle.Text = emptyQuery
                ? "Digite algo para pesquisar."
                : $"Nada encontrado para “{_currentQuery}”.";
            TxtNoResultsHint.Text = emptyQuery
                ? "Busque por páginas, tweaks, ações e serviços do Kit."
                : "Tente termos como: desempenho, rede, drivers, reparos...";
        }

        private void ChipFilter_Checked(object sender, RoutedEventArgs e)
        {
            // O XAML marca "Tudo" durante o InitializeComponent — nesse momento a lista
            // ainda nao existe, e o ctor ja roda a busca com o filtro padrão.
            if (_suppressChipEvent || !IsInitialized) return;

            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
            {
                _typeFilter = tag;
                ApplyTypeFilter();
            }
        }

        private void TxtPageSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingBox) return;
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        private void TxtPageSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _typingTimer.Stop();
            UpdateSearch(TxtPageSearch.Text);
        }

        /// <summary>
        /// Batch: 1 passada de estados com TTL p/ todos os toggles (nunca 1 scan Guardian
        /// por item/tecla como antes).
        /// </summary>
        private void LoadToggleStates(CancellationToken token)
        {
            var toggleItems = _allResults.Where(r => r.IsToggle).ToList();
            if (toggleItems.Count == 0) return;

            _ = Task.Run(() =>
            {
                try
                {
                    var states = SearchEngine.GetStates();

                    foreach (var item in toggleItems)
                    {
                        if (token.IsCancellationRequested) break;

                        try
                        {
                            if (!string.IsNullOrEmpty(item.StateKey) && states.TryGetValue(item.StateKey, out bool s))
                                item.IsActive = s;
                            else if (item.CheckState != null)
                                item.IsActive = item.CheckState.Invoke();
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }, token);
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            GlobalSearchResult? item = null;
            if (sender is Button btn) item = btn.Tag as GlobalSearchResult;
            else if (sender is CheckBox chk) item = chk.Tag as GlobalSearchResult;

            if (item == null) return;
            await ExecuteItemAsync(item);
        }

        /// <summary>
        /// Clique/duplo clique no card abre o item de navegação. Só PÁGINA: ações e
        /// tweaks continuam exigindo o botão/switch, para ninguém disparar algo por engano.
        /// </summary>
        private async void ResultCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { DataContext: GlobalSearchResult item } && item.Type == SearchResultType.Navigation)
                await ExecuteItemAsync(item);
        }

        private async void ListResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListResults.SelectedItem is GlobalSearchResult item && item.Type == SearchResultType.Navigation)
                await ExecuteItemAsync(item);
        }

        private async Task ExecuteItemAsync(GlobalSearchResult item)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            await mw.ExecuteGlobalSearchResultAsync(item);

            if (item.IsToggle)
                UpdateSearch(_currentQuery);
        }
    }
}
