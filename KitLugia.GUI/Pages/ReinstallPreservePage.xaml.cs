using KitLugia.Core;
using KitLugia.GUI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KitLugia.GUI.Pages
{
    public partial class ReinstallPreservePage : Page
    {
        private bool _winpeReady;
        private string? _isoPath;
        private List<DriveInfo> _drives = new();

        public ReinstallPreservePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object s, RoutedEventArgs e)
        {
            await CheckWinpeStatusAsync();
            await RefreshDrivesAsync();
        }

        #region WinPE Status

        private async Task CheckWinpeStatusAsync()
        {
            try
            {
                bool found = await Task.Run(() => WinbootManager.IsWinpeReady());
                _winpeReady = found;

                if (found)
                {
                    BdrWinpeStatus.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#223322");
                    TxtWinpeStatus.Text = "WinPE pronto";
                    TxtWinpeStatus.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#88FF88");
                    BtnRemoveWinpe.Visibility = Visibility.Visible;
                }
                else
                {
                    BdrWinpeStatus.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#222233");
                    TxtWinpeStatus.Text = "WinPE nao preparado";
                    TxtWinpeStatus.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#8888FF");
                    BtnRemoveWinpe.Visibility = Visibility.Collapsed;
                }

                UpdateStartButton();
            }
            catch
            {
                TxtWinpeStatus.Text = "Erro ao verificar";
            }
        }

        #endregion

        #region Disk Loading

        private async Task RefreshDrivesAsync()
        {
            try
            {
                TxtStatusBar.Text = "Carregando unidades...";
                var result = await Task.Run(() => DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .ToList());

                _drives = result ?? new();
                CboTargetDrive.ItemsSource = _drives.Select(d => $"{d.Name}  ({d.TotalSize / 1024 / 1024 / 1024:F0} GB)").ToList();
                if (CboTargetDrive.Items.Count > 0)
                    CboTargetDrive.SelectedIndex = 0;

                TxtStatusBar.Text = $"{_drives.Count} unidade(s) encontrada(s).";
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Erro ao carregar discos: {ex.Message}";
            }
        }

        #endregion

        #region ISO Selection

        private async void BtnLoadIso_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Arquivos ISO|*.iso",
                Title = "Selecione o ISO do Windows para instalar"
            };
            if (dlg.ShowDialog() != true) return;

            _isoPath = dlg.FileName;
            TxtIsoPath.Text = Path.GetFileName(_isoPath);
            TxtStatusBar.Text = $"ISO carregado: {Path.GetFileName(_isoPath)}";

            await DetectIsoEditionsAsync();
            UpdateStartButton();
        }

        private async Task DetectIsoEditionsAsync()
        {
            if (string.IsNullOrEmpty(_isoPath) || !File.Exists(_isoPath))
                return;

            PanelEdition.Visibility = Visibility.Collapsed;
            TxtStatusBar.Text = "Detectando edicoes do ISO...";

            try
            {
                var editions = await Task.Run(() => WinbootManager.DetectIsoEditions(_isoPath));
                if (editions != null && editions.Count > 0)
                {
                    CboEdition.ItemsSource = editions;
                    CboEdition.SelectedIndex = 0;
                    PanelEdition.Visibility = Visibility.Visible;
                    TxtStatusBar.Text = $"{editions.Count} edicao(oes) encontrada(s).";
                }
                else
                {
                    TxtStatusBar.Text = "Nenhuma edicao detectada no ISO.";
                }
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Erro ao ler ISO: {ex.Message}";
            }
        }

        #endregion

        #region WinPE Actions

        private async void BtnPrepareWinpe_Click(object sender, RoutedEventArgs e)
        {
            ShowBusy("PREPARANDO WINPE",
                "Baixando e configurando WinPE no disco local...\n\n" +
                "1. Baixar WinPE base (se necessario)\n" +
                "2. Customizar com script de instalacao\n" +
                "3. Configurar entrada de boot RAMDISK\n\n" +
                "O PC NAO sera reiniciado agora.");

            try
            {
                UpdateStatus("Aguarde...");
                var (ok, msg) = await Task.Run(() => WinbootManager.PrepareWinpeBoot());

                if (ok)
                    ShowBusyResult($"WinPE preparado com sucesso!\n\n{msg}");
                else
                    ShowBusyResult($"Falha ao preparar WinPE.\n{msg}");

                await CheckWinpeStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnRemoveWinpe_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Remover WinPE?\n\nIsso vai remover a entrada de boot RAMDISK e deletar os arquivos.",
                "Remover WinPE", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            ShowBusy("REMOVENDO WINPE", "Limpando artefatos do WinPE...");
            try
            {
                bool ok = await Task.Run(() => WinbootManager.RemoveWinpeAsync());
                ShowBusyResult(ok ? "WinPE removido com sucesso." : "Falha ao remover WinPE.");
                await CheckWinpeStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        #endregion

        #region Start Operation

        private void UpdateStartButton()
        {
            bool hasIso = !string.IsNullOrEmpty(_isoPath) && File.Exists(_isoPath);
            bool hasWinpe = _winpeReady;
            bool hasDrive = CboTargetDrive.SelectedIndex >= 0;

            BtnStart.IsEnabled = hasIso && hasWinpe && hasDrive;
            TxtReadyStatus.Text = hasIso && hasWinpe && hasDrive
                ? "Pronto para iniciar. Revise as opcoes e clique em INICIAR FRESH INSTALL."
                : (!hasWinpe ? "Prepare o WinPE primeiro."
                    : !hasIso ? "Carregue um ISO do Windows."
                    : "Selecione a particao alvo.");
        }

        private void BtnRefreshDisks_Click(object sender, RoutedEventArgs e)
            => _ = RefreshDrivesAsync();

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string? targetDrive = null;
            if (CboTargetDrive.SelectedItem is string driveStr && driveStr.Length > 0)
                targetDrive = driveStr.Substring(0, 1);

            string summary = $"WinPE: {(_winpeReady ? "Pronto" : "Ausente")}\n" +
                             $"ISO: {Path.GetFileName(_isoPath)}\n" +
                             $"Particao alvo: {targetDrive}:\\\n" +
                             $"Edicao: {CboEdition.SelectedItem}\n\n" +
                             $"Preservar:\n" +
                             $"  - Perfis de usuario: {(ChkPreserveUsers.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Program Files: {(ChkPreserveProgramFiles.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Registry (Str. C): {(ChkPreserveRegistry.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Personalizacao: {(ChkPreservePersonalization.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Drivers: {(ChkPreserveDrivers.IsChecked == true ? "Sim" : "Nao")}\n\n" +
                             $"O PC sera reiniciado no WinPE para executar a operacao.";

            TxtConfirmSummary.Text = summary;
            ChkConfirm.IsChecked = false;
            BtnConfirmGo.IsEnabled = false;
            OverlayConfirm.Visibility = Visibility.Visible;
        }

        private void ChkConfirm_Checked(object sender, RoutedEventArgs e)
            => BtnConfirmGo.IsEnabled = ChkConfirm.IsChecked == true;

        private void ChkConfirm_Unchecked(object sender, RoutedEventArgs e)
            => BtnConfirmGo.IsEnabled = false;

        private void BtnCancelConfirm_Click(object sender, RoutedEventArgs e)
            => OverlayConfirm.Visibility = Visibility.Collapsed;

        private async void BtnConfirmGo_Click(object sender, RoutedEventArgs e)
        {
            OverlayConfirm.Visibility = Visibility.Collapsed;

            ShowBusy("INICIANDO FRESH INSTALL + PRESERVACAO",
                "Preparando configuracao e agendando reboot no WinPE...\n\n" +
                "1. Salvar registry do Windows atual\n" +
                "2. Escrever script de instalacao\n" +
                "3. Agendar reboot no WinPE\n\n" +
                "O WinPE executara:\n" +
                "  - Mover dados para C:\\!\n" +
                "  - Aplicar Windows novo via DISM\n" +
                "  - Mesclar registry (se ativado)\n" +
                "  - Restaurar dados\n" +
                "  - Reboot no Windows novo");

            try
            {
                string targetDrive = (CboTargetDrive.SelectedItem as string)?[0].ToString() ?? "C";
                string edition = CboEdition.SelectedItem as string ?? "1";
                string isoPath = _isoPath ?? "";

                var options = new PreservationOptions
                {
                    TargetDrive = targetDrive,
                    IsoPath = isoPath,
                    EditionIndex = edition,
                    PreserveUsers = ChkPreserveUsers.IsChecked == true,
                    PreserveProgramFiles = ChkPreserveProgramFiles.IsChecked == true,
                    PreserveRegistry = ChkPreserveRegistry.IsChecked == true,
                    PreservePersonalization = ChkPreservePersonalization.IsChecked == true,
                    PreserveDrivers = ChkPreserveDrivers.IsChecked == true
                };

                UpdateStatus("Agendando operacao no WinPE...");

                var (ok, msg) = await Task.Run(() =>
                    WinbootManager.ScheduleReinstallPreserve(options));

                if (ok)
                {
                    ShowBusyResult($"{msg}\n\n" +
                        $"O PC sera reiniciado em 10 segundos.\n" +
                        $"O WinPE executara o fresh install com preservacao.\n" +
                        $"Apos a conclusao, o Windows novo iniciara com seus dados.");
                }
                else
                {
                    ShowBusyResult($"Falha ao agendar operacao.\n{msg}");
                }
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        #endregion

        #region Busy Overlay

        private void ShowBusy(string title, string description)
        {
            OverlayBusy.Visibility = Visibility.Visible;
            TxtOpTitle.Text = title;
            TxtOpDesc.Text = description;
            TxtOpStatus.Text = "Processando...";
            PanelOpFooter.Visibility = Visibility.Collapsed;
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                TxtOpStatus.Text = status;
                TxtOpDesc.Text += $"\n{status}";
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowBusyResult(string result)
        {
            TxtOpStatus.Text = result;
            PanelOpFooter.Visibility = Visibility.Visible;
        }

        private void BtnCloseOverlay_Click(object sender, RoutedEventArgs e)
            => OverlayBusy.Visibility = Visibility.Collapsed;

        #endregion

        #region Navigation

        private void BtnBack_Click(object sender, RoutedEventArgs e)
            => NavigateToPage(PageType.Dashboard);

        private void NavigateToPage(PageType type)
            => (Window.GetWindow(this) as MainWindow)?.NavigateToPage(type);

        private void ShowToast(string message)
            => TxtStatusBar.Text = message;

        #endregion
    }
}
