using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  MOTOR FLUIDO 60 FPS — fase 2 do motor de render (paridade TMOG)
    //
    //  O TMOG (Task Manager OG, do Dave Plummer) parece "liso" porque a
    //  APRESENTAÇÃO roda a 60 fps enquanto a COLETA continua pesada e lenta
    //  (1x/s no nosso caso — PDH/NtQSI custam dezenas de ms por amostra,
    //  ver CollectSamplesAsync). Este partial é o lado apresentação:
    //
    //    • GRÁFICOS QUE RODAM — entre duas amostras de 1 s, a polyline desliza
    //      sub-pixel para a esquerda (periodo medido em tempo real entre
    //      chegadas de amostra), igual ao scroll contínuo do TMOG. Acabou o
    //      "pulo" de 1 s: cada novo ponto entra pela borda direita e a linha
    //      inteira anda até a próxima amostra.
    //    • BARRAS COM EASING — largura/altura caminham exponencialmente até o
    //      alvo (tau = 0,09 s, o mesmo do motor de linhas do ProcessRow).
    //    • NÚMEROS QUE ROLAM — os contadores grandes (% de CPU/RAM/GPU, % do
    //      disco, DPC) exibem o valor EASADO ~30x/s em vez de piscar 1x/s.
    //
    //  CUSTO (medido com cuidado — ver o comentário do EaseStep no Render.cs,
    //  que provou que TEXTO/COR por quadro na GRID inteira custa 100% da UI):
    //    • gráfico: por quadro, reconstrói a StreamGeometry SÓ das séries do
    //      canvas VISÍVEL (1-6 canvases por aba). As crianças (grade, fill,
    //      linhas) são criadas UMA vez no registro — nada de Children.Clear()
    //      por quadro, nada de invalidação fora do elemento.
    //    • barra: 1 atribuição de Width/Height por quadro (mutação barata).
    //    • texto: ~30 Hz e só nos contadores registrados (nunca na grid).
    //    • aba parada/oculta/janela minimizada: os 3 primeiros testes saem na
    //      1ª linha — custo zero fora da aba que está na tela.
    //  A COLETA continua 1x/s de propósito: medir mais rápido não é o pedido,
    //  APRESENTAR liso é. As amostras continuam honestas (sem interpolar dado
    //  que não existiu — só a posição entre amostras é animada).
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        private bool _fluidHooked;
        private DateTime _lastFluidUtc = DateTime.UtcNow;
        private int _textFrameParity;

        // ── alvos extras alimentados pelos ticks (valores que não vivem em campo próprio) ──
        private double _fluidStActivityTarget;
        private double _fluidLatDpcTarget;
        private double _fluidMemBigTarget;

        // ── barras com easing ──
        private sealed class FluidBar
        {
            public Border Bar = null!;
            public bool Vertical;
            public double TrackSize;
            public double MinPx;          // largura/altura mínima quando frac = 0 (ex.: pílula de 3px)
            public double MaxPx;          // 0 = usa BarMaxWidth (horizontal) / TrackSize (vertical)
            public double Target, Current;
        }
        private readonly List<FluidBar> _fluidBars = new();
        private readonly Dictionary<Border, FluidBar> _fluidBarIndex = new();

        // ── textos com easing (~30 Hz) ──
        private sealed class FluidText
        {
            public TextBlock Tb = null!;
            public Func<double> Target = null!;
            public string Format = "F0";
            public string Suffix = "";
            public double Current;
            public bool HasValue;
        }
        private readonly List<FluidText> _fluidTexts = new();

        // ── gráficos que rodam a 60 fps ──
        private sealed class FluidSeriesDef
        {
            public Func<Queue<float>?> Data = null!;
            public Color Color;
        }

        private sealed class FluidChart
        {
            public Canvas Canvas = null!;
            public Func<float> Max = null!;          // <= 0 → autoescala pelos valores
            public bool Fill;                        // preenche sob a 1ª série
            public List<FluidSeriesDef> Series = new();
            public Path? FillPath;
            public Path?[] LinePaths = Array.Empty<Path?>();
            public Line[] GridLines = Array.Empty<Line>();
            public float[][] Bufs = Array.Empty<float[]>();
            public float[][] Prev = Array.Empty<float[]>();
            public int[] LastCounts = Array.Empty<int>();
            public DateTime LastArrival = DateTime.UtcNow;
            public double Period = 1.0;              // intervalo medido entre amostras
        }

        private readonly Dictionary<Canvas, FluidChart> _fluidCharts = new();

        // ────────────────────────────────────────────────────────────────────
        //  HOOK (mesmo padrão do HookFrameEngine: um handler por CompositionTarget)
        // ────────────────────────────────────────────────────────────────────
        private void HookFluidEngine()
        {
            if (_fluidHooked) return;
            // Harness/profiling: mesma chave que desliga o motor de linhas (determinismo).
            try { if (Environment.GetEnvironmentVariable("KL_TM_NO_RENDER") == "1") { _fluidHooked = true; return; } } catch { }
            _fluidHooked = true;
            _lastFluidUtc = DateTime.UtcNow;
            CompositionTarget.Rendering += OnFluidFrame;
            Closed += (_, __) =>
            {
                try { CompositionTarget.Rendering -= OnFluidFrame; } catch { }
                _fluidHooked = false;
            };
        }

        // ────────────────────────────────────────────────────────────────────
        //  REGISTRO (chamado pelos ticks 1x/s — barato e idempotente)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Barra horizontal (largura = fração × BarMaxWidth) ou vertical (altura = fração × trackSize) com easing.</summary>
        private void FluidSetBar(Border? bar, double fraction, bool vertical = false, double trackSize = 0, double minPx = 0, double maxPx = 0)
        {
            if (bar == null) return;
            if (!_fluidBarIndex.TryGetValue(bar, out var fb))
            {
                fb = new FluidBar { Bar = bar, Vertical = vertical, TrackSize = trackSize <= 0 ? 1 : trackSize, MinPx = minPx, MaxPx = maxPx };
                fb.Current = Math.Clamp(fraction, 0, 1);   // sem varredura 0→x no registro
                _fluidBarIndex[bar] = fb;
                _fluidBars.Add(fb);
            }
            fb.Target = Math.Clamp(fraction, 0, 1);
        }

        /// <summary>Texto grande com easing. Target &lt; 0 = indisponível → o tick manda "—"/"N/A" e o motor não mexe.</summary>
        private void FluidSetText(TextBlock? tb, Func<double> target, string format = "F0", string suffix = "")
        {
            if (tb == null || target == null) return;
            var ft = _fluidTexts.Find(x => ReferenceEquals(x.Tb, tb));
            if (ft == null)
            {
                double first;
                try { first = target(); } catch { return; }
                ft = new FluidText { Tb = tb, Target = target, Format = format, Suffix = suffix, Current = first, HasValue = first >= 0 };
                _fluidTexts.Add(ft);
            }
            ft.Target = target;
            ft.Format = format;
            ft.Suffix = suffix;
        }

        /// <summary>
        /// Registra/atualiza um gráfico fluido no canvas. As FILAS continuam sendo
        /// preenchidas pelo tick de 1 s (mesma fonte de antes); o motor lê por quadro.
        /// Re-registrar é barato: só troca lambdas/cores (rebuild só se o nº de séries muda).
        /// </summary>
        private void FluidRegisterChart(Canvas canvas, Func<float> max, bool fill, params (Func<Queue<float>?> data, Color color)[] series)
        {
            if (canvas == null || series == null || series.Length == 0) return;
            canvas.ClipToBounds = true;   // a linha rola para FORA pela esquerda — sem clip vaza fora do quadro
            if (!_fluidCharts.TryGetValue(canvas, out var fc))
            {
                fc = new FluidChart { Canvas = canvas };
                _fluidCharts[canvas] = fc;
            }
            fc.Max = max;
            bool rebuild = fc.Fill != fill || fc.Series.Count != series.Length;
            fc.Fill = fill;
            fc.Series.Clear();
            foreach (var (data, color) in series)
                fc.Series.Add(new FluidSeriesDef { Data = data, Color = color });
            if (rebuild) BuildFluidChartChildren(fc);
            else if (fc.FillPath != null && fc.Series.Count > 0)
                fc.FillPath.Fill = MakeFillBrush(fc.Series[0].Color);   // cor mudou sem rebuild
        }

        private void BuildFluidChartChildren(FluidChart fc)
        {
            var canvas = fc.Canvas;
            canvas.Children.Clear();
            int n = fc.Series.Count;

            // Grade horizontal (quartos) — a leitura de escala do TMOG
            fc.GridLines = new Line[3];
            var gridBrush = Freeze(new SolidColorBrush(Color.FromArgb(60, 0x44, 0x44, 0x44)));
            for (int i = 0; i < 3; i++)
            {
                var ln = new Line { Stroke = gridBrush, StrokeThickness = 1 };
                fc.GridLines[i] = ln;
                canvas.Children.Add(ln);
            }

            fc.FillPath = null;
            if (fc.Fill && n > 0)
            {
                fc.FillPath = new Path { StrokeThickness = 0, Fill = MakeFillBrush(fc.Series[0].Color) };
                canvas.Children.Add(fc.FillPath);
            }

            fc.LinePaths = new Path?[n];
            for (int i = 0; i < n; i++)
            {
                var p = new Path
                {
                    StrokeThickness = 1.4,
                    StrokeLineJoin = PenLineJoin.Round,
                    Stroke = Freeze(new SolidColorBrush(fc.Series[i].Color)),
                };
                fc.LinePaths[i] = p;
                canvas.Children.Add(p);
            }

            fc.Bufs = new float[n][];
            fc.Prev = new float[n][];
            fc.LastCounts = new int[n];
            for (int i = 0; i < n; i++)
            {
                fc.Bufs[i] = new float[64];
                fc.Prev[i] = new float[64];
            }
        }

        private static LinearGradientBrush MakeFillBrush(Color c)
        {
            var b = new LinearGradientBrush(
                Color.FromArgb(70, c.R, c.G, c.B),
                Color.FromArgb(8, c.R, c.G, c.B),
                new Point(0, 0), new Point(0, 1));
            b.Freeze();
            return b;
        }

        // ────────────────────────────────────────────────────────────────────
        //  QUADRO (~16,6 ms)
        // ────────────────────────────────────────────────────────────────────
        private void OnFluidFrame(object? sender, EventArgs e)
        {
            try
            {
                if (_isClosed || !IsVisible)
                {
                    _lastFluidUtc = DateTime.UtcNow;   // volta sem "teleporte" de easing
                    return;
                }

                var now = DateTime.UtcNow;
                double dt = (now - _lastFluidUtc).TotalSeconds;
                _lastFluidUtc = now;
                if (dt <= 0) return;
                if (dt > 0.25) dt = 0.25;   // voltou de suspensão/minimizado

                double k = 1.0 - Math.Exp(-dt / 0.09);   // mesmo tau do motor de linhas

                // ── barras ──
                for (int i = _fluidBars.Count - 1; i >= 0; i--)
                {
                    var fb = _fluidBars[i];
                    if (!fb.Bar.IsLoaded) { _fluidBars.RemoveAt(i); _fluidBarIndex.Remove(fb.Bar); continue; }
                    if (!fb.Bar.IsVisible) continue;
                    fb.Current += (fb.Target - fb.Current) * k;
                    if (Math.Abs(fb.Target - fb.Current) < 0.0004) fb.Current = fb.Target;
                    double v = Math.Clamp(fb.Current, 0, 1);
                    if (fb.Vertical)
                    {
                        double hgt = Math.Max(fb.MinPx, fb.TrackSize * v);
                        if (Math.Abs(fb.Bar.Height - hgt) > 0.1) fb.Bar.Height = hgt;
                    }
                    else
                    {
                        double max = fb.MaxPx > 0 ? fb.MaxPx : BarMaxWidth;
                        double wid = fb.MinPx + v * (max - fb.MinPx);
                        if (Math.Abs(fb.Bar.Width - wid) > 0.1) fb.Bar.Width = wid;
                    }
                }

                // ── textos (alternando quadros → ~30 Hz) ──
                if (++_textFrameParity >= 2) { _textFrameParity = 0; EaseTexts(); }

                // ── gráficos ──
                foreach (var fc in _fluidCharts.Values)
                    DrawFluidChart(fc, now);
            }
            catch
            {
                // Um quadro ruim nunca derruba o app (mesmo padrão defensivo do resto da janela).
                _lastFluidUtc = DateTime.UtcNow;
            }
        }

        private void EaseTexts()
        {
            for (int i = _fluidTexts.Count - 1; i >= 0; i--)
            {
                var ft = _fluidTexts[i];
                if (!ft.Tb.IsLoaded) { _fluidTexts.RemoveAt(i); continue; }
                if (!ft.Tb.IsVisible) continue;
                double target;
                try { target = ft.Target(); } catch { continue; }
                if (target < 0) continue;   // indisponível: o tick escreve "—"/"N/A" e o motor não sobrescreve
                if (!ft.HasValue) { ft.Current = target; ft.HasValue = true; }
                ft.Current += (target - ft.Current) * 0.35;   // ~6 atualizações para convergir (~200 ms)
                if (Math.Abs(target - ft.Current) < 0.05) ft.Current = target;
                string s = ft.Current.ToString(ft.Format) + ft.Suffix;
                if (!string.Equals(ft.Tb.Text, s, StringComparison.Ordinal)) ft.Tb.Text = s;
            }
        }

        /// <summary>
        /// Desenha UM gráfico fluido no quadro. Fases:
        ///   1. copia as filas (que o tick de 1 s preenche) para buffers reutilizados;
        ///   2. detecta chegada de amostra nova (qualquer valor mudou) e recalibra o período;
        ///   3. fase de scroll = tempo desde a última chegada / período — com dados flat
        ///      (CPU 0% por muito tempo) a chegada não é detectada, então a fase dá a volta
        ///      sozinha pelo período medido: o gráfico SEGUE rolando suave, nunca congela;
        ///   4. reconstrói a geometria com o deslocamento sub-pixel da fase.
        /// </summary>
        private void DrawFluidChart(FluidChart fc, DateTime now)
        {
            var canvas = fc.Canvas;
            if (!canvas.IsLoaded || !canvas.IsVisible) return;

            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w <= 1 || h <= 1)
            {
                if (canvas.Parent is FrameworkElement p && p.ActualWidth > 1)
                {
                    w = p.ActualWidth - 2;
                    if (h <= 1) h = p.ActualHeight > 1 ? p.ActualHeight - 2 : 90;
                }
                if (w <= 1 || h <= 1) return;
            }

            // grade (posições só mudam no resize — atribuição com guarda)
            if (fc.GridLines.Length == 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    var ln = fc.GridLines[i];
                    double y = h * (i + 1) / 4.0;
                    if (ln.X2 != w) { ln.X1 = 0; ln.X2 = w; }
                    if (ln.Y1 != y) { ln.Y1 = y; ln.Y2 = y; }
                }
            }

            int n = fc.Series.Count;
            if (fc.Bufs.Length != n || fc.LastCounts.Length != n) BuildFluidChartChildren(fc);

            // 1+2. cópia + detecção de chegada
            bool anyChange = false;
            float maxOfAll = 0f;
            for (int si = 0; si < n; si++)
            {
                Queue<float>? q = null;
                try { q = fc.Series[si].Data(); } catch { }
                int cnt = 0;
                if (q != null && q.Count > 0)
                {
                    var buf = fc.Bufs[si];
                    if (buf.Length < q.Count)
                    {
                        fc.Bufs[si] = buf = new float[Math.Max(64, q.Count)];
                        fc.Prev[si] = new float[buf.Length];
                    }
                    q.CopyTo(buf, 0);
                    cnt = q.Count;

                    var prev = fc.Prev[si];
                    bool changed = cnt != fc.LastCounts[si];
                    if (!changed)
                    {
                        for (int i = 0; i < cnt; i++)
                            if (buf[i] != prev[i]) { changed = true; break; }
                    }
                    if (changed)
                    {
                        Array.Copy(buf, prev, cnt);
                        anyChange = true;
                    }
                    for (int i = 0; i < cnt; i++)
                        if (buf[i] > maxOfAll) maxOfAll = buf[i];
                }
                fc.LastCounts[si] = cnt;
            }
            if (anyChange)
            {
                double gap = (now - fc.LastArrival).TotalSeconds;
                if (gap >= 0.2 && gap < 10) fc.Period = gap;   // período REAL entre amostras
                fc.LastArrival = now;
            }

            // 3. fase de scroll (0..1), auto-corrigida com dados flat
            double phase = (now - fc.LastArrival).TotalSeconds / Math.Max(0.2, fc.Period);
            while (phase >= 1) phase -= 1;

            float maxVal;
            try { maxVal = fc.Max(); } catch { maxVal = 0f; }
            if (maxVal <= 0)
            {
                maxVal = maxOfAll;
                if (maxVal <= 0.0001f) maxVal = 1f;
            }

            // 4. geometrias — x_i = w - (cnt-1-i + fase)·dx: o ponto mais novo entra pela
            //    borda direita e a linha inteira desliza para a esquerda até a próxima chegada.
            for (int si = 0; si < n; si++)
            {
                var path = fc.LinePaths[si];
                if (path == null) continue;
                int cnt = fc.LastCounts[si];
                if (cnt < 2)
                {
                    if (path.Visibility != Visibility.Collapsed) path.Visibility = Visibility.Collapsed;
                    continue;
                }
                var buf = fc.Bufs[si];
                double dx = w / (cnt - 1);
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    double xNew = w - phase * dx;
                    double yNew = h - Math.Clamp(buf[cnt - 1] / maxVal, 0f, 1f) * h;
                    ctx.BeginFigure(new Point(xNew, yNew), false, false);
                    for (int i = cnt - 2; i >= 0; i--)
                    {
                        double x = w - (cnt - 1 - i + phase) * dx;
                        double y = h - Math.Clamp(buf[i] / maxVal, 0f, 1f) * h;
                        ctx.LineTo(new Point(x, y), true, false);
                    }
                }
                geom.Freeze();
                path.Data = geom;
                if (path.Visibility != Visibility.Visible) path.Visibility = Visibility.Visible;
                var color = fc.Series[si].Color;
                if (path.Stroke is SolidColorBrush sb && !sb.Color.Equals(color))
                    path.Stroke = Freeze(new SolidColorBrush(color));
            }

            // preenchimento = sob a 1ª série
            if (fc.FillPath != null)
            {
                int cnt = n > 0 ? fc.LastCounts[0] : 0;
                if (cnt < 2)
                {
                    if (fc.FillPath.Visibility != Visibility.Collapsed) fc.FillPath.Visibility = Visibility.Collapsed;
                }
                else
                {
                    var buf = fc.Bufs[0];
                    double dx = w / (cnt - 1);
                    var geom = new StreamGeometry();
                    using (var ctx = geom.Open())
                    {
                        double xNew = w - phase * dx;
                        double yNew = h - Math.Clamp(buf[cnt - 1] / maxVal, 0f, 1f) * h;
                        ctx.BeginFigure(new Point(xNew, yNew), true, true);
                        for (int i = cnt - 2; i >= 0; i--)
                        {
                            double x = w - (cnt - 1 - i + phase) * dx;
                            double y = h - Math.Clamp(buf[i] / maxVal, 0f, 1f) * h;
                            ctx.LineTo(new Point(x, y), true, false);
                        }
                        ctx.LineTo(new Point(-phase * dx, h), true, false);   // canto inferior-esquerdo sob o ponto mais antigo
                        ctx.LineTo(new Point(w, h), true, false);             // fecha até a borda direita
                    }
                    geom.Freeze();
                    fc.FillPath.Data = geom;
                    if (fc.FillPath.Visibility != Visibility.Visible) fc.FillPath.Visibility = Visibility.Visible;
                }
            }
        }
    }
}
