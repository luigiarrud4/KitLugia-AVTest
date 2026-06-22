using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MessageBox = System.Windows.MessageBox;
using KitLugia.Core;
using KitLugia.Core.UninstallTools;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Helpers;

// --- CORREÇÃO DOS CONFLITOS DE AMBIGUIDADE ---
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class AppsPage : Page
    {
        // Bloatware
        private ObservableCollection<BloatwareApp>? BloatwareCollection;
        private ObservableCollection<BloatwareApp>? FilteredBloatwareCollection;
        private CancellationTokenSource? _bloatwareCts;

        // Programs
        private ObservableCollection<ProgramViewModel>? ProgramsCollection;
        private ObservableCollection<ProgramViewModel>? FilteredProgramsCollection;
        private CancellationTokenSource? _programsCts;
        private bool _programsLoaded = false;
        private bool _junkLoaded = false;

        private bool _isAppOperation;

        // Junk (leftovers persistence)
        private List<LeftoverJunkItem>? _junkItems;

        public Visibility MaxCleanupTabVisible
        {
            get { return (Visibility)GetValue(MaxCleanupTabVisibleProperty); }
            set { SetValue(MaxCleanupTabVisibleProperty, value); }
        }
        public static readonly DependencyProperty MaxCleanupTabVisibleProperty =
            DependencyProperty.Register(nameof(MaxCleanupTabVisible), typeof(Visibility), typeof(AppsPage), new PropertyMetadata(Visibility.Visible));

        // Inline review panel
        private List<AppCleanupItem> _reviewFileItems = new();
        private List<AppCleanupItem> _reviewRegItems = new();
        private ProgramViewModel? _reviewProgramContext;
        private BloatwareApp? _reviewBloatwareContext;
        private int _reviewFilesDeleted;
        private int _reviewRegDeleted;
        private DeepUninstaller.UninstallResult? _reviewResult;
        private TaskCompletionSource<bool>? _confirmTcs;

        public AppsPage()
        {
            InitializeComponent();
            // Carrega apenas Bloatware inicialmente (rápido)
            LoadBloatware();
            // Programs carrega apenas quando aba é selecionada (lazy)
            MainTabs.SelectionChanged += MainTabs_SelectionChanged;
            this.Unloaded += AppsPage_Unloaded;
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (MainTabs.SelectedItem is TabItem selectedTab)
                {
                    // Se aba de Programs foi selecionada e ainda não carregou
                    if (selectedTab.Header?.ToString()?.Contains("Programas") == true && !_programsLoaded)
                    {
                        _programsLoaded = true;
                        LoadPrograms();
                    }
                    // Aba de Resíduos carrega lazy também
                    if (selectedTab.Header?.ToString()?.Contains("Resíduos") == true && !_junkLoaded)
                    {
                        _junkLoaded = true;
                        LoadJunkItems();
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"Erro ao trocar aba: {ex.Message}");
                // Tentar recuperar gracefully
                try
                {
                    if (MainTabs?.Items.Count > 0)
                    {
                        MainTabs.SelectedIndex = 0; // Voltar para primeira aba
                    }
                }
                catch
                {
                    KitLugia.Core.Logger.Log("Erro crítico ao recuperar de falha de aba");
                }
            }
        }

        public void Cleanup()
        {
            try
            {
                // Cancelar tokens ANTES de limpar collections
                _bloatwareCts?.Cancel();
                _programsCts?.Cancel();
                
                // Tokens already cancelled — background tasks will observe them asynchronously
                // Note: Do NOT add Thread.Sleep here (it would freeze UI during tab navigation)
                
                // Limpar UI de forma segura na UI thread
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Cleanup Bloatware UI
                        BloatwareList.ItemsSource = null;
                        BloatwareList.Items.Clear();
                        
                        // Cleanup Programs UI
                        ProgramsList.ItemsSource = null;
                        ProgramsList.Items.Clear();
                    }
                    catch (Exception ex)
                    {
                        KitLugia.Core.Logger.Log($"Erro ao limpar UI: {ex.Message}");
                    }
                });
                
                // Cleanup Bloatware Data
                BloatwareCollection?.Clear();
                FilteredBloatwareCollection?.Clear();
                BloatwareCollection = null;
                FilteredBloatwareCollection = null;

                // Cleanup Programs Data
                ProgramsCollection?.Clear();
                FilteredProgramsCollection?.Clear();
                ProgramsCollection = null;
                FilteredProgramsCollection = null;

                // Dispor resources
                _bloatwareCts?.Dispose();
                _programsCts?.Dispose();
                _bloatwareCts = null;
                _programsCts = null;

                // Remover event handlers
                this.Unloaded -= AppsPage_Unloaded;
                MainTabs.SelectionChanged -= MainTabs_SelectionChanged;

                // Limpar DataContext
                this.DataContext = null;

                // Forçar GC e liberar memória
                
                KitLugia.Core.Logger.Log("AppsPage cleanup concluído com sucesso");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"Erro crítico no Cleanup: {ex.Message}");
            }
        }

        private void AppsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        #region BLOATWARE (UWP)

        private async Task LoadBloatware()
        {
            if (BloatwareLoadingPanel != null) BloatwareLoadingPanel.Visibility = Visibility.Visible;
            if (BloatwareList != null) BloatwareList.ItemsSource = null;

            if (BloatwareIconProgressText != null) BloatwareIconProgressText.Text = "Ícones carregados: 0/0";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Carregando Apps Bloatware", "Apps");
            bool success = true;
            string message = "Apps bloatware carregados com sucesso";

            var oldIcons = new Dictionary<string, object?>(200, StringComparer.OrdinalIgnoreCase);
            if (BloatwareCollection != null)
            {
                foreach (var app in BloatwareCollection)
                {
                    if (app.Icon != null)
                    {
                        oldIcons[app.PackageName.Split('_')[0]] = app.Icon;
                    }
                }
            }

            BloatwareCollection = new ObservableCollection<BloatwareApp>();

            _bloatwareCts = new CancellationTokenSource();
            var token = _bloatwareCts.Token;

            try
            {
                var apps = await Task.Run(() => SystemTweaks.GetBloatwareAppsStatus(), token);

                foreach (var app in apps)
                {
                    if (BloatwareCollection != null)
                    {
                        string packageName = app.PackageName.Split('_')[0];
                        if (oldIcons.ContainsKey(packageName))
                        {
                            app.Icon = oldIcons[packageName];
                        }
                        BloatwareCollection.Add(app);
                    }
                }

                FilteredBloatwareCollection = new ObservableCollection<BloatwareApp>(BloatwareCollection ?? Enumerable.Empty<BloatwareApp>());
                if (BloatwareList != null) BloatwareList.ItemsSource = FilteredBloatwareCollection;

                if (apps.Any())
                    _ = Task.Run(() => LoadBloatwareIconsAsync(token));
            }
            catch (OperationCanceledException)
            {
                success = false;
                message = "Carregamento cancelado";
            }
            finally
            {
                if (BloatwareLoadingPanel != null) BloatwareLoadingPanel.Visibility = Visibility.Collapsed;
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, success, message);
            }
        }

        private async Task LoadBloatwareIconsAsync(CancellationToken token)
        {
            if (BloatwareCollection == null) return;

            int totalIcons = BloatwareCollection.Count;

            var items = BloatwareCollection
                .Select((app, idx) => (App: app, Index: idx, Pkg: app.PackageName.Split('_')[0]))
                .Where(x => x.App.Icon == null)
                .ToList();

            int loadedIcons = totalIcons - items.Count;

            if (items.Count == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (BloatwareIconProgressText != null)
                    BloatwareIconProgressText.Text = $"Ícones carregados: {loadedIcons}/{totalIcons}";
            });

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

            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var (idx, icon) in results)
                {
                    if (icon != null && idx < BloatwareCollection.Count)
                        BloatwareCollection[idx].Icon = icon;
                }

                if (BloatwareIconProgressText != null)
                    BloatwareIconProgressText.Text = $"Ícones carregados: {BloatwareCollection.Count(a => a.Icon != null)}/{totalIcons}";
            });
        }

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

        private async void BtnBloatwareAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                if (sender is Button btn && btn.Tag is BloatwareApp app)
                {
                    if (!app.IsInstalled) return;

                    // Feedback visual IMEDIATO: botão mostra "⏳" antes de qualquer bloqueio
                    btn.Content = "⏳";
                    btn.IsEnabled = false;

                    // Confirmação simples
                    if (!await ShowConfirmAsync(
                        $"Remover {app.DisplayName}?\n\n" +
                        "O app será desinstalado e os resíduos ficarão disponíveis na aba \"Resíduos\" para limpeza posterior.",
                        "Remover App"))
                    {
                        btn.Content = "REMOVER";
                        btn.IsEnabled = true;
                        return;
                    }

                    BloatwareLoadingPanel.Visibility = Visibility.Visible;
                    IProgress<string> bloatProgress = new Progress<string>(msg =>
                    {
                        if (TxtBloatwareProgress != null)
                            TxtBloatwareProgress.Text = msg;
                    });

                    bloatProgress.Report($"Pré-scan de {app.DisplayName}...");
                    var (preFiles, preReg) = await Task.Run(() => DeepUninstaller.ScanLeftovers(app.DisplayName, ""));
                    var preFileSet = new HashSet<string>(preFiles.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
                    var preRegSet = new HashSet<string>(preReg.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

                    string baseName = app.PackageName.Split('_')[0];
                    string pkgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", baseName);
                    if (Directory.Exists(pkgFolder))
                        preFileSet.Add(pkgFolder);

                    bloatProgress.Report($"Removendo {app.DisplayName}...");
                    var result = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName);

                    bloatProgress.Report($"Pós-scan de {app.DisplayName}...");
                    var (postFiles, postReg) = await Task.Run(() => DeepUninstaller.ScanLeftovers(app.DisplayName, ""));
                    var postFileSet = new HashSet<string>(postFiles.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
                    var postRegSet = new HashSet<string>(postReg.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

                    // Diff Revo-style: confirmed = items that existed before AND still exist after
                    var confirmedFiles = postFileSet.Intersect(preFileSet).ToList();
                    var confirmedReg = postRegSet.Intersect(preRegSet).ToList();
                    // Heuristic = items found only after removal
                    var heuristicFiles = postFileSet.Except(preFileSet).ToList();
                    var heuristicReg = postRegSet.Except(preRegSet).ToList();

                    // Package folder was in pre-scan (deleted by uninstaller) → add to confirmed
                    if (preFileSet.Contains(pkgFolder) && !confirmedFiles.Contains(pkgFolder, StringComparer.OrdinalIgnoreCase))
                        confirmedFiles.Add(pkgFolder);

                    var scanResult = new DeepUninstaller.UninstallResult
                    {
                        LeftoverFiles = confirmedFiles,
                        LeftoverRegistry = confirmedReg,
                        HeuristicFiles = heuristicFiles,
                        HeuristicRegistry = heuristicReg,
                        BaselineFileCount = preFileSet.Count,
                        BaselineRegistryCount = preRegSet.Count,
                    };

                    // Save leftovers to junk tab silently
                    if (confirmedFiles.Count > 0 || confirmedReg.Count > 0 || heuristicFiles.Count > 0 || heuristicReg.Count > 0)
                    {
                        LeftoverJunkManager.Add(new LeftoverJunkEntry
                        {
                            AppName = app.DisplayName,
                            Date = DateTime.Now,
                            LeftoverFiles = confirmedFiles,
                            LeftoverRegistry = confirmedReg,
                            HeuristicFiles = heuristicFiles,
                            HeuristicRegistry = heuristicReg,
                            BaselineFileCount = preFileSet.Count,
                            BaselineRegistryCount = preRegSet.Count
                        });
                    }

                    BloatwareLoadingPanel.Visibility = Visibility.Collapsed;
                    btn.Content = "REMOVER";
                    btn.IsEnabled = true;

                    if (result.Success)
                        MessageBox.Show($"✅ {app.DisplayName} removido com sucesso!", "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
                    else
                        MessageBox.Show($"⚠️ Falha ao remover {app.DisplayName}.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);

                    await Task.Delay(500);
                    await LoadBloatware();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnBloatwareAction_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private void BtnCreateRestorePoint_Click(object sender, RoutedEventArgs e)
        {
            if (DeepUninstaller.TryCreateRestorePoint("KitLugia: Manual Restore Point"))
                MessageBox.Show("✅ Ponto de restauração criado com sucesso!", "Ponto de Restauração", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("⚠️ Não foi possível criar o ponto de restauração.\nVerifique se o serviço 'Volume Shadow Copy' está ativo.", "Ponto de Restauração", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnSelectAllBloatware_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredBloatwareCollection == null) return;
            foreach (var app in FilteredBloatwareCollection)
                app.IsSelected = true;
        }

        private void BtnDeselectAllBloatware_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredBloatwareCollection == null) return;
            foreach (var app in FilteredBloatwareCollection)
                app.IsSelected = false;
        }

        private void BtnSelectAllPrograms_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredProgramsCollection == null) return;
            foreach (var program in FilteredProgramsCollection)
                program.IsSelected = true;
        }

        private void BtnDeselectAllPrograms_Click(object sender, RoutedEventArgs e)
        {
            if (FilteredProgramsCollection == null) return;
            foreach (var program in FilteredProgramsCollection)
                program.IsSelected = false;
        }

        private void BtnHunterMode_Click(object sender, RoutedEventArgs e)
        {
            var hunter = new KitLugia.GUI.Windows.HunterWindow();
            hunter.Show();
        }

        private async void BtnRefreshBloatware_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                await LoadBloatware();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefreshBloatware_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private void TxtSearchBloatware_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && BloatwareCollection != null)
            {
                string searchText = textBox.Text.ToLower();

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    FilteredBloatwareCollection = new ObservableCollection<BloatwareApp>(BloatwareCollection);
                }
                else
                {
                    var filtered = BloatwareCollection.Where(app =>
                        app.DisplayName.ToLower().Contains(searchText) ||
                        app.PackageName.ToLower().Contains(searchText)).ToList();
                    FilteredBloatwareCollection = new ObservableCollection<BloatwareApp>(filtered);
                }

                if (BloatwareList != null)
                {
                    BloatwareList.ItemsSource = FilteredBloatwareCollection;
                }
            }
        }

        private async void BtnRemoveBloatwareSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                if (FilteredBloatwareCollection == null) return;

                var selectedApps = FilteredBloatwareCollection.Where(app => app.IsSelected && app.IsInstalled).ToList();

                if (selectedApps.Count == 0)
                {
                    MessageBox.Show("Nenhum app selecionado para remoção.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string appNames = string.Join("\n", selectedApps.Select(a => a.DisplayName));
                if (!await ShowConfirmAsync(
                    $"Remover {selectedApps.Count} app(s) selecionado(s)?\n\n{appNames}\n\nOs resíduos ficarão na aba \"Resíduos\" para limpeza posterior.",
                    "Remover Apps"))
                {
                    return;
                }
                {
                    int total = selectedApps.Count;
                    int successCount = 0;
                    int failCount = 0;
                    int completedCount = 0;

                    BloatwareLoadingPanel.Visibility = Visibility.Visible;
                    if (TxtBloatwareProgress != null)
                        TxtBloatwareProgress.Text = $"Pré-scan {0}/{total}...";

                    // Pré-scan Revo-style para cada app (paralelo)
                    IProgress<string> batchProgress = new Progress<string>(msg =>
                    {
                        if (TxtBloatwareProgress != null)
                            TxtBloatwareProgress.Text = msg;
                    });

                    var preScanResults = await Task.WhenAll(selectedApps.Select(async app =>
                    {
                        batchProgress.Report($"Pré-scan {app.DisplayName}...");
                        var (preFiles, preReg) = await Task.Run(() => DeepUninstaller.ScanLeftovers(app.DisplayName, ""));
                        var preFileSet = new HashSet<string>(preFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                        var preRegSet = new HashSet<string>(preReg.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
                        string baseName = app.PackageName.Split('_')[0];
                        string pkgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", baseName);
                        if (Directory.Exists(pkgFolder))
                            preFileSet.Add(pkgFolder);
                        return new { App = app, PreFileSet = preFileSet, PreRegSet = preRegSet, PkgFolder = pkgFolder };
                    }));

                    var preScanLookup = preScanResults.ToDictionary(r => r.App.PackageName);

                    batchProgress.Report($"Removendo 0/{total}...");

                    int maxConcurrent = Math.Min(3, Environment.ProcessorCount);
                    var semaphore = new SemaphoreSlim(maxConcurrent);
                    var removeTasks = selectedApps.Select(async app =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            bool ok = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName) is { Success: true };

                            int completed = Interlocked.Increment(ref completedCount);
                            batchProgress.Report($"Removendo {completed}/{total}...");

                            return (app, ok);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }).ToArray();

                    var removeResults = await Task.WhenAll(removeTasks);
                    successCount = removeResults.Count(r => r.ok);
                    failCount = removeResults.Count(r => !r.ok);
                    var removed = removeResults.Where(r => r.ok).Select(r => r.app).ToList();
                    foreach (var app in removed)
                        app.IsInstalled = false;

                    // Pós-scan Revo-style para apps removidos (paralelo)
                    if (removed.Count > 0)
                    {
                        var postScanTasks = removed.Select(async app =>
                        {
                            batchProgress.Report($"Pós-scan {app.DisplayName}...");
                            var (postFiles, postReg) = await Task.Run(() => DeepUninstaller.ScanLeftovers(app.DisplayName, ""));
                            return new { App = app, PostFiles = postFiles, PostReg = postReg };
                        });
                        var postScanResults = await Task.WhenAll(postScanTasks);

                        foreach (var ps in postScanResults)
                        {
                            if (!preScanLookup.TryGetValue(ps.App.PackageName, out var pre)) continue;

                            var postFileSet = new HashSet<string>(ps.PostFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                            var postRegSet = new HashSet<string>(ps.PostReg.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);

                            var confirmedFiles = postFileSet.Intersect(pre.PreFileSet).ToList();
                            var confirmedReg = postRegSet.Intersect(pre.PreRegSet).ToList();
                            var heuristicFiles = postFileSet.Except(pre.PreFileSet).ToList();
                            var heuristicReg = postRegSet.Except(pre.PreRegSet).ToList();

                            if (pre.PreFileSet.Contains(pre.PkgFolder) &&
                                !confirmedFiles.Contains(pre.PkgFolder, StringComparer.OrdinalIgnoreCase))
                                confirmedFiles.Add(pre.PkgFolder);

                            if (confirmedFiles.Count > 0 || confirmedReg.Count > 0 || heuristicFiles.Count > 0 || heuristicReg.Count > 0)
                            {
                                LeftoverJunkManager.Add(new LeftoverJunkEntry
                                {
                                    AppName = ps.App.DisplayName,
                                    Date = DateTime.Now,
                                    LeftoverFiles = confirmedFiles,
                                    LeftoverRegistry = confirmedReg,
                                    HeuristicFiles = heuristicFiles,
                                    HeuristicRegistry = heuristicReg,
                                    BaselineFileCount = pre.PreFileSet.Count,
                                    BaselineRegistryCount = pre.PreRegSet.Count
                                });
                            }
                        }
                    }

                    if (TxtBloatwareProgress != null) TxtBloatwareProgress.Text = "";
                    BloatwareLoadingPanel.Visibility = Visibility.Collapsed;
                    string message = $"Remoção concluída:\n\n✅ {successCount} app(s) removidos";
                    if (failCount > 0) message += $"\n⚠️ {failCount} falharam";
                    message += "\n\nOs resíduos podem ser limpos na aba \"Resíduos\".";
                    MessageBox.Show(message, "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);

                    await Task.Delay(1000);
                    await LoadBloatware();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemoveBloatwareSelected_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        #endregion

        #region PROGRAMS (REGISTRY)

        private async void LoadPrograms()
        {
            if (ProgramsLoadingPanel != null) ProgramsLoadingPanel.Visibility = Visibility.Visible;
            if (ProgramsList != null) ProgramsList.ItemsSource = null;

            try
            {
                _programsCts?.Cancel();
                _programsCts = new CancellationTokenSource();

                var programs = await Task.Run(() => RegistryProgramFactory.GetInstalledPrograms());

                ProgramsCollection = new ObservableCollection<ProgramViewModel>(
                    programs.Select(p => new ProgramViewModel(p)));

                if (ProgramsIconProgressText != null)
                    ProgramsIconProgressText.Text = $"Ícones carregados: 0/{ProgramsCollection.Count}";

                // Mostra a lista
                FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(ProgramsCollection);
                if (ProgramsList != null) ProgramsList.ItemsSource = FilteredProgramsCollection;

                // Carrega ícones em paralelo após delay para lista aparecer rápido
                await Task.Delay(100);
                await LoadProgramsIconsAsync(_programsCts.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar programas: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ProgramsLoadingPanel != null) ProgramsLoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadProgramsIconsAsync(CancellationToken cancellationToken)
        {
            if (ProgramsCollection == null) return;

            var dispatcher = Dispatcher;
            var semaphore = new SemaphoreSlim(5, 5); // Limite de 5 ícones simultâneos (mais responsivo)
            var tasks = new List<Task>(200);

            foreach (var program in ProgramsCollection)
            {
                if (cancellationToken.IsCancellationRequested) break;

                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        BitmapSource? icon = null;

                        try
                        {
                            if (!string.IsNullOrEmpty(program.DisplayIcon))
                                icon = ProgramIconHelper.GetIconFromFile(program.DisplayIcon.Trim().Trim('"'));

                            if (icon == null && !string.IsNullOrEmpty(program.UninstallString))
                                icon = ProgramIconHelper.GetIconFromFile(ExtractPathFromUninstallString(program.UninstallString));

                            if (icon == null && !string.IsNullOrEmpty(program.InstallLocation))
                                icon = ProgramIconHelper.GetIconFromDirectory(program.InstallLocation);

                            icon ??= ProgramIconHelper.GetGenericIcon();
                        }
                        catch
                        {
                            icon = ProgramIconHelper.GetGenericIcon();
                        }

                        if (icon != null && !cancellationToken.IsCancellationRequested)
                        {
                            await dispatcher.InvokeAsync(() =>
                            {
                                program.Icon = icon;

                                if (ProgramsCollection != null)
                                {
                                    int loadedCount = ProgramsCollection.Count(p => p.Icon != null);
                                    if (ProgramsIconProgressText != null)
                                    {
                                        ProgramsIconProgressText.Text = $"Ícones carregados: {loadedCount}/{ProgramsCollection.Count}";
                                    }
                                }
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);

            await dispatcher.InvokeAsync(() =>
            {
                if (ProgramsIconProgressText != null)
                {
                    ProgramsIconProgressText.Text = "Ícones carregados!";
                }
            });
        }

        private string? ExtractPathFromUninstallString(string uninstallString)
        {
            try
            {
                uninstallString = uninstallString.Trim('"');

                if (uninstallString.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(uninstallString))
                {
                    return uninstallString;
                }

                if (uninstallString.Contains(" "))
                {
                    var parts = uninstallString.Split(new[] { ' ' }, 2);
                    if (parts.Length > 0 && File.Exists(parts[0]))
                    {
                        return parts[0];
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void TxtSearchPrograms_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && ProgramsCollection != null)
            {
                string searchText = textBox.Text.ToLower();

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(ProgramsCollection);
                }
                else
                {
                    var filtered = ProgramsCollection.Where(p =>
                        p.DisplayName.ToLower().Contains(searchText) ||
                        p.Publisher.ToLower().Contains(searchText)).ToList();
                    FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(filtered);
                }

                if (ProgramsList != null)
                {
                    ProgramsList.ItemsSource = FilteredProgramsCollection;
                }
            }
        }

        private async void BtnProgramRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                if (sender is Button btn && btn.Tag is ProgramViewModel program)
                {
                    // Feedback visual IMEDIATO: botão mostra "⏳" antes de qualquer bloqueio
                    btn.Content = "⏳";
                    btn.IsEnabled = false;

                    // Confirmação simples
                    if (!await ShowConfirmAsync(
                        $"Remover {program.DisplayName}?\n\n" +
                        "O app será desinstalado e os resíduos ficarão disponíveis na aba \"Resíduos\" para limpeza posterior.",
                        "Remover App"))
                    {
                        btn.Content = "REMOVER";
                        btn.IsEnabled = true;
                        return;
                    }

                    ProgramsLoadingPanel.Visibility = Visibility.Visible;

                    IProgress<string> progress = new Progress<string>(msg =>
                    {
                        if (TxtProgramsProgress != null)
                            TxtProgramsProgress.Text = msg;
                    });

                    progress.Report($"Removendo {program.DisplayName}...");

                    // Run DeepUninstall (uninstall + scan) entirely on background thread
                    var scanResult = await Task.Run(() =>
                    {
                        var result = DeepUninstaller.DeepUninstallProgram(
                            program.DisplayName, program.UninstallString,
                            program.InstallLocation, program.Publisher, program.DisplayIcon, false, progress).GetAwaiter().GetResult();
                        return result;
                    });

                    if (TxtProgramsProgress != null) TxtProgramsProgress.Text = "";
                    ProgramsLoadingPanel.Visibility = Visibility.Collapsed;
                    btn.Content = "REMOVER";
                    btn.IsEnabled = true;

                    // Save leftovers to junk tab silently
                    int leftoversCount = scanResult.LeftoverFiles.Count + scanResult.LeftoverRegistry.Count;
                    int heuristicCount = scanResult.HeuristicFiles.Count + scanResult.HeuristicRegistry.Count;
                    if (leftoversCount > 0 || heuristicCount > 0)
                    {
                        LeftoverJunkManager.Add(new LeftoverJunkEntry
                        {
                            AppName = program.DisplayName,
                            Date = DateTime.Now,
                            LeftoverFiles = scanResult.LeftoverFiles,
                            LeftoverRegistry = scanResult.LeftoverRegistry,
                            HeuristicFiles = scanResult.HeuristicFiles,
                            HeuristicRegistry = scanResult.HeuristicRegistry,
                            BaselineFileCount = scanResult.BaselineFileCount,
                            BaselineRegistryCount = scanResult.BaselineRegistryCount
                        });
                    }

                    string msgResult = scanResult.UninstallSuccess
                        ? $"✅ {program.DisplayName} desinstalado com sucesso!"
                        : $"⚠️ {program.DisplayName} pode não ter sido completamente desinstalado.";
                    if (leftoversCount > 0)
                        msgResult += $"\n\n🗑️ {leftoversCount} resíduo(s) salvos na aba \"Resíduos\".";
                    MessageBox.Show(msgResult, "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Remove from list
                    FilteredProgramsCollection?.Remove(program);
                    ProgramsCollection?.Remove(program);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnProgramRemove_Click", ex.ToString());
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private async void BtnRemoveProgramsSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                if (FilteredProgramsCollection == null) return;

                var selectedPrograms = FilteredProgramsCollection.Where(p => p.IsSelected).ToList();

                if (selectedPrograms.Count == 0)
                {
                    MessageBox.Show("Nenhum programa selecionado para remoção.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string programNames = string.Join("\n", selectedPrograms.Select(p => p.DisplayName));
                if (!await ShowConfirmAsync(
                    $"Remover {selectedPrograms.Count} programa(s) selecionado(s)?\n\n{programNames}\n\nOs resíduos ficarão na aba \"Resíduos\" para limpeza posterior.",
                    "Remover Apps"))
                {
                    return;
                }
                {
                    int total = selectedPrograms.Count;
                    int successCount = 0;
                    int failCount = 0;

                    ProgramsLoadingPanel.Visibility = Visibility.Visible;

                    IProgress<string> batchProgress = new Progress<string>(msg =>
                    {
                        if (TxtProgramsProgress != null)
                            TxtProgramsProgress.Text = msg;
                    });

                    batchProgress.Report($"Processando 0/{total}...");

                    int maxConcurrent = Math.Min(2, Environment.ProcessorCount);
                    var semaphore = new SemaphoreSlim(maxConcurrent);
                    int progressIdx = 0;

                    var removeTasks = selectedPrograms.Select(async program =>
                    {
                        int idx = Interlocked.Increment(ref progressIdx);
                        batchProgress.Report($"[{idx}/{total}] {program.DisplayName} — Removendo...");

                        await semaphore.WaitAsync();
                        DeepUninstaller.UninstallResult result;
                        try
                        {
                            result = await Task.Run(() => DeepUninstaller.DeepUninstallProgram(
                                program.DisplayName, program.UninstallString,
                                program.InstallLocation, program.Publisher, program.DisplayIcon, false, batchProgress));
                        }
                        finally
                        {
                            semaphore.Release();
                        }

                        bool ok = result.UninstallSuccess;

                        if (result.LeftoverFiles.Count > 0 || result.LeftoverRegistry.Count > 0 || result.HeuristicFiles.Count > 0 || result.HeuristicRegistry.Count > 0)
                        {
                            LeftoverJunkManager.Add(new LeftoverJunkEntry
                            {
                                AppName = program.DisplayName,
                                Date = DateTime.Now,
                                LeftoverFiles = result.LeftoverFiles,
                                LeftoverRegistry = result.LeftoverRegistry,
                                HeuristicFiles = result.HeuristicFiles,
                                HeuristicRegistry = result.HeuristicRegistry,
                                BaselineFileCount = result.BaselineFileCount,
                                BaselineRegistryCount = result.BaselineRegistryCount
                            });
                        }

                        return (program, ok);
                    }).ToArray();

                    var progResults = await Task.WhenAll(removeTasks);
                    successCount = progResults.Count(r => r.ok);
                    failCount = progResults.Count(r => !r.ok);
                    foreach (var (program, ok) in progResults.Where(r => r.ok))
                    {
                        FilteredProgramsCollection?.Remove(program);
                        ProgramsCollection?.Remove(program);
                    }

                    if (TxtProgramsProgress != null) TxtProgramsProgress.Text = "";
                    ProgramsLoadingPanel.Visibility = Visibility.Collapsed;
                    string message = $"Remoção concluída:\n\n✅ {successCount} programa(s) removidos";
                    if (failCount > 0)
                        message += $"\n⚠️ {failCount} falharam";
                    message += "\n\nOs resíduos podem ser limpos na aba \"Resíduos\".";

                    MessageBox.Show(message, "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemoveProgramsSelected_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private async void BtnRefreshPrograms_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                LoadPrograms();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefreshPrograms_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        #endregion

        #region JUNK (leftover persistence with detailed selection)

        private void RefreshJunkList()
        {
            if (_junkItems == null) return;

            JunkItemsList.ItemsSource = null;
            JunkItemsList.ItemsSource = _junkItems;

            bool hasItems = _junkItems.Count > 0;
            if (JunkSection != null) JunkSection.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            if (JunkEmptySection != null) JunkEmptySection.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            if (TxtMaxCleanupInfo != null)
                TxtMaxCleanupInfo.Text = hasItems
                    ? $"{_junkItems.Count} app(s) com resíduos pendentes"
                    : "Nenhum resíduo pendente";
            UpdateJunkCleanCount();
        }

        private void UpdateJunkCleanCount()
        {
            if (_junkItems == null || BtnMaxClean == null) return;
            int totalSelected = _junkItems.Sum(j => j.SelectedCount);
            BtnMaxClean.Content = totalSelected > 0
                ? $"\U0001F5D1 Limpar Selecionados ({totalSelected})"
                : "\U0001F5D1 Limpar Selecionados (0)";
            BtnMaxClean.IsEnabled = totalSelected > 0;
        }

        private void LoadJunkItems()
        {
            _junkItems = new List<LeftoverJunkItem>();
            if (TxtMaxCleanupInfo != null) TxtMaxCleanupInfo.Text = "Carregando...";
            try
            {
                var entries = LeftoverJunkManager.Load();
                foreach (var e in entries)
                {
                    var item = new LeftoverJunkItem
                    {
                        AppName = e.AppName,
                        Date = e.Date,
                        BaselineFileCount = e.BaselineFileCount,
                        BaselineRegistryCount = e.BaselineRegistryCount,
                        Files = e.LeftoverFiles.Select(f => new JunkDetailItem
                        {
                            Path = f,
                            IsFile = true,
                            CanDelete = true,
                            Safety = CleanupSafety.Moderate,
                            IsSelected = true
                        }).ToList(),
                        Registry = e.LeftoverRegistry.Select(r => new JunkDetailItem
                        {
                            Path = r,
                            IsFile = false,
                            CanDelete = true,
                            Safety = CleanupSafety.Moderate,
                            IsSelected = true
                        }).ToList()
                    };
                    // Load heuristic items (post-scan only, lower confidence)
                    foreach (var hf in e.HeuristicFiles)
                    {
                        item.Files.Add(new JunkDetailItem
                        {
                            Path = hf, IsFile = true, CanDelete = true,
                            Safety = CleanupSafety.Moderate, IsSelected = false,
                            IsHeuristic = true
                        });
                    }
                    foreach (var hr in e.HeuristicRegistry)
                    {
                        item.Registry.Add(new JunkDetailItem
                        {
                            Path = hr, IsFile = false, CanDelete = true,
                            Safety = CleanupSafety.Moderate, IsSelected = false,
                            IsHeuristic = true
                        });
                    }
                    // Reclassify for safety (heuristic items remain unselected)
                    ReclassifyJunkItem(item);
                    _junkItems.Add(item);
                }
                RefreshJunkList();
            }
            catch (Exception ex)
            {
                Logger.LogError("LoadJunkItems", ex.Message);
                if (TxtMaxCleanupInfo != null) TxtMaxCleanupInfo.Text = $"Erro: {ex.Message}";
            }
        }

        private static void ReclassifyJunkItem(LeftoverJunkItem item)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string temp = Environment.GetFolderPath(Environment.SpecialFolder.InternetCache);
            string tempPath = Environment.GetEnvironmentVariable("TEMP") ?? "";
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            foreach (var f in item.Files)
            {
                // Confirmed items (pre-scan baseline) are always deletable and safe
                if (!f.IsHeuristic)
                {
                    f.CanDelete = true;
                    f.IsSelected = true;
                    f.Safety = CleanupSafety.Safe;
                    continue;
                }

                string fl = f.Path.ToLowerInvariant();
                if (fl.Contains("\\temp\\") || fl.Contains(tempPath.ToLowerInvariant()))
                { f.Safety = CleanupSafety.Safe; f.CanDelete = true; f.IsSelected = true; }
                else if (fl.StartsWith(localAppData.ToLowerInvariant()))
                { f.Safety = CleanupSafety.Moderate; f.CanDelete = true; f.IsSelected = true; }
                else if (fl.StartsWith(appData.ToLowerInvariant()))
                { f.Safety = CleanupSafety.Moderate; f.CanDelete = true; f.IsSelected = true; }
                else if (fl.StartsWith(programData.ToLowerInvariant()))
                { f.Safety = CleanupSafety.Uncertain; f.CanDelete = false; f.IsSelected = false; }
                else
                { f.Safety = CleanupSafety.Moderate; f.CanDelete = true; f.IsSelected = true; }
            }

            foreach (var r in item.Registry)
            {
                // Confirmed items (pre-scan baseline) are always deletable and safe
                if (!r.IsHeuristic)
                {
                    r.CanDelete = true;
                    r.IsSelected = true;
                    r.Safety = CleanupSafety.Safe;
                    continue;
                }

                string lower = r.Path.ToLowerInvariant();
                if (lower.StartsWith("hkey_local_machine") || lower.StartsWith("hklm"))
                { r.Safety = CleanupSafety.Uncertain; r.CanDelete = false; r.IsSelected = false; }
                else
                { r.Safety = CleanupSafety.Moderate; r.CanDelete = true; r.IsSelected = true; }
            }
        }

        private void JunkCardHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LeftoverJunkItem item)
                item.IsExpanded = !item.IsExpanded;
        }

        private void BtnMaxSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_junkItems == null) return;
            bool anyUnselected = _junkItems.Any(j => j.SelectedCount < j.TotalSelectableCount);
            foreach (var item in _junkItems)
            {
                foreach (var f in item.Files) { if (f.CanDelete) f.IsSelected = anyUnselected; }
                foreach (var r in item.Registry) { if (r.CanDelete) r.IsSelected = anyUnselected; }
            }
            RefreshJunkList();
        }

        private async void BtnMaxClean_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation || _junkItems == null) return;
            _isAppOperation = true;
            try
            {
                var toDeleteFiles = _junkItems
                    .SelectMany(j => j.Files.Where(f => f.IsSelected && f.CanDelete))
                    .Select(f => f.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var toDeleteReg = _junkItems
                    .SelectMany(j => j.Registry.Where(r => r.IsSelected && r.CanDelete))
                    .Select(r => r.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int total = toDeleteFiles.Count + toDeleteReg.Count;
                if (total == 0) return;

                if (!await ShowConfirmAsync(
                    $"Limpar {total} item(ns) de resíduos?\n\n" +
                    $"{toDeleteFiles.Count} arquivo(s)\n{toDeleteReg.Count} registro(s)",
                    "Limpar Resíduos"))
                    return;

                if (TxtMaxCleanupStatus != null) TxtMaxCleanupStatus.Text = "Limpando...";

                var result = new DeepUninstaller.UninstallResult();
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await Task.Run(() => DeepUninstaller.PerformCleanup(toDeleteFiles, toDeleteReg, result, ct: cleanupCts.Token));

                // Remove deleted paths from junk entries and persist
                var fileSet = new HashSet<string>(toDeleteFiles, StringComparer.OrdinalIgnoreCase);
                var regSet = new HashSet<string>(toDeleteReg, StringComparer.OrdinalIgnoreCase);

                for (int i = _junkItems.Count - 1; i >= 0; i--)
                {
                    var item = _junkItems[i];
                    item.Files.RemoveAll(f => fileSet.Contains(f.Path) && f.CanDelete);
                    item.Registry.RemoveAll(r => regSet.Contains(r.Path) && r.CanDelete);

                    if (item.Files.Count == 0 && item.Registry.Count == 0)
                        _junkItems.RemoveAt(i);
                }

                // Persist updated junk list (only items with remaining leftovers)
                var toSave = _junkItems.Select(j => new LeftoverJunkEntry
                {
                    AppName = j.AppName,
                    Date = j.Date,
                    LeftoverFiles = j.Files.Select(f => f.Path).ToList(),
                    LeftoverRegistry = j.Registry.Select(r => r.Path).ToList()
                }).ToList();
                LeftoverJunkManager.Save(toSave);

                RefreshJunkList();

                if (TxtMaxCleanupStatus != null)
                    TxtMaxCleanupStatus.Text = $"{result.FilesDeleted + result.RegistryDeleted} item(ns) limpos";

                if (result.Errors.Count > 0)
                    MessageBox.Show($"Erros ao limpar:\n{string.Join("\n", result.Errors.Take(5))}",
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show($"{total} item(ns) limpos com sucesso.",
                        "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnMaxClean_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private void BtnMaxRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadJunkItems();
        }

        private async void BtnJunkClean_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LeftoverJunkItem item && _junkItems != null)
            {
                int index = _junkItems.IndexOf(item);
                if (index < 0) return;

                try
                {
                    btn.IsEnabled = false;
                    btn.Content = "⏳";

                    var selectedFiles = item.Files.Where(f => f.IsSelected && f.CanDelete).Select(f => f.Path).ToList();
                    var selectedReg = item.Registry.Where(r => r.IsSelected && r.CanDelete).Select(r => r.Path).ToList();

                    if (selectedFiles.Count == 0 && selectedReg.Count == 0) return;

                    var result = new DeepUninstaller.UninstallResult();
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    IProgress<string> junkProgress = new Progress<string>(msg =>
                    {
                        if (TxtBloatwareProgress != null)
                            TxtBloatwareProgress.Text = msg;
                    });
                    await Task.Run(() => DeepUninstaller.PerformCleanup(selectedFiles, selectedReg, result, ct: cleanupCts.Token, progress: junkProgress));

                    item.Files.RemoveAll(f => selectedFiles.Contains(f.Path));
                    item.Registry.RemoveAll(r => selectedReg.Contains(r.Path));

                    if (item.Files.Count == 0 && item.Registry.Count == 0)
                        _junkItems.RemoveAt(index);

                    // Persist
                    var toSave = _junkItems.Select(j => new LeftoverJunkEntry
                    {
                        AppName = j.AppName,
                        Date = j.Date,
                        LeftoverFiles = j.Files.Select(f => f.Path).ToList(),
                        LeftoverRegistry = j.Registry.Select(r => r.Path).ToList()
                    }).ToList();
                    LeftoverJunkManager.Save(toSave);

                    RefreshJunkList();

                    int total = selectedFiles.Count + selectedReg.Count;
                    MessageBox.Show($"{item.AppName}: {total} item(ns) limpos.",
                        "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Logger.LogError("BtnJunkClean_Click", ex.Message);
                }
                finally
                {
                    btn.IsEnabled = true;
                    btn.Content = "\U0001F5D1 Limpar";
                }
            }
        }

        private void BtnJunkDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LeftoverJunkItem item && _junkItems != null)
            {
                int index = _junkItems.IndexOf(item);
                if (index < 0) return;

                _junkItems.RemoveAt(index);
                LeftoverJunkManager.RemoveAt(index);
                RefreshJunkList();
            }
        }

        private void BtnJunkSelectAllFiles_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LeftoverJunkItem item)
            {
                bool anyUnselected = item.Files.Any(f => f.CanDelete && !f.IsSelected);
                foreach (var f in item.Files)
                    if (f.CanDelete) f.IsSelected = anyUnselected;
                RefreshJunkList();
            }
        }

        private void BtnJunkSelectAllReg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is LeftoverJunkItem item)
            {
                bool anyUnselected = item.Registry.Any(r => r.CanDelete && !r.IsSelected);
                foreach (var r in item.Registry)
                    if (r.CanDelete) r.IsSelected = anyUnselected;
                RefreshJunkList();
            }
        }

        #endregion

        // ── Inline Review Panel (flat list — no ObservableCollection) ─

        private static List<AppCleanupItem> BuildFileItems(IEnumerable<string> paths, IEnumerable<ScanEntry>? classified = null)
        {
            var safetyMap = classified?.ToDictionary(e => e.Path, e => e.Safety, StringComparer.OrdinalIgnoreCase) ?? new();
            var items = new List<AppCleanupItem>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullPath in paths)
            {
                if (string.IsNullOrEmpty(fullPath)) continue;
                string normalized = fullPath.TrimEnd('\\', '/');
                var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    bool isLast = i == parts.Length - 1;
                    string part = parts[i];
                    string itemPath = string.Join("\\", parts.Take(i + 1));
                    if (seen.Add(itemPath)) // Add returns false if already present — O(1)
                    {
                        bool isNavigational = !isLast && !safetyMap.ContainsKey(itemPath);
                        items.Add(new AppCleanupItem
                        {
                            DisplayPath = part,
                            FullPath = itemPath,
                            IconChar = isLast ? "\uD83D\uDCC4" : "\uD83D\uDCC1",
                            Depth = i,
                            IsFolder = !isLast,
                            IsNavigational = isNavigational,
                            SafetyLevel = safetyMap.TryGetValue(itemPath, out var s) ? s : CleanupSafety.Moderate
                        });
                    }
                }
            }
            foreach (var item in items)
                if (item.IsNavigational || item.SafetyLevel == CleanupSafety.Uncertain)
                    item.IsSelected = false;
            return items;
        }

        private static List<AppCleanupItem> BuildRegistryItems(IEnumerable<string> paths, IEnumerable<ScanEntry>? classified = null)
        {
            var safetyMap = classified?.ToDictionary(e => e.Path, e => e.Safety, StringComparer.OrdinalIgnoreCase) ?? new();
            var items = new List<AppCleanupItem>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var regPath in paths)
            {
                if (string.IsNullOrEmpty(regPath)) continue;
                string normalized = regPath.TrimEnd('\\', '/');
                var parts = normalized.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                int n = parts.Length;
                // Keep only: first part (hive), second-to-last, and last — skip deep intermediate paths
                var keep = new List<int> { 0 };
                if (n > 3) keep.Add(n - 2);
                else if (n == 3) keep.Add(1);
                if (n >= 2) keep.Add(n - 1);
                keep = keep.Distinct().OrderBy(i => i).ToList();
                for (int ki = 0; ki < keep.Count; ki++)
                {
                    int i = keep[ki];
                    bool isLast = i == n - 1;
                    string part = parts[i];
                    // Condensed path for navigation items; leaf uses real full path for deletion
                    var keptParts = keep.Take(ki + 1).Select(idx => parts[idx]).ToArray();
                    string displayKey = string.Join("\\", keptParts);
                    if (seen.Add(displayKey)) // Add returns false if already present — O(1)
                    {
                        string realPathSoFar = string.Join("\\", parts.Take(i + 1));
                        bool isNavigational = !isLast && !safetyMap.ContainsKey(realPathSoFar);
                        items.Add(new AppCleanupItem
                        {
                            DisplayPath = part,
                            FullPath = isLast ? regPath : displayKey,
                            IconChar = isLast ? "\uD83D\uDCD1" : "\uD83D\uDDC2\uFE0F",
                            Depth = ki,
                            IsFolder = !isLast,
                            IsNavigational = isNavigational,
                            SafetyLevel = safetyMap.TryGetValue(realPathSoFar, out var s) ? s : CleanupSafety.Moderate
                        });
                    }
                }
            }
            foreach (var item in items)
                if (item.IsNavigational || item.SafetyLevel == CleanupSafety.Uncertain)
                    item.IsSelected = false;
            return items;
        }

        private void ShowReviewPanel(string appName, DeepUninstaller.UninstallResult scanResult, List<ScanEntry>? classifiedFiles = null, List<ScanEntry>? classifiedReg = null)
        {
            _reviewResult = scanResult;
            _reviewFileItems = BuildFileItems(scanResult.LeftoverFiles, classifiedFiles);
            _reviewRegItems = BuildRegistryItems(scanResult.LeftoverRegistry, classifiedReg);
            _reviewFilesDeleted = 0;
            _reviewRegDeleted = 0;

            ReviewFileList.ItemsSource = null;
            ReviewFileList.ItemsSource = _reviewFileItems;
            ReviewRegList.ItemsSource = null;
            ReviewRegList.ItemsSource = _reviewRegItems;

            ReviewTitle.Text = $"Deep Cleanup — {appName}";
            ReviewSubtitle.Text = "Revise os resíduos encontrados após a desinstalação";
            ReviewInfoText.Text = scanResult.UninstallSuccess
                ? "Desinstalação concluída. Resíduos encontrados:"
                : "Desinstalação pode não ter sido totalmente bem-sucedida. Resíduos encontrados:";
            ReviewInfoCount.Text = $"{_reviewFileItems.Count} arquivo(s), {_reviewRegItems.Count} registro(s)";

            UpdateReviewCounts();
            NormalContent.Visibility = Visibility.Collapsed;
            ReviewPanel.Visibility = Visibility.Visible;
        }

        private void UpdateReviewCounts()
        {
            // Only leaf items (actual scan results) count — folders are visual context only
            int fSel = _reviewFileItems.Count(f => f.IsSelected && !f.IsFolder);
            int fDel = _reviewFileItems.Count(f => f.IsSelected && f.CanDelete && !f.IsFolder);
            int rSel = _reviewRegItems.Count(r => r.IsSelected && !r.IsFolder);
            int rDel = _reviewRegItems.Count(r => r.IsSelected && r.CanDelete && !r.IsFolder);

            ReviewFileCount.Text = $"{_reviewFileItems.Count} item(ns) — {fSel} sel. ({fDel} deletáveis)";
            ReviewRegCount.Text = $"{_reviewRegItems.Count} item(ns) — {rSel} sel. ({rDel} deletáveis)";

            BtnReviewDeleteFiles.Content = $"\U0001F5D1 Remover Selecionados ({fDel})";
            BtnReviewDeleteReg.Content = $"\U0001F5D1 Remover Selecionados ({rDel})";
            BtnReviewDeleteFiles.IsEnabled = fDel > 0;
            BtnReviewDeleteReg.IsEnabled = rDel > 0;
            BtnReviewRestore.IsEnabled = _reviewResult != null &&
                (_reviewResult.BackupFiles.Count > 0 || _reviewResult.BackupRegistryFiles.Count > 0);

            int totalDel = fDel + rDel;
            int totalInfo = (fSel - fDel) + (rSel - rDel);
            ReviewStatusText.Text = totalDel > 0
                ? $"{totalDel} item(ns) serão deletados" + (totalInfo > 0 ? $" | {totalInfo} informativo(s) ignorado(s)" : "")
                : totalInfo > 0
                    ? $"{totalInfo} informativo(s) — não podem ser deletados"
                    : "Nenhum item selecionado";
        }

        private async void BtnReviewDeleteFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                var toDelete = _reviewFileItems.Where(f => f.IsSelected && f.CanDelete && !f.IsNavigational).ToList();
                var infoItems = _reviewFileItems.Where(f => f.IsSelected && !f.CanDelete && !f.IsNavigational).ToList();
                if (toDelete.Count == 0)
                {
                    if (infoItems.Count > 0)
                        MessageBox.Show("Itens informativos não podem ser deletados.\nUse 'Marcar como Criado' se tiver certeza.",
                            "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var msg = $"Excluir permanentemente {toDelete.Count} arquivo(s)/pasta(s)?";
                if (infoItems.Count > 0)
                    msg += $"\n\n⚠️ {infoItems.Count} item(ns) informativo(s) ignorado(s) — não serão deletados.";
                if (MessageBox.Show(msg + "\n\nEsta ação não pode ser desfeita.",
                    "Confirmar Exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                this.Cursor = System.Windows.Input.Cursors.Wait;
                BtnReviewDeleteFiles.IsEnabled = false;

                var selectedFullPaths = toDelete.Select(f => f.FullPath).ToList();
                var result = new DeepUninstaller.UninstallResult();
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await Task.Run(() => DeepUninstaller.PerformCleanup(
                    selectedFullPaths, new List<string>(), result, ct: cleanupCts.Token));

                _reviewFilesDeleted += selectedFullPaths.Count;
                var selectedSet = new HashSet<string>(selectedFullPaths, StringComparer.OrdinalIgnoreCase);
                _reviewFileItems = _reviewFileItems.Where(f => !selectedSet.Contains(f.FullPath)).ToList();
                ReviewFileList.ItemsSource = null;
                ReviewFileList.ItemsSource = _reviewFileItems;

                UpdateReviewCounts();
                this.Cursor = System.Windows.Input.Cursors.Arrow;
                BtnReviewDeleteFiles.IsEnabled = _reviewFileItems.Any(f => f.IsSelected && f.CanDelete);

                if (result.Errors.Count > 0)
                    MessageBox.Show($"Erros ao excluir:\n{string.Join("\n", result.Errors.Take(5))}",
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show($"{selectedFullPaths.Count} arquivo(s)/pasta(s) removidos com sucesso.",
                        "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnReviewDeleteFiles_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private async void BtnReviewDeleteReg_Click(object sender, RoutedEventArgs e)
        {
            if (_isAppOperation) return;
            _isAppOperation = true;
            try
            {
                var toDelete = _reviewRegItems.Where(r => r.IsSelected && r.CanDelete && !r.IsNavigational).ToList();
                var infoItems = _reviewRegItems.Where(r => r.IsSelected && !r.CanDelete && !r.IsNavigational).ToList();
                if (toDelete.Count == 0)
                {
                    if (infoItems.Count > 0)
                        MessageBox.Show("Itens informativos não podem ser deletados.\nUse 'Marcar como Criado' se tiver certeza.",
                            "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var msg = $"Excluir permanentemente {toDelete.Count} chave(s) de registro?";
                if (infoItems.Count > 0)
                    msg += $"\n\n⚠️ {infoItems.Count} item(ns) informativo(s) ignorado(s) — não serão deletados.";
                if (MessageBox.Show(msg + "\n\nEsta ação não pode ser desfeita.",
                    "Confirmar Exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                this.Cursor = System.Windows.Input.Cursors.Wait;
                BtnReviewDeleteReg.IsEnabled = false;

                var selectedFullPaths = toDelete.Select(r => r.FullPath).ToList();
                var result = new DeepUninstaller.UninstallResult();
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await Task.Run(() => DeepUninstaller.PerformCleanup(
                    new List<string>(), selectedFullPaths, result, ct: cleanupCts.Token));

                _reviewRegDeleted += selectedFullPaths.Count;
                var selectedSet = new HashSet<string>(selectedFullPaths, StringComparer.OrdinalIgnoreCase);
                _reviewRegItems = _reviewRegItems.Where(r => !selectedSet.Contains(r.FullPath)).ToList();
                ReviewRegList.ItemsSource = null;
                ReviewRegList.ItemsSource = _reviewRegItems;

                UpdateReviewCounts();
                this.Cursor = System.Windows.Input.Cursors.Arrow;
                BtnReviewDeleteReg.IsEnabled = _reviewRegItems.Any(r => r.IsSelected && r.CanDelete);

                if (result.Errors.Count > 0)
                    MessageBox.Show($"Erros ao excluir:\n{string.Join("\n", result.Errors.Take(5))}",
                        "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show($"{selectedFullPaths.Count} chave(s) de registro removidas com sucesso.",
                        "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnReviewDeleteReg_Click", ex.Message);
            }
            finally
            {
                _isAppOperation = false;
            }
        }

        private void ToggleKeep_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AppCleanupItem item)
            {
                item.IsKept = !item.IsKept;
                UpdateReviewCounts();
            }
        }

        private void BtnReviewSelectDeletableFiles_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _reviewFileItems)
                item.IsSelected = item.CanDelete;
            UpdateReviewCounts();
        }

        private void BtnReviewSelectDeletableReg_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _reviewRegItems)
                item.IsSelected = item.CanDelete;
            UpdateReviewCounts();
        }

        private async void BtnReviewRestore_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_reviewResult == null) return;
                int regCount = _reviewResult.BackupRegistryFiles.Count;
                int fileCount = _reviewResult.BackupFiles.Count;

                if (MessageBox.Show($"Restaurar backup?\n\n" +
                    $"{regCount} registro(s) (.reg)\n" +
                    $"{fileCount} arquivo(s)/pasta(s)\n\n" +
                    "Isso irá reimportar os backups de registro e copiar de volta os arquivos.\n" +
                    "Itens enviados para a Lixeira podem não estar disponíveis.",
                    "Restaurar Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                this.Cursor = System.Windows.Input.Cursors.Wait;
                BtnReviewRestore.IsEnabled = false;


                var regFiles = _reviewResult.BackupRegistryFiles.ToList();
                var backupEntries = _reviewResult.BackupFiles.ToList();

                var (restoredReg, restoredFiles) = await Task.Run(() =>
                {
                    int rReg = 0;
                    foreach (var regFile in regFiles)
                    {
                        DeepUninstaller.RestoreRegistryBackup(regFile);
                        rReg++;
                    }

                    int rFiles = 0;
                    foreach (var entry in backupEntries)
                    {
                        int sep = entry.IndexOf('|');
                        if (sep > 0)
                        {
                            string origPath = entry[..sep];
                            string backupPath = entry[(sep + 1)..];
                            DeepUninstaller.RestoreFileBackup(backupPath, origPath);
                            rFiles++;
                        }
                    }
                    return (rReg, rFiles);
                });

                this.Cursor = System.Windows.Input.Cursors.Arrow;
                BtnReviewRestore.IsEnabled = true;

                MessageBox.Show($"Restauração concluída:\n" +
                    $"{restoredReg} registro(s) restaurados\n" +
                    $"{restoredFiles} arquivo(s)/pasta(s) restaurados",
                    "Restauração", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                this.Cursor = System.Windows.Input.Cursors.Arrow;
                Logger.LogError("BtnReviewRestore_Click", ex.Message);
            }
        }

        private void BtnReviewCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== ARQUIVOS E PASTAS ===");
                foreach (var f in _reviewFileItems)
                    sb.AppendLine(f.FullPath);
                sb.AppendLine();
                sb.AppendLine("=== REGISTRO ===");
                foreach (var r in _reviewRegItems)
                    sb.AppendLine(r.FullPath);

                System.Windows.Clipboard.SetText(sb.ToString());
                ReviewStatusText.Text = $"📋 {_reviewFileItems.Count + _reviewRegItems.Count} caminho(s) copiados para área de transferência!";
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnReviewCopy_Click", ex.Message);
            }
        }

        private void BtnReviewBack_Click(object sender, RoutedEventArgs e)
        {
            ReviewPanel.Visibility = Visibility.Collapsed;
            NormalContent.Visibility = Visibility.Visible;

            bool filesDone = _reviewFileItems.Count == 0;
            bool regDone = _reviewRegItems.Count == 0;

            string appName = _reviewProgramContext?.DisplayName ?? _reviewBloatwareContext?.DisplayName ?? "";
            int fDeleted = _reviewFilesDeleted;
            int rDeleted = _reviewRegDeleted;

            // Save remaining leftovers as junk entry for the Max Cleanup tab
            var remainingFiles = _reviewFileItems
                .Where(f => !f.IsNavigational && !f.IsFolder)
                .Select(f => f.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var remainingReg = _reviewRegItems
                .Where(r => !r.IsNavigational && !r.IsFolder)
                .Select(r => r.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if ((remainingFiles.Count > 0 || remainingReg.Count > 0) && !string.IsNullOrEmpty(appName))
            {
                var entry = new LeftoverJunkEntry
                {
                    AppName = appName,
                    Date = DateTime.Now,
                    LeftoverFiles = remainingFiles,
                    LeftoverRegistry = remainingReg
                };
                LeftoverJunkManager.Add(entry);
            }

            if (fDeleted > 0 || rDeleted > 0)
                MessageBox.Show($"{appName}: {fDeleted} arquivo(s) e {rDeleted} registro(s) removidos.",
                    "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);

            if (filesDone && regDone && _reviewProgramContext != null)
            {
                FilteredProgramsCollection?.Remove(_reviewProgramContext);
                ProgramsCollection?.Remove(_reviewProgramContext);
                _reviewProgramContext = null;
            }

            if (filesDone && _reviewBloatwareContext != null)
            {
                _reviewBloatwareContext.IsInstalled = false;
                _ = LoadBloatware();
                _reviewBloatwareContext = null;
            }
        }

        // ─── Browser Extensions ────────────────────────────────────────────────

        private List<BrowserDetected> _detectedBrowsers = new();

        private void RefreshBrowserList()
        {
            _detectedBrowsers = BrowserExtensionManager.DetectInstalledBrowsers();
            BrowserList.ItemsSource = null;
            BrowserList.ItemsSource = _detectedBrowsers;

            var names = _detectedBrowsers.Where(b => b.HasUserData).Select(b => b.Name).ToList();
            names.Insert(0, "(selecione)");
            CboSourceBrowser.ItemsSource = null;
            CboSourceBrowser.ItemsSource = names;
            CboSourceBrowser.SelectedIndex = 0;
            CboTargetBrowser.ItemsSource = null;
            CboTargetBrowser.ItemsSource = names;
            CboTargetBrowser.SelectedIndex = 0;

            BtnExportExtensions.IsEnabled = false;
            BtnImportExtensions.IsEnabled = false;
            BtnTransferExtensions.IsEnabled = false;
        }

        private void BtnRefreshBrowsers_Click(object sender, RoutedEventArgs e)
        {
            RefreshBrowserList();
            ExtensionsList.ItemsSource = null;
            TxtExtCount.Text = "0 extensões";
            TxtExtTotalSize.Text = "0 B";
            TxtExtBrowserStatus.Text = "Navegador fechado ✓";
        }

        private async void BtnScanExtensions_Click(object sender, RoutedEventArgs e)
        {
            string src = CboSourceBrowser.SelectedItem as string ?? "";
            if (src == "" || src == "(selecione)")
            {
                MessageBox.Show("Selecione um navegador de origem.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnScanExtensions.IsEnabled = false;

            var exts = await Task.Run(() => BrowserExtensionManager.ScanExtensions(src));
            ExtensionsList.ItemsSource = exts;

            long totalSize = exts.Sum(e => e.SizeBytes);
            TxtExtCount.Text = $"{exts.Count} extensões";
            TxtExtTotalSize.Text = totalSize < 1024 ? $"{totalSize} B" :
                totalSize < 1024 * 1024 ? $"{totalSize / 1024.0:N1} KB" :
                $"{totalSize / (1024.0 * 1024.0):N1} MB";

            bool running = BrowserExtensionManager.IsBrowserRunning(src);
            TxtExtBrowserStatus.Text = running ? "⚠ Navegador aberto — feche para importar" : "Navegador fechado ✓";

            BtnExportExtensions.IsEnabled = exts.Count > 0;
            BtnTransferExtensions.IsEnabled = exts.Count > 0 && CboTargetBrowser.SelectedIndex > 0;
            BtnScanExtensions.IsEnabled = true;
        }

        private void BtnExportExtensions_Click(object sender, RoutedEventArgs e)
        {
            string src = CboSourceBrowser.SelectedItem as string ?? "";
            if (src == "" || src == "(selecione)") return;

            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = $"Selecione a pasta para exportar extensões de {src}";
            dlg.ShowNewFolderButton = true;
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            bool ok = BrowserExtensionManager.ExportExtensions(src, dlg.SelectedPath);
            MessageBox.Show(ok
                ? $"Extensões de {src} exportadas com sucesso para:\n{dlg.SelectedPath}"
                : $"Falha ao exportar extensões de {src}.",
                ok ? "Exportado" : "Erro", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private void BtnImportExtensions_Click(object sender, RoutedEventArgs e)
        {
            string target = CboTargetBrowser.SelectedItem as string ?? "";
            if (target == "" || target == "(selecione)") return;

            if (BrowserExtensionManager.IsBrowserRunning(target))
            {
                MessageBox.Show($"Feche {target} antes de importar extensões.", "Navegador Aberto",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.Description = $"Selecione a pasta com o backup das extensões para {target}";
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            bool ok = BrowserExtensionManager.ImportExtensions(dlg.SelectedPath, target);
            if (ok)
            {
                try { BrowserExtensionManager.RegisterExtensionsViaCdpPipe(target); } catch { }
            }
            MessageBox.Show(ok
                ? $"Extensões importadas para {target} com sucesso!"
                : $"Falha ao importar extensões para {target}. Verifique se a pasta contém o formato correto.",
                ok ? "Importado" : "Erro", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private async void BtnTransferExtensions_Click(object sender, RoutedEventArgs e)
        {
            string src = CboSourceBrowser.SelectedItem as string ?? "";
            string target = CboTargetBrowser.SelectedItem as string ?? "";
            if (src == "" || src == "(selecione)" || target == "" || target == "(selecione)") return;

            if (src == target)
            {
                MessageBox.Show("Selecione navegadores diferentes.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (BrowserExtensionManager.IsBrowserRunning(target))
            {
                MessageBox.Show($"Feche {target} antes de transferir extensões.", "Navegador Aberto",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnTransferExtensions.IsEnabled = false;
            var progress = new Progress<string>(msg =>
            {
                TxtExtBrowserStatus.Text = msg;
            });

            bool ok = await Task.Run(() => BrowserExtensionManager.TransferExtensions(src, target, progress));
            BtnTransferExtensions.IsEnabled = true;

            if (ok)
            {
                var exts = await Task.Run(() => BrowserExtensionManager.ScanExtensions(target));
                ExtensionsList.ItemsSource = exts;
                long totalSize = exts.Sum(e => e.SizeBytes);
                TxtExtCount.Text = $"{exts.Count} extensões";
                TxtExtTotalSize.Text = totalSize < 1024 ? $"{totalSize} B" :
                    totalSize < 1024 * 1024 ? $"{totalSize / 1024.0:N1} KB" :
                    $"{totalSize / (1024.0 * 1024.0):N1} MB";
            }

            MessageBox.Show(ok
                ? $"Extensões transferidas de {src} → {target} com sucesso!"
                : $"Falha ao transferir extensões de {src} para {target}.",
                ok ? "Transferido" : "Erro", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        private void BtnRemoveExtension_Click(object sender, RoutedEventArgs e)
        {
            var ext = (sender as FrameworkElement)?.DataContext as ExtensionInfo;
            if (ext == null) return;

            var result = MessageBox.Show(
                $"Remover extensão \"{ext.Name}\" ({ext.Id})?",
                "Remover Extensão", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (Directory.Exists(ext.SourcePath))
                    Directory.Delete(ext.SourcePath, true);
            }
            catch { }

            var current = ExtensionsList.ItemsSource as IList<ExtensionInfo>;
            if (current != null)
            {
                var list = current.ToList();
                list.Remove(ext);
                ExtensionsList.ItemsSource = list;
                TxtExtCount.Text = $"{list.Count} extensões";
                long totalSize = list.Sum(e => e.SizeBytes);
                TxtExtTotalSize.Text = totalSize < 1024 ? $"{totalSize} B" :
                    totalSize < 1024 * 1024 ? $"{totalSize / 1024.0:N1} KB" :
                    $"{totalSize / (1024.0 * 1024.0):N1} MB";
            }
        }
    }

    /// <summary>
    /// ViewModel para programas instalados
    /// </summary>
    public class DepthToMarginConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            int depth = value is int d ? d : 0;
            return new System.Windows.Thickness(depth * 20, 0, 0, 0);
        }
        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new System.NotImplementedException();
    }

    /// <summary>
    /// Flat cleanup item for the review panel (no ObservableCollection — avoids Collection was modified error)
    /// </summary>
    public class AppCleanupItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isKept;
        public string DisplayPath { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string IconChar { get; set; } = "";
        public int Depth { get; set; }
        public bool IsFolder { get; set; }
        public bool IsNavigational { get; set; }
        public CleanupSafety SafetyLevel { get; set; } = CleanupSafety.Safe;

        public bool IsBold => !IsNavigational && SafetyLevel != CleanupSafety.Uncertain;
        public bool CanDelete => !IsNavigational && SafetyLevel != CleanupSafety.Uncertain && !_isKept;
        public bool IsKept
        {
            get => _isKept;
            set { _isKept = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(KeepIcon)); OnPropertyChanged(nameof(KeepTooltip)); }
        }
        public string KeepIcon => (!IsNavigational && _isKept) ? "\U0001F512" : "";
        public string KeepTooltip => IsNavigational ? "" : (_isKept ? "Marcado como Mantido — clique para desmarcar" : "Clique para marcar como Mantido (não será deletado)");
        public string SafetyIcon => IsNavigational ? "\U0001F536" : SafetyLevel switch
        {
            CleanupSafety.Safe => "\U0001F7E2",
            CleanupSafety.Moderate => "\U0001F7E1",
            _ => "\U0001F534"
        };
        public string SafetyTooltip => IsNavigational
            ? "Apenas navegação — não será deletado"
            : SafetyLevel switch
            {
                CleanupSafety.Safe => "Seguro — pertence ao programa",
                CleanupSafety.Moderate => "Provável — será deletado",
                _ => "Informativo — não será deletado. Marcado como Mantido por padrão"
            };

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class JunkDetailItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public string Path { get; set; } = "";
        public bool IsFile { get; set; }
        public bool CanDelete { get; set; } = true;
        public CleanupSafety Safety { get; set; } = CleanupSafety.Moderate;
        public bool IsHeuristic { get; set; }

        public string SafetyIcon => IsHeuristic ? "\u26D4" : Safety switch
        {
            CleanupSafety.Safe => "\U0001F7E2",
            CleanupSafety.Moderate => "\U0001F7E1",
            _ => "\U0001F534"
        };
        public string SafetyTooltip => IsHeuristic
            ? "Heurístico — encontrado apenas após desinstalação (menor confiança)"
            : Safety switch
            {
                CleanupSafety.Safe => "Seguro — confirmado no pré-scan",
                CleanupSafety.Moderate => "Provável — confirmado no pré-scan",
                _ => "Informativo — não recomendado deletar"
            };

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class LeftoverJunkItem : INotifyPropertyChanged
    {
        private bool _isExpanded;

        public string AppName { get; set; } = "";
        public DateTime Date { get; set; }
        public string DateString => Date.ToString("g");
        public string ExpandIcon => IsExpanded ? "\u25BC" : "\u25B6";

        public List<JunkDetailItem> Files { get; set; } = new();
        public List<JunkDetailItem> Registry { get; set; } = new();
        public int BaselineFileCount { get; set; }
        public int BaselineRegistryCount { get; set; }

        public string SelectionSummary
        {
            get
            {
                int fSel = Files.Count(f => f.IsSelected && f.CanDelete);
                int rSel = Registry.Count(r => r.IsSelected && r.CanDelete);
                int fTotal = Files.Count(f => f.CanDelete);
                int rTotal = Registry.Count(r => r.CanDelete);
                int confirmed = Files.Count(f => !f.IsHeuristic) + Registry.Count(r => !r.IsHeuristic);
                int heuristic = Files.Count(f => f.IsHeuristic) + Registry.Count(r => r.IsHeuristic);
                return $"{fSel + rSel}/{fTotal + rTotal} sel | {confirmed} conf, {heuristic} heur";
            }
        }

        public int SelectedCount => Files.Count(f => f.IsSelected && f.CanDelete) + Registry.Count(r => r.IsSelected && r.CanDelete);
        public int TotalSelectableCount => Files.Count(f => f.CanDelete) + Registry.Count(r => r.CanDelete);
        public string ItemsCount => $"{Files.Count} arquivo(s), {Registry.Count} registro(s)";

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandIcon)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class ProgramViewModel
    {
        public ProgramViewModel(RegistryProgram program)
        {
            DisplayName = program.DisplayName;
            Publisher = program.Publisher;
            DisplayVersion = program.DisplayVersion;
            InstallLocation = program.InstallLocation;
            UninstallString = program.UninstallString;
            QuietUninstallString = program.QuietUninstallString;
            InstallDate = program.InstallDate;
            EstimatedSize = program.EstimatedSize;
            DisplayIcon = program.DisplayIcon;
            AboutUrl = program.AboutUrl;
            IsProtected = program.IsProtected;
            IsSystemComponent = program.IsSystemComponent;
            Is64Bit = program.Is64Bit;
            RegistryPath = program.RegistryPath;
            RegistryKeyName = program.RegistryKeyName;
            Icon = null;
        }

        public string DisplayName { get; set; }
        public string Publisher { get; set; }
        public string DisplayVersion { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }
        public string InstallDate { get; set; }
        public string EstimatedSize { get; set; }
        public string DisplayIcon { get; set; }
        public string AboutUrl { get; set; }
        public bool IsProtected { get; set; }
        public bool IsSystemComponent { get; set; }
        public bool Is64Bit { get; set; }
        public string RegistryPath { get; set; }
        public string RegistryKeyName { get; set; }
        public BitmapSource? Icon { get; set; }
        public bool IsSelected { get; set; }
    }
}
