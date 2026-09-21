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
            private string _name = "", _cpu = "", _ram = "";
            private ImageSource? _icon;
            public int Pid { get; set; }
            public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
            public string Cpu { get => _cpu; set { _cpu = value; Raise(nameof(Cpu)); } }
            public string RamMB { get => _ram; set { _ram = value; Raise(nameof(RamMB)); } }

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

        private void SetVital(TextBlock txt, Border bar, string text, double fraction, System.Windows.Media.Brush color)
        {
            try
            {
                txt.Text = text;
                txt.Foreground = color;
                // Largura agora é EASADA pelo motor fluido (60fps) — o tick só registra o alvo.
                FluidSetBar(bar, fraction, vertical: false);
                bar.Background = color;
            }
            catch { }
        }

        private static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("F1");
        private static string Mb(ulong bytes) => (bytes / 1048576.0).ToString("F0");

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
            // Motor fluido: a linha rola continuamente entre amostras de 1s (60fps).
            var cpuSeries = new List<(Func<Queue<float>?> data, Color color)>
            {
                (() => _sumCpuHist, Color.FromRgb(0x4C, 0xAF, 0x50)),
                (() => _sumKernelHist, Color.FromRgb(0xF4, 0x43, 0x36)),
            };
            if (_sumTempHist.Count > 1) cpuSeries.Add((() => _sumTempHist, Color.FromRgb(0xFF, 0x98, 0x00)));
            FluidRegisterChart(SumCpuCanvas, () => 100f, true, cpuSeries.ToArray());

            // Número grande com easing (~30Hz) — o valor sobe/desce suave entre amostras.
            FluidSetText(TxtSumCpuBig, () => cpu, "F0", "%");
            TxtSumCpuBig.Foreground = GetHeatColor(cpu, 80, 95);
            TxtSumCpuSub.Text = kernelF > 0 ? $"Kernel {kernelF:F0}%" : "";
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            TxtSumCpuFooter.Text =
                $"Frequência {(freq > 0 ? (freq / 1000.0).ToString("F2") + " GHz" : "—")} · " +
                $"{Environment.ProcessorCount} núcleos · uptime do sistema {(int)uptime.TotalDays}d {uptime.Hours:00}h {uptime.Minutes:00}m";
            TxtSumSubtitle.Text = $"Atualizando a cada {_refreshSeconds}s · métricas nativas (sem admin)";

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
                    TxtSumCoresInfo.Text = $"{cores.Length} processadores lógicos";
                    for (int i = 0; i < _coreBars.Count && i < cores.Length; i++)
                    {
                        float pct = (float)cores[i];
                        var bar = _coreBars[i];
                        // Altura/cor: altura é EASADA pelo motor fluido; cor/texto ficam no tick 1s.
                        FluidSetBar(bar.Fill, Math.Min(1, pct / 100.0), vertical: true, bar.TrackHeight);
                        var brush = GetHeatColor(pct, 60, 85);
                        bar.Fill.Background = brush;
                        bar.Pct.Text = pct >= 100 ? "100" : pct.ToString("F0");
                        bar.Pct.Foreground = brush;
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

                    FluidSetText(TxtSumMemPctBig, () => md.LoadPercent, "F1", "%");
                    TxtSumMemUsed.Text = $"Em uso: {Gb(md.UsedBytes)} GB de {Gb(md.TotalBytes)} GB ({md.LoadPercent:F0}%)";
                    TxtSumMemAvail.Text = $"Disponível: {Gb(md.AvailableBytes)} GB";
                    TxtSumMemCache.Text = $"Em cache: {Gb(md.CachedBytes)} GB";
                    TxtSumMemSwap.Text = pf != null && pf.Valid
                        ? $"Swap (arquivo de paginação em uso): {Gb((ulong)(pf.CurrentUsageMB * 1048576))} GB de {Gb((ulong)(pf.AllocatedMB * 1048576))} GB alocados"
                        : $"Além da RAM: {Gb(beyondRam)} GB comprometidos (medindo o swap…)";
                    TxtSumMemCommit.Text = $"Comprometida: {Gb(md.CommitTotalBytes)} / {Gb(md.CommitLimitBytes)} GB";
                    TxtSumMemPools.Text = md.Native
                        ? $"Pool paginado: {Mb(md.PagedPoolBytes)} MB · não paginado: {Mb(md.NonPagedPoolBytes)} MB"
                        : "Pool: indisponível neste sistema";
                    TxtSumMemMeta.Text = md.Native
                        ? $"{md.HandleCount:N0} handles · {md.ThreadCount:N0} threads · {md.ProcessCount:N0} processos"
                        : "";

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
                    TxtSumMemTip.Text =
                        $"Agora: {md.LoadPercent:F1}% usado · {Gb(md.UsedBytes)} GB em uso · {Gb(md.AvailableBytes)} GB disponível · " +
                        $"pico desta sessão: {Gb(md.CommitPeakBytes)} GB comprometidos." + Environment.NewLine +
                        (md.LoadPercent >= 85
                            ? "ESTADO: RAM quase cheia. É aqui que o Windows começa a buscar no DISCO (paginação) e tudo engasga — veja a aba Latência."
                            : md.LoadPercent >= 70
                                ? "ESTADO: uso alto, mas ainda dentro do normal para muitas abas/jogos abertos. Observe se o disco começa a trabalhar sozinho."
                                : "ESTADO: tranquilo. Há RAM de sobra para o que está rodando agora.");
                }
                FluidRegisterChart(SumMemCanvas, () => 100f, true, (() => _sumMemHist, Color.FromRgb(0xB3, 0x6A, 0xE2)));
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
                    TxtSumNet.Text = FormatBytesSpeed(netTotal);
                    TxtSumNetDetail.Text = $"↓ {FormatBytesSpeed(net.InBytesPerSec)} · ↑ {FormatBytesSpeed(net.OutBytesPerSec)}";
                    TxtSumNet.ToolTip = net.PrimaryName;
                }
                else
                {
                    TxtSumNet.Text = "—";
                    TxtSumNetDetail.Text = $"lendo {net.Interfaces} interfaces…";
                }
                FluidRegisterChart(SumNetCanvas, () => 0f, true, (() => _sumNetHist, Color.FromRgb(0x9C, 0x27, 0xB0)));
            }
            catch { }

            try
            {
                float diskMB = _lastDiskReadMBps + _lastDiskWriteMBps;
                Enqueue(_sumDiskHist, diskMB);
                TxtSumDisk.Text = FormatBytesSpeed(diskMB * 1048576.0);
                TxtSumDiskDetail.Text = $"R {FormatBytesSpeed(_lastDiskReadMBps * 1048576.0)} · W {FormatBytesSpeed(_lastDiskWriteMBps * 1048576.0)}";
                FluidRegisterChart(SumDiskCanvas, () => 0f, true, (() => _sumDiskHist, Color.FromRgb(0xFF, 0x98, 0x00)));
            }
            catch { }

            try
            {
                TxtSumGpuTile.Text = gpu >= 0 ? $"{gpu:F0}%" : "N/A";
                string topGpu;
                lock (_lock) topGpu = _allRows.Where(r => r.GpuValue > 0).OrderByDescending(r => r.GpuValue).FirstOrDefault()?.Name ?? "";
                TxtSumGpuDetail.Text = topGpu.Length > 0 ? $"maior: {topGpu}" : "sem uso de GPU";
                FluidRegisterChart(SumGpuCanvas, () => 100f, true, (() => _sumGpuHist, Color.FromRgb(0x21, 0x96, 0xF3)));
            }
            catch { }

            try
            {
                TxtSumPowerTile.Text = power >= 0 ? $"{power:F1} W" : "—";
                TxtSumPowerDetail.Text = power >= 0 ? "pacote da CPU (PDH)" : "sensor indisponível";
                FluidRegisterChart(SumPowerCanvas, () => 0f, true, (() => _sumPowerHist, Color.FromRgb(0xF4, 0x43, 0x36)));
            }
            catch { }
        }

        /// <summary>Lista "Top processos por CPU" (merge in-place para não perder a seleção).</summary>
        private void UpdateSummaryTopCpu()
        {
            try
            {
                if (TabSummary.Visibility != Visibility.Visible) return;
                List<ProcessRow> rows;
                lock (_lock) rows = _allRows;
                if (rows == null || rows.Count == 0) return;

                var top = rows.OrderByDescending(r => r.CpuValue).ThenByDescending(r => r.RamValue).Take(14).ToList();
                EnsureSummaryBuilt();
                for (int i = 0; i < top.Count; i++)
                {
                    var src = top[i];
                    if (i < _topProcRows.Count && _topProcRows[i].Pid == src.Pid)
                    {
                        var dst = _topProcRows[i];
                        dst.Name = src.Name;
                        dst.Cpu = src.Cpu;
                        dst.RamMB = src.RamMB;
                        // Ícone chega depois (carregado em background) — não perde a chance
                        if (dst.Icon == null && src.ProcessIcon != null) dst.Icon = src.ProcessIcon;
                    }
                    else
                    {
                        var novo = new TopProcRow { Pid = src.Pid, Name = src.Name, Cpu = src.Cpu, RamMB = src.RamMB, Icon = src.ProcessIcon };
                        if (i < _topProcRows.Count) _topProcRows[i] = novo;
                        else _topProcRows.Add(novo);
                    }
                }
                while (_topProcRows.Count > top.Count) _topProcRows.RemoveAt(_topProcRows.Count - 1);
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

        private void CmbSummaryInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item || item.Content is not string txt) return;
                if (!int.TryParse(txt.Replace("s", ""), out int sec) || sec <= 0) return;
                _refreshSeconds = sec;
                if (_refreshTimer != null) _refreshTimer.Interval = TimeSpan.FromSeconds(sec);
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

                var list = q.OrderByDescending(e => !e.Listening)
                            .ThenBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(e => e.Protocol)
                            .ThenBy(e => e.Local)
                            .ToList();

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
