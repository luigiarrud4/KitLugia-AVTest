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
        private int _initialTabIndex = 0;
        private CancellationTokenSource? _cts;
        private string _addMode = "Normal";
        private bool _isServiceOperation;

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
                var apps = await Task.Run(() => StartupManager.GetStartupAppsWithDetails(false), cancellationToken);
                var schedulerApps = await Task.Run(() => StartupManager.GetExternalTaskSchedulerApps(), cancellationToken);
                var merged = apps.Concat(schedulerApps)
                    .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(a => a.Status.ToString() == "Elevated")
                    .ThenByDescending(a => a.Status.ToString() == "Enabled")
                    .ThenBy(a => a.Name)
                    .ToList();
                GridStartup.ItemsSource = merged;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task LoadStartupApps() => await LoadStartupApps(_cts?.Token ?? CancellationToken.None);

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
                                string script = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{startupDir}\\{appName}.lnk');$s.TargetPath='{finalCommand}';$s.Save()";
                                SystemUtils.RunExternalProcess("powershell", $"-Command \"{script}\"", hidden: true);
                                return (true, $"'{appName}' adicionado à inicialização padrão.");
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

        private async void MenuMoveToTurbo_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (GridStartup.SelectedItem is StartupAppDetails selectedApp && Application.Current.MainWindow is MainWindow mw)
                {
                    if (selectedApp.Location.Contains("Turbo Boot"))
                    {
                        mw.ShowInfo("TURBO BOOT", "Este aplicativo já está no KitLugia Turbo Boot.");
                        return;
                    }

                    if (!await mw.ShowConfirmationDialog($"Mover '{selectedApp.Name}' para o Turbo Boot (KitLugia)?\n\nIsso utilizará uma inicialização paralela de alta prioridade.")) return;

                    var resultAdd = await Task.Run(() => StartupManager.DelegateToKitLugia(selectedApp.Name));
                    if (resultAdd.Success) { mw.ShowSuccess("BEM VINDO AO TURBO BOOT", resultAdd.Message); LoadStartupApps(); }
                    else mw.ShowError("ERRO", resultAdd.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuMoveToTurbo_Click", ex.Message);
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

                    await Task.Run(() => StartupManager.RemoveStartupItem(selectedApp.Name));
                    var result = await Task.Run(() => StartupManager.CreateElevatedStartupTask(selectedApp.Name, path, args));
                    
                    if (result.Success) { mw.ShowSuccess("ELEVADO COM SUCESSO", result.Message); LoadStartupApps(); }
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

                    await Task.Run(() => StartupManager.RemoveStartupItem(selectedApp.Name));
                    var result = await Task.Run(() => StartupManager.CreateElevatedDelayedStartupTask(selectedApp.Name, path, args));
                    
                    if (result.Success) { mw.ShowSuccess("ELEVADO (ATRASO) COM SUCESSO", result.Message); LoadStartupApps(); }
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
                        LoadStartupApps();
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

                StartupManager.ExtractCommandParts(app.FullCommand, out string? path, out string? args);
                if (string.IsNullOrWhiteSpace(path))
                {
                    mw.ShowError("ERRO", "Caminho do executável inválido.");
                    return;
                }

                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Executando {app.Name}", "Services");
                bool success = false;
                string message = "";

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
                        success = true;
                        message = $"{app.Name} iniciado com sucesso.";
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        message = $"Erro ao executar: {ex.Message}";
                    }
                });

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, success, message);

                if (success)
                    mw.ShowSuccess("EXECUTANDO", message);
                else
                    mw.ShowError("ERRO", message);
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
            catch (OperationCanceledException)
            {

            }
        }

        private async Task LoadServices() => await LoadServices(_cts?.Token ?? CancellationToken.None);

        private void ApplyServiceFilter()
        {
            string filter = TxtSearchService.Text.Trim().ToLower();
            bool showDangerous = ChkShowDangerous.IsChecked == true;

            var filtered = _allServices.Where(s =>
            {
                bool matchesText = string.IsNullOrEmpty(filter) || s.DisplayName.ToLower().Contains(filter) || s.Name.ToLower().Contains(filter);
                bool matchesSafety = showDangerous || s.Safety != ServiceSafetyLevel.Dangerous;
                return matchesText && matchesSafety;
            }).ToList();

            GridServices.ItemsSource = filtered;
            if (TxtServiceCount != null) TxtServiceCount.Text = $"{filtered.Count} Serviços";
        }

        private void TxtSearchService_TextChanged(object sender, TextChangedEventArgs e) => ApplyServiceFilter();
        private void ChkShowDangerous_Click(object sender, RoutedEventArgs e) => ApplyServiceFilter();
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
        // ABA 3: TAREFAS AGENDADAS (NOVO)
        // =========================================================
        #region Scheduled Tasks Logic

        private async Task LoadScheduledTasks(CancellationToken cancellationToken)
        {
            try
            {
                var tasks = await Task.Run(() => BackgroundProcessManager.GetScheduledTasksStatus(), cancellationToken);
                GridTasks.ItemsSource = tasks;
            }
            catch (OperationCanceledException)
            {

            }
        }

        private async Task LoadScheduledTasks() => await LoadScheduledTasks(_cts?.Token ?? CancellationToken.None);

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

        private async void BtnDisableAllTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_isServiceOperation) return;
            _isServiceOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (!await mw.ShowConfirmationDialog("Isso desativará TODAS as tarefas de telemetria listadas.\nDeseja continuar?")) return;

                    var result = await Task.Run(() => BackgroundProcessManager.DisableTelemetryTasks());
                    mw.ShowSuccess("TELEMETRIA", result.Message);
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
        #endregion

        // =========================================================
        // ABA 4: ANÁLISE DE BOOT (NOVO)
        // =========================================================
        #region Boot Analysis Logic

        private async void BtnAnalyzeBoot_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                TxtBootTime.Text = "Analisando...";
                mw.ShowInfo("AGUARDE", "Lendo logs de eventos do sistema...");

                try
                {
                    var result = await Task.Run(() => BootOptimizerManager.AnalyzeBootPerformance());

                    if (!string.IsNullOrEmpty(result.ServiceStatusMessage))
                    {
                        mw.ShowError("AVISO", result.ServiceStatusMessage);
                        TxtBootTime.Text = "N/A";
                        return;
                    }

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

                    // Junta as duas listas para exibir na tabela

                    // Típico: 10-30 itens de boot lento
                    var combinedList = new List<PerformanceEvent>(30);
                    combinedList.AddRange(result.SlowStartupItems);
                    combinedList.AddRange(result.HighImpactApps);

                    GridBootItems.ItemsSource = combinedList;

                    if (combinedList.Count == 0)
                        mw.ShowSuccess("ÓTIMO", "Nenhum atraso significativo (>1s) encontrado no último boot.");
                    else
                        mw.ShowInfo("ANÁLISE", $"Encontrados {combinedList.Count} itens que impactaram o boot.");

                }
                catch (Exception ex)
                {
                    mw.ShowError("ERRO", ex.Message);
                    TxtBootTime.Text = "Erro";
                }
            }
        }
        #endregion
    }
}
