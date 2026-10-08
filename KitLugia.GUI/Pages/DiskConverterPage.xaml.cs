using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// Conversor MBR <-> GPT (as duas direcoes), inicializacao de disco RAW, backup/restauro do
    /// setor 0 (MBR) e diagnostico de alinhamento. Core: DiskConverterManager
    /// (tecnica equal ao DiskConverter.dll do EaseUS - ver docs/EASEUS_EPM_ANALYSIS.md).
    ///
    /// Regra da pagina: nada acontece sem ANALISAR antes, e todo resultado volta verificado.
    /// </summary>
    public partial class DiskConverterPage : Page
    {
        private readonly List<DiskRow> _disks = new List<DiskRow>();
        private ConvertPlan? _planGpt;
        private ConvertPlan? _planMbr;
        private bool _busy;
        private bool _loaded;

        public DiskConverterPage()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (_loaded) return;
                _loaded = true;
                await LoadDisksAsync();
            };
        }

        // ---------------------------------------------------------------- UI

        private DiskRow? SelectedDisk =>
            CmbDisk.SelectedIndex >= 0 && CmbDisk.SelectedIndex < _disks.Count ? _disks[CmbDisk.SelectedIndex] : null;

        private void SetBusy(bool busy, string title = "", string sub = "")
        {
            _busy = busy;
            BtnAnalyze.IsEnabled = !busy;
            BtnReload.IsEnabled = !busy;
            BtnToGpt.IsEnabled = !busy;
            BtnToMbr.IsEnabled = !busy;
            BtnWinpeGpt.IsEnabled = !busy;
            BtnWinpeMbr.IsEnabled = !busy;
            BtnInitGpt.IsEnabled = !busy;
            BtnInitMbr.IsEnabled = !busy;
            BtnBackupMbr.IsEnabled = !busy;
            BtnRestoreMbr.IsEnabled = !busy;
            BtnReadMbr.IsEnabled = !busy;
            BtnAlign.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(title)) TxtStatusTitle.Text = title;
            if (!string.IsNullOrEmpty(sub)) TxtStatusSub.Text = sub;
        }

        private void Report(string line) => TxtReport.AppendText(line + Environment.NewLine);
        private void ReportLines(string text) => TxtReport.AppendText(text + Environment.NewLine + Environment.NewLine);

        private void ShowSuccess(string t, string m) => (Application.Current.MainWindow as MainWindow)?.ShowSuccess(t, m);
        private void ShowErrorMsg(string t, string m) => (Application.Current.MainWindow as MainWindow)?.ShowError(t, m);

        private Task<bool> ConfirmAsync(string title, string msg) =>
            Application.Current.MainWindow is MainWindow mw
                ? mw.ShowConfirmationDialog(msg)
                : Task.FromResult(MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

        private void Progress(double pct, string status) =>
            Dispatcher.Invoke(() => TxtStatusSub.Text = $"[{pct:0}%] {status}");

        // ------------------------------------------------------------- discos

        private async Task LoadDisksAsync()
        {
            SetBusy(true, "CARREGANDO DISCOS...", "Lendo MSFT_Disk (Storage Management API)...");
            try
            {
                _disks.Clear();
                CmbDisk.Items.Clear();

                var disks = await Task.Run(DiskConverterManager.GetDisks);
                foreach (var d in disks)
                {
                    _disks.Add(d);
                    CmbDisk.Items.Add(d.Label);
                }

                if (_disks.Count == 0)
                {
                    TxtDiskInfo.Text = "Nenhum disco retornado pela Storage API.";
                    TxtStatusTitle.Text = "SEM DISCOS";
                    TxtStatusSub.Text = "A Storage Management API não retornou discos (provável falta de privilégio de administrador).";
                    return;
                }

                CmbDisk.SelectedIndex = 0;
                UpdateDiskInfo();
                TxtStatusSub.Text = $"{_disks.Count} disco(s) encontrados. Clique em ANALISAR para ver as verificações de conversão.";
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA AO CARREGAR";
                TxtStatusSub.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void UpdateDiskInfo()
        {
            var disk = SelectedDisk;
            if (disk == null) return;

            var parts = DiskConverterManager.GetPartitions(disk.Number);
            var flags = new List<string>();
            if (disk.IsSystem) flags.Add("sistema");
            if (disk.IsBoot) flags.Add("boot");
            if (disk.IsReadOnly) flags.Add("somente leitura");
            if (disk.IsOffline) flags.Add("offline");

            string flagText = flags.Count > 0 ? string.Join(", ", flags) : "sem flags de sistema/boot";
            string mbr = DiskConverterManager.ReadMbr(disk.Number)?.Summary ?? "setor 0 ilegível";

            TxtDiskInfo.Text =
                $"Estilo {disk.StyleText} - {parts.Count} partição(ões) - {flagText}\n" +
                $"Setor 0: {mbr}";

            _planGpt = null;
            _planMbr = null;

            // Mapa visual do disco selecionado (barra proporcional + legenda).
            try
            {
                PanelDiskMap.Children.Clear();
                PanelDiskMap.Children.Add(Controls.DiskMapPanel.Build(disk.Number));
            }
            catch { }
        }

        private void CmbDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_busy) return;
            UpdateDiskInfo();
            TxtReport.Clear();
            TxtStatusTitle.Text = "DISCO SELECIONADO";
            TxtStatusSub.Text = "Clique em ANALISAR para checar a conversão.";
        }

        private async void BtnReload_Click(object sender, RoutedEventArgs e) => await LoadDisksAsync();

        // ------------------------------------------------------------ análise

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

        /// <summary>Roda a análise para GPT e para MBR e mostra as duas na tela.</summary>
        private async Task AnalyzeAsync()
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            TxtReport.Clear();
            SetBusy(true, "ANALISANDO...", $"Verificando o disco {disk.Number}...");

            try
            {
                _planGpt = await Task.Run(() => DiskConverterManager.Analyze(disk.Number, DiskStyle.Gpt));
                _planMbr = await Task.Run(() => DiskConverterManager.Analyze(disk.Number, DiskStyle.Mbr));

                Report($"DISCO {disk.Number}: {disk.Model}");
                Report($"Tamanho : {disk.SizeText}");
                Report($"Estilo  : {disk.StyleText}");
                Report($"Partições: {disk.PartitionCount}");
                Report($"Assinatura: {disk.IdText}");
                Report($"Firmware: {(DiskConverterManager.IsWinPeSession() ? "sessão WinPE" : "Windows/WinPE normal")}");
                Report("");

                foreach (var plan in new[] { _planGpt, _planMbr })
                {
                    if (plan == null) continue;
                    Report($"── {plan.CurrentName}  ->  {plan.TargetName} " + new string('─', Math.Max(2, 44 - plan.TargetName.Length)));
                    if (plan.Blockers.Count == 0)
                        Report("  ✔ Pode converter.");
                    foreach (var b in plan.Blockers) Report("  ✖ " + b);
                    foreach (var w in plan.Warnings) Report("  ⚠ " + w);
                    foreach (var n in plan.Notes) Report("  • " + n);
                    Report("");
                }

                bool anyOk = (_planGpt?.CanConvert ?? false) || (_planMbr?.CanConvert ?? false);
                TxtStatusTitle.Text = anyOk ? "CONVERSÃO POSSÍVEL" : "CONVERSÃO BLOQUEADA";
                TxtStatusSub.Text = anyOk
                    ? "Um dos sentidos está liberado. Confira os avisos antes de converter."
                    : "Leia os bloqueios acima: cada um precisa ser resolvido antes de converter.";
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
        }

        // ---------------------------------------------------------- conversão

        private async void BtnToGpt_Click(object sender, RoutedEventArgs e) => await ConvertAsync(DiskStyle.Gpt);
        private async void BtnToMbr_Click(object sender, RoutedEventArgs e) => await ConvertAsync(DiskStyle.Mbr);

        // -------------------------------------------------- conversão via WinPE

        private async void BtnWinpeGpt_Click(object sender, RoutedEventArgs e) => await ConvertWinPeAsync("gpt");
        private async void BtnWinpeMbr_Click(object sender, RoutedEventArgs e) => await ConvertWinPeAsync("mbr");

        /// <summary>
        /// Agenda a conversão no pré-boot (WinPE, estilo EaseUS PreOS): para discos que o
        /// Windows em execução não libera (sistema/em uso). Com dupla confirmação (a segunda
        /// avisa do reboot) + elegibilidade verificada no Core (BitLocker/dinâmico/&gt;2 TiB
        /// continuam bloqueados) + preflight antes do reboot + log persistente no WinPE.
        /// </summary>
        private async Task ConvertWinPeAsync(string target)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            var plan = DiskConverterManager.Analyze(disk.Number,
                target == "mbr" ? DiskStyle.Mbr : DiskStyle.Gpt);
            var (eligible, whyNot) = DiskConverterManager.IsWinPeConvertEligible(plan);
            if (!eligible)
            {
                ShowErrorMsg("WinPE recusado",
                    $"A conversão via WinPE não resolve este caso: {whyNot}\n\n" +
                    "O WinPE só libera o que é 'Windows em uso' — o resto continua bloqueado.");
                return;
            }

            bool ok = await ConfirmAsync("AGENDAR NO WinPE?",
                $"Disco {disk.Number} ({disk.Model}, {disk.SizeText}): {plan.CurrentName} -> {(target == "mbr" ? "MBR" : "GPT")} " +
                $"com o Windows DESLIGADO (pré-boot, estilo EaseUS PreOS).\n\n" +
                $"Partições: {plan.PartitionCount} (convertidas no lugar, dados preservados).\n" +
                (plan.Target == DiskStyle.Gpt
                    ? "\n⚠️ Após converter para GPT em disco de sistema: crie a ESP + rode Reparar Boot/BCD.\n" : "\n") +
                "\nO PC vai REINICIAR em 10 segundos. Feche seus programas antes de confirmar.\n\nAgendar?");
            if (!ok) return;

            bool ok2 = await ConfirmAsync("CONFIRMAR REBOOT",
                "Última chance: o PC reinicia em 10 segundos para converter o disco no WinPE.\n\n" +
                "O log da operação ficará em C:\\KitLugia_Convert_Log.txt.\n\nConfirmar?");
            if (!ok2) return;

            TxtReport.Clear();
            SetBusy(true, "AGENDANDO WinPE...", "Preparando WIM, marcador, BCD e preflight...");

            try
            {
                // Pesado (WIM + BCD + reboot): fora da UI thread (regra CheckUiThreading).
                var (success, message) = await Task.Run(() => WinbootManager.ScheduleWinpeConvertAsync(disk.Number, target));
                ReportLines(message);
                TxtStatusTitle.Text = success ? "CONVERSÃO AGENDADA" : "FALHA AO AGENDAR";
                TxtStatusSub.Text = message.Split('\n')[0];
                if (success) ShowSuccess("Agendado no WinPE", message);
                else ShowErrorMsg("Falha", message);
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA";
                TxtStatusSub.Text = ex.Message;
                Report("ERRO: " + ex);
                ShowErrorMsg("Falha", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task ConvertAsync(DiskStyle target)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            // Semantic analysis (rapida) so pra montar a confirmacao; a decisao final e do Core.
            var plan = DiskConverterManager.Analyze(disk.Number, target);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Disco {disk.Number} ({disk.Model}, {disk.SizeText}): {plan.CurrentName} -> {plan.TargetName}");
            sb.AppendLine($"Partições: {plan.PartitionCount} (os dados são preservados - a conversão é feita no lugar).");
            if (plan.Blockers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ATENÇÃO - bloqueios detectados:");
                foreach (var b in plan.Blockers) sb.AppendLine("  ✖ " + b);
            }
            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine();
                foreach (var w in plan.Warnings) sb.AppendLine("  ⚠ " + w);
            }
            sb.AppendLine();
            if (plan.Blockers.Count > 0)
            {
                sb.AppendLine("⛔ HÁ BLOQUEIOS — a conversão NÃO é segura nestas condições.");
                sb.AppendLine("Forçar pode deixar o PC sem boot ou perder partições.");
                sb.AppendLine();
                sb.AppendLine("Deseja revisar os bloqueios (NÃO = voltar)?");
            }
            else
            {
                sb.AppendLine("Deseja converter agora?");
            }

            bool ok = await ConfirmAsync($"CONVERTER PARA {plan.TargetName}", sb.ToString());
            if (!ok) return;

            // Com bloqueios, a primeira confirmação só autoriza VER os bloqueios: exige uma
            // SEGUNDA confirmação explícita para FORÇAR (antes era 1 clique com force:true
            // sempre — um SIM apressado ignorava disco de sistema, BitLocker e >2 TiB).
            bool force = false;
            if (plan.Blockers.Count > 0)
            {
                bool forceOk = await ConfirmAsync("FORÇAR CONVERSÃO?",
                    $"Você está prestes a IGNORAR {plan.Blockers.Count} bloqueio(s):\n" +
                    string.Join("\n", plan.Blockers.Select(b => "  ✖ " + b)) +
                    "\n\n⚠️ RISCOS REAIS: PC sem boot, partições perdidas, dados inacessíveis.\n" +
                    "Só force se souber exatamente o que está fazendo (ex.: disco de dados com backup).\n\n" +
                    "Forçar mesmo assim?");
                if (!forceOk) return;
                force = true;
            }

            TxtReport.Clear();
            SetBusy(true, $"CONVERTENDO PARA {plan.TargetName}...", "Aplicando a conversão...");

            try
            {
                var (success, message) = await DiskConverterManager.ConvertAsync(
                    disk.Number, target, force: force, progress: Progress, logLine: Report);

                ReportLines(message);
                TxtStatusTitle.Text = success ? "CONVERSÃO CONCLUÍDA" : "CONVERSÃO FALHOU";
                TxtStatusSub.Text = message.Split('\n')[0];

                if (success) ShowSuccess("Conversão concluída", message);
                else ShowErrorMsg("Falha na conversão", message);

                await LoadDisksAsync();
            }
            catch (Exception ex)
            {
                TxtStatusTitle.Text = "FALHA NA CONVERSÃO";
                TxtStatusSub.Text = ex.Message;
                Report("ERRO: " + ex);
                ShowErrorMsg("Falha na conversão", ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ------------------------------------------------------- inicialização

        private async void BtnInitGpt_Click(object sender, RoutedEventArgs e) => await InitializeAsync(DiskStyle.Gpt);
        private async void BtnInitMbr_Click(object sender, RoutedEventArgs e) => await InitializeAsync(DiskStyle.Mbr);

        private async Task InitializeAsync(DiskStyle target)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            if (disk.Style != DiskStyle.Raw)
            {
                await ConfirmAsync("INICIALIZAR",
                    $"O disco {disk.Number} já tem tabela ({disk.StyleText}). Inicializar só serve para discos RAW.\n\n" +
                    "Deseja que eu rode a CONVERSÃO para " + (target == DiskStyle.Gpt ? "GPT" : "MBR") + " em vez disso?");
                await ConvertAsync(target);
                return;
            }

            bool ok = await ConfirmAsync("INICIALIZAR DISCO",
                $"Criar uma tabela {(target == DiskStyle.Gpt ? "GPT" : "MBR")} no disco {disk.Number} ({disk.Model}, {disk.SizeText}).\n\n" +
                "O disco precisa estar sem partições (RAW).\n\nDeseja continuar?");
            if (!ok) return;

            // Re-verificação entre a confirmação e a execução: se o disco ganhou partições
            // (outro programa, outra aba) no meio do caminho, aborta em vez de apagar.
            var fresh = DiskConverterManager.GetPartitions(disk.Number);
            if (fresh.Count > 0)
            {
                ShowErrorMsg("Disco mudou",
                    $"O disco {disk.Number} agora tem {fresh.Count} partição(ões) — não vou inicializar. " +
                    "Recarregue a lista e confira antes de continuar.");
                await LoadDisksAsync();
                return;
            }

            TxtReport.Clear();
            SetBusy(true, "INICIALIZANDO...", $"Criando tabela {(target == DiskStyle.Gpt ? "GPT" : "MBR")}...");

            try
            {
                var (success, message) = await DiskConverterManager.InitializeAsync(disk.Number, target, false, Progress);
                ReportLines(message);
                TxtStatusTitle.Text = success ? "DISCO INICIALIZADO" : "FALHA AO INICIALIZAR";
                TxtStatusSub.Text = message.Split('\n')[0];
                if (success) ShowSuccess("Disco inicializado", message);
                else ShowErrorMsg("Falha", message);

                await LoadDisksAsync();
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

        // -------------------------------------------------------------- MBR

        private async void BtnReadMbr_Click(object sender, RoutedEventArgs e)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            TxtReport.Clear();
            SetBusy(true, "LENDO SETOR 0...", "Abrindo o disco físico...");
            try
            {
                var info = await Task.Run(() => DiskConverterManager.ReadMbr(disk.Number));
                if (info == null)
                {
                    TxtStatusTitle.Text = "SETOR 0 ILEGÍVEL";
                    TxtStatusSub.Text = "Não consegui ler o setor 0 (execute como administrador).";
                    return;
                }

                Report($"Disco {disk.Number} - {disk.Model}");
                Report($"  assinatura 0x55AA : {(info.SignatureOk ? "presente" : "AUSENTE")}");
                Report($"  código de boot    : {(info.HasBootstrap ? info.BootstrapId : "AUSENTE (446 bytes zerados)")}");
                Report($"  assinatura disco  : 0x{info.DiskSignature:X8}");
                Report($"  entradas na tabela: {info.PartitionEntries}");
                Report($"  tabela coerente   : {(info.TableSane ? "sim" : "NÃO")}");
                Report("");
                foreach (var n in info.Notes) Report("  ⚠ " + n);

                TxtStatusTitle.Text = info.SignatureOk ? "SETOR 0 OK" : "SETOR 0 COM PROBLEMA";
                TxtStatusSub.Text = info.Summary;
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

        private async void BtnBackupMbr_Click(object sender, RoutedEventArgs e)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            TxtReport.Clear();
            SetBusy(true, "SALVANDO SETOR 0...", "Cópia de segurança dos 512 bytes...");
            try
            {
                var (success, message) = await DiskConverterManager.BackupMbrAsync(disk.Number, null, Progress);
                ReportLines(message);
                TxtStatusTitle.Text = success ? "BACKUP SALVO" : "FALHA NO BACKUP";
                TxtStatusSub.Text = message.Split('\n')[0];
                if (success) ShowSuccess("Backup do setor 0 salvo", message);
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

        private async void BtnRestoreMbr_Click(object sender, RoutedEventArgs e)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha o arquivo de MBR (.bin)",
                Filter = "MBR (*.bin;*.mbr)|*.bin;*.mbr|Todos (*.*)|*.*",
                InitialDirectory = Directory.Exists(DiskConverterManager.BackupRoot) ? DiskConverterManager.BackupRoot : null
            };
            if (dialog.ShowDialog() != true) return;

            bool ok = await ConfirmAsync("RESTAURAR SETOR 0",
                $"Reescrever o setor 0 do disco {disk.Number} usando {Path.GetFileName(dialog.FileName)}?\n\n" +
                "• Modo SEGURO (padrão): copia só os 446 bytes de código de boot e mantém a tabela de partições\n" +
                "  que está no disco agora — é o que serve para recuperar a inicialização.\n" +
                "• Modo COMPLETO: grava os 512 bytes do arquivo, incluindo a tabela de partições dele.\n\n" +
                "Um backup do setor 0 atual é salvo automaticamente antes da escrita, e o resultado\n" +
                "é lido de volta para conferência.\n\nDeseja continuar?");
            if (!ok) return;

            TxtReport.Clear();
            SetBusy(true, "RESTAURANDO SETOR 0...", "Gravando e verificando...");

            try
            {
                bool keepTable = await ConfirmAsync("MODO DE ESCRITA",
                    "Usar o MODO SEGURO (mantém a tabela de partições atual e troca só o código de boot)?\n\n" +
                    "SIM = modo seguro (recomendado para recuperar o boot).\n" +
                    "NÃO = grava o setor 0 inteiro do arquivo (inclui a tabela dele).");

                var (success, message) = await DiskConverterManager.RestoreMbrAsync(
                    disk.Number, dialog.FileName, keepTable, Progress, Report);

                ReportLines(message);
                TxtStatusTitle.Text = success ? "SETOR 0 RESTAURADO" : "FALHA AO RESTAURAR";
                TxtStatusSub.Text = message.Split('\n')[0];
                if (success) ShowSuccess("Setor 0 restaurado", message);
                else ShowErrorMsg("Falha", message);

                UpdateDiskInfo();
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

        // -------------------------------------------------------- alinhamento

        private async void BtnAlign_Click(object sender, RoutedEventArgs e)
        {
            var disk = SelectedDisk;
            if (disk == null || _busy) return;

            TxtReport.Clear();
            SetBusy(true, "ANALISANDO ALINHAMENTO...", "Lendo os inícios das partições...");
            try
            {
                var report = await Task.Run(() => DiskConverterManager.AnalyzeAlignment(disk.Number));

                if (report.Partitions.Count == 0)
                {
                    TxtStatusTitle.Text = "SEM PARTIÇÕES";
                    TxtStatusSub.Text = $"O disco {disk.Number} não tem partições para checar.";
                    return;
                }

                Report($"Alinhamento do disco {disk.Number} - {disk.Model}");
                Report($"{(report.Misaligned1M == 0 ? "OK" : "ATENÇÃO")}: {report.Misaligned1M} de {report.Partitions.Count} partições fora de 1 MiB; {report.Misaligned4K} fora de 4 KiB");
                Report("");
                Report("Partição      Início (MiB)   1 MiB   4 KiB");

                foreach (var p in report.Partitions)
                {
                    double mib = p.Offset / (1024.0 * 1024);
                    Report($"  {p.Letter,-10} {mib,10:F2}      {(p.Aligned1M ? "sim" : "NÃO"),-6} {(p.Aligned4K ? "sim" : "NÃO")}");
                }

                Report("");
                if (!report.NeedsFix)
                {
                    Report("✔ Tudo alinhado em 1 MiB - ideal para NVMe/SSD e para o instalador do Windows.");
                }
                else
                {
                    Report("⚠ Alinhamento irregular. Reposicionar uma partição existente exige MOVER os dados,");
                    Report("  por isso o diagnóstico é informativo: o ajuste é feito na criação da partição.");
                }

                TxtStatusTitle.Text = report.NeedsFix ? "ALINHAMENTO IRREGULAR" : "ALINHAMENTO OK";
                TxtStatusSub.Text = report.Summary;
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
