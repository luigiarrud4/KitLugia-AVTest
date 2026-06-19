using System;
using System.Collections.Concurrent;
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
        private static ConcurrentDictionary<string, bool> _statusCache = new();

        public GlobalSearchPage(string query = "")
        {
            InitializeComponent();
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

            var results = SearchEngine.Search(query);

            ListResults.ItemsSource = null;
            ListResults.ItemsSource = results;
            TxtResultCount.Text = $"{results.Count} itens";

            bool hasResults = results.Count > 0;
            ListResults.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            PanelNoResults.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;

            if (!hasResults) return;

            // Batch: coleta todos os itens toggle e verifica em lote
            var toggleItems = results.Where(r => r.IsToggle).ToList();
            if (toggleItems.Count == 0) return;

            _ = Task.Run(() =>
            {
                try
                {
                    // Faz uma única scan para todos os Guardian tweaks
                    var guardianStatuses = new Dictionary<string, bool>();
                    try
                    {
                        var allTweaks = Guardian.GetHarmfulTweaksWithStatus();
                        foreach (var t in allTweaks)
                            guardianStatuses[t.Name] = t.Status == TweakStatus.MODIFIED;
                    }
                    catch { }

                    foreach (var item in toggleItems)
                    {
                        if (token.IsCancellationRequested) break;

                        try
                        {
                            bool state = false;
                            // Tenta cache Guardian primeiro
                            if (guardianStatuses.TryGetValue(item.Title, out var cached))
                                state = cached;
                            else if (item.CheckState != null)
                                state = item.CheckState.Invoke();

                            _statusCache[item.Title] = state;
                            item.IsActive = state;
                        }
                        catch { }
                    }
                }
                catch { }
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
