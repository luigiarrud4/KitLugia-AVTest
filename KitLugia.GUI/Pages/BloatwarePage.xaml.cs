using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Helpers;

// --- CORREÇÃO DOS CONFLITOS DE AMBIGUIDADE ---
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

using Application = System.Windows.Application;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class BloatwarePage : Page
    {
        private ObservableCollection<BloatwareApp>? AppsCollection;
        private ObservableCollection<BloatwareApp>? FilteredAppsCollection;
        private CancellationTokenSource? _cts;
        private bool _isBloatwareOperation;

        public BloatwarePage()
        {
            InitializeComponent();
            LoadApps();
            this.Unloaded += BloatwarePage_Unloaded;
        }

        private ObservableCollection<BloatwareApp> AllAppsCollection
        {
            get => AppsCollection ?? new ObservableCollection<BloatwareApp>();
            set => AppsCollection = value;
        }

        public void Cleanup()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            AppsCollection?.Clear();
            FilteredAppsCollection?.Clear();
            AppsCollection = null;
            FilteredAppsCollection = null;
            AppsList.ItemsSource = null;
            AppsList.Items.Clear();
            this.Unloaded -= BloatwarePage_Unloaded;
            this.DataContext = null;
        }

        private void BloatwarePage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task LoadApps()
        {
            if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Visible;
            if (AppsList != null) AppsList.ItemsSource = null;

            // Inicializa contador
            if (IconProgressText != null) IconProgressText.Text = "Ícones carregados: 0/0";

            // Salva ícones já carregados para não perder ao atualizar

            // Típico: 10-100 ícones salvos
            var oldIcons = new Dictionary<string, object?>(100, StringComparer.OrdinalIgnoreCase);
            if (AppsCollection != null)
            {
                foreach (var app in AppsCollection)
                {
                    if (app.Icon != null)
                    {
                        oldIcons[app.PackageName.Split('_')[0]] = app.Icon;
                    }
                }
            }

            AppsCollection = new ObservableCollection<BloatwareApp>();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                // 1. Carrega a lista primeiro (rápido)
                var apps = await Task.Run(() => SystemTweaks.GetBloatwareAppsStatus(), token);

                foreach (var app in apps)
                {
                    if (AppsCollection != null)
                    {
                        // Restaura ícone já carregado se existir
                        string packageName = app.PackageName.Split('_')[0];
                        if (oldIcons.ContainsKey(packageName))
                        {
                            app.Icon = oldIcons[packageName];
                        }
                        AppsCollection.Add(app);
                    }
                }

                // 2. Mostra a lista imediatamente
                FilteredAppsCollection = new ObservableCollection<BloatwareApp>(AppsCollection ?? Enumerable.Empty<BloatwareApp>());
                if (AppsList != null) AppsList.ItemsSource = FilteredAppsCollection;

                // 3. Carrega ícones em background (apenas para apps sem ícone)
                _ = Task.Run(() => LoadIconsAsync(token));
            }
            catch (OperationCanceledException)
            {
                // Ignora cancelamento esperado
            }
            finally
            {
                if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadIconsAsync(CancellationToken token)
        {
            if (AppsCollection == null) return;

            int totalIcons = AppsCollection.Count;

            var items = AppsCollection
                .Select((app, idx) => (App: app, Index: idx, Pkg: app.PackageName.Split('_')[0]))
                .Where(x => x.App.Icon == null)
                .ToList();

            int loadedIcons = totalIcons - items.Count;

            if (items.Count == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (IconProgressText != null)
                    IconProgressText.Text = $"Ícones carregados: {loadedIcons}/{totalIcons}";
            });

            // Carrega todos os ícones em paralelo SEM invocar Dispatcher durante o carregamento
            var results = new (int Index, object? Icon)[items.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 5,
                CancellationToken = token
            };

            Parallel.For(0, items.Count, parallelOptions, i =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var icon = AppIconHelper.GetAppIcon(items[i].Pkg, 32) ?? AppIconHelper.GetGenericStoreIcon();
                    results[i] = (items[i].Index, icon);
                }
                catch
                {
                    results[i] = (items[i].Index, null);
                }
            });

            if (token.IsCancellationRequested) return;

            // ÚNICO Invoke para aplicar todos os ícones de uma vez
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var (idx, icon) in results)
                {
                    if (icon != null && idx < AppsCollection.Count)
                        AppsCollection[idx].Icon = icon;
                }

                if (IconProgressText != null)
                    IconProgressText.Text = $"Ícones carregados: {AppsCollection.Count(a => a.Icon != null)}/{totalIcons}";
            });
        }

        private TaskCompletionSource<bool>? _confirmTcs;

        private Task<bool> ShowConfirmAsync(string message, string title)
        {
            _confirmTcs = new TaskCompletionSource<bool>();
            ConfirmTitle.Text = title;
            ConfirmMessage.Text = message;
            ConfirmOverlay.Visibility = Visibility.Visible;
            BtnConfirmYes.Focus();
            return _confirmTcs.Task;
        }

        private void BtnConfirmYes_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _confirmTcs?.TrySetResult(true);
        }

        private void BtnConfirmNo_Click(object sender, RoutedEventArgs e)
        {
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            _confirmTcs?.TrySetResult(false);
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isBloatwareOperation) return;
            _isBloatwareOperation = true;
            try
            {
                if (sender is Button btn && btn.Tag is BloatwareApp app)
                {
                    if (app.IsInstalled)
                    {
                        // Feedback visual IMEDIATO: botão mostra "?" antes de qualquer bloqueio
                        btn.Content = "?";
                        btn.IsEnabled = false;

                        // Diálogo de confirmação não-bloqueante (não trava a UI thread)
                        if (!await ShowConfirmAsync(
                            $"Deep Uninstall: Remover {app.DisplayName} e limpar arquivos residuais?\n\nIsso irá:\n- Remover o app para todos os usuários\n- Remover da imagem do Windows (evita reinstalação)\n- Limpar arquivos residuais",
                            "Bloatware"))
                        {
                            // Cancelado: reverte o botão
                            btn.Content = "REMOVER";
                            btn.IsEnabled = true;
                            return;
                        }

                        // Registra tarefa no BackgroundTaskTracker
                        string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Removendo {app.DisplayName}", "Bloatware");

                        // Executa desinstalação em background
                        var result = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName);

                        if (result.Success)
                        {
                            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);

                            btn.Content = "✅ REMOVIDO";
                            btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0));

                            // Atualiza o status do app na lista
                            app.IsInstalled = false;

                            // Recarrega a lista após 1 segundo
                            await Task.Delay(1000);
                            await LoadApps();
                        }
                        else
                        {
                            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, result.Message);

                            btn.Content = "❌ ERRO";
                            btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(192, 0, 0));
                            MessageBox.Show($"Erro ao remover {app.DisplayName}:\n{result.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);

                            // Reabilita o botão após erro
                            await Task.Delay(2000);
                            btn.Content = "REMOVER";
                            btn.IsEnabled = true;
                            btn.Background = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAction_Click", ex.Message);
            }
            finally
            {
                _isBloatwareOperation = false;
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isBloatwareOperation) return;
            _isBloatwareOperation = true;
            try
            {
                await LoadApps();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefresh_Click", ex.Message);
            }
            finally
            {
                _isBloatwareOperation = false;
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && AppsCollection != null)
            {
                string searchText = textBox.Text.ToLower();

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    // Mostra todos os apps
                    FilteredAppsCollection = new ObservableCollection<BloatwareApp>(AppsCollection);
                }
                else
                {
                    // Filtra apps por DisplayName ou PackageName
                    var filtered = AppsCollection.Where(app => 
                        app.DisplayName.ToLower().Contains(searchText) || 
                        app.PackageName.ToLower().Contains(searchText)).ToList();
                    FilteredAppsCollection = new ObservableCollection<BloatwareApp>(filtered);
                }

                if (AppsList != null)
                {
                    AppsList.ItemsSource = FilteredAppsCollection;
                }
            }
        }

        private async void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isBloatwareOperation) return;
            _isBloatwareOperation = true;
            try
            {
                if (FilteredAppsCollection == null) return;

                var selectedApps = FilteredAppsCollection.Where(app => app.IsSelected && app.IsInstalled).ToList();

                if (selectedApps.Count == 0)
                {
                    MessageBox.Show("Nenhum app selecionado para remoção.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string appNames = string.Join("\n", selectedApps.Select(a => a.DisplayName));
                if (!await ShowConfirmAsync(
                    $"Deep Uninstall: Remover {selectedApps.Count} app(s) selecionado(s)?\n\n{appNames}\n\nIsso irá:\n- Remover os apps para todos os usuários\n- Remover da imagem do Windows (evita reinstalação)\n- Limpar arquivos residuais",
                    "Bloatware"))
                {
                    return;
                }
                {
                    int successCount = 0;
                    int failCount = 0;

                    foreach (var app in selectedApps)
                    {
                        var result = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName);
                        
                        if (result.Success)
                        {
                            successCount++;
                            app.IsInstalled = false;
                        }
                        else
                        {
                            failCount++;
                        }
                    }

                    string message = $"Remoção concluída:\n\n✅ {successCount} app(s) removido(s) com sucesso";
                    if (failCount > 0)
                    {
                        message += $"\n❌ {failCount} app(s) falharam na remoção";
                    }
                    
                    MessageBox.Show(message, "Resultado", MessageBoxButton.OK, MessageBoxImage.Information);
                    
                    // Recarrega a lista após 1 segundo
                    await Task.Delay(1000);
                    await LoadApps();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemoveSelected_Click", ex.Message);
            }
            finally
            {
                _isBloatwareOperation = false;
            }
        }
    }
}