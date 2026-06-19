using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;

using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace KitLugia.GUI.Pages
{
    public partial class ActivationPage : Page
    {
        private bool _isActivationOperation;

        public ActivationPage()
        {
            InitializeComponent();
            Loaded += ActivationPage_Loaded;

            Unloaded += ActivationPage_Unloaded;
        }


        public void Cleanup()
        {
            Loaded -= ActivationPage_Loaded;
            Unloaded -= ActivationPage_Unloaded;


            this.DataContext = null;




        }

        private void ActivationPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async void ActivationPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isActivationOperation) return;
            _isActivationOperation = true;
            try
            {
                await LoadStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("ActivationPage_Loaded", ex.Message);
            }
            finally
            {
                _isActivationOperation = false;
            }
        }

        /// <summary>
        /// Botão principal: executa MAS_AIO.cmd
        /// </summary>
        private void BtnExecutarMAS_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string masPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "External", "MAS", "MAS_AIO.cmd");

                if (!System.IO.File.Exists(masPath))
                {
                    ShowError("ERRO", $"Arquivo não encontrado:\n{masPath}\n\nVerifique se a pasta External\\MAS está junto ao executável.");
                    return;
                }

                ShowInfo("MAS", "Iniciando Microsoft Activation Scripts...");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{masPath}\"",
                    UseShellExecute = true,
                    Verb = "runas", // Admin
                    WorkingDirectory = System.IO.Path.GetDirectoryName(masPath)
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User cancelled UAC
                ShowInfo("CANCELADO", "Execução cancelada pelo usuário.");
            }
            catch (Exception ex)
            {
                ShowError("ERRO", ex.Message);
            }
        }

        /// <summary>
        /// Carrega status de ativação via slmgr (rápido).
        /// </summary>
        private async Task LoadStatusAsync()
        {
            try
            {
                TxtWindowsStatus.Text = "Verificando...";
                TxtWindowsProduct.Text = "Verificando...";

                var status = await ActivationManager.GetWindowsActivationStatusAsync();

                TxtWindowsProduct.Text = status.ProductName;
                TxtWindowsStatus.Text = status.LicenseStatus;
                TxtPartialKey.Text = string.IsNullOrEmpty(status.PartialProductKey) ? "N/A" : status.PartialProductKey;
                TxtActivationMethod.Text = string.IsNullOrEmpty(status.ActivationMethod) ? "N/A" : status.ActivationMethod;

                // Cor baseada no status
                var color = status.IsActivated ? "#4CAF50" : "#FFA500";
                TxtWindowsStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            }
            catch (Exception ex)
            {
                Logger.Log($"[ActivationPage] Erro: {ex.Message}");
                TxtWindowsStatus.Text = "Erro";
                TxtWindowsProduct.Text = "Erro ao verificar";
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isActivationOperation) return;
            _isActivationOperation = true;
            try
            {
                if (sender is Button btn) btn.IsEnabled = false;
                await LoadStatusAsync();
                ShowInfo("ATUALIZADO", "Status de ativação atualizado.");
                if (sender is Button btn2) btn2.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRefresh_Click", ex.Message);
            }
            finally
            {
                _isActivationOperation = false;
            }
        }

        private void BtnDocumentation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://massgrave.dev/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowError("ERRO", ex.Message);
            }
        }

        private void BtnCheckInternet_Click(object sender, RoutedEventArgs e)
        {
            bool connected = ActivationManager.IsInternetConnected();
            if (connected)
                ShowInfo("INTERNET", "✅ Conexão com a internet detectada.");
            else
                ShowError("INTERNET", "❌ Sem conexão com a internet.");
        }

        private void ShowInfo(string title, string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw) mw.ShowInfo(title, msg);
        }
        private void ShowError(string title, string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw) mw.ShowError(title, msg);
        }
    }
}
