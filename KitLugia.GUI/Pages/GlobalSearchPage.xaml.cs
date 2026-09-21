using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

        public GlobalSearchPage(string query = "")
        {
            InitializeComponent();
            SearchProviders.EnsureRegistered();
            UpdateSearch(query);
            this.Unloaded += GlobalSearchPage_Unloaded;
        }

        public void Cleanup()
        {
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
            _currentQuery = query;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Busca sincrona no indice em memoria (<2ms p/ ~700 itens), SEM teto:
            // a lista e virtualizada (so materializa o visivel) e os estados vao
            // pelo cache TTL. Sem recriar a pagina, sem scan por tecla.
            var results = SearchEngine.Search(query);

            ListResults.ItemsSource = null;
            ListResults.ItemsSource = results;
            TxtResultCount.Text = $"{results.Count} itens";

            bool hasResults = results.Count > 0;
            ListResults.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            PanelNoResults.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;

            if (!hasResults) return;

            // Batch: 1 passada de estados com TTL p/ todos os toggles (nunca 1 scan
            // Guardian por item/tecla como antes).
            var toggleItems = results.Where(r => r.IsToggle).ToList();
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

            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            await mw.ExecuteGlobalSearchResultAsync(item);

            if (item.IsToggle)
                UpdateSearch(_currentQuery);
        }
    }
}
