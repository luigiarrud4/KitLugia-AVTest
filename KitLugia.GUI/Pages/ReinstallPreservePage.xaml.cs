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
        private List<(string Letter, ulong Free, ulong Size, uint Disk, uint Part)> _targets = new();

        public ReinstallPreservePage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object s, RoutedEventArgs e)
        {
            await CheckWinpeStatusAsync();
            await RefreshDrivesAsync();
            LoadWinpeLogs();
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
                TxtStatusBar.Text = "Carregando unidades (Storage API)...";
                var targets = await Task.Run(() =>
                {
                    var list = new List<(string Letter, ulong Free, ulong Size, uint Disk, uint Part)>();
                    var disks = PartitionManager.GetAllDisks() ?? new List<DiskInfoEx>();
                    foreach (var disk in disks)
                    {
                        foreach (var part in disk.Partitions)
                        {
                            if (part.IsUnallocated) continue;
                            string letter = part.DriveLetter?.Trim().TrimEnd(':');
                            if (string.IsNullOrEmpty(letter) || letter.Length != 1 || letter[0] < 'A' || letter[0] > 'Z') continue;
                            list.Add((letter.ToUpperInvariant(), part.FreeSpace, part.Size, disk.Index, part.Index));
                        }
                    }
                    return list.OrderBy(x => x.Letter).ToList();
                });

                _targets = targets;
                CboTargetDrive.ItemsSource = targets
                    .Select(t => $"{t.Letter}:  ({t.Free / 1024.0 / 1024 / 1024:F0} GB livre de {t.Size / 1024.0 / 1024 / 1024:F0} GB)  [Disco {t.Disk} Part {t.Part}]")
                    .ToList();
                if (CboTargetDrive.Items.Count > 0)
                    CboTargetDrive.SelectedIndex = 0;

                TxtStatusBar.Text = $"{targets.Count} volume(s) elegivel(eis) encontrado(s).";
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Erro ao carregar discos: {ex.Message}";
            }
        }

        /// <summary>
        /// Lê os logs persistentes gerados pelo WinPE (shrink + fresh install) e mostra na página.
        /// </summary>
        private void LoadWinpeLogs()
        {
            try
            {
                var logs = WinbootManager.ReadAllWinpeLogs();
                if (logs.Count == 0)
                {
                    TxtOperationLog.Text = "Nenhum log do WinPE encontrado ainda.\nApos o reboot e a operacao, o resultado aparecera aqui.";
                    return;
                }

                var sb = new System.Text.StringBuilder();
                foreach (var kv in logs)
                {
                    sb.AppendLine($"===== {kv.Key} =====");
                    sb.AppendLine(kv.Value);
                    sb.AppendLine();
                }
                TxtOperationLog.Text = sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                TxtOperationLog.Text = $"Erro ao ler logs: {ex.Message}";
            }
        }

        private void BtnRefreshLog_Click(object sender, RoutedEventArgs e)
            => LoadWinpeLogs();

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
            int selIdx = CboTargetDrive.SelectedIndex;

            ulong freeGb = 0;
            bool hasSpace = false;
            if (selIdx >= 0 && selIdx < _targets.Count)
            {
                freeGb = _targets[selIdx].Free / (1024UL * 1024 * 1024);
                hasSpace = freeGb >= 10; // apenas informativo — o WinPE deleta o Windows antigo se faltar espaco
            }

            // O WinPE inicia de qualquer jeito: se nao houver espaco, ele deleta o Windows antigo
            // e extrai o ISO dentro do proprio WinPE. O botao so exige ISO + alvo selecionado.
            BtnStart.IsEnabled = hasIso && (selIdx >= 0);
            TxtReadyStatus.Text =
                !hasIso ? "Carregue um ISO do Windows."
                : selIdx < 0 ? "Selecione a particao alvo."
                : !hasWinpe && !hasSpace ? $"WinPE ausente (sera preparado automaticamente ao iniciar) e espaco livre baixo ({freeGb} GB): o WinPE deletara o Windows antigo e extraira o ISO no proprio WinPE."
                : !hasWinpe ? "WinPE nao preparado — sera preparado automaticamente ao iniciar."
                : !hasSpace ? $"Espaco livre baixo ({freeGb} GB): o WinPE deletara o Windows antigo e extraira o ISO dentro do proprio WinPE."
                : "Pronto para iniciar. O WinPE removera o Windows antigo e aplicara a imagem DIRETAMENTE na particao alvo, preservando seus dados.";
        }

        /// <summary>
        /// Extrai o indice numerico da edicao selecionada (ex: "2 - Windows Pro" → "2").
        /// </summary>
        private string GetSelectedEditionIndex()
        {
            string? item = CboEdition.SelectedItem as string;
            if (string.IsNullOrEmpty(item)) return "1";
            var m = System.Text.RegularExpressions.Regex.Match(item, @"^\s*(\d+)");
            return m.Success ? m.Groups[1].Value : "1";
        }

        private void BtnRefreshDisks_Click(object sender, RoutedEventArgs e)
            => _ = RefreshDrivesAsync();

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            string? targetDrive = null;
            if (CboTargetDrive.SelectedItem is string driveStr && driveStr.Length > 0)
                targetDrive = driveStr.Substring(0, 1);

            string freeInfo = "";
            if (CboTargetDrive.SelectedIndex >= 0 && CboTargetDrive.SelectedIndex < _targets.Count)
            {
                var t = _targets[CboTargetDrive.SelectedIndex];
                freeInfo = $"Espaco livre: {t.Free / 1024.0 / 1024 / 1024:F0} GB\n";
            }

            string summary = $"WinPE: {(_winpeReady ? "Pronto" : "Ausente (sera preparado automaticamente)")}\n" +
                             $"ISO: {Path.GetFileName(_isoPath)}\n" +
                             $"Particao alvo: {targetDrive}:\\\n" +
                             $"{freeInfo}" +
                             $"Edicao: {CboEdition.SelectedItem}\n\n" +
                             $"Preservar:\n" +
                             $"  - Perfis de usuario: {(ChkPreserveUsers.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Program Files: {(ChkPreserveProgramFiles.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Registry (Str. C): {(ChkPreserveRegistry.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Personalizacao: {(ChkPreservePersonalization.IsChecked == true ? "Sim" : "Nao")}\n" +
                             $"  - Drivers: {(ChkPreserveDrivers.IsChecked == true ? "Sim" : "Nao")}\n\n" +
                             $"O WinPE aplicara a imagem do Windows direto na particao {targetDrive}:\\\n" +
                             $"e restaurara seus dados em seguida.\n" +
                             $"Se faltar espaco, o WinPE deleta o Windows antigo para liberar.";

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
                "1. Resolver particao alvo + ESP (Storage API)\n" +
                "2. Exportar drivers e config para a particao alvo\n" +
                "3. Extrair install.wim para a particao alvo (se couber; senao, o WinPE extrai)\n" +
                "4. Agendar reboot unico no WinPE (bootsequence)\n\n" +
                "O WinPE executara:\n" +
                "  - Backup dos dados na propria particao (Z:\\!)\n" +
                "  - Deletar o Windows antigo para liberar espaco (se necessario)\n" +
                "  - Aplicar a imagem do Windows DIRETO no disco\n" +
                "  - Mesclar registry (se ativado)\n" +
                "  - Restaurar dados\n" +
                "  - Reboot no Windows novo");

            try
            {
                string targetDrive = (CboTargetDrive.SelectedItem as string)?[0].ToString() ?? "C";
                string edition = GetSelectedEditionIndex();
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
                        $"O WinPE aplicara a imagem direto na particao {targetDrive}:\\ com preservacao.\n" +
                        $"O resultado ficara em KitLugia_FreshInstall_Log.txt na raiz do alvo.");
                }
                else
                {
                    ShowBusyResult($"Falha ao agendar operacao.\n{msg}");
                }

                LoadWinpeLogs();
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
