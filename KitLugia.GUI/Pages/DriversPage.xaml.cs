using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private CancellationTokenSource? _cts;
        private bool _isDriverOperation;

        public DriversPage()
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();
            // Carrega drivers em background para não travar a UI
            _ = Task.Run(() => LoadDrivers());
            CheckVerifierStatus(); // Inicia a checagem da aba Diagnóstico

            this.Unloaded += DriversPage_Unloaded;
        }


        public void Cleanup()
        {

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;


            _allDrivers?.Clear();
            _allDrivers = null!;

            if (GridDrivers != null)
            {
                GridDrivers.ItemsSource = null;
                GridDrivers.Items.Clear();
            }

            this.Unloaded -= DriversPage_Unloaded;


            this.DataContext = null;



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
        // ABA 2: DIAGNÓSTICO (BSOD / VERIFIER)
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