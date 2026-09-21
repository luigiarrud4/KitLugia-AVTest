using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.Core.TaskManager;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: aba ARMAZENAMENTO — "meu disco está em 100%, o que exatamente
    //  está fazendo isso?"
    //
    //  Fluxo inteiro fica visível na tela:
    //    1. FATO      → StorageDiagnostics.Sample() mede atividade/fila/latência/R/W.
    //    2. HIPÓTESE  → ranqueia quem está fazendo E/S (o snapshot do refresh já
    //                   traz leitura/escrita por PID — nada é relido aqui).
    //    3. PROVA     → "Testar impacto" suspende o suspeito, mede de novo e
    //                   compara. Sem prova, o Kit NÃO afirma causa.
    //    4. AÇÃO      → suspender / retomar / reiniciar serviço / parar serviço /
    //                   Force Stop (último recurso).
    //
    //  O motor vive no Core (StorageDiagnostics) — esta partial só apresenta,
    //  decide o que habilitar e chama as intervenções.
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        private bool _stBuilt;
        private DispatcherTimer? _stTimer;
        private bool _stMonitoring;
        private bool _stSampling;
        private bool _stBusy;                 // teste/intervenção em andamento
        private bool _stSweeping;             // varredura automática de suspeitos
        private bool _stIsAdmin;
        private int _stTick;
        private StorageDiagnostics.StorageSnapshot? _stSnap;
        private StorageDiagnostics.ImpactTest? _stLastImpact;
        private string _stSelInstance = "";
        // Enquanto o usuário não clicar num chip de disco, a faixa SEGUE o disco mais
        // ocupado — "meu disco está em 100%" é sobre o disco que está sofrendo, e travar
        // no primeiro render (que pega um instante qualquer) mostraria o disco errado.
        private bool _stUserPickedDisk;
        private StProcRow? _stSelected;
        private CancellationTokenSource? _stTestCts;

        private readonly ObservableCollection<StProcRow> _stRows = new();
        private readonly Dictionary<string, Button> _stChips = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<float> _stActHist = new(61);
        private readonly Queue<float> _stQueueHist = new(61);
        private readonly Queue<float> _stLatHist = new(61);

        private static readonly SolidColorBrush StOk = StBrush(0x4C, 0xAF, 0x50);
        private static readonly SolidColorBrush StWarn = StBrush(0xFF, 0xB7, 0x4D);
        private static readonly SolidColorBrush StBad = StBrush(0xF4, 0x43, 0x36);
        private static readonly SolidColorBrush StIdle = StBrush(0x88, 0x88, 0x88);
        private static readonly SolidColorBrush StAccent = StBrush(0x4F, 0xC3, 0xF7);

        private static SolidColorBrush StBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // ══════════════ LINHA DA TABELA (quem está fazendo E/S) ══════════════
        public sealed class StProcRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            public int Pid { get; set; }
            public string Path { get; set; } = "";
            public double ReadBps { get; set; }
            public double WriteBps { get; set; }
            public double IopsValue { get; set; }
            public ulong TotalBytes { get; set; }
            public string ServiceName { get; set; } = "";
            public string ServiceDisplay { get; set; } = "";
            /// <summary>Estado do serviço antes de qualquer ação do Kit ("Running", "Stopped"…).</summary>
            public string ServiceState { get; set; } = "";
            public bool IsCritical { get; set; }
            public string CriticalReason { get; set; } = "";
            public string Risk { get; set; } = "";

            private string _name = "";
            public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
            private System.Windows.Media.Imaging.BitmapSource? _icon;
            public System.Windows.Media.Imaging.BitmapSource? Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); } }
            private bool _suspended;
            public bool Suspended
            {
                get => _suspended;
                set { _suspended = value; Raise(nameof(Suspended)); Raise(nameof(SuspensionVisibility)); }
            }

            public double TotalBps => ReadBps + WriteBps;
            public string Read => RunKitTaskManagerFormat(ReadBps);
            public string Write => RunKitTaskManagerFormat(WriteBps);
            public string Total => RunKitTaskManagerFormat(TotalBps);
            public string Iops => IopsValue >= 1000 ? $"{IopsValue / 1000:F1}k" : $"{IopsValue:F0}";
            public string ServiceText => ServiceDisplay.Length > 0 ? ServiceDisplay : (ServiceName.Length > 0 ? ServiceName : "—");
            public string ServiceBadge => ServiceName.Length > 0 ? $"SERVICO: {ServiceName}" : "";
            /// <summary>
            /// Impressão digital do PID no momento em que esta linha foi montada. Serve para
            /// RECUSAR a ação se o Windows reciclar o número antes do clique — sem isso o Kit
            /// suspenderia um processo que o usuário nunca escolheu.
            /// </summary>
            public StorageDiagnostics.ProcessIdentity Identity { get; set; }
            public Visibility ServiceVisibility => ServiceName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            public Visibility SuspensionVisibility => Suspended ? Visibility.Visible : Visibility.Collapsed;

            public SolidColorBrush TotalColor =>
                TotalBps >= 50 * 1024 * 1024 ? StBad :
                TotalBps >= 5 * 1024 * 1024 ? StWarn :
                TotalBps >= 512 * 1024 ? StAccent : StIdle;

            public string Tooltip
            {
                get
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"{Name} (PID {Pid})");
                    sb.AppendLine($"Leitura: {Read}    Escrita: {Write}");
                    sb.AppendLine($"Total: {Total}    Operações: {Iops}/s");
                    if (Path.Length > 0) sb.AppendLine($"Caminho: {Path}");
                    if (ServiceName.Length > 0) sb.AppendLine($"Serviço: {ServiceDisplay} ({ServiceName})");
                    if (IsCritical) sb.AppendLine($"Protegido: {CriticalReason}");
                    else if (Risk.Length > 0) sb.AppendLine($"Atenção: {Risk}");
                    if (Suspended) sb.AppendLine("Estado: SUSPENSO pelo Kit (reversível)");
                    return sb.ToString().TrimEnd();
                }
            }

            internal void CopyMetricsFrom(StProcRow src)
            {
                ReadBps = src.ReadBps; WriteBps = src.WriteBps; IopsValue = src.IopsValue;
                TotalBytes = src.TotalBytes; ServiceName = src.ServiceName; ServiceDisplay = src.ServiceDisplay;
                IsCritical = src.IsCritical; CriticalReason = src.CriticalReason; Risk = src.Risk;
                Path = src.Path; Suspended = src.Suspended;
                if (src.Icon != null) Icon = src.Icon;
                Raise(nameof(ReadBps)); Raise(nameof(WriteBps)); Raise(nameof(Iops));
                Raise(nameof(Read)); Raise(nameof(Write)); Raise(nameof(Total));
                Raise(nameof(ServiceText)); Raise(nameof(ServiceBadge)); Raise(nameof(ServiceVisibility));
                Raise(nameof(TotalColor)); Raise(nameof(Tooltip));
            }
        }

        /// <summary>Formatador local (o da janela é estático privado em outra partial).</summary>
        private static string RunKitTaskManagerFormat(double bps)
        {
            if (bps <= 0.5) return "—";
            if (bps < 1024) return $"{bps:F0} B/s";
            if (bps < 1024 * 1024) return $"{bps / 1024:F0} KB/s";
            if (bps < 1024L * 1024 * 1024) return $"{bps / 1024 / 1024:F1} MB/s";
            return $"{bps / 1024 / 1024 / 1024:F2} GB/s";
        }

        // ══════════════ INICIALIZAÇÃO (chamada pelo SwitchTab) ══════════════

        private void EnsureStorageBuilt()
        {
            if (_stBuilt) return;
            _stBuilt = true;

            // O cruzamento com o áudio precisa dos ganchos do monitor (o "quem" está tocando
            // e quem faz E/S), mesmo que o usuário nunca abra a aba Latência. E já LIGA a
            // escuta: senão este card nunca mostraria correlação nenhuma.
            AutoStartAudioListen();

            DgStProcs.ItemsSource = _stRows;

            try
            {
                _stIsAdmin = SystemUtils.IsRunningAsAdministrator();
                BtnStElevate.Visibility = _stIsAdmin ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { BtnStElevate.Visibility = Visibility.Collapsed; }

            // Ao fechar a janela, NUNCA deixar processo suspenso para trás.
            this.Closing += (_, __) =>
            {
                try { StopStorageMonitoring(); } catch { }
                try
                {
                    int n = StorageDiagnostics.ResumeAll();
                    if (n > 0) Logger.Log($"[KIT TASK MANAGER] {n} processo(s) suspenso(s) pelo diagnóstico de disco foram retomados ao fechar.");
                }
                catch { }
            };

            UpdateStorageActionButtons();
            StartStorageMonitoring();
        }

        private void StartStorageMonitoring()
        {
            if (_stTimer == null)
            {
                _stTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _stTimer.Tick += (_, __) => StorageTick();
            }
            _stMonitoring = true;
            _stTimer.Start();
            BtnStToggle.Content = "⏸ PAUSAR";
            BtnStToggle.ToolTip = "Pausa a medição (a aba continua aberta)";
            StState.Text = "MONITORANDO";
            StStateBadge.Background = StBrush(0x1B, 0x3A, 0x24);
            StState.Foreground = StOk;
            StorageTick();
        }

        private void StopStorageMonitoring()
        {
            _stMonitoring = false;
            _stTimer?.Stop();
            if (_stBuilt)
            {
                BtnStToggle.Content = "▶ ESCUTAR";
                BtnStToggle.ToolTip = "Começa a medir os discos e ranquear quem está fazendo E/S";
                StState.Text = "PAUSADO";
                StStateBadge.Background = StBrush(0x33, 0x33, 0x33);
                StState.Foreground = StIdle;
            }
        }

        private void BtnStToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_stMonitoring) StopStorageMonitoring();
            else StartStorageMonitoring();
        }

        private void BtnStElevate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = Environment.ProcessPath ?? typeof(KitTaskManagerWindow).Assembly.Location;
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--tray",
                });
                TxtStatus.Text = "🛡️ Abrindo uma cópia elevada do Kit (aceite o UAC) — com admin o Kit consegue suspender processos de outros usuários.";
            }
            catch (Exception ex) { TxtStatus.Text = $"UAC recusado ou falhou: {ex.Message}"; }
        }

        // ══════════════ TICK DE 1 s (coleta em worker, render na UI) ══════════════

        private void StorageTick()
        {
            if (!_stMonitoring || _stBusy || _stSampling) return;
            _stSampling = true;

            // A coleta (PerformanceCounter + CIM de identidade de disco) NUNCA roda na
            // UI: quando o cache de identidade expira, ela faz uma consulta CIM que
            // travaria o app por dezenas de ms. Render volta por InvokeAsync.
            Task.Run(() =>
            {
                StorageDiagnostics.StorageSnapshot snap;
                List<StProcRow> ranked;
                try
                {
                    // Primeira amostra depois de criar os contadores volta ZERADA (contadores de
                    // taxa do Windows precisam de duas leituras). Use SampleWarm no primeiro tick,
                    // senão o Kit escolhe "o disco mais ocupado" no escuro.
                    snap = _stTick == 0 ? StorageDiagnostics.SampleWarm() : StorageDiagnostics.Sample();
                    ranked = BuildStorageRanking();
                }
                catch
                {
                    _stSampling = false;
                    return;
                }
                Dispatcher.InvokeAsync(() =>
                {
                    _stSampling = false;
                    if (!_stMonitoring && !_stBuilt) return;
                    _stSnap = snap;
                    _stTick++;
                    RenderStorageSnapshot(snap);
                    ApplyStorageRanking(ranked);
                    RenderInterventionJournal();
                    RenderStorageAudioCard();
                }, System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        /// <summary>
        /// Monta o ranking a partir do snapshot que o refresh do TMOG já produziu
        /// (leitura/escrita por PID). Contadores de E/S são destrutivos — reler aqui
        /// devolveria zero. Processos suspensos pelo Kit ficam na lista mesmo com
        /// E/S zerada, senão sumiriam justamente durante o teste.
        /// </summary>
        private List<StProcRow> BuildStorageRanking()
        {
            List<ProcessRow> live;
            lock (_lock) live = _allRows.ToList();

            var svcMap = StorageDiagnostics.GetServicesByPid();
            var result = new List<StProcRow>(live.Count);
            int selectedPid = _stSelected?.Pid ?? -1;

            foreach (var r in live)
            {
                bool suspended = StorageDiagnostics.IsSuspendedByKit(r.Pid);
                double total = r.DiskReadBytesPerSec + r.DiskWriteBytesPerSec;
                if (total <= 1 && !suspended && r.Pid != selectedPid) continue;

                var row = new StProcRow
                {
                    Pid = r.Pid,
                    Name = r.Name,
                    Path = r.Path,
                    ReadBps = r.DiskReadBytesPerSec,
                    WriteBps = r.DiskWriteBytesPerSec,
                    IopsValue = r.DiskOpsPerSec,
                    TotalBytes = r.IoReadTotal + r.IoWriteTotal,
                    Suspended = suspended,
                    Icon = r.ProcessIcon,
                };

                if (svcMap.TryGetValue(r.Pid, out var svcs) && svcs.Count > 0)
                {
                    row.ServiceName = svcs[0].Name;
                    row.ServiceState = svcs[0].State;
                    row.ServiceDisplay = svcs.Count > 1
                        ? $"{svcs[0].DisplayName} (+{svcs.Count - 1})"
                        : svcs[0].DisplayName;
                }

                row.IsCritical = StorageDiagnostics.IsCritical(r.Pid, r.Name, out var why);
                row.CriticalReason = why;
                row.Risk = StorageDiagnostics.RiskWarning(r.Name);
                result.Add(row);
            }

            var ordered = result.OrderByDescending(x => x.TotalBps).Take(60).ToList();
            // O processo marcado precisa continuar visível para as ações seguirem fazendo sentido.
            var sel = result.FirstOrDefault(x => x.Pid == selectedPid);
            if (sel != null && !ordered.Contains(sel)) ordered.Add(sel);

            // Identidade (hora de criação) só das linhas acionáveis — custa microssegundos por
            // processo e dá base para recusar ação em PID reciclado. Não faz sentido para as
            // centenas de processos irrelevantes que nem entram na lista.
            foreach (var row in ordered)
            {
                try { row.Identity = StorageDiagnostics.GetIdentity(row.Pid, row.Name); } catch { }
            }
            return ordered;
        }

        private void ApplyStorageRanking(List<StProcRow> ranked)
        {
            // MERGE por PID: preserva a INSTÂNCIA viva na grid, para os valores
            // atualizarem in-place (trocar a instância "congela" o que a grid mostra).
            var existing = new Dictionary<int, StProcRow>();
            foreach (var r in _stRows) existing[r.Pid] = r;

            var merged = new List<StProcRow>(ranked.Count);
            bool stateChanged = false;
            foreach (var r in ranked)
            {
                if (existing.TryGetValue(r.Pid, out var keep))
                {
                    bool wasSuspended = keep.Suspended;
                    bool wasService = keep.ServiceName.Length > 0;
                    keep.Name = r.Name;
                    keep.CopyMetricsFrom(r);
                    if (keep.Suspended != wasSuspended || (keep.ServiceName.Length > 0) != wasService)
                        stateChanged = true;
                    merged.Add(keep);
                    existing.Remove(r.Pid);
                }
                else merged.Add(r);
            }

            // O estado suspenso/retomado (e o serviço associado) só aparece DEPOIS do
            // tick assíncrono — sem este refresh os botões ficavam no estado antigo
            // (Retomar cinza mesmo com o processo já suspenso).
            if (stateChanged) UpdateStorageActionButtons();

            // Reconciliação por Move/Insert/RemoveAt — NUNCA Clear() + Add().
            // Clear() faz o DataGrid limpar a seleção (SelectionChanged com null) a
            // cada segundo: a linha marcada pelo usuário desmarcava, os botões de ação
            // piscavam habilitado/desabilitado e a dica voltava para "marque um
            // processo". Move() reordena sem disparar mudança de seleção.
            for (int target = 0; target < merged.Count; target++)
            {
                var want = merged[target];
                int cur = _stRows.IndexOf(want);
                if (cur < 0) _stRows.Insert(target, want);
                else if (cur != target) _stRows.Move(cur, target);
            }
            while (_stRows.Count > merged.Count)
                _stRows.RemoveAt(_stRows.Count - 1);

            // Se a linha marcada saiu da lista (processo morreu), as ações não podem
            // continuar apontando para um fantasma.
            if (_stSelected != null && !_stRows.Contains(_stSelected))
            {
                _stSelected = null;
                DgStProcs.SelectedItem = null;
                UpdateStorageActionButtons();
            }

            StEmptyHint.Visibility = _stRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_stRows.Count == 0)
                StEmptyHint.Text = _stMonitoring
                    ? "Nenhum processo está transferindo dados de/para o disco neste momento.\nSe o disco aparece ocupado mesmo assim, a atividade é de baixo nível (driver/anti-vírus/paginação) — veja a conclusão ao lado."
                    : "Clique em ESCUTAR para começar a medir.";
        }

        // ══════════════ RENDER DAS MÉTRICAS ══════════════

        private void RenderStorageSnapshot(StorageDiagnostics.StorageSnapshot snap)
        {
            var disks = snap.Disks.Where(d => d.Available).ToList();
            if (disks.Count == 0)
            {
                StDiskTitle.Text = "—";
                StDiskMeta.Text = snap.Error.Length > 0 ? snap.Error : "nenhum disco físico encontrado";
                StActivity.Text = "—"; StRead.Text = "—"; StWrite.Text = "—";
                StQueue.Text = "—"; StLatency.Text = "—"; StTransfers.Text = "sem dados";
                StDiagTitle.Text = "Sem dados de disco";
                StDiagText.Text = snap.Error.Length > 0
                    ? snap.Error + "\n\nSem os contadores de disco o Kit não consegue afirmar nada — e prefere dizer isso a inventar um culpado."
                    : "Nenhum contador de disco físico respondeu.";
                return;
            }

            BuildDiskChips(disks);

            StorageDiagnostics.DiskRate focus;
            if (_stUserPickedDisk)
            {
                focus = disks.FirstOrDefault(d => d.Instance.Equals(_stSelInstance, StringComparison.OrdinalIgnoreCase))
                        ?? snap.Busiest ?? disks[0];
            }
            else
            {
                // Auto-foco NUNCA deve trocar de disco por causa de empate em zero: com todos os
                // discos ociosos "o mais ocupado" é arbitrário e a faixa ficaria pulando de disco
                // sozinha. Sem atividade real, mantém o disco que já estava em foco.
                bool anyActive = disks.Any(d => d.ActivityPct >= 0.5 || d.Queue >= 0.05);
                focus = anyActive
                    ? (snap.Busiest ?? disks[0])
                    : (disks.FirstOrDefault(d => d.Instance.Equals(_stSelInstance, StringComparison.OrdinalIgnoreCase)) ?? snap.Busiest ?? disks[0]);
            }
            _stSelInstance = focus.Instance;
            HighlightDiskChip(focus.Instance);

            StDiskTitle.Text = focus.Display;
            var dev = focus.Device;
            StDiskMeta.Text = (dev == null
                ? ""
                : $"{dev.Model}{(dev.Media.Length > 0 ? " · " + dev.Media : "")}{(dev.Bus.Length > 0 ? " · " + dev.Bus : "")}" +
                  (dev.SizeGb > 0 ? $" · {dev.SizeGb:N0} GB" : "") +
                  (dev.IsSystem ? " · disco do sistema" : "") +
                  (dev.Health.Length > 0 && dev.Health != "Saudável" ? " · SAÚDE: " + dev.Health : ""))
                + (_stUserPickedDisk ? "" : " · auto");

            StActivity.Text = $"{focus.ActivityPct:F0}%";
            StActivity.Foreground = StorageActivityColor(focus);
            FluidSetText(StActivity, () => focus.ActivityPct, "F0", "%");   // número grande com easing
            StActivityHint.Text = focus.ActivityPct >= 90
                ? "disco saturado"
                : focus.ActivityPct >= 50 ? "ocupado" : "tempo ocupado";

            StRead.Text = RunKitTaskManagerFormat(focus.ReadBps);
            StWrite.Text = RunKitTaskManagerFormat(focus.WriteBps);
            StQueue.Text = focus.Queue.ToString("F2");
            StQueue.Foreground = focus.Queue >= 4 ? StBad : focus.Queue >= 1.5 ? StWarn : StOk;
            StQueueHint.Text = focus.Queue >= 1.5 ? "operações esperando" : "sem acúmulo";
            StLatency.Text = focus.LatencyMs >= 1 ? $"{focus.LatencyMs:F1} ms" : $"{focus.LatencyMs * 1000:F0} µs";
            StLatency.Foreground = focus.LatencyMs >= 50 ? StBad : focus.LatencyMs >= 15 ? StWarn : StOk;
            StTransfers.Text = $"{focus.TransfersPerSec:N0} operações/s";

            // Histórico curto (60 s) do disco em foco — é o "antes/depois" visual.
            // Motor fluido: as 3 linhas rolam a 60fps entre amostras de 1s.
            EnqueueFloat(_stActHist, (float)focus.ActivityPct);
            EnqueueFloat(_stQueueHist, (float)focus.Queue);
            EnqueueFloat(_stLatHist, (float)focus.LatencyMs);
            FluidRegisterChart(StChartActivity, () => 100f, true,
                (() => _stActHist, Color.FromRgb(0x4F, 0xC3, 0xF7)));
            FluidRegisterChart(StChartQueue,
                () => Math.Max(6f, _stQueueHist.Count > 0 ? _stQueueHist.Max() : 6f), true,
                (() => _stQueueHist, Color.FromRgb(0xFF, 0xB7, 0x4D)));
            FluidRegisterChart(StChartLatency,
                () => Math.Max(40f, _stLatHist.Count > 0 ? _stLatHist.Max() : 40f), true,
                (() => _stLatHist, Color.FromRgb(0xE5, 0x73, 0x73)));

            BuildStorageDiagnosis(snap, focus);
        }

        private static void EnqueueFloat(Queue<float> q, float v)
        {
            if (q.Count >= 61) q.Dequeue();
            q.Enqueue(v);
        }

        private SolidColorBrush StorageActivityColor(StorageDiagnostics.DiskRate d)
        {
            if (!d.Available) return StIdle;
            // "100% com pouca vazão" é o caso grave: fila/latência indicam que o disco
            // não está dando conta — não que ele esteja trabalhando rápido.
            if (d.ActivityPct >= 90 && d.LatencyMs >= 15) return StBad;
            if (d.ActivityPct >= 75) return StWarn;
            if (d.ActivityPct >= 40) return StAccent;
            return StOk;
        }

        // ══════════════ CHIPS DE DISCO ══════════════

        private void BuildDiskChips(List<StorageDiagnostics.DiskRate> disks)
        {
            if (_stChips.Count == disks.Count) return;
            StDiskChips.Children.Clear();
            _stChips.Clear();

            foreach (var d in disks)
            {
                var btn = new Button
                {
                    Content = d.Display,
                    Style = (Style)FindResource("TmToolButton"),
                    Margin = new Thickness(0, 0, 6, 0),
                    Padding = new Thickness(10, 0, 10, 0),
                    Tag = d.Instance,
                    ToolTip = d.Device?.Describe ?? d.Display,
                };
                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is string inst)
                    {
                        _stSelInstance = inst;
                        _stUserPickedDisk = true;
                        if (_stSnap != null) RenderStorageSnapshot(_stSnap);
                    }
                };
                _stChips[d.Instance] = btn;
                StDiskChips.Children.Add(btn);
            }
        }

        private void HighlightDiskChip(string instance)
        {
            foreach (var kv in _stChips)
            {
                bool active = kv.Key.Equals(instance, StringComparison.OrdinalIgnoreCase);
                kv.Value.Foreground = active ? StAccent : StIdle;
                kv.Value.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // ══════════════ DIAGNÓSTICO EM TEXTO SIMPLES ══════════════

        private void BuildStorageDiagnosis(StorageDiagnostics.StorageSnapshot snap, StorageDiagnostics.DiskRate focus)
        {
            // O diagnóstico descreve o disco EM FOCO (o que a faixa está mostrando).
            // Se outro disco estiver mais ocupado, o texto avisa no fim — assim a
            // conclusão nunca contradiz a faixa.
            var target = focus;
            var worst = snap.Disks.Where(d => d.Available)
                                  .OrderByDescending(d => d.ActivityPct).ThenByDescending(d => d.Queue)
                                  .FirstOrDefault();

            // O JULGAMENTO vem do Core (StorageAssessment) — MESMAS regras usadas no relatório
            // e no cruzamento com o áudio. Antes cada tela tinha o seu limite próprio, e isso
            // faz a tela e o relatório discordarem. Os limites agora dependem do MEIO: um HDD
            // com latência de 20 ms está afogado; um NVMe com 20 ms está com problema grave.
            var verdict = StorageAssessment.Assess(target);
            var brush = verdict.Level == StorageAssessment.Level.Ok ? StOk
                      : verdict.Level == StorageAssessment.Level.Critical ? StBad : StWarn;
            var badge = verdict.Level == StorageAssessment.Level.Ok ? "OK"
                      : verdict.Level == StorageAssessment.Level.Critical ? "CRÍTICO" : "ATENÇÃO";
            SetDiagCard(brush, badge, verdict.Title);
            StDiagTitle.Text = verdict.Title;
            StDiagText.Text = verdict.Text;

            if (worst != null && !worst.Instance.Equals(target.Instance, StringComparison.OrdinalIgnoreCase)
                && (worst.ActivityPct >= 60 || worst.Queue >= 3 || worst.LatencyMs >= 20))
            {
                StDiagText.Text += $"\n\nObservação: outro disco está mais ocupado agora — {worst.Display} com " +
                                   $"{worst.ActivityPct:F0}% de atividade, fila {worst.Queue:F1} e latência {worst.LatencyMs:F1} ms. " +
                                   "Clique no chip dele acima para analisar aquele disco.";
            }

            // Explica POR QUE o veredito saiu assim (limites usados) — o usuário precisa poder
            // contestar o Kit, e a IA que receber o relatório precisa saber o critério.
            if (verdict.Level != StorageAssessment.Level.Ok)
                StDiagText.Text += "\n\nCritério usado: " + verdict.ThresholdsUsed;

            BuildStorageCandidateText(snap, target);
            AppendAudioCrossCheck();
        }

        /// <summary>
        /// Cruza o diagnóstico de disco com os glitches de áudio da sessão. O estalo de áudio é
        /// o sintoma que o usuário SENTE antes de olhar número: se o áudio falhou enquanto o disco
        /// estava saturado, isso é evidência independente (e é dito como indício, não como prova).
        /// </summary>
        private void AppendAudioCrossCheck()
        {
            try
            {
                var audio = AudioGlitchMonitor.Instance;
                if (!audio.IsRunning) return;
                int confirmed = audio.AudibleConfirmedCount;
                if (confirmed == 0) return;

                var recent = audio.Glitches.Where(g => g.Kind == "CONFIRMADO")
                                           .OrderByDescending(g => g.At).FirstOrDefault();
                if (recent == null) return;

                bool diskWasBusy = recent.DiskActivityPct >= 80 || recent.DiskQueue >= 3 || recent.DiskLatencyMs >= 20;

                // Duração já calculada fora da interpolação: interpolação aninhada aqui só
                // atrapalha a leitura (e o compilador WPF reclamou).
                string dur = recent.LostMs >= 1
                    ? recent.LostMs.ToString("F0") + " ms de áudio perdidos"
                    : "motor de áudio parado por " + recent.InterruptionMs.ToString("F0") + " ms";

                string head = $"\n\nO áudio estalou {confirmed} vez(es) nesta sessão. Na pior delas " +
                              $"({recent.At:HH:mm:ss}, {dur}) ";
                if (diskWasBusy)
                {
                    StDiagText.Text += head +
                        $"o disco {recent.DiskLabel} estava saturado ({recent.DiskActivityPct:F0}% de atividade, " +
                        $"fila {recent.DiskQueue:F1}, latência {recent.DiskLatencyMs:F1} ms). " +
                        "Isso é evidência independente de que a saturação chegou a atrapalhar o que você ouve " +
                        "— mas ainda é indício: não é prova da causa do disco.";
                }
                else
                {
                    StDiagText.Text += head +
                        "os discos estavam calmos nesse momento (veja a aba Latência e travamentos) — " +
                        "o problema de áudio pode não ser de armazenamento.";
                }
            }
            catch { }
        }

        private void SetDiagCard(SolidColorBrush color, string badge, string title)
        {
            StDiagCard.BorderBrush = color;
            StDiagTitle.Foreground = color;
            StDiagBadgeText.Text = badge;
            StDiagBadgeText.Foreground = color;
            StDiagBadge.Background = color == StOk ? StBrush(0x1B, 0x3A, 0x24)
                : color == StBad ? StBrush(0x3A, 0x1B, 0x1B) : StBrush(0x3A, 0x30, 0x12);
            _ = title;
        }

        /// <summary>
        /// A parte que o usuário pediu: ligar a causa. Aqui separamos explicitamente
        /// "quem faz mais E/S" de "quem provavelmente está causando".
        /// </summary>
        private void BuildStorageCandidateText(StorageDiagnostics.StorageSnapshot snap, StorageDiagnostics.DiskRate target)
        {
            // A análise da distribuição do E/S vem do CORE (StorageAssessment.Analyze).
            // Antes esta tela tinha a própria cópia da regra, com limites próprios: duas
            // cópias = tela e relatório discordando. Agora existe UMA regra, coberta por
            // asserções no harness (disco ocioso, HDD x NVMe, espalhado, System, sem culpado).
            var ranks = _stRows.Select(r => new StorageDiagnostics.ProcIoRank
            {
                Pid = r.Pid,
                Name = r.Name,
                Path = r.Path,
                ReadBps = r.ReadBps,
                WriteBps = r.WriteBps,
                Iops = r.IopsValue,
                Services = r.ServiceName.Length == 0
                    ? new List<StorageDiagnostics.ServiceRef>()
                    : new List<StorageDiagnostics.ServiceRef> { new() { Name = r.ServiceName, DisplayName = r.ServiceDisplay } }
            }).ToList();

            var dist = StorageAssessment.Analyze(ranks, target.TotalBps);
            StCandidate.Text = dist.Text;
        }

        // ══════════════ SELEÇÃO + ESTADO DOS BOTÕES ══════════════

        private void DgStProcs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _stSelected = DgStProcs.SelectedItem as StProcRow;
            UpdateStorageActionButtons();
            if (_stSelected != null && _stLastImpact != null && _stLastImpact.Pid != _stSelected.Pid)
            {
                StImpactCard.Visibility = Visibility.Collapsed;
                _stLastImpact = null;
            }
        }

        private void UpdateStorageActionButtons()
        {
            var s = _stSelected;
            bool has = s != null;

            bool sweepable = CountSweepCandidates() > 0;
            BtnStSweep.IsEnabled = !_stBusy && sweepable;
            BtnStSweep.Content = _stSweeping ? "⏹ Cancelar varredura" : "🔎 Varrer suspeitos";

            BtnStInvestigate.IsEnabled = has && !_stBusy;
            BtnStTest.IsEnabled = has && !_stBusy && !s!.IsCritical;
            BtnStSuspend.IsEnabled = has && !_stBusy && !s!.IsCritical && !s.Suspended;
            BtnStResume.IsEnabled = has && !_stBusy && s!.Suspended;
            BtnStForceStop.IsEnabled = has && !_stBusy && !s!.IsCritical;
            bool hasSvc = has && s!.ServiceName.Length > 0;
            BtnStRestartSvc.IsEnabled = hasSvc && !_stBusy;
            BtnStStopSvc.IsEnabled = hasSvc && !_stBusy;
            BtnStStartSvc.IsEnabled = hasSvc && !_stBusy;

            if (_stSweeping)
            {
                StActionHint.Text = "🔎 Varredura de suspeitos em andamento — veja o progresso acima. Clique em 'Cancelar varredura' para parar (tudo é retomado).";
                return;
            }
            if (_stBusy)
            {
                StActionHint.Text = "⏳ Operação em andamento…";
                return;
            }
            if (!has)
            {
                StActionHint.Text = "Marque um processo na tabela acima para liberar as ações. Force Stop é o último recurso — teste antes.";
                return;
            }
            if (s!.IsCritical)
            {
                StActionHint.Text = $"⛔ '{s.Name}' é protegido: {s.CriticalReason}. O Kit não suspende nem encerra este processo.";
            }
            else if (s.Risk.Length > 0)
            {
                StActionHint.Text = $"⚠ Atenção com '{s.Name}': {s.Risk}";
            }
            else if (s.Suspended)
            {
                StActionHint.Text = $"'{s.Name}' está SUSPENSO pelo Kit — o processo está congelado, não encerrado. Use Retomar para devolver ao normal.";
            }
            else
            {
                StActionHint.Text = $"Pronto para agir em '{s.Name}' (PID {s.Pid}). Investigar → Testar impacto → Suspender → serviço → Force Stop.";
            }
        }

        // O projeto tem UseWindowsForms=true, então "MessageBox" sozinho é ambíguo
        // (System.Windows.Forms × System.Windows) — aqui queremos o do WPF.
        private static bool Confirm(string title, string text)
            => System.Windows.MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

        // ══════════════ INVESTIGAR ══════════════

        private async void BtnStInvestigate_Click(object sender, RoutedEventArgs e)
        {
            var s = _stSelected;
            if (s == null) return;

            ProcessRow? live;
            lock (_lock) live = _allRows.FirstOrDefault(x => x.Pid == s.Pid);

            StInvestigatePanel.Visibility = Visibility.Visible;
            StInvestigateText.Text = $"Investigando {s.Name} (PID {s.Pid})…";

            var pid = s.Pid; var name = s.Name; var path = s.Path;
            double cpu = live?.CpuValue ?? 0, ws = live?.RamValue ?? 0, commit = live?.CommitMB ?? 0;
            double rd = s.ReadBps, wr = s.WriteBps;
            ulong rt = live?.IoReadTotal ?? 0, wt = live?.IoWriteTotal ?? 0, ops = live?.IoOpsTotal ?? 0;
            int threads = int.TryParse(live?.Threads, out var t) ? t : 0;
            int handles = int.TryParse(live?.Handles, out var h) ? h : 0;
            string user = live?.UserName ?? "";

            var diag = await Task.Run(() =>
                StorageDiagnostics.Investigate(pid, name, path, cpu, ws, commit, rd, wr, rt, wt, ops, threads, handles, user));

            StInvestigateText.Text = FormatInvestigation(diag);
        }

        private static string FormatInvestigation(StorageDiagnostics.ProcessDiagnostics d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"══ {d.Name} (PID {d.Pid}) ══");
            if (d.Description.Length > 0) sb.AppendLine($"Descrição   : {d.Description}");
            if (d.Company.Length > 0) sb.AppendLine($"Fabricante  : {d.Company}");
            if (d.Version.Length > 0) sb.AppendLine($"Versão      : {d.Version}");
            sb.AppendLine($"Caminho     : {(d.Path.Length > 0 ? d.Path : "—")}");
            sb.AppendLine($"Usuário     : {(d.UserName.Length > 0 ? d.UserName : "—")}" +
                          (d.IsElevated ? "  (elevado)" : ""));
            sb.AppendLine($"Pai         : {(d.ParentName.Length > 0 ? $"{d.ParentName} (PID {d.ParentPid})" : "—")}");
            sb.AppendLine();
            sb.AppendLine("── E/S AGORA ──");
            sb.AppendLine($"Leitura     : {RunKitTaskManagerFormat(d.ReadBps)}");
            sb.AppendLine($"Escrita     : {RunKitTaskManagerFormat(d.WriteBps)}");
            sb.AppendLine($"Acumulado   : R {FormatBytes(d.ReadTotalBytes)} / W {FormatBytes(d.WriteTotalBytes)}");
            sb.AppendLine($"Operações   : {d.OpsTotal:N0}");
            sb.AppendLine();
            sb.AppendLine("── RECURSO ──");
            sb.AppendLine($"CPU         : {d.CpuPct:F1}%");
            sb.AppendLine($"RAM (WS)    : {d.WorkingSetMb:N0} MB   privado: {d.PrivateMb:N0} MB");
            sb.AppendLine($"Threads     : {d.Threads}   Handles: {d.Handles}   Módulos: {d.ModuleCount}");
            sb.AppendLine($"Iniciado    : {(d.StartedAt.Length > 0 ? d.StartedAt : "—")}   ({(d.Uptime.Length > 0 ? d.Uptime : "—")})");
            sb.AppendLine();
            sb.AppendLine("── ARMAZENAMENTO ──");
            sb.AppendLine($"Unidade     : {(d.DiskLetter.Length > 0 ? d.DiskLetter : "—")}");
            sb.AppendLine($"Disco físico: {(d.DiskLabel.Length > 0 ? $"{d.DiskLabel} ({d.DiskMedia})" : "não identificado")}");
            if (d.Services.Count > 0)
            {
                sb.AppendLine($"Serviço(s)  : {string.Join(", ", d.Services.Select(s => $"{s.DisplayName} [{s.Name}] ({s.State})"))}");
                sb.AppendLine("              → reiniciar/parar o serviço é a intervenção correta para este caso.");
            }
            else sb.AppendLine("Serviço(s)  : nenhum (processo comum)");
            sb.AppendLine();
            sb.AppendLine("── ESTADO ──");
            sb.AppendLine($"Respondendo : {d.Responding}");
            sb.AppendLine($"Suspenso    : {(d.Suspended ? "SIM (pelo Kit, reversível)" : "não")}");
            sb.AppendLine($"Processo de sistema / crítico: {d.IsSystem} / {d.IsCritical}");
            if (d.IsCritical && d.CriticalReason.Length > 0) sb.AppendLine($"Motivo      : {d.CriticalReason}");
            if (d.Error.Length > 0) sb.AppendLine($"Observação  : {d.Error}");
            return sb.ToString().TrimEnd();
        }

        private static string FormatBytes(ulong b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024UL * 1024) return $"{b / 1024.0:F1} KB";
            if (b < 1024UL * 1024 * 1024) return $"{b / 1024.0 / 1024:F1} MB";
            return $"{b / 1024.0 / 1024 / 1024:F2} GB";
        }

        private void BtnStCloseInvestigate_Click(object sender, RoutedEventArgs e)
            => StInvestigatePanel.Visibility = Visibility.Collapsed;

        private void BtnStCopyInvestigate_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(StInvestigateText.Text ?? ""); TxtStatus.Text = "📋 Investigação copiada para a área de transferência."; }
            catch { }
        }

        // ══════════════ TESTE DE IMPACTO ══════════════

        private async void BtnStTest_Click(object sender, RoutedEventArgs e)
        {
            var s = _stSelected;
            if (s == null || _stBusy) return;

            string risk = s.Risk.Length > 0
                ? $"\n\nATENÇÃO: {s.Risk}\n"
                : "\n";
            bool ok = Confirm("Testar impacto de " + s.Name,
                $"O Kit vai:\n" +
                $"  1. medir o disco por 5 segundos (estado normal);\n" +
                $"  2. SUSPENDER '{s.Name}' (PID {s.Pid}) por 8 segundos;\n" +
                $"  3. medir de novo e comparar;\n" +
                $"  4. retomar o processo automaticamente.\n" +
                risk +
                $"\nSuspender CONGELA o processo — não encerra, e é sempre reversível." +
                $"\nUse apenas com o problema acontecendo: com o disco calmo, o teste não conclui nada.");
            if (!ok) return;

            _stBusy = true;
            UpdateStorageActionButtons();
            _stTestCts = new CancellationTokenSource();

            var pid = s.Pid; var name = s.Name;
            var progress = new Progress<string>(msg => StActionHint.Text = "🧪 " + msg);

            // Marca o instante exato do início: sem isso não dá para saber se um estalo de
            // áudio aconteceu no estado normal ou durante a suspensão (evidência independente).
            DateTime testStartedAt = DateTime.Now;
            _stAudioCrossCheck = "";

            StorageDiagnostics.ImpactTest result;
            try
            {
                result = await StorageDiagnostics.RunImpactTestAsync(
                    pid, name, 5, 8, () => ReadProcIoTotal(pid),
                    msg => ((IProgress<string>)progress).Report(msg),
                    _stTestCts.Token);
            }
            catch (OperationCanceledException)
            {
                result = new StorageDiagnostics.ImpactTest { Pid = pid, ProcessName = name, Message = "Teste cancelado. O processo foi retomado." };
            }
            catch (Exception ex)
            {
                result = new StorageDiagnostics.ImpactTest { Pid = pid, ProcessName = name, Message = "Falha no teste: " + ex.Message };
            }
            finally
            {
                _stBusy = false;
                _stTestCts?.Dispose();
                _stTestCts = null;
            }

            if (result.Ok)
                _stAudioCrossCheck = BuildAudioImpactCrossCheck(testStartedAt, 5, 8);

            RenderStorageImpact(result);
            // A lista precisa refletir imediatamente o estado suspenso/retomado
            StorageTick();
            UpdateStorageActionButtons();
        }

        /// <summary>Complemento de áudio do último teste de impacto (vazio quando não houve glitch).</summary>
        private string _stAudioCrossCheck = "";

        private ulong ReadProcIoTotal(int pid)
        {
            lock (_lock)
            {
                var r = _allRows.FirstOrDefault(x => x.Pid == pid);
                return r == null ? 0 : r.IoReadTotal + r.IoWriteTotal;
            }
        }

        private void RenderStorageImpact(StorageDiagnostics.ImpactTest r)
        {
            // Guardar aqui (e não só no handler) garante que o relatório para IA carregue
            // o antes/depois sempre que o card estiver visível.
            _stLastImpact = r;
            StImpactCard.Visibility = Visibility.Visible;

            if (r.Message.Length > 0 && !r.Ok)
            {
                StImpactTitle.Text = r.Verdict.Length > 0 ? "Teste inconclusivo" : "Não foi possível testar";
                StImpactTitle.Foreground = StWarn;
                StImpactText.Text = r.Message;
                StImpactTable.Visibility = Visibility.Collapsed;
                return;
            }

            StImpactTable.Visibility = Visibility.Visible;
            var color = r.Verdict switch
            {
                "FORTE" => StOk,
                "MODERADA" => StWarn,
                "FRACA" => StWarn,
                _ => StBad
            };
            StImpactTitle.Foreground = color;
            StImpactTitle.Text = r.Verdict switch
            {
                "FORTE" => $"✔ Correlação FORTE — {r.ProcessName} é o responsável",
                "MODERADA" => $"~ Correlação MODERADA — {r.ProcessName} pesa, mas não explica tudo",
                "FRACA" => $"· Correlação FRACA — {r.ProcessName} provavelmente não é a causa",
                "NENHUMA" => $"✖ SEM correlação — {r.ProcessName} NÃO é a causa",
                _ => "Teste inconclusivo"
            };

            StImpactText.Text =
                $"Disco medido: {r.DiskLabel}\n" +
                $"{r.VerdictDetail}\n" +
                (r.ResumedOk ? $"✅ '{r.ProcessName}' foi retomado (o teste é sempre reversível)." : "⚠ " + r.ResumeMessage) +
                $"\n\nÍndice de correlação: {r.CorrelationPct:F0}%  ·  {r.SamplesBefore} amostras antes / {r.SamplesAfter} depois" +
                (r.Corroborations > 0 ? $"  ·  {r.Corroborations}/4 métricas caíram juntas" : "") +
                (r.IoFrozen ? "\n✅ A E/S do próprio processo congelou durante a suspensão — pegamos o processo certo." : "") +
                (r.CorroborationText.Length > 0 ? "\n" + r.CorroborationText : "") +
                _stAudioCrossCheck;

            StImpActB.Text = $"{r.ActBefore:F0}%";
            StImpActA.Text = $"{r.ActAfter:F0}%";
            StImpActB.Foreground = StorageActivityBrush(r.ActBefore);
            StImpActA.Foreground = StorageActivityBrush(r.ActAfter);
            StImpQb.Text = r.QueueBefore.ToString("F2");
            StImpQa.Text = r.QueueAfter.ToString("F2");
            StImpLb.Text = $"{r.LatBefore:F1} ms";
            StImpLa.Text = $"{r.LatAfter:F1} ms";
            StImpWb.Text = RunKitTaskManagerFormat(r.WriteBefore);
            StImpWa.Text = RunKitTaskManagerFormat(r.WriteAfter);
        }

        private SolidColorBrush StorageActivityBrush(double activity)
            => activity >= 85 ? StBad : activity >= 60 ? StWarn : activity >= 25 ? StAccent : StOk;

        // ══════════════ SUSPENDER / RETOMAR ══════════════

        private async void BtnStSuspend_Click(object sender, RoutedEventArgs e)
        {
            var s = _stSelected;
            if (s == null || _stBusy) return;
            if (s.Risk.Length > 0 &&
                !Confirm("Suspender " + s.Name, $"ATENÇÃO: {s.Risk}\n\nDeseja suspender mesmo assim?\n\nSuspender é reversível — o processo volta com 'Retomar'."))
                return;

            _stBusy = true; UpdateStorageActionButtons();
            // Passa a identidade capturada na montagem da linha: se o PID mudou de dono desde
            // então, o Core RECUSA em vez de congelar o programa errado.
            var wanted = s.Identity;
            var res = await Task.Run(() => StorageDiagnostics.SuspendProcess(s.Pid, s.Name, wanted));
            _stBusy = false;
            TxtStatus.Text = (res.Ok ? "⏸ " : "⚠ ") + res.Message;
            StorageTick();
            UpdateStorageActionButtons();
        }

        private async void BtnStResume_Click(object sender, RoutedEventArgs e)
        {
            var s = _stSelected;
            if (s == null || _stBusy) return;

            _stBusy = true; UpdateStorageActionButtons();
            var res = await Task.Run(() => StorageDiagnostics.ResumeProcess(s.Pid, s.Name));
            _stBusy = false;
            TxtStatus.Text = (res.Ok ? "▶ " : "⚠ ") + res.Message;
            StorageTick();
            UpdateStorageActionButtons();
        }

        // ══════════════ VARREDURA AUTOMÁTICA ("continua testando até encontrar") ══════════════

        /// <summary>
        /// Candidatos elegíveis à varredura: os que realmente movimentam o disco.
        /// Processos do núcleo e os de risco conhecido ficam FORA — a varredura suspende
        /// automaticamente e não há como confirmar caso a caso.
        /// </summary>
        private List<StProcRow> GetSweepCandidates(int max)
        {
            return _stRows
                .Where(r => r.TotalBps > 0 && !r.IsCritical && r.Risk.Length == 0 && !r.Suspended)
                .OrderByDescending(r => r.TotalBps)
                .Take(max)
                .ToList();
        }

        private int CountSweepCandidates() => GetSweepCandidates(1).Count;

        private static int VerdictRank(string v) => v switch
        {
            "FORTE" => 5, "MODERADA" => 4, "FRACA" => 3, "NENHUMA" => 2, "INCONCLUSIVO" => 1, _ => 0
        };

        private async void BtnStSweep_Click(object sender, RoutedEventArgs e)
        {
            if (_stSweeping) { _stTestCts?.Cancel(); return; }
            if (_stBusy) return;

            var candidates = GetSweepCandidates(5);
            if (candidates.Count == 0)
            {
                TxtStatus.Text = "Nenhum candidato elegível: os processos com E/S agora são do sistema/protegidos ou não movimentam o disco o bastante.";
                return;
            }

            int seconds = candidates.Count * 10;
            var names = string.Join(", ", candidates.Select(c => c.Name));
            if (!Confirm($"Varrer {candidates.Count} suspeito(s)",
                    "O Kit vai testar um por um, sozinho, com o problema acontecendo:\n\n" +
                    string.Join("\n", candidates.Select((c, i) => $"  {i + 1}. {c.Name} (PID {c.Pid}) — {RunKitTaskManagerFormat(c.TotalBps)}")) +
                    $"\n\nCada teste suspende o processo por ~6 s e retoma em seguida (total ~{seconds} s).\n" +
                    "Para no primeiro que aliviar o disco de verdade.\n\n" +
                    "Processos do núcleo do Windows e de risco conhecido NÃO entram na varredura."))
                return;

            _stSweeping = true;
            _stBusy = true;
            _stTestCts = new CancellationTokenSource();
            UpdateStorageActionButtons();

            var results = new List<StorageDiagnostics.ImpactTest>();
            var tested = new List<string>();
            bool cancelled = false;

            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    int idx = i;
                    StActionHint.Text = ($"🔎 Testando {idx + 1}/{candidates.Count}: {c.Name} (PID {c.Pid})…");

                    StorageDiagnostics.ImpactTest r;
                    try
                    {
                        r = await StorageDiagnostics.RunImpactTestAsync(
                            c.Pid, c.Name, 4, 6, () => ReadProcIoTotal(c.Pid),
                            msg => Dispatcher.InvokeAsync(() =>
                                StActionHint.Text = $"🔎 [{idx + 1}/{candidates.Count}] {msg}"),
                            _stTestCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        break;
                    }

                    results.Add(r);
                    tested.Add($"{c.Name}: {(r.Verdict.Length > 0 ? r.Verdict : "não testado")}" +
                               (r.Ok ? $" ({r.CorrelationPct:F0}%)" : ""));
                    RenderStorageImpact(r);

                    if (r.Ok && r.Verdict == "FORTE") break;   // achou: para aqui
                }
            }
            finally
            {
                _stSweeping = false;
                _stBusy = false;
                _stTestCts?.Dispose();
                _stTestCts = null;
            }

            var conclusive = results.FirstOrDefault(r => r.Verdict == "FORTE")
                          ?? results.Where(r => r.Ok).OrderByDescending(r => VerdictRank(r.Verdict))
                                    .ThenByDescending(r => r.CorrelationPct).FirstOrDefault()
                          ?? results.FirstOrDefault();

            if (conclusive != null)
            {
                RenderStorageImpact(conclusive);
                var bestRow = _stRows.FirstOrDefault(r => r.Pid == conclusive.Pid);
                if (bestRow != null)
                {
                    _stSelected = bestRow;
                    DgStProcs.SelectedItem = bestRow;
                    DgStProcs.ScrollIntoView(bestRow);
                }
            }

            var summary = new StringBuilder();
            summary.AppendLine();
            summary.AppendLine("── RESUMO DA VARREDURA ──");
            foreach (var t in tested) summary.AppendLine("  " + t);
            if (cancelled) summary.AppendLine("  (cancelada pelo usuário — todos os processos foram retomados)");
            if (conclusive != null && conclusive.Ok)
            {
                summary.AppendLine(conclusive.Verdict == "FORTE"
                    ? $"  ➜ Culpado encontrado: {conclusive.ProcessName} (correlação {conclusive.CorrelationPct:F0}%)."
                    : conclusive.Verdict is "MODERADA" or "FRACA"
                        ? $"  ➜ Nenhum processo explicou tudo. O mais relevante foi {conclusive.ProcessName} ({conclusive.Verdict}, {conclusive.CorrelationPct:F0}%) — provavelmente há mais de um responsável ou a atividade é de baixo nível."
                        : "  ➜ Nenhum dos processos testados alivia o disco. A causa provavelmente não é um programa: investigue o dispositivo e os drivers (aba Latência).");
            }
            else
            {
                summary.AppendLine("  ➜ A varredura não chegou a uma conclusão. Tente novamente com o disco realmente ocupado.");
            }

            StImpactText.Text = (StImpactText.Text ?? "") + summary.ToString();
            TxtStatus.Text = conclusive != null && conclusive.Ok
                ? "🔎 Varredura concluída — veja o resumo no card 'Teste de impacto'."
                : "🔎 Varredura concluída sem conclusão firme.";

            StorageTick();
            UpdateStorageActionButtons();
        }

        // ══════════════ SERVIÇOS (a intervenção correta quando o processo é um serviço) ══════════════

        private async void BtnStRestartSvc_Click(object sender, RoutedEventArgs e) => await StorageServiceActionAsync("restart");
        private async void BtnStStopSvc_Click(object sender, RoutedEventArgs e) => await StorageServiceActionAsync("stop");
        private async void BtnStStartSvc_Click(object sender, RoutedEventArgs e) => await StorageServiceActionAsync("start");

        private async Task StorageServiceActionAsync(string action)
        {
            var s = _stSelected;
            if (s == null || s.ServiceName.Length == 0 || _stBusy) return;

            string verb = action switch { "stop" => "PARAR", "start" => "INICIAR", _ => "REINICIAR" };
            if (!Confirm($"{verb} serviço {s.ServiceDisplay}",
                    $"Serviço: {s.ServiceDisplay} ({s.ServiceName})\n" +
                    $"Hospedado no processo: {s.Name} (PID {s.Pid})\n\n" +
                    "Parar/reiniciar o SERVIÇO é a intervenção correta para serviços do Windows — " +
                    "matar o processo faria o Windows tentar reiniciá-lo, às vezes criando instabilidade.\n\n" +
                    "O Kit registra o estado anterior para você poder reverter com 'Iniciar serviço'."))
                return;

            _stBusy = true; UpdateStorageActionButtons();
            string svcName = s.ServiceName;
            string svcDisplay = s.ServiceDisplay;
            string prevState = s.ServiceState;
            string msg = await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(svcName);
                    // Estado ANTERIOR registrado antes de mexer: é o que permite restaurar depois.
                    try { prevState = sc.Status.ToString(); } catch { }
                    StorageDiagnostics.RecordServiceAction(svcName, svcDisplay, action, prevState);
                    if (action == "stop" || action == "restart")
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            if (sc.CanStop) { sc.Stop(); try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); } catch { } }
                            else return $"⚠ O serviço {svcName} reporta que não pode ser parado (dependência ou motivo de sistema).";
                        }
                    }
                    if (action == "start" || action == "restart")
                    {
                        sc.Refresh();
                        if (sc.Status != ServiceControllerStatus.Running)
                        {
                            sc.Start();
                            try { sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25)); } catch { }
                        }
                    }
                    return $"✔ Serviço {svcName}: {verb.ToLower()} concluído (estado agora: {sc.Status}).";
                }
                catch (Exception ex) { return $"⚠ Falha ao {verb.ToLower()} {svcName}: {ex.Message}"; }
            });

            _stBusy = false;
            TxtStatus.Text = msg;
            StorageDiagnostics.GetServicesByPid(forceRefresh: true);
            StorageTick();
            UpdateStorageActionButtons();
        }

        // ══════════════ FORCE STOP (último recurso) ══════════════

        private async void BtnStForceStop_Click(object sender, RoutedEventArgs e)
        {
            var s = _stSelected;
            if (s == null || _stBusy) return;

            if (!Confirm("Force Stop " + s.Name,
                    $"ÚLTIMO RECURSO\n\nIsto ENCERRA '{s.Name}' (PID {s.Pid}) de verdade — libera handles e drivers, " +
                    "sem chance de reverter.\n\nPrefira: Investigar → Testar impacto → Suspender → Reiniciar serviço.\n\n" +
                    "Confirmar Force Stop?"))
                return;

            _stBusy = true; UpdateStorageActionButtons();
            int pid = s.Pid; string name = s.Name; string path = s.Path;

            await Task.Run(() =>
            {
                try
                {
                    string target = !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)) ? path : name;
                    var blocking = ForceStopUnlockService.FindBlockingProcesses(target);
                    if (blocking.Count == 0)
                    {
                        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
                    }
                    else ForceStopUnlockService.Unlock(target, blocking, deleteTarget: false);
                }
                catch (Exception ex) { Logger.Log($"[KIT TASK MANAGER] ForceStop (armazenamento) {name}: {ex.Message}"); }
            });

            lock (_lock) { _allRows.RemoveAll(r => r.Pid == pid); }
            _stBusy = false;
            _stSelected = null;
            DgStProcs.SelectedItem = null;
            TxtStatus.Text = $"⚡ Force Stop em {name} concluído.";
            StorageTick();
            UpdateStorageActionButtons();
        }

        // ══════════════ RELATÓRIO PARA IA ══════════════

        private void BtnStCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildStorageReport());
                TxtStatus.Text = "📋 Relatório de armazenamento copiado — cole numa IA para análise.";
            }
            catch (Exception ex) { TxtStatus.Text = "Falha ao copiar: " + ex.Message; }
        }

        private string BuildStorageReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== KitLugia — Relatório de Armazenamento ===");
            sb.AppendLine($"Gerado: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
            sb.AppendLine($"Monitorando: {_stMonitoring} (amostras: {_stTick})");
            sb.AppendLine($"Windows: {Environment.OSVersion.VersionString} | x64 | {Environment.ProcessorCount} núcleos lógicos");
            sb.AppendLine($"Administrador: {_stIsAdmin}");
            sb.AppendLine();

            var snap = _stSnap;
            if (snap == null || snap.Disks.Count == 0)
            {
                sb.AppendLine("--- DISCOS FÍSICOS ---");
                sb.AppendLine(snap?.Error.Length > 0 ? snap.Error : "(nenhum dado coletado)");
            }
            else
            {
                sb.AppendLine("--- DISCOS FÍSICOS (agora) ---");
                foreach (var d in snap.Disks)
                {
                    sb.AppendLine($"  {d.Display,-18} ativ={d.ActivityPct,6:F1}%  leitura={RunKitTaskManagerFormat(d.ReadBps),-10} " +
                                  $"escrita={RunKitTaskManagerFormat(d.WriteBps),-10} fila={d.Queue,6:F2} lat={d.LatencyMs,7:F1}ms " +
                                  $"ops/s={d.TransfersPerSec,7:F0}  modelo='{d.Device?.Model}' meio={d.Device?.Media} barramento={d.Device?.Bus}" +
                                  $"{(d.Device?.IsSystem == true ? " [SISTEMA]" : "")}{(d.Available ? "" : " ERRO:" + d.Error)}");
                }
                sb.AppendLine();
                sb.AppendLine("--- DIAGNÓSTICO ---");
                sb.AppendLine(StDiagTitle.Text);
                foreach (var line in (StDiagText.Text ?? "").Split('\n'))
                    if (line.Trim().Length > 0) sb.AppendLine("  " + line.Trim());
                var cand = StCandidate.Text ?? "";
                if (cand.Length > 0)
                {
                    sb.AppendLine("  Candidato/hipótese:");
                    foreach (var line in cand.Split('\n'))
                        if (line.Trim().Length > 0) sb.AppendLine("    " + line.Trim());
                }
            }
            sb.AppendLine();

            sb.AppendLine("--- PROCESSOS COM E/S (topo) ---");
            sb.AppendLine("  nome;pid;leitura;escrita;total;servico;suspenso");
            foreach (var r in _stRows.Take(25))
                sb.AppendLine($"  {r.Name};{r.Pid};{RunKitTaskManagerFormat(r.ReadBps)};{RunKitTaskManagerFormat(r.WriteBps)};" +
                              $"{RunKitTaskManagerFormat(r.TotalBps)};{r.ServiceName};{(r.Suspended ? "SIM" : "nao")}");
            if (_stRows.Count == 0) sb.AppendLine("  (nenhum processo com E/S relevante no momento)");
            sb.AppendLine();

            if (_stLastImpact != null)
            {
                var r = _stLastImpact;
                sb.AppendLine("--- TESTE DE IMPACTO (antes/depois real) ---");
                sb.AppendLine($"  processo: {r.ProcessName} (PID {r.Pid})   disco: {r.DiskLabel}");
                sb.AppendLine($"  veredito: {r.Verdict}   correlação: {r.CorrelationPct:F0}%");
                sb.AppendLine($"  ANTES : atividade={r.ActBefore:F1}% fila={r.QueueBefore:F2} latência={r.LatBefore:F1}ms leitura={RunKitTaskManagerFormat(r.ReadBefore)} escrita={RunKitTaskManagerFormat(r.WriteBefore)} ({r.SamplesBefore} amostras)");
                sb.AppendLine($"  DEPOIS: atividade={r.ActAfter:F1}% fila={r.QueueAfter:F2} latência={r.LatAfter:F1}ms leitura={RunKitTaskManagerFormat(r.ReadAfter)} escrita={RunKitTaskManagerFormat(r.WriteAfter)} ({r.SamplesAfter} amostras)");
                sb.AppendLine($"  processo retomado: {r.ResumedOk}   detalhe: {r.VerdictDetail}");
                if (r.Message.Length > 0) sb.AppendLine($"  mensagem: {r.Message}");
                sb.AppendLine();
            }

            if (StInvestigatePanel.Visibility == Visibility.Visible && (StInvestigateText.Text ?? "").Length > 0)
            {
                sb.AppendLine("--- INVESTIGAÇÃO DO PROCESSO ---");
                sb.AppendLine(StInvestigateText.Text);
                sb.AppendLine();
            }

            sb.AppendLine("--- METODOLOGIA (para a IA entender os números) ---");
            sb.AppendLine("  Atividade = tempo ocupado do disco por segundo (latência × operações/s), o mesmo '% Active Time' do Windows.");
            sb.AppendLine("  100% com poucos MB/s indica fila/latência (dispositivo saturado), não vazão.");
            sb.AppendLine("  E/S por processo vem dos contadores do kernel (IO_COUNTERS) do snapshot de 1 s do TMOG.");
            sb.AppendLine("  O teste de impacto suspende o processo de verdade e compara as médias antes/depois.");
            sb.AppendLine("  Processos do núcleo do Windows nunca são suspensos/encerrados por este diagnóstico.");
            return sb.ToString();
        }
    }
}
