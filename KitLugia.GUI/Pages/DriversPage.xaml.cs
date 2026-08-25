using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KitLugia.Core;
// --- RESOLUÇÃO DE CONFLITOS DE NAMESPACE ---
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using WinForms = System.Windows.Forms; // Para diálogos de pasta
using Color = System.Windows.Media.Color;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class DriversPage : Page
    {
        private List<DriverItem> _allDrivers = new();
        private List<KernelDriverInfo> _allKernelDrivers = new();
        private CancellationTokenSource? _cts;
        private bool _isDriverOperation;

        public DriversPage(int tabIndex = 0)
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();
            // Carrega drivers em background para não travar a UI
            _ = Task.Run(() => LoadDrivers());
            _ = Task.Run(() => LoadKernelDrivers());
            CheckVerifierStatus(); // Inicia a checagem da aba Diagnóstico

            Loaded += (s, e) => { if (MainTabs != null) MainTabs.SelectedIndex = tabIndex; };
            this.Unloaded += DriversPage_Unloaded;
        }


        public void Cleanup()
        {

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;


            _allDrivers?.Clear();
            _allDrivers = null!;
            _allKernelDrivers?.Clear();
            _allKernelDrivers = null!;

            if (GridDrivers != null)
            {
                GridDrivers.ItemsSource = null;
                GridDrivers.Items.Clear();
            }
            if (GridKernel != null)
            {
                GridKernel.ItemsSource = null;
                GridKernel.Items.Clear();
            }

            this.Unloaded -= DriversPage_Unloaded;


            this.DataContext = null;

            MemoryHelper.TrimWorkingSet();

        }

        private void DriversPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        // =========================================================
        // ABA 1: LISTA DE DRIVERS (GERENCIAMENTO)
        // =========================================================
        #region Drivers List Logic

        private async Task LoadDrivers()
        {
            await Dispatcher.InvokeAsync(() => SetLoading(true, "Analisando Hardware..."));

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Carregando Drivers", "Drivers");

            try
            {
                // Carrega usando o novo método nativo Async
                _allDrivers = await DriverManager.GetSystemDriversAsync(includeMicrosoft: false);

                await Dispatcher.InvokeAsync(() =>
                {
                    FilterAndRefresh();
                    SetLoading(false);
                });

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, $"{_allDrivers.Count} drivers carregados");
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => SetLoading(false));
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
        }

        private void FilterAndRefresh()
        {
            string query = TxtFilter.Text.ToLower().Trim();
            var filtered = _allDrivers;

            if (!string.IsNullOrEmpty(query))
            {
                filtered = _allDrivers.Where(d =>
                    d.DeviceName.ToLower().Contains(query) ||
                    d.Provider.ToLower().Contains(query) ||
                    d.InfName.ToLower().Contains(query)
                ).ToList();
            }

            GridDrivers.ItemsSource = filtered;
            if (TxtCount != null) TxtCount.Text = $"{filtered.Count} Drivers";
            if (TxtStatus != null) TxtStatus.Text = "Pronto.";
        }

        private void SetLoading(bool isLoading, string msg = "Processando...")
        {
            if (LoadingOverlay != null)
            {
                if (TxtLoadingMsg != null) TxtLoadingMsg.Text = msg;
                LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // --- EVENTOS DE UI ---

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => FilterAndRefresh();

        // --- FERRAMENTAS ---

        private async void BtnInstallFromFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Selecione o driver baixado (CAB, ZIP ou INF)",
                    Filter = "Drivers Compactados|*.cab;*.zip|Arquivo INF|*.inf|Todos|*.*",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() == true)
                {
                    string path = dialog.FileName;
                    SetLoading(true, "Extraindo e Instalando...");

                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Instalando Driver", "Drivers");

                    var result = await DriverManager.SmartInstallDriver(path);

                    SetLoading(false);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        if (result.Success)
                        {
                            mw.ShowSuccess("SUCESSO", result.Message);
                            LoadDrivers();
                        }
                        else mw.ShowError("FALHA", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnInstallFromFolder_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }

        private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                using (var dialog = new WinForms.FolderBrowserDialog())
                {
                    dialog.Description = "Selecione onde salvar o backup dos drivers";
                    if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                    {
                        if (Application.Current.MainWindow is MainWindow mw)
                        {
                            var res = await Task.Run(() => DriverManager.BackupDrivers(dialog.SelectedPath));
                            if (res.Success) mw.ShowSuccess("BACKUP", res.Message);
                            else mw.ShowError("ERRO", res.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnBackup_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }

        private async void BtnExportList_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "Drivers_List.txt",
                    Filter = "Texto (*.txt)|*.txt"
                };

                if (dialog.ShowDialog() == true)
                {
                    await Task.Run(() => DriverManager.ExportDriverListToTxt(dialog.FileName));
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("EXPORTADO", "Lista salva com sucesso.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnExportList_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }

        private void BtnWindowsUpdate_Click(object sender, RoutedEventArgs e)
        {
            DriverManager.OpenWindowsUpdateSettings();
        }

        // --- MENU DE CONTEXTO ---

        private async void CtxUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                if (GridDrivers.SelectedItem is DriverItem driver && Application.Current.MainWindow is MainWindow mw)
                {
                    if (await mw.ShowConfirmationDialog($"REMOVER DRIVER?\n\n{driver.DeviceName}\nIsso pode desativar o dispositivo."))
                    {
                        SetLoading(true, "Removendo...");

                        string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Desinstalando {driver.DeviceName}", "Drivers");

                        var result = await Task.Run(() => DriverManager.UninstallDriver(driver.InfName));
                        SetLoading(false);

                        Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

                        if (result.Success) { mw.ShowSuccess("SUCESSO", result.Message); LoadDrivers(); }
                        else mw.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("CtxUninstall_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }

        private void CtxCopyName_Click(object sender, RoutedEventArgs e)
        {
            if (GridDrivers.SelectedItem is DriverItem driver) Clipboard.SetText(driver.DeviceName);
        }

        private void CtxCopyId_Click(object sender, RoutedEventArgs e)
        {
            if (GridDrivers.SelectedItem is DriverItem driver) Clipboard.SetText(driver.HardwareId);
        }
        #endregion

        // =========================================================
        // ABA 2: DRIVERS DE INICIALIZAÇÃO (KERNEL - Registry Type 1/2)
        // =========================================================
        #region Kernel Drivers Logic

        private async Task LoadKernelDrivers()
        {
            try
            {
                await Dispatcher.InvokeAsync(() => SetLoading(true, "Analisando drivers de inicialização..."));
                var token = _cts?.Token ?? CancellationToken.None;
                var drivers = await Task.Run(() => KernelDriverManager.GetKernelDrivers(includeDisabled: false), token);
                _allKernelDrivers = drivers;

                await Dispatcher.InvokeAsync(() =>
                {
                    ApplyKernelFilter();
                    UpdateKernelSummary();
                    SetLoading(false);
                });
            }
            catch (OperationCanceledException) { await Dispatcher.InvokeAsync(() => SetLoading(false)); }
            catch (Exception ex)
            {
                Logger.LogError("LoadKernelDrivers", ex.Message);
                await Dispatcher.InvokeAsync(() => SetLoading(false));
            }
        }

        private void UpdateKernelSummary()
        {
            var (total, third, boot, sys, auto) = KernelDriverManager.GetSummary(_allKernelDrivers);
            int risk = _allKernelDrivers.Count(d => d.IsThirdParty && d.StartValue <= 1);
            if (TxtKernelTotal != null) TxtKernelTotal.Text = total.ToString();
            if (TxtKernelMs != null) TxtKernelMs.Text = (total - third).ToString();
            if (TxtKernelThird != null) TxtKernelThird.Text = third.ToString();
            if (TxtKernelRisk != null) TxtKernelRisk.Text = risk.ToString();
        }

        private void ApplyKernelFilter()
        {
            if (_allKernelDrivers == null || GridKernel == null) return;
            string filter = TxtKernelFilter?.Text?.ToLower().Trim() ?? "";
            bool thirdOnly = ChkKernelThirdOnly?.IsChecked == true;
            bool riskOnly = ChkKernelRiskOnly?.IsChecked == true;

            int startSel = CboKernelStart?.SelectedIndex ?? 0;
            int? wantedStart = startSel switch { 1 => 0, 2 => 1, 3 => 2, 4 => 3, _ => null };

            var filtered = _allKernelDrivers.Where(d =>
            {
                if (wantedStart.HasValue && d.StartValue != wantedStart.Value) return false;
                if (thirdOnly && !d.IsThirdParty) return false;
                if (riskOnly && !(d.IsThirdParty && d.StartValue <= 1)) return false;
                if (!string.IsNullOrEmpty(filter))
                {
                    if (!d.Name.ToLower().Contains(filter) && !(d.ImagePath ?? "").ToLower().Contains(filter) && !(d.ParentSoftware ?? "").ToLower().Contains(filter))
                        return false;
                }
                return true;
            }).ToList();

            // Ordena: risco primeiro (Boot terceiros no topo)
            filtered = filtered.OrderBy(d => d.IsThirdParty && d.StartValue <= 1 ? 0 : d.IsThirdParty ? 1 : 2).ThenBy(d => d.StartValue).ThenBy(d => d.Name).ToList();

            GridKernel.ItemsSource = filtered;
            if (TxtKernelCount != null) TxtKernelCount.Text = $"{filtered.Count} drivers";
        }

        private void TxtKernelFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyKernelFilter();
        private void CboKernelStart_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyKernelFilter();
        private void ChkKernelThirdOnly_Click(object sender, RoutedEventArgs e) => ApplyKernelFilter();
        private void ChkKernelRiskOnly_Click(object sender, RoutedEventArgs e) => ApplyKernelFilter();

        private async void BtnReloadKernel_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try { await LoadKernelDrivers(); } finally { _isDriverOperation = false; }
        }

        private void BtnCopyKernel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var list = GridKernel.ItemsSource as IEnumerable<KernelDriverInfo>;
                if (list == null) return;
                var lines = list.Select(d => $"{d.StartIcon} {d.Name,-30} {d.StartName,-8} {(d.IsThirdParty ? "TERCEIROS" : "Microsoft"),-10} {d.ParentSoftware} | {d.ImagePath}");
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                if (Application.Current.MainWindow is MainWindow mw) mw.ShowSuccess("COPIADO", $"{list.Count()} linhas copiadas.");
            }
            catch (Exception ex) { Logger.LogError("BtnCopyKernel", ex.Message); }
        }

        private void CtxKernelCopyName_Click(object sender, RoutedEventArgs e)
        {
            if (GridKernel.SelectedItem is KernelDriverInfo d) Clipboard.SetText(d.Name);
        }
        private void CtxKernelCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (GridKernel.SelectedItem is KernelDriverInfo d) Clipboard.SetText(string.IsNullOrWhiteSpace(d.ResolvedPath) ? d.ImagePath : d.ResolvedPath);
        }
        private void CtxKernelOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GridKernel.SelectedItem is not KernelDriverInfo d) return;
                string p = d.ResolvedPath;
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
                {
                    if (Application.Current.MainWindow is MainWindow mw) mw.ShowError("ERRO", $"Arquivo não encontrado:\n{d.ImagePath}");
                    return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.LogError("CtxKernelOpenFolder", ex.Message); }
        }
        private void CtxKernelViewJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GridKernel.SelectedItem is not KernelDriverInfo d) return;
                string json = System.Text.Json.JsonSerializer.Serialize(d, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Clipboard.SetText(json);
                if (Application.Current.MainWindow is MainWindow mw) mw.ShowInfo("JSON", "Detalhes copiados como JSON.");
            }
            catch (Exception ex) { Logger.LogError("CtxKernelViewJson", ex.Message); }
        }

        #endregion

        // =========================================================
        // ABA 3: DIAGNÓSTICO (BSOD / VERIFIER)
        // =========================================================
        #region Diagnostics Logic

        private async Task CheckVerifierStatus()
        {
            await Task.Run(() =>
            {
                if (_cts?.IsCancellationRequested == true) return;

                // Chama o método que restauramos no DiagnosticsManager
                var status = Toolbox.GetDriverVerifierStatus();

                Dispatcher.Invoke(() =>
                {
                    TxtVerifierStatus.Text = status.StatusMessage;

                    if (status.IsActive)
                    {
                        // Vermelho (Ativo = Teste de estresse rodando)
                        TxtVerifierStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 85, 85));
                    }
                    else
                    {
                        // Cinza (Inativo = Normal)
                        TxtVerifierStatus.Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150));
                    }
                });
            });
        }

        private async void BtnEnableVerifier_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    bool confirm = await mw.ShowConfirmationDialog(
                        "PERIGO: ATIVAR DRIVER VERIFIER\n\n" +
                        "Isso forçará um teste de estresse em todos os drivers na próxima reinicialização.\n" +
                        "Se houver um driver ruim, seu PC dará TELA AZUL (BSOD) durante o boot.\n\n" +
                        "Você sabe entrar em Modo de Segurança para desativar isso se algo der errado?");

                    if (!confirm) return;

                    mw.ShowInfo("ATIVANDO", "Configurando Verifier...");

                    var result = await Task.Run(() => Toolbox.EnableDriverVerifier());

                    if (result.Success)
                    {
                        mw.ShowSuccess("ATIVADO", result.Message);
                        CheckVerifierStatus();
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnEnableVerifier_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }

        private async void BtnDisableVerifier_Click(object sender, RoutedEventArgs e)
        {
            if (_isDriverOperation) return;
            _isDriverOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var result = await Task.Run(() => Toolbox.ResetDriverVerifier());

                    if (result.Success)
                    {
                        mw.ShowSuccess("DESATIVADO", "Driver Verifier foi resetado com sucesso.");
                        CheckVerifierStatus();
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDisableVerifier_Click", ex.Message);
            }
            finally
            {
                _isDriverOperation = false;
            }
        }
        #endregion
    }
}