using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KitLugia.Core;
using KitLugia.Core.TaskManager;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
// O projeto referencia WinForms além do WPF — sem estes aliases os tipos curtos ficam ambíguos.
using Point = System.Windows.Point;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle = System.Windows.Shapes.Rectangle;
using Line = System.Windows.Shapes.Line;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: aba RESUMO (paridade com o Summary do TMOG) + aba CONEXÕES.
    //
    //  O Resumo é a tela de abertura: vitais (CPU/relógio/temp/GPU/potência),
    //  gráfico de CPU com uso + kernel + temperatura, CPU POR NÚCLEO, memória
    //  detalhada, TOP processos por CPU e a faixa inferior de subsistemas.
    //  Todos os números vêm do tick de 1s já existente (_graphTimer) e das
    //  métricas nativas do Core — nenhum PerformanceCounter novo.
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        // ── Histórico de 60 amostras (1/s) por série ──
        private readonly Queue<float> _sumCpuHist = new(61);
        private readonly Queue<float> _sumKernelHist = new(61);
        private readonly Queue<float> _sumTempHist = new(61);
        private readonly Queue<float> _sumMemHist = new(61);
        private readonly Queue<float> _sumMemBytesHist = new(61); // GB em uso (min/max do gráfico, paridade TMOG)
        private readonly Queue<float> _sumNetHist = new(61);
        private readonly Queue<float> _sumDiskHist = new(61);
        private readonly Queue<float> _sumGpuHist = new(61);
        private readonly Queue<float> _sumPowerHist = new(61);

        private const double BarMaxWidth = 214;   // largura útil das barras dos vitais

        private sealed class CoreBar
        {
            public Border Fill = null!;
            public TextBlock Pct = null!;
            public double TrackHeight = 34;
        }

        private readonly List<CoreBar> _coreBars = new();
        private bool _summaryBuilt;

        /// <summary>Linha do grid "Top processos por CPU" — INPC para atualizar no lugar (sem piscar).</summary>
        private sealed class TopProcRow : INotifyPropertyChanged
        {
            private string _name = "", _cpu = "", _ram = "", _gpu = "—";
            private ImageSource? _icon;
            public int Pid { get; set; }
            public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
            public string Cpu { get => _cpu; set { _cpu = value; Raise(nameof(Cpu)); } }
            public string RamMB { get => _ram; set { _ram = value; Raise(nameof(RamMB)); } }
            public string Gpu { get => _gpu; set { _gpu = value; Raise(nameof(Gpu)); } }

            // Valores NUMÉRICOS (o histórico e o pin precisam da métrica, não do texto).
            public double CpuValue { get; set; }
            public double RamValue { get; set; }

            // Metadados do cartão de detalhe (o clique simples precisa mostrar ALGO útil).
            public string UserName { get; set; } = "";
            public string Status { get; set; } = "";
            public string Path { get; set; } = "";
            public string Disk { get; set; } = "";
            public string Network { get; set; } = "";
            public int GroupCount { get; set; } = 1;

            private bool _pinned;
            /// <summary>"Congelado" na lista pelo usuário: continua no topo mesmo caindo do Top 14.</summary>
            public bool Pinned { get => _pinned; set { if (_pinned == value) return; _pinned = value; Raise(nameof(Pinned)); Raise(nameof(PinIcon)); } }
            public string PinIcon => Pinned ? "📌" : "";

            /// <summary>Ícone real do executável (mesmo cache usado na aba Processos).</summary>
            public ImageSource? Icon
            {
                get => _icon;
                set { _icon = value; Raise(nameof(Icon)); Raise(nameof(IconVisibility)); }
            }
            public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly ObservableCollection<TopProcRow> _topProcRows = new();

        /// <summary>
        /// PID selecionado no Top do Resumo. Sobrevive a reconstrucao da lista: sem ele o
        /// cartao do processo fecha sozinho a cada mudanca de ordem (1x por segundo).
        /// </summary>
        private int _sumSelectedPid;

        /// <summary>
        /// Ajusta a largura de uma coluna proporcional (GridLength com pesos).
        /// Usado na barra de composição da RAM (em uso / em cache / livre).
        /// </summary>
        private static void SetStarWidth(System.Windows.Controls.ColumnDefinition col, double weight)
        {
            try { col.Width = new GridLength(Math.Max(0, weight), GridUnitType.Star); } catch { }
        }

        // ══════════════════════════════════════════════
        //  CONSTRUÇÃO / ATUALIZAÇÃO DO RESUMO
        // ══════════════════════════════════════════════
        private void EnsureSummaryBuilt()
        {
            if (_summaryBuilt) return;
            _summaryBuilt = true;
            try
            {
                DgSumTopCpu.ItemsSource = _topProcRows;
                int cores = Environment.ProcessorCount;
                TxtSumCpuCores.Text = $"{cores} processadores lógicos";
                BuildCoreBars(cores);
            }
            catch { }
        }

        /// <summary>
        /// Cria as barras verticais de cada núcleo lógico (padrão "CPU Expandida" do TMOG).
        /// Feito uma vez; o tick só ajusta altura/cor/texto.
        /// </summary>
        private void BuildCoreBars(int cores)
        {
            if (_coreBars.Count == cores) return;
            try
            {
                PnlCores.Children.Clear();
                _coreBars.Clear();
                var trackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15)));
                for (int i = 0; i < cores; i++)
                {
                    var col = new StackPanel { Orientation = Orientation.Vertical, Width = 26, Margin = new Thickness(0, 0, 4, 0) };
                    var track = new Border
                    {
                        Height = 34,
                        Width = 22,
                        Background = trackBrush,
                        CornerRadius = new CornerRadius(3),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };
                    var fill = new Border
                    {
                        Height = 1,
                        Background = GetHeatColor(0, 60, 85),
                        CornerRadius = new CornerRadius(3),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };
                    track.Child = fill;
                    var pct = new TextBlock
                    {
                        Text = "0",
                        FontSize = 9,
                        Foreground = Brushes.Gray,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0),
                    };
                    var idx = new TextBlock
                    {
                        Text = i.ToString(),
                        FontSize = 8,
                        Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66))),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    };
                    track.ToolTip = $"Núcleo lógico {i}";
                    col.Children.Add(track);
                    col.Children.Add(pct);
                    col.Children.Add(idx);
                    PnlCores.Children.Add(col);
                    _coreBars.Add(new CoreBar { Fill = fill, Pct = pct });
                }
            }
            catch { }
        }

        private static void Enqueue(Queue<float> q, float value, int max = 60)
        {
            q.Enqueue(value);
            while (q.Count > max) q.Dequeue();
        }

        private static void SetVital(TextBlock txt, Border bar, string text, double fraction, System.Windows.Media.Brush color)
        {
            try
            {
                // Guardas anti-pisca: reescrever o mesmo texto/largura a cada tick
                // força re-render do WPF mesmo com o dado parado.
                if (!string.Equals(txt.Text, text, StringComparison.Ordinal)) txt.Text = text;
                if (!Equals(txt.Foreground, color)) txt.Foreground = color;
                double w = Math.Max(0, Math.Min(1, fraction)) * BarMaxWidth;
                if (Math.Abs(bar.Width - w) > 0.5) bar.Width = w;
                if (!Equals(bar.Background, color)) bar.Background = color;
            }
            catch { }
        }

        private static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("F1");
        private static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("F0");

        /// <summary>
        /// Traduz o gráfico de CPU numa frase: O QUE está acontecendo, não o número.
        /// Era a queixa "o gráfico central não ajuda em muita coisa" — sem uma leitura, três
        /// linhas coloridas não dizem se o problema é do kernel (driver) ou de um programa.
        /// </summary>
        private static string BuildCpuVerdict(float cpuNow, double kernel, Queue<float> hist, float temp)
        {
            int n = hist?.Count ?? 0;
            double avg = 0, peak = 0;
            if (n > 0)
            {
                foreach (var v in hist) { avg += v; if (v > peak) peak = v; }
                avg /= n;
            }

            // Kernel alto = tempo preso no driver; é a pista mais valiosa do gráfico.
            string causa;
            if (kernel >= 25)
                causa = $" uso de Kernel em {kernel:F0}% — o gargalo está em um DRIVER (clique no gráfico para ver qual).";
            else if (cpuNow >= 85)
                causa = " CPU no limite — algum programa está consumindo tudo (o Top ao lado mostra quem).";
            else if (cpuNow >= 50)
                causa = " CPU ocupada, mas nada saturado — uso normal com várias abas abertas.";
            else if (cpuNow <= 10)
                causa = " CPU ociosa — se algo estiver lento, o problema NÃO é a CPU.";
            else
                causa = " CPU em uso moderado.";

            string janela = n < 2
                ? ""
                : $" Últimos {n}s: média {avg:F0}%, pico {peak:F0}%.";
            string calor = temp >= 82 ? $" CPU a {temp:F0} °C — quite quente." : "";
            return causa + janela + calor;
        }

        /// <summary>Tick do Resumo (chamado do mesmo timer de 1s dos gráficos).</summary>
        private void UpdateSummaryTick()
        {
            if (TabSummary.Visibility != Visibility.Visible) return;
            EnsureSummaryBuilt();

            float cpu = _lastCpuPct;
            float mem = _lastMemPct;
            float gpu = _lastGpuPct;
            float temp = _lastTempC >= 0 ? (float)_lastTempC : -1f;
            float power = _lastPowerW >= 0 ? (float)_lastPowerW : -1f;
            double freq = _lastFreqMhz;
            double kernel = _lastKernelPct;
            float kernelF = kernel > 0 ? (float)kernel : 0f;

            Enqueue(_sumCpuHist, cpu);
            Enqueue(_sumKernelHist, kernelF);
            if (temp >= 0) Enqueue(_sumTempHist, temp);
            Enqueue(_sumMemHist, mem);
            Enqueue(_sumGpuHist, gpu >= 0 ? gpu : 0f);
            if (power >= 0) Enqueue(_sumPowerHist, power);

            // ── Gráfico da CPU: 3 séries no mesmo canvas (a leitura do TMOG) ──
            // Redesenho por amostra: o canvas é redesenhado no tick (~1s), como era antes.
            var cpuSeries = new List<(Queue<float> data, Color color)>
            {
                (_sumCpuHist, Color.FromRgb(0x4C, 0xAF, 0x50)),
                (_sumKernelHist, Color.FromRgb(0xF4, 0x43, 0x36)),
            };
            if (_sumTempHist.Count > 1) cpuSeries.Add((_sumTempHist, Color.FromRgb(0xFF, 0x98, 0x00)));
            DrawMultiSeriesChart(SumCpuCanvas, 100f, true, cpuSeries);

            SetText(TxtSumCpuBig, $"{cpu:F0}%", GetHeatColor(cpu, 80, 95));
            SetText(TxtSumCpuSub, kernelF > 0 ? $"Kernel {kernelF:F0}%" : "");
            // LEITURA DO GRÁFICO em português: antes o rodapé só trazia frequência/núcleos/
            // uptime — números que não respondem "o gráfico está dizendo o quê?".
            SetText(TxtSumCpuVerdict, BuildCpuVerdict(cpu, kernel, _sumCpuHist, temp), GetHeatColor(cpu, 80, 95));
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            // Uptime em minutos: sem isso o rodapé mudava a cada segundo e piscava.
            SetText(TxtSumCpuFooter,
                $"Frequência {(freq > 0 ? (freq / 1000.0).ToString("F2") + " GHz" : "—")} · " +
                $"{Environment.ProcessorCount} núcleos · uptime do sistema {(int)uptime.TotalDays}d {uptime.Hours:00}h {uptime.Minutes:00}m");
            SetText(TxtSumSubtitle, $"Atualizando a cada {_refreshSeconds}s · métricas nativas (sem admin)");

            // ── Vitais ──
            SetVital(TxtSumCpu, BarSumCpu, $"{cpu:F0}%", cpu / 100.0, GetHeatColor(cpu, 80, 95));
            SetVital(TxtSumClock, BarSumClock, freq > 0 ? $"{(freq / 1000.0):F2} GHz" : "—",
                freq > 0 ? freq / 5500.0 : 0, Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0x57, 0xC2))));
            SetVital(TxtSumTemp, BarSumTemp, temp >= 0 ? $"{temp:F0} °C" : "—",
                temp >= 0 ? temp / 100.0 : 0, temp >= 0 ? GetHeatColor(temp, 75, 90) : Brushes.Gray);
            SetVital(TxtSumGpu, BarSumGpu, gpu >= 0 ? $"{gpu:F0}%" : "N/A",
                gpu >= 0 ? gpu / 100.0 : 0, gpu >= 0 ? GetHeatColor(gpu, 70, 90) : Brushes.Gray);
            SetVital(TxtSumPower, BarSumPower, power >= 0 ? $"{power:F1} W" : "—",
                power >= 0 ? power / 150.0 : 0, power >= 0 ? GetHeatColor(power, 45, 80) : Brushes.Gray);

            // ── CPU por núcleo ──
            try
            {
                double[] cores = NativeMetricsHelper.GetPerCoreCpuPercent();
                if (cores.Length > 0)
                {
                    BuildCoreBars(cores.Length);
                    SetText(TxtSumCoresInfo, $"{cores.Length} processadores lógicos");
                    for (int i = 0; i < _coreBars.Count && i < cores.Length; i++)
                    {
                        float pct = (float)cores[i];
                        var bar = _coreBars[i];
                        // Altura/cor/texto só quando mudam (tick ~1s sem re-render à toa).
                        var brush = GetHeatColor(pct, 60, 85);
                        double wantH = Math.Max(1, bar.TrackHeight * Math.Min(1, pct / 100.0));
                        if (Math.Abs(bar.Fill.Height - wantH) > 0.5) bar.Fill.Height = wantH;
                        if (!Equals(bar.Fill.Background, brush)) bar.Fill.Background = brush;
                        SetText(bar.Pct, pct >= 100 ? "100" : pct.ToString("F0"), brush);
                    }
                }
            }
            catch { }

            // ── Memória ──
            try
            {
                var md = NativeMetricsHelper.GetMemoryDetails();
                if (md.TotalBytes > 0)
                {
                    // "Swap" = uso REAL do arquivo de paginação (WMI Win32_PageFileUsage,
                    // consultado em background e cacheado). Se o Windows ainda não respondeu,
                    // mostramos a medida HONESTA equivalente (comprometido além da RAM física)
                    // em vez de inventar um número de pagefile.
                    ulong physInUse = md.TotalBytes > md.AvailableBytes ? md.TotalBytes - md.AvailableBytes : 0;
                    ulong beyondRam = md.CommitTotalBytes > physInUse ? md.CommitTotalBytes - physInUse : 0;
                    var pf = NativeMetricsHelper.GetPageFileUsageNonBlocking();

                    SetText(TxtSumMemPctBig, $"{md.LoadPercent:F1}%");
                    Enqueue(_sumMemBytesHist, (float)(md.UsedBytes / 1073741824.0));
                    try { SetText(TxtSumMemMax, $"{_sumMemBytesHist.Max():F1} GB"); SetText(TxtSumMemMin, $"{_sumMemBytesHist.Min():F1} GB"); } catch { }
                    SetText(TxtSumMemUsed, $"Em uso: {Gb(md.UsedBytes)} GB de {Gb(md.TotalBytes)} GB ({md.LoadPercent:F0}%)");
                    SetText(TxtSumMemAvail, $"Disponível: {Gb(md.AvailableBytes)} GB");
                    SetText(TxtSumMemCache, $"Em cache: {Gb(md.CachedBytes)} GB");
                    SetText(TxtSumMemSwap, pf != null && pf.Valid
                        ? $"Swap (arquivo de paginação em uso): {Gb((ulong)(pf.CurrentUsageMB * 1048576))} GB de {Gb((ulong)(pf.AllocatedMB * 1048576))} GB alocados"
                        : $"Além da RAM: {Gb(beyondRam)} GB comprometidos (medindo o swap…)");
                    SetText(TxtSumMemCommit, $"Comprometida: {Gb(md.CommitTotalBytes)} / {Gb(md.CommitLimitBytes)} GB");
                    SetText(TxtSumMemPools, md.Native
                        ? $"Pool paginado: {Mb(md.PagedPoolBytes)} MB · não paginado: {Mb(md.NonPagedPoolBytes)} MB"
                        : "Pool: indisponível neste sistema");
                    SetText(TxtSumMemMeta, md.Native
                        ? $"{md.HandleCount:N0} handles · {md.ThreadCount:N0} threads · {md.ProcessCount:N0} processos"
                        : "");

                    // Barra de composição: EM USO (roxo) | EM CACHE (azul) | LIVRE
                    try
                    {
                        double total = md.TotalBytes;
                        double usedFrac = Math.Max(0, Math.Min(1, md.UsedBytes / total));
                        double cacheFrac = Math.Max(0, Math.Min(1 - usedFrac, md.CachedBytes / total));
                        SetStarWidth(ColMemUsed, usedFrac * 1000);
                        SetStarWidth(ColMemCache, cacheFrac * 1000);
                    }
                    catch { }

                    double memTotalGb = md.TotalBytes / 1073741824.0;
                    SetText(TxtSumMemTip,
                        $"Agora: {md.LoadPercent:F1}% usado · {Gb(md.UsedBytes)} GB em uso · {Gb(md.AvailableBytes)} GB disponível · " +
                        $"pico desta sessão: {Gb(md.CommitPeakBytes)} GB comprometidos." + Environment.NewLine +
                        (md.LoadPercent >= 85
                            ? "ESTADO: RAM quase cheia. É aqui que o Windows começa a buscar no DISCO (paginação) e tudo engasga — veja a aba Latência."
                            : md.LoadPercent >= 70
                                ? "ESTADO: uso alto, mas ainda dentro do normal para muitas abas/jogos abertos. Observe se o disco começa a trabalhar sozinho."
                                : "ESTADO: tranquilo. Há RAM de sobra para o que está rodando agora."));
                }
                DrawMultiSeriesChart(SumMemCanvas, 100f, true, new List<(Queue<float>, Color)> { (_sumMemHist, Color.FromRgb(0xB3, 0x6A, 0xE2)) });
            }
            catch { }

            // ── Faixa inferior: rede / disco / GPU / energia ──
            try
            {
                var net = NativeMetricsHelper.GetNetworkRates();
                double netTotal = net.Valid ? net.InBytesPerSec + net.OutBytesPerSec : 0;
                Enqueue(_sumNetHist, (float)(netTotal / 1048576.0));
                if (net.Valid)
                {
                    SetText(TxtSumNet, FormatBytesSpeed(netTotal));
                    SetText(TxtSumNetDetail, $"↓ {FormatBytesSpeed(net.InBytesPerSec)} · ↑ {FormatBytesSpeed(net.OutBytesPerSec)}");
                    if (!Equals(TxtSumNet.ToolTip, net.PrimaryName)) TxtSumNet.ToolTip = net.PrimaryName;
                }
                else
                {
                    SetText(TxtSumNet, "—");
                    SetText(TxtSumNetDetail, $"lendo {net.Interfaces} interfaces…");
                }
                DrawMultiSeriesChart(SumNetCanvas, 0f, true, new List<(Queue<float>, Color)> { (_sumNetHist, Color.FromRgb(0x9C, 0x27, 0xB0)) });
            }
            catch { }

            try
            {
                float diskMB = _lastDiskReadMBps + _lastDiskWriteMBps;
                Enqueue(_sumDiskHist, diskMB);
                SetText(TxtSumDisk, FormatBytesSpeed(diskMB * 1048576.0));
                SetText(TxtSumDiskDetail, $"R {FormatBytesSpeed(_lastDiskReadMBps * 1048576.0)} · W {FormatBytesSpeed(_lastDiskWriteMBps * 1048576.0)}");
                DrawMultiSeriesChart(SumDiskCanvas, 0f, true, new List<(Queue<float>, Color)> { (_sumDiskHist, Color.FromRgb(0xFF, 0x98, 0x00)) });
            }
            catch { }

            try
            {
                SetText(TxtSumGpuTile, gpu >= 0 ? $"{gpu:F0}%" : "N/A");
                string topGpu;
                lock (_lock) topGpu = _allRows.Where(r => r.GpuValue > 0).OrderByDescending(r => r.GpuValue).FirstOrDefault()?.Name ?? "";
                SetText(TxtSumGpuDetail, topGpu.Length > 0 ? $"maior: {topGpu}" : "sem uso de GPU");
                DrawMultiSeriesChart(SumGpuCanvas, 100f, true, new List<(Queue<float>, Color)> { (_sumGpuHist, Color.FromRgb(0x21, 0x96, 0xF3)) });
            }
            catch { }

            try
            {
                SetText(TxtSumPowerTile, power >= 0 ? $"{power:F1} W" : "—");
                SetText(TxtSumPowerDetail, power >= 0 ? "pacote da CPU (PDH)" : "sensor indisponível");
                DrawMultiSeriesChart(SumPowerCanvas, 0f, true, new List<(Queue<float>, Color)> { (_sumPowerHist, Color.FromRgb(0xF4, 0x43, 0x36)) });
            }
            catch { }

            // Histórico: 1 amostra por tick (com QUEM estava usando a CPU) e, se o painel
            // estiver aberto, ele acompanha ao vivo.
            double memGb = _sumMemBytesHist.Count > 0 ? _sumMemBytesHist.Last() : 0;
            RecordHistorySample(cpu, kernelF, memGb);
            if (_histOpen) DrawHistory();
        }

        /// <summary>PIDs fixados pelo usuário ("congelar na lista"), na ordem em que foram fixados.</summary>
        private readonly List<int> _sumPinned = new();
        /// <summary>Menu de contexto aberto: a lista NÃO é reordenada/atualizada embaixo do mouse.</summary>
        private bool _sumMenuOpen;

        /// <summary>Lista "Top processos por CPU" (merge in-place para não perder a seleção).</summary>
        private void UpdateSummaryTopCpu()
        {
            try
            {
                if (TabSummary.Visibility != Visibility.Visible) return;
                // O menu de contexto está aberto: CONGELA a lista. Sem isto o refresh de 1s
                // reordenava/removia a linha em que o usuário ia clicar (era o pedido:
                // "congelar o processo na lista para ele não sumir e poder modificar dali").
                if (_sumMenuOpen) return;
                List<ProcessRow> rows;
                lock (_lock) rows = _allRows;
                if (rows == null || rows.Count == 0) return;

                var vivos = rows.OrderByDescending(r => r.CpuValue).ThenByDescending(r => r.RamValue).ToList();
                var top = new List<ProcessRow>(14);
                // 1) FIXADOS primeiro (mesmo que tenham saído do Top 14).
                foreach (int pid in _sumPinned.ToList())
                {
                    var pin = vivos.FirstOrDefault(r => r.Pid == pid);
                    if (pin == null) continue;              // processo morreu: libera o pin
                    if (!top.Contains(pin)) top.Add(pin);
                }
                // 2) Completa com o Top ao vivo (sem duplicar quem já está fixado).
                foreach (var r in vivos)
                {
                    if (top.Count >= 14) break;
                    if (!top.Contains(r)) top.Add(r);
                }
                // Faxina: PID fixado que sumiu do sistema sai da lista de pins.
                for (int i = _sumPinned.Count - 1; i >= 0; i--)
                    if (vivos.All(r => r.Pid != _sumPinned[i])) _sumPinned.RemoveAt(i);

                EnsureSummaryBuilt();
                // Anti-pisca: _topProcRows[i] = novo dispara Replace (recria a linha e o
                // template pisca). Se a ordem de PIDs é a mesma, atualiza os CAMPOS no
                // lugar (INPC atualiza só o texto); só reconstrói quando entrou/saiu PID.
                bool sameOrder = _topProcRows.Count == top.Count;
                if (sameOrder)
                {
                    for (int i = 0; i < top.Count; i++)
                        if (_topProcRows[i].Pid != top[i].Pid) { sameOrder = false; break; }
                }
                if (sameOrder)
                {
                    for (int i = 0; i < top.Count; i++)
                    {
                        var dst = _topProcRows[i];
                        var src = top[i];
                        dst.Name = src.Name;
                        dst.Cpu = src.Cpu;
                        dst.CpuValue = src.CpuValue;
                        dst.RamMB = src.RamMB;
                        dst.RamValue = src.RamValue;
                        dst.Gpu = src.Gpu;
                        dst.Pinned = _sumPinned.Contains(src.Pid);
                        dst.UserName = src.UserName; dst.Status = src.Status;
                        dst.Path = src.Path; dst.Disk = src.Disk; dst.Network = src.Network;
                        // Ícone chega depois (carregado em background) — não perde a chance
                        if (dst.Icon == null && src.ProcessIcon != null) dst.Icon = src.ProcessIcon;
                    }
                }
                else
                {
                    _topProcRows.Clear();
                    foreach (var src in top)
                        _topProcRows.Add(new TopProcRow
                        {
                            Pid = src.Pid, Name = src.Name, Cpu = src.Cpu, CpuValue = src.CpuValue,
                            RamMB = src.RamMB, RamValue = src.RamValue, Gpu = src.Gpu,
                            UserName = src.UserName, Status = src.Status, Path = src.Path,
                            Disk = src.Disk, Network = src.Network,
                            Icon = src.ProcessIcon, Pinned = _sumPinned.Contains(src.Pid)
                        });
                }

                SetText(TxtSumStatus, $"{rows.Count} processos · top {top.Count} por CPU" +
                    (_sumPinned.Count > 0 ? $"· {_sumPinned.Count} fixado(s) 📌" : ""));

                // Reaplica a selecao depois da reconstrucao da lista (Clear + Add zera a
                // SelectedItem): e o que mantem o cartao aberto enquanto o Top muda de ordem.
                RestoreSummarySelection();
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────────────
        //  CLIQUE SIMPLES NA LISTA DO TOP — abre o CARTÃO do processo.
        //  Antes só havia duplo clique e botão direito: clicar numa linha não fazia
        //  NADA e a lista parecia morta. Aqui o clique mostra quem é o processo
        //  (usuário, estado, caminho, disco, rede) e habilita as ações.
        // ────────────────────────────────────────────────────────────────────

        private void DgSumTopCpu_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                var t = DgSumTopCpu.SelectedItem as TopProcRow;
                if (t == null)
                {
                    // O refresh de 1 s reconstroi a lista (Clear + Add) sempre que a ORDEM
                    // muda — com CPU viva isso acontece o tempo todo e a selecao (logo, o
                    // cartao) sumia. Nao recolapsa aqui: o PID selecionado e reescolhido em
                    // UpdateSummaryTopCpu; so recolapsa se o processo tiver mesmo sumido.
                    if (Volatile.Read(ref _sumSelectedPid) > 0) return;
                    SumProcCard.Visibility = Visibility.Collapsed;
                    TxtSumTopHint.Visibility = Visibility.Visible;
                    return;
                }
                Volatile.Write(ref _sumSelectedPid, t.Pid);
                var live = SumRowByPid(t.Pid);
                string user = !string.IsNullOrEmpty(live?.UserName) ? live!.UserName : (string.IsNullOrEmpty(t.UserName) ? "—" : t.UserName);
                string status = !string.IsNullOrEmpty(live?.Status) ? live!.Status : t.Status;
                string path = live?.Path ?? t.Path;
                int instances = CountInstances(live);
                string group = instances > 1 ? $" · {instances} instâncias" : "";

                TxtSumProcTitle.Text = $"{t.Name} (PID {t.Pid}){group}" + (t.Pinned ? "  📌" : "");
                TxtSumProcMeta.Text = $"CPU {t.Cpu}   RAM {t.RamMB}   GPU {t.Gpu}   ·   usuário: {user}   ·   {status}";
                TxtSumProcPath.Text = string.IsNullOrEmpty(path)
                    ? "caminho indisponível (processo protegido ou encerrado)"
                    : path;
                TxtSumProcPath.ToolTip = path;
                BtnSumFix.Content = t.Pinned ? "📌 Soltar" : "📌 Congelar";
                SumProcCard.Visibility = Visibility.Visible;
                TxtSumTopHint.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        /// <summary>Quantas instâncias do mesmo nome existem agora (o Top agrupa por nome).</summary>
        private int CountInstances(ProcessRow? groupRow)
        {
            if (groupRow == null) return 1;
            try
            {
                string name = groupRow.Name;
                lock (_lock) return Math.Max(1, _allRows.Count(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return 1; }
        }

        /// <summary>Processo selecionado no Top, resolvido na fonte viva (_allRows).</summary>
        private ProcessRow? SumSelectedLive() =>
            DgSumTopCpu.SelectedItem is TopProcRow t ? SumRowByPid(t.Pid) : null;

        private void BtnSumGoProcess_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SumRowByPid((DgSumTopCpu.SelectedItem as TopProcRow)?.Pid ?? -1) is not ProcessRow target) return;
                SwitchTab(BtnTabProcesses, new RoutedEventArgs());
                DgProcesses.SelectedItem = target;
                DgProcesses.ScrollIntoView(target);
                DgProcesses.Focus();
            }
            catch { }
        }

        private async void BtnSumKillSelected_Click(object sender, RoutedEventArgs e)
        {
            if (SumRowByPid((DgSumTopCpu.SelectedItem as TopProcRow)?.Pid ?? -1) != null) await KillAsync(false);
        }

        private void BtnSumPinSelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DgSumTopCpu.SelectedItem is not TopProcRow t) return;
                if (_sumPinned.Contains(t.Pid)) _sumPinned.Remove(t.Pid);
                else _sumPinned.Add(t.Pid);
                t.Pinned = _sumPinned.Contains(t.Pid);
                UpdateSummaryTopCpu();
                TxtStatus.Text = t.Pinned
                    ? $"📌 {t.Name} fixado: continua na lista mesmo caindo do Top 14."
                    : $"📌 {t.Name} liberado da lista fixa.";
            }
            catch { }
        }

        /// <summary>
        /// Reescolhe o processo do cartao apos a lista ser reconstruida. Se o PID nao esta
        /// mais no Top, o cartao e fechado (o processo morreu ou saiu do ranking).
        /// </summary>
        private void RestoreSummarySelection()
        {
            try
            {
                int pid = Volatile.Read(ref _sumSelectedPid);
                if (pid <= 0) return;
                if (DgSumTopCpu.SelectedItem is TopProcRow cur && cur.Pid == pid) return;

                var again = _topProcRows.FirstOrDefault(r => r.Pid == pid);
                if (again != null)
                {
                    DgSumTopCpu.SelectedItem = again;
                    return;
                }

                // Saiu do Top: solta o cartao em vez de deixar texto velho na tela.
                Volatile.Write(ref _sumSelectedPid, 0);
                SumProcCard.Visibility = Visibility.Collapsed;
                TxtSumTopHint.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void DgSumTopCpu_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DgSumTopCpu.SelectedItem is not TopProcRow t) return;
                ProcessRow? target;
                lock (_lock) target = _allRows.FirstOrDefault(r => r.Pid == t.Pid);
                SwitchTab(BtnTabProcesses, new RoutedEventArgs());
                if (target != null)
                {
                    DgProcesses.SelectedItem = target;
                    DgProcesses.ScrollIntoView(target);
                    DgProcesses.Focus();
                }
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────────────
        //  MENU DE CONTEXTO do TOP (congela a lista + age no processo escolhido)
        // ────────────────────────────────────────────────────────────────────

        private static DataGridRow? RowUnderMouse(object? src)
        {
            var d = src as DependencyObject;
            while (d != null && d is not DataGridRow) d = VisualTreeHelper.GetParent(d);
            return d as DataGridRow;
        }

        private void DgSumTopCpu_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                // O botão direito NÃO seleciona a linha no WPF: sem isto o menu agia no
                // processo que estava selecionado antes, e não no que o usuário clicou.
                if (RowUnderMouse(e.OriginalSource) is DataGridRow rw && rw.Item is TopProcRow alvo)
                    DgSumTopCpu.SelectedItem = alvo;

                _sumMenuOpen = true;   // congela a lista enquanto o menu estiver aberto

                if (MenuSumPin != null && DgSumTopCpu.SelectedItem is TopProcRow sel)
                    MenuSumPin.Header = _sumPinned.Contains(sel.Pid)
                        ? "Liberar da lista (desafixar)"
                        : "Congelar na lista (fixar)";
            }
            catch { }
        }

        private void DgSumTopCpuMenu_Closed(object sender, RoutedEventArgs e)
        {
            _sumMenuOpen = false;
            UpdateSummaryTopCpu();   // descongela já com o estado atual
        }

        /// <summary>Acha a linha correspondente na aba Processos (mesma fonte de dados).</summary>
        private ProcessRow? SumRowByPid(int pid)
        {
            lock (_lock) return _allRows.FirstOrDefault(r => r.Pid == pid);
        }

        /// <summary>Seleciona a linha na aba Processos e devolve o ProcessRow (reuso das ações já existentes).</summary>
        private ProcessRow? SumPrepareAction()
        {
            if (DgSumTopCpu.SelectedItem is not TopProcRow t) return null;
            var row = SumRowByPid(t.Pid);
            if (row == null)
            {
                TxtStatus.Text = $"{t.Name} (PID {t.Pid}) não está mais em execução.";
                return null;
            }
            try { DgProcesses.SelectedItem = row; DgProcesses.ScrollIntoView(row); } catch { }
            return row;
        }

        private void MenuSumOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SumPrepareAction() is not ProcessRow row) return;
                SwitchTab(BtnTabProcesses, new RoutedEventArgs());
                DgProcesses.SelectedItem = row;
                DgProcesses.ScrollIntoView(row);
                DgProcesses.Focus();
            }
            catch { }
        }

        private void MenuSumPin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DgSumTopCpu.SelectedItem is not TopProcRow t) return;
                if (_sumPinned.Contains(t.Pid)) _sumPinned.Remove(t.Pid);
                else _sumPinned.Add(t.Pid);
                TxtStatus.Text = _sumPinned.Contains(t.Pid)
                    ? $"📌 {t.Name} fixado: continua na lista mesmo caindo do Top 14."
                    : $"📌 {t.Name} liberado da lista fixa.";
                UpdateSummaryTopCpu();
            }
            catch { }
        }

        private async void MenuSumKill_Click(object sender, RoutedEventArgs e)
        {
            if (SumPrepareAction() != null) await KillAsync(false);
        }

        private void MenuSumSuspend_Click(object sender, RoutedEventArgs e)
        {
            if (SumPrepareAction() != null) SuspendResume(true);
        }

        private void MenuSumResume_Click(object sender, RoutedEventArgs e)
        {
            if (SumPrepareAction() != null) SuspendResume(false);
        }

        private void MenuSumPriority_Click(object sender, RoutedEventArgs e)
        {
            if (SumPrepareAction() != null) MenuPriority_Click(sender, e);
        }

        private void MenuSumClearMem_Click(object sender, RoutedEventArgs e)
        {
            if (SumPrepareAction() != null) BtnClearMemory_Click(sender, e);
        }

        private void MenuSumCopyPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = SumPrepareAction();
                if (row == null) return;
                if (string.IsNullOrEmpty(row.Path)) { TxtStatus.Text = "Sem caminho para copiar (processo do sistema)."; return; }
                Clipboard.SetText(row.Path);
                TxtStatus.Text = $"📋 Caminho copiado: {row.Path}";
            }
            catch { }
        }

        private void MenuSumCopyPid_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DgSumTopCpu.SelectedItem is not TopProcRow t) return;
                Clipboard.SetText(t.Pid.ToString());
                TxtStatus.Text = $"📋 PID copiado: {t.Pid}";
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────────────
        //  HISTÓRICO ("Click for History" do TMOG): linha do tempo da CPU com
        //  QUEM estava usando em cada instante.
        // ────────────────────────────────────────────────────────────────────

        private sealed class HistorySample
        {
            public DateTime Time;
            public float Cpu, Kernel;
            public double MemGb;
            public List<(int Pid, string Name, float Cpu, double RamMb)> Top = new();
        }

        private const int HistoryMax = 600;                       // 10 min a 1 amostra/s
        private readonly List<HistorySample> _history = new(HistoryMax + 1);
        private int _histSel = -1;                                // índice selecionado (-1 = ao vivo)
        private bool _histOpen;

        /// <summary>1 amostra por tick: CPU/kernel/RAM + os processos que estavam consumindo CPU.</summary>
        private void RecordHistorySample(float cpu, float kernel, double memGb)
        {
            try
            {
                var s = new HistorySample { Time = DateTime.Now, Cpu = cpu, Kernel = kernel, MemGb = memGb };
                List<ProcessRow> rows;
                lock (_lock) rows = _allRows;
                if (rows != null && rows.Count > 0)
                {
                    foreach (var r in rows.OrderByDescending(x => x.CpuValue).Take(6))
                    {
                        if (r.CpuValue <= 0.05) break;   // ninguém consumindo: não inventa linha
                        s.Top.Add((r.Pid, r.Name, (float)r.CpuValue, r.RamValue));
                    }
                }
                _history.Add(s);
                bool removeu = false;
                if (_history.Count > HistoryMax) { _history.RemoveAt(0); removeu = true; }
                // A amostra selecionada "anda" junto com o ring buffer.
                if (_histSel >= 0 && removeu) _histSel = Math.Max(0, _histSel - 1);
            }
            catch { }
        }

        private void SumCpuCanvas_Click(object sender, MouseButtonEventArgs e)
        {
            _histOpen = true;
            _histSel = -1;
            if (PnlCpuHistory != null) PnlCpuHistory.Visibility = Visibility.Visible;
            // O painel ocupa a area do grafico: o numero grande ao vivo tem de sair de cena,
            // senao fica CORTADO atras da borda (aparecia so um pedaco do "5%").
            if (TxtSumCpuBig != null) TxtSumCpuBig.Visibility = Visibility.Collapsed;
            if (TxtSumCpuSub != null) TxtSumCpuSub.Visibility = Visibility.Collapsed;
            DrawHistory();
        }

        private void BtnHistClose_Click(object sender, RoutedEventArgs e)
        {
            _histOpen = false;
            _histSel = -1;
            if (PnlCpuHistory != null) PnlCpuHistory.Visibility = Visibility.Collapsed;
            if (TxtSumCpuBig != null) TxtSumCpuBig.Visibility = Visibility.Visible;
            if (TxtSumCpuSub != null) TxtSumCpuSub.Visibility = Visibility.Visible;
        }

        private void BtnHistNow_Click(object sender, RoutedEventArgs e)
        {
            _histSel = -1;
            DrawHistory();
        }

        private void BtnHistClear_Click(object sender, RoutedEventArgs e)
        {
            _history.Clear();
            _histSel = -1;
            DrawHistory();
            TxtStatus.Text = "🧹 Histórico zerado.";
        }

        private void HistCpuCanvas_Click(object sender, MouseButtonEventArgs e)
        {
            SelecionarAmostraPeloX(e.GetPosition(HistCpuCanvas).X);
        }

        private void HistCpuCanvas_Move(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) SelecionarAmostraPeloX(e.GetPosition(HistCpuCanvas).X);
        }

        private void SelecionarAmostraPeloX(double x)
        {
            try
            {
                if (_history.Count == 0 || HistCpuCanvas == null) return;
                double w = HistCpuCanvas.ActualWidth > 1 ? HistCpuCanvas.ActualWidth : HistCpuCanvas.Width;
                if (w <= 1) return;
                double barW = Math.Max(1.0, w / HistoryMax);
                double x0 = Math.Max(0, w - _history.Count * barW);   // amostras alinhadas à direita
                int idx = (int)Math.Floor((x - x0) / barW);
                if (idx < 0) idx = 0;
                if (idx >= _history.Count) idx = _history.Count - 1;
                _histSel = idx;
                DrawHistory();
            }
            catch { }
        }

        /// <summary>Desenha a linha do tempo (barras = CPU por amostra, linha = kernel) e o "quem" do instante.</summary>
        private void DrawHistory()
        {
            try
            {
                if (HistCpuCanvas == null) return;
                var cv = HistCpuCanvas;
                cv.Children.Clear();
                double w = cv.ActualWidth > 1 ? cv.ActualWidth : 400;
                double h = cv.ActualHeight > 1 ? cv.ActualHeight : 58;

                if (_history.Count == 0)
                {
                    SetText(TxtHistSel, "sem amostras");
                    SetText(TxtHistWhat, "Deixe o Resumo aberto alguns segundos — cada segundo vira uma barra aqui.");
                    PnlHistTop?.Children.Clear();
                    return;
                }

                double barW = Math.Max(1.0, w / HistoryMax);
                double x0 = Math.Max(0, w - _history.Count * barW);
                double baseY = h - 1;
                var kernelPoints = new PointCollection();

                for (int i = 0; i < _history.Count; i++)
                {
                    var s = _history[i];
                    double px = x0 + i * barW;
                    double frac = Math.Max(0, Math.Min(1, s.Cpu / 100.0));
                    double bh = Math.Max(1, (h - 6) * frac);
                    var bar = new Rectangle
                    {
                        Width = Math.Max(1, barW - 1),
                        Height = bh,
                        Fill = GetHeatColor(s.Cpu, 80, 95),
                        Opacity = i == (_histSel >= 0 ? _histSel : _history.Count - 1) ? 1.0 : 0.75
                    };
                    Canvas.SetLeft(bar, px);
                    Canvas.SetTop(bar, baseY - bh);
                    cv.Children.Add(bar);

                    double kf = Math.Max(0, Math.Min(1, s.Kernel / 100.0));
                    kernelPoints.Add(new Point(px + barW / 2, baseY - (h - 6) * kf));
                }

                if (kernelPoints.Count > 1)
                {
                    var poly = new System.Windows.Shapes.Polyline
                    {
                        Points = kernelPoints,
                        Stroke = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)),
                        StrokeThickness = 1.2,
                        Opacity = 0.9
                    };
                    cv.Children.Add(poly);
                }

                int sel = _histSel >= 0 && _histSel < _history.Count ? _histSel : _history.Count - 1;
                var smp = _history[sel];

                // Marcador do instante selecionado.
                var marker = new Line
                {
                    X1 = x0 + sel * barW + barW / 2, X2 = x0 + sel * barW + barW / 2,
                    Y1 = 0, Y2 = baseY,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
                    StrokeThickness = 1,
                    Opacity = 0.9
                };
                cv.Children.Add(marker);

                SetText(TxtHistSel, (_histSel >= 0 ? smp.Time.ToString("HH:mm:ss") : "ao vivo (mais recente)") +
                    $" · {_history.Count} amostra(s) de {HistoryMax / 60} min");

                SetText(TxtHistWhat, smp.Top.Count == 0
                    ? $"CPU {smp.Cpu:F0}% (kernel {smp.Kernel:F0}%) · RAM {smp.MemGb:F1} GB em uso — nenhum processo consumindo CPU neste instante."
                    : $"CPU {smp.Cpu:F0}% (kernel {smp.Kernel:F0}%) · RAM {smp.MemGb:F1} GB em uso — quem estava usando a CPU:");

                if (PnlHistTop == null) return;
                PnlHistTop.Children.Clear();
                foreach (var t in smp.Top.Take(6))
                {
                    var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    var nome = new TextBlock
                    {
                        Text = t.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
                        ToolTip = $"{t.Name} (PID {t.Pid}) · {t.RamMb:F0} MB de RAM"
                    };
                    var pct = new TextBlock
                    {
                        Text = $"{t.Cpu:F0}%", FontSize = 11, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        Foreground = GetHeatColor(t.Cpu, 80, 95), Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(pct, 1);
                    g.Children.Add(nome);
                    g.Children.Add(pct);
                    PnlHistTop.Children.Add(g);
                }
            }
            catch { }
        }

        private void CmbSummaryInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item || item.Content is not string txt) return;
                if (!int.TryParse(txt.Replace("s", ""), out int sec) || sec <= 0) return;
                _refreshSeconds = sec;
                if (_refreshTimer != null) _refreshTimer.Interval = TimeSpan.FromSeconds(sec);
                if (_graphTimer != null) _graphTimer.Interval = TimeSpan.FromSeconds(sec);
                // Mantém o combo da aba Processos em sincronia
                if (CmbRefreshInterval != null)
                {
                    foreach (ComboBoxItem ci in CmbRefreshInterval.Items)
                        if (ci.Content as string == txt) { CmbRefreshInterval.SelectedItem = ci; break; }
                }
            }
            catch { }
        }

        private void BtnSummaryCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string kernelTxt = TxtSumCpuSub.Text is { Length: > 0 } ks ? ks.Replace("Kernel ", "") : "—";
                string txt =
                    $"KitLugia — Resumo do sistema ({DateTime.Now:dd/MM/yyyy HH:mm:ss}){Environment.NewLine}" +
                    $"CPU: {TxtSumCpu.Text} (kernel {kernelTxt}) · Relógio: {TxtSumClock.Text} · Temp: {TxtSumTemp.Text} · GPU: {TxtSumGpu.Text} · Potência: {TxtSumPower.Text}{Environment.NewLine}" +
                    $"Memória: {TxtSumMemUsed.Text} · {TxtSumMemAvail.Text} · {TxtSumMemCommit.Text} · {TxtSumMemCache.Text}{Environment.NewLine}" +
                    $"Rede: {TxtSumNet.Text} ({TxtSumNetDetail.Text}) · Disco: {TxtSumDisk.Text} ({TxtSumDiskDetail.Text}){Environment.NewLine}" +
                    $"Top processos por CPU: {string.Join(", ", _topProcRows.Take(8).Select(r => $"{r.Name} {r.Cpu}"))}";
                Clipboard.SetText(txt);
                if (TxtStatus != null) TxtStatus.Text = "📋 Resumo copiado.";
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  GRÁFICO MULTI-SÉRIE (uso + kernel + temperatura no mesmo canvas)
        // ══════════════════════════════════════════════
        private void DrawMultiSeriesChart(Canvas canvas, float maxVal, bool fillFirst,
            List<(Queue<float> data, Color color)> series)
        {
            try
            {
                canvas.Children.Clear();
                double w = canvas.ActualWidth > 1 ? canvas.ActualWidth : canvas.Width;
                double h = canvas.ActualHeight > 1 ? canvas.ActualHeight : canvas.Height;
                if (double.IsNaN(w) || double.IsNaN(h) || w <= 1 || h <= 1)
                {
                    if (canvas.Parent is FrameworkElement p && p.ActualWidth > 1)
                    {
                        w = p.ActualWidth - 2;
                        h = p.ActualHeight > 1 ? p.ActualHeight - 2 : 90;
                    }
                    if (w <= 1) return;
                }

                // Grade horizontal (quartos) — dá a leitura de escala do TMOG
                var gridBrush = Freeze(new SolidColorBrush(Color.FromArgb(60, 0x44, 0x44, 0x44)));
                for (int i = 1; i < 4; i++)
                {
                    canvas.Children.Add(new System.Windows.Shapes.Line
                    {
                        X1 = 0, X2 = w, Y1 = h * i / 4.0, Y2 = h * i / 4.0,
                        Stroke = gridBrush, StrokeThickness = 1,
                    });
                }

                bool first = true;
                foreach (var (data, color) in series)
                {
                    if (data == null || data.Count < 2) { first = false; continue; }
                    var values = data.ToArray();
                    float actualMax = maxVal > 0 ? maxVal : Math.Max(1f, values.Max());
                    var points = new PointCollection(values.Length);
                    for (int i = 0; i < values.Length; i++)
                    {
                        double x = (double)i / (values.Length - 1) * w;
                        double y = h - Math.Max(0, Math.Min(1, values[i] / actualMax)) * h;
                        points.Add(new Point(x, y));
                    }

                    if (fillFirst && first && points.Count > 1)
                    {
                        var fillPoints = new PointCollection(points);
                        fillPoints.Add(new Point(w, h));
                        fillPoints.Add(new Point(0, h));
                        var geom = new StreamGeometry();
                        using (var ctx = geom.Open())
                        {
                            ctx.BeginFigure(fillPoints[0], true, true);
                            for (int i = 1; i < fillPoints.Count; i++) ctx.LineTo(fillPoints[i], true, false);
                        }
                        geom.Freeze();
                        var brush = new LinearGradientBrush(
                            Color.FromArgb(70, color.R, color.G, color.B),
                            Color.FromArgb(8, color.R, color.G, color.B),
                            new Point(0, 0), new Point(0, 1));
                        brush.Freeze();
                        canvas.Children.Add(new System.Windows.Shapes.Path { Data = geom, Fill = brush, StrokeThickness = 0 });
                    }

                    var lineGeom = new StreamGeometry();
                    using (var ctx = lineGeom.Open())
                    {
                        ctx.BeginFigure(points[0], false, false);
                        for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
                    }
                    lineGeom.Freeze();
                    var stroke = new SolidColorBrush(color);
                    stroke.Freeze();
                    canvas.Children.Add(new System.Windows.Shapes.Path
                    {
                        Data = lineGeom,
                        Stroke = stroke,
                        StrokeThickness = 1.4,
                        StrokeLineJoin = PenLineJoin.Round,
                    });
                    first = false;
                }
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  ABA CONEXÕES (TCP/UDP por processo — estilo TMOG)
        // ══════════════════════════════════════════════
        private List<NetworkTrafficMonitor.NetEndpoint> _connEndpoints = new();
        private bool _connLoading;
        private int _connTick;

        /// <summary>Chamado a cada refresh quando a aba está visível (2 refreshes ≈ 2s).</summary>
        private void MaybeRefreshConnections()
        {
            try
            {
                if (TabConnections.Visibility != Visibility.Visible) return;
                if (_connLoading) return;
                if (++_connTick % 2 != 0) return;
                _ = RefreshConnectionsAsync();
            }
            catch { }
        }

        private async Task RefreshConnectionsAsync()
        {
            if (_connLoading) return;
            _connLoading = true;
            try
            {
                var eps = await Task.Run(() =>
                {
                    try { return NetworkTrafficMonitor.GetEndpoints(); }
                    catch { return new List<NetworkTrafficMonitor.NetEndpoint>(); }
                });
                if (_isClosed) return;

                var nameByPid = new Dictionary<uint, string>();
                lock (_lock) { foreach (var r in _allRows) nameByPid[(uint)r.Pid] = r.Name; }
                foreach (var ep in eps)
                    ep.ProcessName = nameByPid.TryGetValue(ep.Pid, out var n) ? n : $"({ep.Pid})";

                _connEndpoints = eps;
                ApplyConnectionFilter();
            }
            catch { }
            finally { _connLoading = false; }
        }

        private void ApplyConnectionFilter()
        {
            try
            {
                string filter = TxtConnFilter.Text?.Trim() ?? "";
                bool onlyEstablished = ChkConnOnlyEstablished.IsChecked == true;
                bool group = ChkConnGroupProc.IsChecked == true;

                IEnumerable<NetworkTrafficMonitor.NetEndpoint> q = _connEndpoints;
                if (onlyEstablished) q = q.Where(e => !e.Listening && e.State == "Estabelecido");
                if (filter.Length > 0)
                {
                    q = q.Where(e =>
                        (e.ProcessName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        e.Pid.ToString() == filter ||
                        e.Protocol.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.Local.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.Remote.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        e.State.Contains(filter, StringComparison.OrdinalIgnoreCase));
                }

                List<NetworkTrafficMonitor.NetEndpoint> list;
                if (!string.IsNullOrEmpty(_connSortColumn))
                {
                    // Criterio do usuario (clique no cabecalho) sobrepoe o default.
                    bool asc = _connSortDir == ListSortDirection.Ascending;
                    Func<NetworkTrafficMonitor.NetEndpoint, IComparable> key = _connSortColumn switch
                    {
                        "Pid" => e => e.Pid,
                        "Protocol" => e => e.Protocol,
                        "Local" => e => e.Local,
                        "Remote" => e => e.Remote,
                        "State" => e => e.State,
                        _ => e => e.ProcessName ?? "",
                    };
                    list = (asc ? q.OrderBy(key) : q.OrderByDescending(key)).ToList();
                }
                else
                {
                    list = q.OrderByDescending(e => !e.Listening)
                                .ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(e => e.Protocol)
                                .ThenBy(e => e.Local)
                                .ToList();
                }

                if (group)
                {
                    var cvs = new CollectionViewSource { Source = list };
                    cvs.GroupDescriptions.Add(new PropertyGroupDescription("ProcessName"));
                    DgConnections.ItemsSource = cvs.View;
                }
                else
                {
                    DgConnections.ItemsSource = list;
                }

                int ativas = _connEndpoints.Count(e => !e.Listening);
                int procs = _connEndpoints.Select(e => e.Pid).Distinct().Count();
                TxtConnStatus.Text = $"{list.Count} de {_connEndpoints.Count} endpoints · {ativas} conexões ativas · {procs} processos";
                TxtConnSubtitle.Text = "TCP/UDP por processo — lido direto do iphlpapi (GetExtendedTcp/UdpTable), sem admin";
            }
            catch { }
        }

        private void TxtConnFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyConnectionFilter();

        // Ordenacao da aba Conexoes (padrao de qualidade: toda coluna clicavel tem
        // SortMemberPath e o criterio sobrevive ao refresh — antes o clique caia no sort
        // default do WPF e o refresh seguinte (que reconstroi a lista) desfazia tudo).
        private string _connSortColumn = "";
        private ListSortDirection _connSortDir = ListSortDirection.Ascending;

        private void DgConnections_Sorting(object sender, DataGridSortingEventArgs e)
        {
            try
            {
                e.Handled = true;
                string prop = e.Column?.SortMemberPath ?? "";
                if (string.IsNullOrEmpty(prop)) return;
                if (_connSortColumn == prop)
                    _connSortDir = _connSortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                else
                {
                    _connSortColumn = prop;
                    _connSortDir = prop == "Pid" ? ListSortDirection.Descending : ListSortDirection.Ascending;
                }
                ApplyConnectionFilter();
                foreach (var c in DgConnections.Columns) c.SortDirection = null;
                var active = DgConnections.Columns.FirstOrDefault(c => c.SortMemberPath == _connSortColumn);
                if (active != null) active.SortDirection = _connSortDir;
            }
            catch { }
        }

        private void ChkConnFilter_Changed(object sender, RoutedEventArgs e) => ApplyConnectionFilter();

        private void BtnConnRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshConnectionsAsync();

        // ══════════════════════════════════════════════
        //  DETALHES AVANÇADOS DO PROCESSO (painel do TMOG)
        // ══════════════════════════════════════════════
        private async Task LoadAdvancedDetailsAsync(ProcessRow row)
        {
            string? prevLoading = null;
            try
            {
                int pid = row.Pid;

                // Campos que já vêm do refresh — sem I/O nenhum
                // 1ª passada: valores que já vieram do refresh da lista (sem I/O nenhum).
                // Logo abaixo, quando os contadores DIRETOS do processo chegarem, estes
                // mesmos campos são refinados — assim o painel nunca fica cheio de "—".
                DetailCommit.Text = row.CommitMB > 0 ? $"{row.CommitMB:F1} MB" : "…";
                DetailPeak.Text = string.IsNullOrEmpty(row.PeakMemMB) ? "…" : row.PeakMemMB;
                DetailKernelTime.Text = row.KernelTimeSec > 0 ? FormatCpuTime(row.KernelTimeSec) : "…";
                DetailUserTime.Text = row.UserTimeSec > 0 ? FormatCpuTime(row.UserTimeSec) : "…";
                DetailIoRead.Text = row.IoReadTotal > 0 ? FormatTotalBytes(row.IoReadTotal) : "…";
                DetailIoWrite.Text = row.IoWriteTotal > 0 ? FormatTotalBytes(row.IoWriteTotal) : "…";
                DetailIoOps.Text = row.IoOpsTotal > 0 ? row.IoOpsTotal.ToString("N0") : "…";
                DetailGroup.Text = string.IsNullOrEmpty(row.Group) ? "—" : row.Group;
                DetailPageFaults.Text = "…";
                DetailGuiUser.Text = "…";
                DetailElevated.Text = "…";

                string parentName = "";
                lock (_lock)
                {
                    var p = _allRows.FirstOrDefault(r => r.Pid == row.ParentPid);
                    parentName = p?.Name ?? "";
                }
                DetailParent.Text = row.ParentPid > 0
                    ? (parentName.Length > 0 ? $"{parentName} ({row.ParentPid})" : row.ParentPid.ToString())
                    : "—";

                // Campos que exigem OpenProcess — carregam fora da UI
                DetailSession.Text = "…";
                DetailArch.Text = "…";
                DetailBasePrio.Text = "…";
                DetailCommandLine.Text = "lendo…";
                prevLoading = TxtDetailAdvLoading.Text;
                TxtDetailAdvLoading.Text = "lendo…";

                var d = await Task.Run(() =>
                {
                    try { return NativeMetricsHelper.GetProcessDetails(pid); }
                    catch { return null; }
                });
                if (d == null || _isClosed) return;
                if (SelectedRow?.Pid != pid) return; // seleção mudou nesse meio tempo

                DetailSession.Text = d.SessionKnown ? d.SessionId.ToString() : "—";
                DetailArch.Text = string.IsNullOrWhiteSpace(d.Architecture) ? "—" : d.Architecture;
                DetailBasePrio.Text = d.BasePriority > 0
                    ? $"{d.BasePriority} ({d.PriorityClass})"
                    : (string.IsNullOrWhiteSpace(d.PriorityClass) ? "—" : d.PriorityClass);
                DetailCommandLine.Text = string.IsNullOrWhiteSpace(d.CommandLine) ? "—" : d.CommandLine;

                // ══ Contadores DIRETOS do processo (GetProcessMemoryInfo/GetProcessTimes/
                //    GetProcessIoCounters) — independentes do caminho da lista, então
                //    nunca ficam "—" num processo normal (TMOG: commit/peak/tempos/E-S).
                if (d.CountersKnown)
                {
                    DetailCommit.Text = FormatTotalBytes(d.CommitBytes);
                    DetailPeak.Text = FormatTotalBytes(d.PeakWorkingSetBytes);
                }
                if (d.KernelTime100ns > 0 || d.UserTime100ns > 0)
                {
                    DetailKernelTime.Text = FormatCpuTime(d.KernelTime100ns / 1e7);
                    DetailUserTime.Text = FormatCpuTime(d.UserTime100ns / 1e7);
                }
                if (d.IoReadBytes > 0 || d.IoWriteBytes > 0)
                {
                    DetailIoRead.Text = FormatTotalBytes(d.IoReadBytes);
                    DetailIoWrite.Text = FormatTotalBytes(d.IoWriteBytes);
                }
                ulong ops = d.IoReadOps + d.IoWriteOps + d.IoOtherOps;
                if (ops > 0) DetailIoOps.Text = ops.ToString("N0");
                DetailPageFaults.Text = d.PageFaults > 0 ? d.PageFaults.ToString("N0") : "—";
                // GDI/USER rotulado: "0 / 10" solto parecia bug de renderização (e se confundia
                // com a linha Elevado logo abaixo). Falha de leitura ≠ 0 real — GetGuiResources
                // devolve 0 nos dois casos; o Core agora distingue via GuiObjectsKnown.
                DetailGuiUser.Text = d.GuiObjectsKnown
                    ? $"GDI: {d.GdiObjects:N0} · USER: {d.UserObjects:N0}"
                    : (d.ThreadCount > 0 ? $"— ({d.HandleCount:N0} handles, {d.ThreadCount} threads)" : "—");
                DetailElevated.Text = d.ElevatedKnown ? (d.Elevated ? "Sim (admin)" : "Não") : "—";

                // Tira o "…" de onde não houve leitura (processo protegido/sem permissão)
                foreach (var tb in new[] { DetailCommit, DetailPeak, DetailKernelTime, DetailUserTime, DetailIoRead, DetailIoWrite, DetailIoOps })
                    if (tb.Text == "…") tb.Text = "—";
                foreach (var tb in new[] { DetailPageFaults, DetailGuiUser, DetailElevated })
                    if (tb.Text == "…") tb.Text = "—";

                TxtDetailAdvLoading.Text = "";
            }
            catch { }
            finally
            {
                if (prevLoading != null && TxtDetailAdvLoading.Text == "lendo…") TxtDetailAdvLoading.Text = "";
            }
        }

        private static string FormatCpuTime(double seconds)
        {
            try
            {
                // Menos de 1s: "00:00:00" parecia bug no painel — mostramos em milissegundos.
                if (seconds < 1) return seconds <= 0 ? "—" : $"{(int)(seconds * 1000)} ms";
                var t = TimeSpan.FromSeconds(seconds);
                return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            }
            catch { return "—"; }
        }

        private static string FormatTotalBytes(ulong bytes)
        {
            if (bytes >= 1073741824UL) return $"{bytes / 1073741824.0:F2} GB";
            if (bytes >= 1048576UL) return $"{bytes / 1048576.0:F1} MB";
            if (bytes >= 1024UL) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
