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

        private bool _isAppOperation;

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

                    // Diálogo de confirmação não-bloqueante
                    if (!await ShowConfirmAsync(
                        $"🧹 Deep Uninstall UWP: {app.DisplayName}\n\n" +
                        "Isso irá:\n" +
                        "1. Remover o appx package (todos os usuários)\n" +
                        "2. Remover da imagem do Windows (evita reinstalação)\n" +
                        "3. Escanear pastas e registro por resíduos\n" +
                        "4. Revisar e selecionar o que limpar",
                        "Deep Uninstall"))
                    {
                        // Cancelado: reverte o botão
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

                    var settings = AppSettingsHelper.Load();
                    bloatProgress.Report($"Criando ponto de restauração...");
                    if (settings.CreateRestorePointBeforeUninstall)
                        await Task.Run(() => DeepUninstaller.TryCreateRestorePoint($"KitLugia: Uninstall {app.DisplayName}"));

                    bloatProgress.Report($"Removendo {app.DisplayName}...");
                    var result = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName);

                    bloatProgress.Report($"Escaneando resíduos de {app.DisplayName}...");
                    var (scanFileEntries, scanRegEntries) = await Task.Run(() => DeepUninstaller.ScanLeftovers(app.DisplayName, ""));
                    var scanResult = new DeepUninstaller.UninstallResult
                    {
                        LeftoverFiles = scanFileEntries.Select(e => e.Path).ToList(),
                        LeftoverRegistry = scanRegEntries.Select(e => e.Path).ToList()
                    };

                    // Also include the package folder
                    string baseName = app.PackageName.Split('_')[0];
                    string pkgFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", baseName);
                    if (!scanResult.LeftoverFiles.Contains(pkgFolder, StringComparer.OrdinalIgnoreCase))
                    {
                        scanResult.LeftoverFiles.Add(pkgFolder);
                        scanFileEntries.Add(new ScanEntry { Path = pkgFolder, Safety = CleanupSafety.Safe });
                    }

                    BloatwareLoadingPanel.Visibility = Visibility.Collapsed;
                    btn.Content = "REMOVER";
                    btn.IsEnabled = true;

                    _reviewProgramContext = null;
                    _reviewBloatwareContext = app;
                    await Dispatcher.InvokeAsync(() => ShowReviewPanel(app.DisplayName, scanResult, scanFileEntries, scanRegEntries));
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
                    $"Deep Uninstall: Remover {selectedApps.Count} app(s) selecionado(s)?\n\n{appNames}\n\nOs resíduos serão limpos automaticamente.",
                    "Deep Uninstall"))
                {
                    return;
                }
                {
                    int total = selectedApps.Count;
                    int successCount = 0;
                    int failCount = 0;
                    int completedCount = 0;
                    object countLock = new();

                    BloatwareLoadingPanel.Visibility = Visibility.Visible;
                    if (TxtBloatwareProgress != null)
                        TxtBloatwareProgress.Text = $"Removendo 0/{total}...";

                    var settings = AppSettingsHelper.Load();
                    if (settings.CreateRestorePointBeforeUninstall)
                        await Task.Run(() => DeepUninstaller.TryCreateRestorePoint($"KitLugia: Batch UWP ({total} apps)"));

                    foreach (var app in selectedApps)
                    {
                        bool ok = await SystemTweaks.DeepRemoveBloatwareAppAsync(app.PackageName, app.DisplayName) is { Success: true };
                        if (ok) { successCount++; app.IsInstalled = false; }
                        else failCount++;

                        completedCount++;
                        if (TxtBloatwareProgress != null)
                            TxtBloatwareProgress.Text = $"Removendo {completedCount}/{total}...";
                    }

                    if (TxtBloatwareProgress != null) TxtBloatwareProgress.Text = "";
                    BloatwareLoadingPanel.Visibility = Visibility.Collapsed;
                    string message = $"Deep Uninstall concluído:\n\n✅ {successCount} app(s) processados";
                    if (failCount > 0) message += $"\n⚠️ {failCount} falharam";
                    MessageBox.Show(message, "Resultado", MessageBoxButton.OK, MessageBoxImage.Information);

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
            // OTIMIZAÇÃO .NET 10: Capacidade pré-definida para List
            // Típico: 50-200 programas instalados
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
                            // 1) DisplayIcon (pode conter "caminho.dll,indice")
                            if (!string.IsNullOrEmpty(program.DisplayIcon))
                                icon = ProgramIconHelper.GetIconFromFile(program.DisplayIcon.Trim().Trim('"'));

                            // 2) Caminho do desinstalador
                            if (icon == null && !string.IsNullOrEmpty(program.UninstallString))
                                icon = ProgramIconHelper.GetIconFromFile(ExtractPathFromUninstallString(program.UninstallString));

                            // 3) Busca recursiva no diretório de instalação
                            if (icon == null && !string.IsNullOrEmpty(program.InstallLocation))
                                icon = ProgramIconHelper.GetIconFromDirectory(program.InstallLocation);

                            // 4) Genérico
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
                                // Não chama Items.Refresh() para evitar travar a UI com virtualização

                                // Atualiza contador (verifica se ProgramsCollection não é nulo)
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

            // Atualiza a UI após todos os ícones serem carregados
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

                    // Diálogo de confirmação não-bloqueante
                    if (!await ShowConfirmAsync(
                        $"🧹 Deep Uninstall: {program.DisplayName}\n\n" +
                        "Isso irá:\n" +
                        "1. Executar o uninstaller original\n" +
                        "2. Escanear pastas e registro por resíduos\n" +
                        "3. Revisar e selecionar o que limpar",
                        "Deep Uninstall"))
                    {
                        // Cancelado: reverte o botão
                        btn.Content = "REMOVER";
                        btn.IsEnabled = true;
                        return;
                    }

                    var settings = AppSettingsHelper.Load();
                    bool createRp = settings.CreateRestorePointBeforeUninstall;

                    ProgramsLoadingPanel.Visibility = Visibility.Visible;

                    IProgress<string> progress = new Progress<string>(msg =>
                    {
                        if (TxtProgramsProgress != null)
                            TxtProgramsProgress.Text = msg;
                    });

                    progress.Report($"Preparando desinstalação de {program.DisplayName}...");

                    // Run DeepUninstall + leftover classification entirely on background thread
                    var (scanResult, classifiedFiles, classifiedReg) = await Task.Run(() =>
                    {
                        var result = DeepUninstaller.DeepUninstallProgram(
                            program.DisplayName, program.UninstallString,
                            program.InstallLocation, program.Publisher, program.DisplayIcon, createRp, progress).GetAwaiter().GetResult();

                        var files = result.LeftoverFiles
                            .Select(f => new ScanEntry
                            {
                                Path = f,
                                Safety = DeepUninstaller.ClassifyFileSafety(program.DisplayName, program.InstallLocation, f)
                            }).ToList();
                        var reg = result.LeftoverRegistry
                            .Select(r => new ScanEntry
                            {
                                Path = r,
                                Safety = DeepUninstaller.ClassifyRegistrySafety(program.DisplayName, program.InstallLocation, r)
                            }).ToList();

                        return (result, files, reg);
                    });

                    if (TxtProgramsProgress != null) TxtProgramsProgress.Text = "";
                    ProgramsLoadingPanel.Visibility = Visibility.Collapsed;
                    btn.Content = "REMOVER";
                    btn.IsEnabled = true;

                    _reviewProgramContext = program;
                    _reviewBloatwareContext = null;
                    // Defensive: disconnect ItemsSource before switching to review panel
                    // to prevent any WPF CollectionView enumeration conflict
                    ProgramsList.ItemsSource = null;
                    BloatwareList.ItemsSource = null;
                    await Dispatcher.InvokeAsync(() => ShowReviewPanel(program.DisplayName, scanResult, classifiedFiles, classifiedReg));
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
                    $"Deep Uninstall: Remover {selectedPrograms.Count} programa(s) selecionado(s)?\n\n{programNames}\n\nOs resíduos serão limpos automaticamente.",
                    "Deep Uninstall"))
                {
                    return;
                }
                {
                    var settings = AppSettingsHelper.Load();
                    bool batchCreateRp = settings.CreateRestorePointBeforeUninstall;

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

                    for (int i = 0; i < total; i++)
                    {
                        var program = selectedPrograms[i];
                        int idx = i;

                        batchProgress.Report($"[{idx + 1}/{total}] {program.DisplayName} — Preparando...");

                        var result = await Task.Run(() => DeepUninstaller.DeepUninstallProgram(
                            program.DisplayName, program.UninstallString,
                            program.InstallLocation, program.Publisher, program.DisplayIcon, batchCreateRp, batchProgress));

                        await Task.Run(() => DeepUninstaller.PerformCleanup(
                            result.LeftoverFiles, result.LeftoverRegistry, result));

                        bool ok = result.UninstallSuccess || result.FilesDeleted > 0 || result.RegistryDeleted > 0;

                        if (ok)
                        {
                            successCount++;
                            FilteredProgramsCollection?.Remove(program);
                            ProgramsCollection?.Remove(program);
                        }
                        else
                        {
                            failCount++;
                        }
                    }

                    if (TxtProgramsProgress != null) TxtProgramsProgress.Text = "";
                    ProgramsLoadingPanel.Visibility = Visibility.Collapsed;
                    string message = $"Deep Uninstall concluído:\n\n✅ {successCount} programa(s) processados";
                    if (failCount > 0)
                        message += $"\n⚠️ {failCount} falharam";

                    MessageBox.Show(message, "Resultado", MessageBoxButton.OK, MessageBoxImage.Information);
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
                await Task.Run(() => DeepUninstaller.PerformCleanup(
                    selectedFullPaths, new List<string>(), result));

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
                await Task.Run(() => DeepUninstaller.PerformCleanup(
                    new List<string>(), selectedFullPaths, result));

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

        #endregion
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
