using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using Microsoft.Win32; // Para OpenFileDialog

// Resolve conflito de nomes
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class ServicesPage : Page
    {
        private List<ServiceInfo> _allServices = new();
        private List<StartupAppDetails> _allStartupApps = new();
        private readonly object _startupAppsLock = new();
        private int _initialTabIndex = 0;
        private CancellationTokenSource? _cts;
        private string _addMode = "Normal";
        private bool _isServiceOperation;
        private static readonly System.Windows.Media.Brush RedBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 107, 107));

        public ServicesPage(int tabIndex = 0)
        {
            InitializeComponent();
            _initialTabIndex = tabIndex;
            Loaded += ServicesPage_Loaded;

            Unloaded += ServicesPage_Unloaded;
        }


        public void Cleanup()
        {

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;


            _allServices?.Clear();
            _allServices = null!;

            if (GridStartup != null)
            {
                GridStartup.ItemsSource = null;
                GridStartup.Items.Clear();
            }

            if (GridServices != null)
            {
                GridServices.ItemsSource = null;
                GridServices.Items.Clear();
            }

            if (GridTasks != null)
            {
                GridTasks.ItemsSource = null;
                GridTasks.Items.Clear();
            }

            if (GridBootItems != null)
            {
                GridBootItems.ItemsSource = null;
                GridBootItems.Items.Clear();
            }

            Loaded -= ServicesPage_Loaded;
            Unloaded -= ServicesPage_Unloaded;


            this.DataContext = null;



        }

        private void ServicesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async void ServicesPage_Loaded(object sender, RoutedEventArgs e)
        {

            if (_cts == null || _cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();

            if (MainTabs != null) MainTabs.SelectedIndex = _initialTabIndex;


            var token = _cts?.Token ?? CancellationToken.None;

            // Carrega os dados iniciais das abas principais
            await LoadStartupApps(token);
            await LoadServices(token);
            await LoadScheduledTasks(token);
        }

        // =========================================================
        // ABA 1: INICIALIZAÇÃO (STARTUP)
        // =========================================================
        #region Startup Logic

        private async Task LoadStartupApps(CancellationToken cancellationToken)
        {
            try
            {
                TxtStartupLoadingStatus.Text = "Carregando...";
                StartupLoadingOverlay.Visibility = Visibility.Visible;

                // FASE 1 (rápida): registro + pastas + tarefas KitLugia — sem Task Scheduler externo
                var fast = await Task.Run(() => StartupManager.GetStartupAppsFast(), cancellationToken);
                lock (_startupAppsLock) { _allStartupApps = fast; }
                ApplyStartupFilter();

                // FASE 2 (background): enriquecer com tarefas externas + UWP + Active Setup
                TxtStartupLoadingStatus.Text = "Buscando mais apps...";
                var full = await Task.Run(() => StartupManager.GetStartupAppsWithDetails(true), cancellationToken);
                lock (_startupAppsLock)
                {
                    if (full.Count > _allStartupApps.Count)
                    {
                        _allStartupApps = full;
                    }
                }
                ApplyStartupFilter();
            }
            catch { Logger.LogWarning("ServicesPage", "Exception suppressed"); }
            finally
            {
                StartupLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadStartupApps() => await LoadStartupApps(_cts?.Token ?? CancellationToken.None);

        private void ApplyStartupFilter()
        {
            List<StartupAppDetails> snapshot;
            lock (_startupAppsLock)
            {
                if (_allStartupApps == null || _allStartupApps.Count == 0) return;
                snapshot = new List<StartupAppDetails>(_allStartupApps);
            }

            string filter = TxtSearchStartup.Text.ToLower().Trim();

            var filtered = snapshot
                .Where(a =>
                    string.IsNullOrEmpty(filter) ||
                    a.Name.ToLower().Contains(filter) ||
                    (a.FullCommand ?? "").ToLower().Contains(filter) ||
                    (a.Location ?? "").ToLower().Contains(filter))
                .OrderByDescending(a => a.Status == StartupStatus.TurboBoot || a.Status == StartupStatus.TurboBootNormal)
                .ThenByDescending(a => a.Status == StartupStatus.Elevated)
                .ThenByDescending(a => a.Status == StartupStatus.Enabled)
                .ThenBy(a => a.Name)
                .ToList();

            GridStartup.ItemsSource = filtered;
            UpdateStartupCount(filtered.Count);
        }

        private void TxtSearchStartup_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyStartupFilter();
        }

        private void UpdateStartupCount(int count)
        {
            if (TxtStartupCount != null)
                TxtStartupCount.Text = $"{count} ite{(count == 1 ? "m" : "ns")}";
        }

        private async void BtnRefreshStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                await LoadStartupApps();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefreshStartup_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async void BtnToggleStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp)
                {
                    bool willEnable = selectedApp.Status == StartupStatus.Disabled;
                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"{(willEnable ? "Habilitando" : "Desabilitando")} {selectedApp.Name}", "Services");

                    var result = await Task.Run(() => StartupManager.SetStartupItemState(selectedApp.Name, willEnable));

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        if (result.Success)
                        {
                            mw.ShowSuccess("STARTUP", $"{selectedApp.Name} foi {(willEnable ? "Habilitado" : "Desabilitado")}.");
                            await LoadStartupApps();
                        }
                        else mw.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnToggleStartup_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async void BtnRemoveStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp)
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        if (!await mw.ShowConfirmationDialog($"Excluir '{selectedApp.Name}' permanentemente?")) return;
                        string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Removendo {selectedApp.Name}", "Services");

                        var result = await Task.Run(() => StartupManager.RemoveStartupItem(selectedApp.Name));

                        Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                        if (result.Success) { mw.ShowSuccess("REMOVIDO", result.Message); await LoadStartupApps(); }
                        else mw.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemoveStartup_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        // --- Abrir Local do Arquivo ---
        private void BtnOpenStartupLocation_Click(object sender, RoutedEventArgs e)
        {
            OpenStartupFileLocation(GridStartup.SelectedItem as StartupAppDetails);
        }

        private void MenuOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            OpenStartupFileLocation(GridStartup.SelectedItem as StartupAppDetails);
        }

        // Botão direito seleciona a linha sob o cursor ANTES de abrir o menu de contexto,
        // evitando que as ações (incluindo "Abrir Local") operem no item errado.
        private void GridStartup_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.OriginalSource is not System.Windows.DependencyObject dep) return;
                var row = ItemsControl.ContainerFromElement(GridStartup, dep) as DataGridRow;
                if (row == null || row.Item is not StartupAppDetails) return;

                if (!row.IsSelected)
                {
                    row.IsSelected = true;
                    GridStartup.SelectedItem = row.Item;
                }
                row.Focus();
            }
            catch (Exception ex)
            {
                Logger.LogError("GridStartup_PreviewMouseRightButtonDown", ex.Message);
            }
        }

        // Método único e totalmente guardado: NUNCA deixa o kit crashar ao abrir o local do arquivo.
        private void OpenStartupFileLocation(StartupAppDetails? app)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            try
            {
                if (app == null)
                {
                    mw?.ShowError("ERRO", "Nenhum item selecionado na lista de inicialização.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(app.FullCommand) && string.IsNullOrWhiteSpace(app.ExePath))
                {
                    mw?.ShowError("ERRO", $"Não foi possível extrair um caminho de arquivo para \"{app.Name}\".");
                    return;
                }

                string path = app.ExePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    mw?.ShowError("ERRO", $"Não foi possível extrair um caminho de arquivo para \"{app.Name}\".");
                    return;
                }

                // 1) Expandir variáveis de ambiente (ex.: %ProgramFiles%...) e remover aspas residuais
                string full = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"', '\''));

                // 1.5) Itens UWP/MSIX ("StartupTask: FamilyName!TaskId") — resolvem pelo manifest do pacote
                if (full.StartsWith("StartupTask:", StringComparison.OrdinalIgnoreCase))
                {
                    OpenPackagedAppLocation(full, app.Name, mw);
                    return;
                }

                // 2) URI web não tem "local" físico — informar em vez de tentar abrir
                if (Uri.TryCreate(full, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
                {
                    mw?.ShowInfo("LINK", $"Este item é um link, não um arquivo local:\n{full}");
                    return;
                }

                // 3) Caminho existe: arquivo -> Explorer selecionando o arquivo; pasta -> abre a pasta
                if (System.IO.File.Exists(full)) { LaunchExplorerSelect(full); return; }
                if (System.IO.Directory.Exists(full)) { LaunchExplorerFolder(full); return; }

                // 4) Nome simples (ex.: "cmd.exe") -> busca no PATH do sistema
                if (!full.Contains('\\') && !full.Contains('/'))
                {
                    string? inPath = FindInEnvironmentPath(full);
                    if (inPath != null)
                    {
                        if (System.IO.File.Exists(inPath)) { LaunchExplorerSelect(inPath); return; }
                        if (System.IO.Directory.Exists(inPath)) { LaunchExplorerFolder(inPath); return; }
                    }
                }

                // 5) Arquivo não existe no disco -> sobe até a pasta pai existente mais próxima
                string? probe = System.IO.Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(probe) && !System.IO.Directory.Exists(probe))
                    probe = System.IO.Path.GetDirectoryName(probe);

                if (!string.IsNullOrEmpty(probe) && System.IO.Directory.Exists(probe))
                {
                    LaunchExplorerFolder(probe);
                    mw?.ShowInfo("PASTA PAI", $"O arquivo \"{full}\" não foi encontrado no disco.\nAbrindo a pasta existente mais próxima:\n{probe}");
                    return;
                }

                // 6) Não foi possível resolver nada
                mw?.ShowError("ERRO", $"Não foi possível localizar o arquivo ou a pasta:\n{full}");
            }
            catch (Exception ex)
            {
                Logger.LogError("OpenStartupFileLocation", ex.Message);
                mw?.ShowError("ERRO", $"Erro ao abrir o local do arquivo:\n{ex.Message}");
            }
        }

        // Resolve e abre o local de um item "StartupTask: FamilyName!TaskId" (app UWP/MSIX).
        // Nunca deixa o kit crashar: todo caminho de falha vira toast informativo.
        private static void OpenPackagedAppLocation(string fullCommand, string displayName, MainWindow? mw)
        {
            try
            {
                const string prefix = "StartupTask:";
                string after = fullCommand.Substring(prefix.Length).Trim().Trim('"', '\'');
                int bang = after.IndexOf('!');
                if (bang <= 0)
                {
                    mw?.ShowError("ERRO", $"Identificador de tarefa inválido:\n{fullCommand}");
                    return;
                }

                string familyName = after.Substring(0, bang).Trim();
                string taskId = after.Substring(bang + 1).Trim();
                if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(taskId))
                {
                    mw?.ShowError("ERRO", $"Identificador de tarefa inválido:\n{fullCommand}");
                    return;
                }

                string? install = StartupManager.GetPackagedAppInstallLocation(familyName);
                if (string.IsNullOrWhiteSpace(install))
                {
                    mw?.ShowInfo("NÃO INSTALADO", $"O pacote \"{familyName}\" não está instalado para este usuário.");
                    return;
                }

                // 1) Tenta o executável real do pacote (via AppxManifest.xml)
                string? exe = StartupManager.ResolvePackagedAppExecutable(familyName, taskId);
                if (!string.IsNullOrWhiteSpace(exe) && System.IO.File.Exists(exe))
                {
                    LaunchExplorerSelect(exe);
                    if (IsUnderWindowsApps(exe))
                        mw?.ShowInfo("PACOTE", $"Este app empacotado fica em pasta protegida do sistema.\nSe o Explorer negar acesso, use a guia \"Segurança\" para assumir a propriedade da pasta:\n{exe}");
                    return;
                }

                // 2) Fallback: abre a pasta de instalação do pacote
                if (System.IO.Directory.Exists(install))
                {
                    LaunchExplorerFolder(install);
                    string msg = IsUnderWindowsApps(install)
                        ? $"Executável não resolvido no manifest de \"{displayName}\".\nPasta protegida do sistema — se o Explorer negar acesso, assuma a propriedade (guia Segurança):\n{install}"
                        : $"Executável não resolvido no manifest de \"{displayName}\".\nAbrindo a pasta de instalação:\n{install}";
                    mw?.ShowInfo("PACOTE", msg);
                    return;
                }

                mw?.ShowError("ERRO", $"Não foi possível localizar o aplicativo:\n{fullCommand}");
            }
            catch (Exception ex)
            {
                Logger.LogError("OpenPackagedAppLocation", ex.Message);
                mw?.ShowError("ERRO", $"Erro ao resolver o aplicativo empacotado:\n{ex.Message}");
            }
        }

        private static bool IsUnderWindowsApps(string path)
        {
            try
            {
                return path.StartsWith(@"C:\Program Files\WindowsApps", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void LaunchExplorerSelect(string filePath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("LaunchExplorerSelect", ex.Message);
            }
        }

        private static void LaunchExplorerFolder(string folder)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("LaunchExplorerFolder", ex.Message);
            }
        }

        private static string? FindInEnvironmentPath(string fileName)
        {
            try
            {
                string? pathVar = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrWhiteSpace(pathVar)) return null;

                foreach (string dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        string candidate = System.IO.Path.Combine(dir.Trim('"'), fileName);
                        if (System.IO.File.Exists(candidate)) return candidate;
                    }
                    catch { /* diretório inválido do PATH — ignora e segue */ }
                }
            }
            catch { }
            return null;
        }

        // --- Adicionar Novo ---
        private void BtnAddStartup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private string? PickFile()
        {
            // Adicione "Microsoft.Win32." antes de OpenFileDialog
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Executáveis (*.exe)|*.exe|Todos (*.*)|*.*"
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private void MenuAddNormal_Click(object sender, RoutedEventArgs e)
        {
            _addMode = "Normal";
            ShowAddStartupOverlay("Padrão (Sem Admin)");
        }

        private void MenuAddAdmin_Click(object sender, RoutedEventArgs e)
        {
            _addMode = "Admin";
            ShowAddStartupOverlay("Administrador (Elevado)");
        }

        private void MenuAddDelayed_Click(object sender, RoutedEventArgs e)
        {
            _addMode = "Delayed";
            ShowAddStartupOverlay("Atraso (2 min)");
        }

        private void MenuAddAdminDelayed_Click(object sender, RoutedEventArgs e)
        {
            _addMode = "AdminDelayed";
            ShowAddStartupOverlay("Administrador + Atraso (2 min)");
        }

        private void ShowAddStartupOverlay(string modeLabel)
        {
            TxtAddMode.Text = $"Modo: {modeLabel}";
            TxtAddExePath.Text = "";
            TxtAddArgs.Text = "";
            TxtAddPreview.Text = "";
            BtnAddSave.IsEnabled = false;
            AddSuggestionsPanel.Visibility = Visibility.Collapsed;
            EditArgsOverlay.Visibility = Visibility.Collapsed;
            AddStartupOverlay.Visibility = Visibility.Visible;
        }

        private void BtnAddPickFile_Click(object sender, RoutedEventArgs e)
        {
            string? path = PickFile();
            if (path == null) return;
            TxtAddExePath.Text = path;
            UpdateAddPreview();
            BtnAddSave.IsEnabled = true;
            PopulateAddSuggestions(path);
        }

        private void PopulateAddSuggestions(string exePath)
        {
            var suggestions = KnownStartupArgs.SuggestArgs(exePath);
            if (suggestions != null && suggestions.Length > 0)
            {
                AddSuggestionsPanel.ItemsSource = suggestions;
                AddSuggestionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                AddSuggestionsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnAddSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string arg)
            {
                string current = TxtAddArgs.Text.Trim();
                if (string.IsNullOrEmpty(current))
                    TxtAddArgs.Text = arg;
                else if (!current.Contains(arg))
                    TxtAddArgs.Text = current + " " + arg;
            }
        }

        private void TxtAddArgs_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateAddPreview();
        }

        private void UpdateAddPreview()
        {
            string exePath = TxtAddExePath.Text;
            if (string.IsNullOrEmpty(exePath))
            {
                TxtAddPreview.Text = "";
                return;
            }
            string args = TxtAddArgs.Text.Trim();
            TxtAddPreview.Text = string.IsNullOrEmpty(args) ? exePath : $"\"{exePath}\" {args}";
        }

        private async void BtnAddSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                string exePath = TxtAddExePath.Text;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("ERRO", "Selecione um executável primeiro.");
                    return;
                }

                string appName = System.IO.Path.GetFileNameWithoutExtension(exePath);
                string args = TxtAddArgs.Text.Trim();
                string finalCommand = string.IsNullOrEmpty(args) ? exePath : $"\"{exePath}\" {args}";

                // Duplicate detection
                bool exists;
                lock (_startupAppsLock) { exists = _allStartupApps?.Any(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase)) ?? false; }
                if (exists)
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        bool overwrite = await mw.ShowConfirmationDialog($"'{appName}' já existe na lista. Sobrescrever?");
                        if (!overwrite) return;
                    }
                }

                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    (bool Success, string Message) result = (false, "");
                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Adicionando {appName} à inicialização", "Services");

                    result = await Task.Run(() =>
                    {
                        switch (_addMode)
                        {
                            case "Normal":
                                string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                                string shortcutPath = System.IO.Path.Combine(startupDir, appName + ".lnk");
                                bool created = StartupManager.CreateShortcut(shortcutPath, exePath, args, appName, System.IO.Path.GetDirectoryName(exePath) ?? "");
                                return (created, created ? $"'{appName}' adicionado à inicialização padrão." : $"Erro ao criar atalho para '{appName}'.");
                            case "Admin":
                                return StartupManager.CreateElevatedStartupTask(appName, exePath, args);
                            case "Delayed":
                                return StartupManager.CreateDelayedStartupTask(appName, exePath, args);
                            case "AdminDelayed":
                                return StartupManager.CreateElevatedDelayedStartupTask(appName, exePath, args);
                            default:
                                return (false, "Modo inválido.");
                        }
                    });

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                    if (result.Success)
                    {
                        mainWindow.ShowSuccess("ADICIONADO", result.Message);
                        AddStartupOverlay.Visibility = Visibility.Collapsed;
                        await Task.Delay(800);
                        await LoadStartupApps();
                    }
                    else
                    {
                        mainWindow.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAddSave_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void BtnAddCancel_Click(object sender, RoutedEventArgs e)
        {
            AddStartupOverlay.Visibility = Visibility.Collapsed;
        }

        private void GridStartup_ContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (GridStartup.SelectedItem is StartupAppDetails selectedApp)
            {
                bool inKitLugia = selectedApp.IsInBootTray || selectedApp.Location.Contains("KitLugia");
                MenuRestoreNormal.Visibility = inKitLugia ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

                if (selectedApp.IsInBootTray)
                {
                    MenuMoveToTurboBoot_Admin.Visibility = System.Windows.Visibility.Collapsed;
                    MenuMoveToTurboBoot_Normal.Visibility = System.Windows.Visibility.Collapsed;
                    MenuRemoveFromTurboBoot.Visibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    MenuMoveToTurboBoot_Admin.Visibility = System.Windows.Visibility.Visible;
                    MenuMoveToTurboBoot_Normal.Visibility = System.Windows.Visibility.Visible;
                    MenuRemoveFromTurboBoot.Visibility = System.Windows.Visibility.Collapsed;
                }
            }

            // "Abrir Local do Arquivo" só fica disponível quando há um caminho extraível
            if (MenuOpenLocation != null)
            {
                var app = GridStartup.SelectedItem as StartupAppDetails;
                MenuOpenLocation.IsEnabled = app != null && !string.IsNullOrWhiteSpace(app.ExePath);
            }
        }

        private async Task MoveToBootTray(bool runAsAdmin)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    string mode = runAsAdmin ? "Admin" : "Normal (Sem Admin)";
                    if (!await mw.ShowConfirmationDialog($"Mover '{selectedApp.Name}' para o KitLugia Boot Tray ({mode})?")) return;
                    var resultAdd = await Task.Run(() => StartupManager.DelegateToKitLugia(selectedApp.Name, runAsAdmin));
                    if (resultAdd.Success) { mw.ShowSuccess("BOOT TRAY", resultAdd.Message); await LoadStartupApps(); }
                    else mw.ShowError("ERRO", resultAdd.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MoveToBootTray", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void MenuMoveToTurboAdmin_Click(object sender, RoutedEventArgs e)
        {
            _ = MoveToBootTray(true);
        }

        private void MenuMoveToTurboNormal_Click(object sender, RoutedEventArgs e)
        {
            _ = MoveToBootTray(false);
        }

        private async void MenuRemoveFromTurboBoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog($"Remover '{selectedApp.Name}' do KitLugia Boot Tray?")) return;
                    var result = await Task.Run(() => StartupManager.RemoveFromKitLugia(selectedApp.Name));
                    if (result.Success) { mw.ShowSuccess("BOOT TRAY", result.Message); await LoadStartupApps(); }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuRemoveFromTurboBoot_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async void MenuConvertToAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    if (selectedApp.Status.ToString() == "Elevated")
                    {
                        mw.ShowInfo("JÁ ELEVADO", "Este aplicativo já está rodando como Administrador.");
                        return;
                    }
                    
                    StartupManager.ExtractCommandParts(selectedApp.FullCommand, out string? path, out string? args);
                    if (string.IsNullOrEmpty(path)) { mw.ShowError("ERRO", "Caminho inválido ou não pode ser convertido."); return; }

                    var result = await Task.Run(() =>
                    {
                        var taskResult = StartupManager.CreateElevatedStartupTask(selectedApp.Name, path, args);
                        if (taskResult.Success)
                            StartupManager.RemoveStartupItem(selectedApp.Name);
                        return taskResult;
                    });

                    if (result.Success) { mw.ShowSuccess("ELEVADO COM SUCESSO", result.Message); await LoadStartupApps(); }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuConvertToAdmin_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async void MenuConvertToAdminDelayed_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    StartupManager.ExtractCommandParts(selectedApp.FullCommand, out string? path, out string? args);
                    if (string.IsNullOrEmpty(path)) { mw.ShowError("ERRO", "Caminho inválido ou não pode ser convertido."); return; }

                    var result = await Task.Run(() =>
                    {
                        var taskResult = StartupManager.CreateElevatedDelayedStartupTask(selectedApp.Name, path, args);
                        if (taskResult.Success)
                            StartupManager.RemoveStartupItem(selectedApp.Name);
                        return taskResult;
                    });

                    if (result.Success) { mw.ShowSuccess("ELEVADO (ATRASO) COM SUCESSO", result.Message); await LoadStartupApps(); }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuConvertToAdminDelayed_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async void MenuRestoreNormal_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    if (selectedApp.Status == StartupStatus.Enabled)
                    {
                        mw.ShowInfo("RESTAURAR", "Este aplicativo já está na inicialização padrão.");
                        return;
                    }

                    if (!await mw.ShowConfirmationDialog($"Restaurar '{selectedApp.Name}' para a inicialização padrão do Windows?")) return;

                    var result = await Task.Run(() => StartupManager.RestoreToNormal(selectedApp.Name));
                    if (result.Success)
                    {
                        mw.ShowSuccess("SUCESSO", result.Message);
                        await LoadStartupApps();
                    }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuRestoreNormal_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void MenuEditArgs_Click(object sender, RoutedEventArgs e)
        {
            if (GridStartup.SelectedItem is not StartupAppDetails app) return;
            ShowEditArgsOverlay(app);
        }

        private void ShowEditArgsOverlay(StartupAppDetails app)
        {
            TxtEditAppName.Text = app.Name;
            TxtEditExePath.Text = app.ExePath;
            TxtEditArgs.Text = app.Arguments;
            UpdateEditPreview();
            PopulateEditSuggestions(app.ExePath);
            EditArgsOverlay.Visibility = Visibility.Visible;
        }

        private void PopulateEditSuggestions(string exePath)
        {
            var suggestions = KnownStartupArgs.SuggestArgs(exePath);
            if (suggestions != null && suggestions.Length > 0)
            {
                EditSuggestionsPanel.ItemsSource = suggestions;
                EditSuggestionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                EditSuggestionsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnSuggestion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string arg)
            {
                string current = TxtEditArgs.Text.Trim();
                if (string.IsNullOrEmpty(current))
                    TxtEditArgs.Text = arg;
                else if (!current.Contains(arg))
                    TxtEditArgs.Text = current + " " + arg;
            }
        }

        private void TxtEditArgs_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateEditPreview();
        }

        private void UpdateEditPreview()
        {
            string exePath = TxtEditExePath.Text;
            string args = TxtEditArgs.Text.Trim();
            if (string.IsNullOrEmpty(args))
                TxtEditPreview.Text = exePath;
            else
                TxtEditPreview.Text = $"\"{exePath}\" {args}";
        }

        private async void BtnEditArgsSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                string appName = TxtEditAppName.Text;
                string newCommand = TxtEditPreview.Text;

                if (string.IsNullOrWhiteSpace(newCommand))
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("ERRO", "O comando não pode estar vazio.");
                    return;
                }

                if (Application.Current.MainWindow is MainWindow mainWindow)
                {
                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Atualizando argumentos de {appName}", "Services");
                    var result = await Task.Run(() => StartupManager.UpdateStartupArgs(appName, newCommand));
                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                    if (result.Success)
                    {
                        mainWindow.ShowSuccess("ARGUMENTOS", result.Message);
                        EditArgsOverlay.Visibility = Visibility.Collapsed;
                        await Task.Delay(800);
                        await LoadStartupApps();
                    }
                    else
                    {
                        mainWindow.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnEditArgsSave_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void BtnEditArgsCancel_Click(object sender, RoutedEventArgs e)
        {
            EditArgsOverlay.Visibility = Visibility.Collapsed;
        }

        private async void MenuRunNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is not StartupAppDetails app) return;
                if (Application.Current.MainWindow is not MainWindow mw) return;

                // Non-admin boot tray: usar tarefa agendada dormente para evitar herdar admin
                if (app.IsInBootTray && !app.BootTrayRunAsAdmin)
                {
                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Executando {app.Name} (sem admin)", "Services");
                    bool success = false;
                    string message = "";
                    await Task.Run(() =>
                    {
                        try
                        {
                            StartupManager.RunNonAdminTask(app.Name);
                            success = true;
                            message = $"{app.Name} iniciado como NORMAL com sucesso.";
                        }
                        catch (Exception ex)
                        {
                            success = false;
                            message = $"Erro ao executar via tarefa: {ex.Message}";
                        }
                    });
                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, success, message);
                    if (success) mw.ShowSuccess("EXECUTANDO (NORMAL)", message);
                    else mw.ShowError("ERRO", message);
                    return;
                }

                StartupManager.ExtractCommandParts(app.FullCommand, out string? path, out string? args);
                if (string.IsNullOrWhiteSpace(path))
                {
                    mw.ShowError("ERRO", "Caminho do executável inválido.");
                    return;
                }

                string taskId2 = Services.BackgroundTaskTracker.Instance.RegisterTask($"Executando {app.Name}", "Services");
                bool success2 = false;
                string message2 = "";

                await Task.Run(() =>
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = path,
                            Arguments = args ?? "",
                            UseShellExecute = true,
                            WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? ""
                        };
                        System.Diagnostics.Process.Start(psi);
                        success2 = true;
                        message2 = $"{app.Name} iniciado com sucesso.";
                    }
                    catch (Exception ex)
                    {
                        success2 = false;
                        message2 = $"Erro ao executar: {ex.Message}";
                    }
                });

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId2, success2, message2);

                if (success2)
                    mw.ShowSuccess("EXECUTANDO", message2);
                else
                    mw.ShowError("ERRO", message2);
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuRunNow_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        #endregion

        // =========================================================
        // ABA 2: OTIMIZAÇÃO DE SERVIÇOS
        // =========================================================
        #region Services Logic

        private async Task LoadServices(CancellationToken cancellationToken)
        {
            try
            {
                var services = await Task.Run(() => BackgroundProcessManager.GetAllServices(), cancellationToken);
                _allServices = services;
                ApplyServiceFilter();
            }
            catch { Logger.LogWarning("ServicesPage", "Exception suppressed"); }
        }

        private async Task LoadServices() => await LoadServices(_cts?.Token ?? CancellationToken.None);

        private void ApplyServiceFilter()
        {
            string filter = TxtSearchService.Text.Trim().ToLower();
            bool showDangerous = ChkShowDangerous.IsChecked == true;
            bool onlyThirdParty = ChkShowThirdParty.IsChecked == true;

            var filtered = _allServices.Where(s =>
            {
                bool matchesText = string.IsNullOrEmpty(filter) || s.DisplayName.ToLower().Contains(filter) || s.Name.ToLower().Contains(filter);
                bool matchesSafety = showDangerous || s.Safety != ServiceSafetyLevel.Dangerous;
                bool matchesManufacturer = !onlyThirdParty || s.Manufacturer == "Terceiros";
                return matchesText && matchesSafety && matchesManufacturer;
            }).ToList();

            GridServices.ItemsSource = filtered;
            if (TxtServiceCount != null) TxtServiceCount.Text = $"{filtered.Count} Serviços";
        }

        private void TxtSearchService_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();
        private void ChkShowDangerous_Click(object sender, RoutedEventArgs e) => ApplyServiceFilter();
        private void ChkShowThirdParty_Click(object sender, RoutedEventArgs e) => ApplyServiceFilter();
        private async void BtnRefreshServices_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                await LoadServices();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefreshServices_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async Task RunServicePreset(string presetName, string friendlyName)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                if (!await mw.ShowConfirmationDialog($"Aplicar perfil '{friendlyName}'?")) return;
                mw.ShowInfo("AGUARDE", "Aplicando configurações...");
                var result = await Task.Run(() => BackgroundProcessManager.ApplyServicePreset(presetName));
                mw.ShowSuccess("SERVIÇOS", result.Message);
                LoadServices();
            }
        }

        private void BtnSafeOpt_Click(object sender, RoutedEventArgs e) => RunServicePreset("Safe", "Seguro");
        private void BtnGamerOpt_Click(object sender, RoutedEventArgs e) => RunServicePreset("Gamer", "Gamer");
        private void BtnGamerPlusOpt_Click(object sender, RoutedEventArgs e) => RunServicePreset("GamerPlus", "Gamer+");
        private void BtnRestoreServices_Click(object sender, RoutedEventArgs e) => RunServicePreset("Restore", "Padrão");

        // Menu de Contexto
        private async Task ChangeServiceState(string mode)
        {
            if (GridServices.SelectedItem is ServiceInfo svc && Application.Current.MainWindow is MainWindow mw)
            {
                if (svc.Safety == ServiceSafetyLevel.Dangerous && mode == "disabled")
                {
                    if (!await mw.ShowConfirmationDialog($"PERIGO: '{svc.DisplayName}' é crítico. Desativar?")) return;
                }

                mw.ShowInfo("AGUARDE", $"Configurando '{svc.DisplayName}'...");

                var result = mode == "default"
                    ? await Task.Run(() => BackgroundProcessManager.ResetServiceToDefault(svc.Name))
                    : await Task.Run(() => BackgroundProcessManager.ToggleServiceState(svc.Name, mode));

                if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                else mw.ShowError("ERRO", result.Message);

                LoadServices();
            }
        }

        private void MenuSvcAuto_Click(object sender, RoutedEventArgs e) => ChangeServiceState("auto");
        private void MenuSvcManual_Click(object sender, RoutedEventArgs e) => ChangeServiceState("demand");
        private void MenuSvcDisabled_Click(object sender, RoutedEventArgs e) => ChangeServiceState("disabled");
        private void MenuSvcDefault_Click(object sender, RoutedEventArgs e) => ChangeServiceState("default");
        #endregion

        // =========================================================
        // ABA 3: TAREFAS AGENDADAS
        // =========================================================
        #region Scheduled Tasks Logic

        private List<ScheduledTaskInfo> _allTasks = new();

        private async Task LoadScheduledTasks(CancellationToken cancellationToken)
        {
            try
            {
                var tasks = await Task.Run(() => BackgroundProcessManager.GetScheduledTasksStatus(), cancellationToken);
                _allTasks = tasks;
                ApplyTaskFilter();
            }
            catch (Exception ex)
            {
                Logger.LogError("LoadScheduledTasks", ex.Message);
            }
        }

        private async Task LoadScheduledTasks() => await LoadScheduledTasks(_cts?.Token ?? CancellationToken.None);

        private void ApplyTaskFilter()
        {
            try
            {
                string filter = "";
                if (CboTaskFilter?.SelectedItem is ComboBoxItem item && item.Content is string s)
                    filter = s;

                var source = _allTasks ?? new();
                var filtered = source.Where(t =>
                {
                    if (filter == "Apenas Microsoft") return t.Category == "Microsoft";
                    return true;
                }).ToList();

                if (GridTasks != null)
                    GridTasks.ItemsSource = filtered;
                if (TxtTaskCount != null)
                    TxtTaskCount.Text = $"{filtered.Count} tarefa{(filtered.Count == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                Logger.LogError("ApplyTaskFilter", ex.Message);
            }
        }

        private void CboTaskFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ApplyTaskFilter();
        }

        private async void BtnToggleTask_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridTasks.SelectedItem is ScheduledTaskInfo task && Application.Current.MainWindow is MainWindow mw)
                {
                    bool newState = !task.IsEnabled;
                    var result = await Task.Run(() => BackgroundProcessManager.ToggleTaskState(task.Path, newState));

                    if (result.Success)
                    {
                        mw.ShowSuccess("TAREFA", result.Message);
                        LoadScheduledTasks();
                    }
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnToggleTask_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private async Task RunTaskPreset(string presetName, string confirmMessage, string successLabel)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog(confirmMessage)) return;
                    var result = await Task.Run(() => BackgroundProcessManager.ApplyTaskPreset(presetName));
                    mw.ShowSuccess(successLabel, result.Message);
                    LoadScheduledTasks();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("RunTaskPreset", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void BtnDisableMSTasks_Click(object sender, RoutedEventArgs e)
        {
            _ = RunTaskPreset("DisableMicrosoft",
                "Desativar todas as tarefas de telemetria e manutenção da Microsoft?",
                "MICROSOFT");
        }

        private async void BtnDisableAllTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog("Isso desativará TODAS as tarefas monitoradas (Microsoft + Terceiros).\nDeseja continuar?")) return;

                    var result = await Task.Run(() => BackgroundProcessManager.DisableTelemetryTasks());
                    mw.ShowSuccess("TODAS", result.Message);
                    LoadScheduledTasks();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDisableAllTasks_Click", ex.Message);
            }
            finally
            {
                _isServiceOperation = false;
            }
        }

        private void BtnRestoreAllTasks_Click(object sender, RoutedEventArgs e)
        {
            _ = RunTaskPreset("RestoreAll",
                "Restaurar TODAS as tarefas monitoradas ao estado ativo?",
                "RESTAURADO");
        }
        #endregion

        // =========================================================
        // ABA 4: ANÁLISE DE BOOT (NOVO)
        // =========================================================
        #region Boot Analysis Logic

        private async void BtnAnalyzeBoot_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    TxtBootTime.Text = "Analisando...";
                    TxtBootDate.Text = "";
                    mw.ShowInfo("AGUARDE", "Lendo logs de eventos do sistema...");

                    var result = await Task.Run(() => BootOptimizerManager.AnalyzeBootPerformance());

                    if (result.HasWarning)
                        mw.ShowInfo("AVISO", result.ServiceStatusMessage);

                    if (result.TotalTimeEvent != null)
                    {
                        double seconds = result.TotalTimeEvent.TimeTaken / 1000.0;
                        TxtBootTime.Text = $"{seconds:F2} segundos";
                        TxtBootDate.Text = $"Data: {result.TotalTimeEvent.TimeOfEvent}";
                    }
                    else
                    {
                        TxtBootTime.Text = "Sem dados recentes";
                    }

                    var combinedList = new List<PerformanceEvent>(30);
                    combinedList.AddRange(result.SlowStartupItems);
                    combinedList.AddRange(result.HighImpactApps);

                    GridBootItems.ItemsSource = combinedList;

                    if (combinedList.Count == 0)
                        mw.ShowSuccess("ÓTIMO", "Nenhum atraso significativo (>1s) encontrado no último boot.");
                    else
                        mw.ShowInfo("ANÁLISE", $"Encontrados {combinedList.Count} itens que impactaram o boot.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAnalyzeBoot_Click", ex.Message);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowError("ERRO", ex.Message);
                TxtBootTime.Text = "Erro";
            }
            finally
            {
                _isServiceOperation = false;
            }
        }
        #endregion
    }
}
