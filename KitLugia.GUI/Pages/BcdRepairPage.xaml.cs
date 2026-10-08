using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace KitLugia.GUI.Pages
{
    /// <summary>
    /// Pagina de reparo de BOOT/BCD. Usa o Core BcdRepairManager (tecnica equal ao EaseUS
    /// BootRepair, ver docs/EASEUS_EPM_ANALYSIS.md): diagnostico honesto + reconstrucao com
    /// bcdboot (e bootsect no BIOS), sempre com backup antes e verificacao depois.
    /// </summary>
    public partial class BcdRepairPage : Page
    {
        private BcdRepairManager.BcdDiagnosis? _diag;
        private bool _busy;

        public BcdRepairPage()
        {
            InitializeComponent();
        }

        // ---------------------------------------------------------------- UI

        private void SetBusy(bool busy, string title = "", string sub = "")
        {
            _busy = busy;
            BtnAnalyze.IsEnabled = !busy;
            BtnRebuild.IsEnabled = !busy;
            BtnRestoreFiles.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(title)) TxtStatusTitle.Text = title;
            if (!string.IsNullOrEmpty(sub)) TxtStatusSub.Text = sub;
        }

        private void Report(string line) => TxtReport.AppendText(line + Environment.NewLine);

        private void ShowSuccess(string t, string m) => (Application.Current.MainWindow as MainWindow)?.ShowSuccess(t, m);
        private void ShowErrorMsg(string t, string m) => (Application.Current.MainWindow as MainWindow)?.ShowError(t, m);

        private Task<bool> ConfirmAsync(string title, string msg) =>
            Application.Current.MainWindow is MainWindow mw
                ? mw.ShowConfirmationDialog(msg)
                : Task.FromResult(MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

        // ------------------------------------------------------------ análise

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            await AnalyzeAsync();
        }

        /// <summary>Diagnóstico completo (reaproveitado pelos botoes de reparo).</summary>
        private async Task<BcdRepairManager.BcdDiagnosis?> AnalyzeAsync()
        {
            if (_busy) return _diag;

            TxtReport.Clear();
            SetBusy(true, "ANALISANDO...", "Verificando firmware, partição EFI, loja BCD e arquivos de boot...");

            try
            {
                void Progress(double pct, string status) =>
                    Dispatcher.Invoke(() => TxtStatusSub.Text = $"[{pct:0}%] {status}");

                _diag = await BcdRepairManager.DiagnoseAsync(null, Progress);

                TxtReport.Clear();
                Report($"Firmware        : {_diag.FirmwareMode}   ({_diag.FirmwareEvidence})");
                Report($"Partição EFI    : {(_diag.EspLetter != null ? _diag.EspLetter + ":  " + _diag.EspDescription + "  (" + (_diag.EspSize / 1024 / 1024) + " MB)" : "NÃO ENCONTRADA")}");
                Report($"Windows         : {(_diag.WindowsLetter != null ? _diag.WindowsLetter + ":\\Windows" : "NÃO ENCONTRADO")}");
                Report($"Loja BCD        : {(_diag.BcdStoreExists ? "presente" : "AUSENTE")}");
                Report($"bootmgfw.efi    : {(_diag.BootMgfwPresent ? "presente" : "AUSENTE")}");
                Report($"Arquivos de boot: {(_diag.BootFilesPresent ? "presentes" : "AUSENTES")}");
                Report($"bcdedit /enum   : {(_diag.BcdeditReadable ? $"OK — {_diag.RealWindowsEntries} entrada(s) do Windows, {_diag.OsLoaderCount} no total" : "FALHOU — " + _diag.BcdeditError)}");
                Report("");

                if (_diag.Problems.Count > 0)
                {
                    Report("PROBLEMAS:");
                    foreach (var p in _diag.Problems) Report("  ✖ " + p);
                }
                else
                {
                    Report("✔ Nenhum problema crítico encontrado.");
                }

                if (_diag.Warnings.Count > 0)
                {
                    Report("");
                    Report("AVISOS:");
                    foreach (var w in _diag.Warnings) Report("  ⚠ " + w);
                }

                bool canRepair = _diag.CanRepair;
                TxtStatusTitle.Text = _diag.Healthy ? "BOOT SAUDÁVEL" : "PROBLEMAS ENCONTRADOS";
                TxtStatusSub.Text = _diag.Summary;

                if (!canRepair)
                    TxtStatusSub.Text += "  (Para reparar, é necessário ter a partição EFI e o Windows detectados acima.)";

                // Mapa visual: disco do Windows com Windows + ESP em destaque.
                try
                {
                    PanelBcdMap.Children.Clear();
                    uint? winDisk = FindDiskByLetter(_diag.WindowsLetter);
                    if (winDisk.HasValue)
                    {
                        var hl = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrEmpty(_diag.WindowsLetter)) hl.Add(_diag.WindowsLetter);
                        if (!string.IsNullOrEmpty(_diag.EspLetter)) hl.Add(_diag.EspLetter);
                        PanelBcdMap.Children.Add(Controls.DiskMapPanel.Build(winDisk.Value, hl));
                    }
                    else
                    {
                        PanelBcdMap.Children.Add(new TextBlock
                        {
                            Text = "Disco do Windows não localizado para o mapa.",
                            Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11
                        });
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA NA ANÁLISE";
                TxtStatusSub.Text = ex.Message;
                Report("ERRO: " + ex);
            }
            finally
            {
                SetBusy(false);
            }

            return _diag;
        }

        /// <summary>Acha o número do disco que contém a letra (para o mapa visual).</summary>
        private static uint? FindDiskByLetter(string? letter)
        {
            if (string.IsNullOrWhiteSpace(letter)) return null;
            try
            {
                foreach (var d in DiskConverterManager.GetDisks())
                {
                    foreach (var p in DiskConverterManager.GetPartitions(d.Number))
                    {
                        if (string.Equals(p.DriveLetter, letter.Trim().TrimEnd(':'), StringComparison.OrdinalIgnoreCase))
                            return d.Number;
                    }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------ reparos

        private async void BtnRebuild_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_diag == null)
            {
                await AnalyzeAsync();   // analyze primeiro (preenche _diag)
            }

            if (_diag == null || !_diag.CanRepair)
            {
                ShowErrorMsg("Não é possível reconstruir",
                    "A análise não encontrou os dois ingredientes necessários: uma instalação do Windows e a partição EFI.\n\n" +
                    "Se a ESP existe mas está sem letra, o reparo a monta sozinho — rode ANALISAR de novo. " +
                    "Se nem a análise a encontra, confira no Gerenciar Discos se a partição EFI (FAT32, ~100 MB) existe.");
                return;
            }

            bool ok = await ConfirmAsync("RECONSTRUIR BCD",
                $"A loja BCD será recriada do zero a partir de {_diag.WindowsLetter}:\\Windows, usando bcdboot, no modo {_diag.FirmwareMode}.\n\n" +
                "⚠️ As entradas atuais da BCD serão APAGADAS — inclusive as do Kit Lugia.\n" +
                "Um backup completo é feito antes, em %LOCALAPPDATA%\\KitLugia\\BCD-Backup.\n\n" +
                (_diag.IsUefi ? "" : "Como o sistema está em BIOS/legacy, o bootsect também vai reescrever o código de boot e a MBR.\n\n") +
                "Deseja continuar?");

            if (!ok) return;

            // Re-verificação entre a confirmação e a execução: o diagnóstico pode estar
            // obsoleto (usuário mexeu nos discos depois de analisar). Sem Windows ou sem
            // ESP acessível agora, aborta em vez de rodar bcdboot no vazio.
            if (!System.IO.Directory.Exists($"{_diag.WindowsLetter}:\\Windows\\System32") ||
                (string.IsNullOrEmpty(_diag.EspLetter) || !System.IO.Directory.Exists($"{_diag.EspLetter}:\\")))
            {
                ShowErrorMsg("Estado mudou",
                    "O Windows ou a ESP não estão mais acessíveis nas mesmas letras da análise. " +
                    "Clique em ANALISAR de novo antes de reconstruir.");
                await AnalyzeAsync();
                return;
            }

            TxtReport.Clear();
            SetBusy(true, "RECONSTRUINDO BCD...", "Executando bcdboot. Não desligue o PC.");

            try
            {
                void Progress(double pct, string status) =>
                    Dispatcher.Invoke(() => TxtStatusSub.Text = $"[{pct:0}%] {status}");

                var (success, message) = await BcdRepairManager.RebuildAsync(
                    _diag.WindowsLetter!, _diag.EspLetter!, _diag.IsUefi,
                    createBackup: true, bcdLanguage: null, progress: Progress);

                Report(message);
                TxtStatusTitle.Text = success ? "BCD RECONSTRUÍDA" : "REPARO FALHOU";
                TxtStatusSub.Text = success ? "Reinicie o PC para validar o resultado." : message.Split('\n')[0];

                if (success) ShowSuccess("Reparo concluído", message);
                else ShowErrorMsg("Falha no reparo", message);

                await AnalyzeAsync();   // re-diagnostica para mostrar o estado final
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA NO REPARO";
                TxtStatusSub.Text = ex.Message;
                Report("ERRO: " + ex);
                ShowErrorMsg("Falha no reparo", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void BtnRestoreFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;

            if (_diag == null) await AnalyzeAsync();
            if (_diag == null || !_diag.CanRepair)
            {
                ShowErrorMsg("Não é possível restaurar", "A análise não encontrou o Windows e a partição EFI.");
                return;
            }

            bool ok = await ConfirmAsync("RESTAURAR ARQUIVOS DE BOOT",
                $"Copiar os arquivos de boot de {_diag.WindowsLetter}:\\Windows\\Boot\\EFI para a ESP ({_diag.EspLetter}:).\n\n" +
                "A loja BCD NÃO é tocada (só os arquivos .efi). " +
                "Arquivos existentes na ESP com o mesmo nome serão substituídos.\n\nDeseja continuar?");
            if (!ok) return;

            TxtReport.Clear();
            SetBusy(true, "RESTAURANDO ARQUIVOS...", "Copiando arquivos de boot do Windows para a partição EFI.");

            try
            {
                var (success, message) = await BcdRepairManager.RestoreBootFilesAsync(
                    _diag.WindowsLetter!, _diag.EspLetter!,
                    (pct, status) => Dispatcher.Invoke(() => TxtStatusSub.Text = $"[{pct:0}%] {status}"));

                Report(message);
                TxtStatusTitle.Text = success ? "ARQUIVOS RESTAURADOS" : "FALHA AO RESTAURAR";
                TxtStatusSub.Text = message.Split('\n')[0];

                if (success) ShowSuccess("Arquivos restaurados", message);
                else ShowErrorMsg("Falha", message);
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA";
                TxtStatusSub.Text = ex.Message;
                Report("ERRO: " + ex);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}