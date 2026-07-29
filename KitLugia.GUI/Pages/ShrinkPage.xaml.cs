using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Pages
{
    public partial class ShrinkPage : Page
    {
        private bool _isBusy;
        private CancellationTokenSource? _cts;

        public ShrinkPage()
        {
            InitializeComponent();
            Loaded += ShrinkPage_Loaded;
        }

        public void Cleanup()
        {
            this.Loaded -= ShrinkPage_Loaded;
            this.DataContext = null;
        }

        private async void ShrinkPage_Loaded(object sender, RoutedEventArgs e)
        {
            await CheckRefindStatusAsync();
        }

        private async Task CheckRefindStatusAsync()
        {
            try
            {
                AppendLog("Verificando status do rEFInd...");
                bool installed = await Task.Run(() =>
                {
                    string? esp = RefindManager.MountEspSync();
                    if (esp == null) return false;
                    string backup = System.IO.Path.Combine(esp, "EFI", "KitLugia", "bootmgfw.original.efi");
                    return System.IO.File.Exists(backup);
                });

                if (installed)
                {
                    BdrStatus.BorderBrush = (System.Windows.Media.Brush)
                        new System.Windows.Media.BrushConverter().ConvertFromString("#4CAF50");
                    TxtStatusIcon.Text = "\u2705";
                    TxtStatusTitle.Text = "rEFInd instalado";
                    TxtStatusDetail.Text = "rEFInd esta ativo no ESP. Use Desinstalar para remove-lo.";
                }
                else
                {
                    BdrStatus.BorderBrush = (System.Windows.Media.Brush)
                        new System.Windows.Media.BrushConverter().ConvertFromString("#FF9800");
                    TxtStatusIcon.Text = "\u26A0\uFE0F";
                    TxtStatusTitle.Text = "rEFInd nao detectado";
                    TxtStatusDetail.Text = "Nenhuma instalacao do rEFInd encontrada no ESP.";
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO ao verificar status: {ex.Message}");
            }
        }

        #region Actions

        private async void BtnInstallRefind_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                ShowProgress("Instalando rEFInd...", "Substituindo bootmgfw.efi...", showCancel: false);

                AppendLog("=== INSTALAR rEFInd ===");

                var (ok, msg) = await RefindManager.InstallRefindOnlyAsync();

                if (ok)
                {
                    AppendLog("rEFInd instalado com sucesso.");
                    TxtStatusTitle.Text = "rEFInd instalado";
                    TxtStatusDetail.Text = "rEFInd esta ativo no ESP.";
                    BdrStatus.BorderBrush = (System.Windows.Media.Brush)
                        new System.Windows.Media.BrushConverter().ConvertFromString("#4CAF50");
                    TxtStatusIcon.Text = "\u2705";
                }
                else
                {
                    AppendLog($"ERRO: {msg}");
                    TxtStatusTitle.Text = "Falha ao instalar";
                    TxtStatusDetail.Text = msg;
                    MessageBox.Show(msg, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO: {ex.Message}");
                MessageBox.Show($"Erro ao instalar rEFInd: {ex.Message}", "Erro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                _cts?.Dispose(); _cts = null;
                HideProgress();
            }
        }

        private async void BtnRemoveRefind_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                var confirm = MessageBox.Show(
                    "Restaurar o Windows Boot Manager original (bootmgfw.efi)?\n" +
                    "Isso removera o rEFInd do ESP.\n\n" +
                    "Deseja continuar?",
                    "Desinstalar rEFInd",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                {
                    AppendLog("Desinstalacao cancelada.");
                    return;
                }

                ShowProgress("Removendo rEFInd...", "Restaurando boot original...", showCancel: false);
                AppendLog("=== DESINSTALAR rEFInd ===");

                var (ok, msg) = await EmergencyBcdBootManager.CleanupAsync(false);

                if (ok)
                {
                    AppendLog("Windows Boot Manager restaurado.");
                    TxtStatusTitle.Text = "Windows Boot Manager restaurado";
                    TxtStatusDetail.Text = "rEFInd removido do ESP.";
                    BdrStatus.BorderBrush = (System.Windows.Media.Brush)
                        new System.Windows.Media.BrushConverter().ConvertFromString("#FF9800");
                    TxtStatusIcon.Text = "\u26A0\uFE0F";
                }
                else
                {
                    AppendLog($"ERRO: {msg}");
                    TxtStatusTitle.Text = "Falha ao desinstalar";
                    TxtStatusDetail.Text = msg;
                    MessageBox.Show($"Falha: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO: {ex.Message}");
                MessageBox.Show($"Erro ao remover rEFInd: {ex.Message}", "Erro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                _cts?.Dispose(); _cts = null;
                HideProgress();
            }
        }

        #endregion

        #region Overlay Helpers

        private void ShowProgress(string title, string step, double pct = -1, bool showCancel = true)
        {
            OverlayBusy.Visibility = Visibility.Visible;
            TxtProgressTitle.Text = title;
            TxtProgressStep.Text = step;
            BtnCancelOp.IsEnabled = showCancel;
            BtnCancelOp.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
            if (pct >= 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = pct;
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
            }
        }

        private void HideProgress()
        {
            OverlayBusy.Visibility = Visibility.Collapsed;
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Value = 0;
            BtnCancelOp.IsEnabled = true;
            BtnCancelOp.Visibility = Visibility.Collapsed;
        }

        private async void BtnCancelOp_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Tem certeza que deseja CANCELAR a operacao atual?",
                "Cancelar Operacao",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            AppendLog(">>> CANCELAMENTO solicitado pelo usuario.");
            _cts?.Cancel();
            BtnCancelOp.IsEnabled = false;
            TxtProgressTitle.Text = "Cancelando...";
            TxtProgressStep.Text = "Aguardando finalizacao do passo atual...";
        }

        #endregion

        #region Shared

        private void AppendLog(string line)
        {
            Dispatcher.Invoke(() =>
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.AppendText($"[{ts}] {line}\n");
                if (LogScroll != null)
                    LogScroll.ScrollToEnd();
                Core.Logger.Log($"[REFIND] {line}");
            });
        }

        #endregion
    }
}
