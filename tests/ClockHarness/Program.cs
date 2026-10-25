// Harness do RELÓGIO DA CPU: o vital "Relógio" precisa mostrar o clock ATUAL
// (turbo incluído), como o Gerenciador de Tarefas do Windows.
//
// O que prova (pela interface REAL, janela WPF fora da tela):
//   1) Core: GetCpuBaseFrequencyMhz devolve o clock NOMINAL (velocidade base);
//   2) Core: GetCpuProcessorPerformancePercent devolve o "% Processor Performance";
//   3) Core: GetCpuFrequencyMhz devolve o clock ATUAL (nominal x %) — e ele MOVE:
//      o CallNtPowerInformation (fonte antiga) devolve o BASE FIXO (3500) enquanto
//      o clock real sobe para ~5 GHz com carga — é a prova de que o bug morreu;
//   4) o vital "Relógio" do Resumo mostra o clock atual (e não o base fixo);
//   5) o detalhe "base ... pico ..." aparece junto do número;
//   6) o card da CPU na aba Desempenho mostra "% + GHz" (estilo TM);
//   7) o painel de detalhes da CPU tem a linha viva "Velocidade atual:";
//   8) a barra superior da aba Processos mostra a frequência ATUAL.
//
// A janela e aberta FORA DA TELA (Left/Top = -32000): o usuario nao ve nada.
// Nada aqui altera o sistema (a carga é só aritmética em threads).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using KitLugia.Core.TaskManager;
using KitLugia.GUI.Windows.TaskManager;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern int CallNtPowerInformation(int infoClass, IntPtr inputBuffer, uint inputBufferLength, IntPtr outputBuffer, uint outputBufferLength);

    /// <summary>Clock pelo CallNtPowerInformation — a fonte ANTIGA (base fixo no Intel moderno).</summary>
    private static double ClockLegado()
    {
        int cores = Environment.ProcessorCount;
        IntPtr buf = Marshal.AllocHGlobal(24 * cores);
        try
        {
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, buf, (uint)(24 * cores)) != 0) return -1;
            long sum = 0; int n = 0;
            for (int i = 0; i < cores; i++)
            {
                uint cur = (uint)Marshal.ReadInt32(buf, i * 24 + 8);
                if (cur > 0) { sum += cur; n++; }
            }
            return n > 0 ? sum / (double)n : -1;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine("=== Relógio da CPU: valor ATUAL (turbo), como o Gerenciador de Tarefas ===");

        // ─────────────────────────────────────────────────────────────────
        // 0) QUIRK do WPF: TextBlock.Text com Inlines
        // ─────────────────────────────────────────────────────────────────
        // Medido nesta máquina: um TextBlock montado só com Inlines tem .Text == ""
        // (o texto vive nos Runs), enquanto outros TextBlocks da MESMA lista devolvem
        // o texto agregado. Por isso o harness lê os RUNS (é o que a tela mostra).
        {
            var p = new StackPanel();
            var t1 = new TextBlock();
            t1.Inlines.Add(new Run("A  "));
            t1.Inlines.Add(new Run("B") { Foreground = System.Windows.Media.Brushes.White });
            var t2 = new TextBlock();
            t2.Inlines.Add(new Run("C  "));
            var v2 = new Run("D") { Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold };
            t2.Inlines.Add(v2);
            var t3 = new TextBlock();
            t3.Text = "";
            t3.Inlines.Add(new Run("E  "));
            t3.Inlines.Add(new Run("F"));
            p.Children.Add(t1); p.Children.Add(t2); p.Children.Add(t3);
            Console.WriteLine($"  [quirk WPF] t1.Text='{t1.Text}' t2.Text='{t2.Text}' t3.Text='{t3.Text}'");
        }

        // ─────────────────────────────────────────────────────────────────
        // 1) CORE: base + % + clock atual
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 1) Core: NativeMetricsHelper (clock) ==");
        double baseMhz = NativeMetricsHelper.GetCpuBaseFrequencyMhz();
        double pct = NativeMetricsHelper.GetCpuProcessorPerformancePercent();
        double atual = NativeMetricsHelper.GetCpuFrequencyMhz();
        Console.WriteLine($"  base (nominal) = {baseMhz:F0} MHz");
        Console.WriteLine($"  % Processor Performance = {pct:F2}%");
        Console.WriteLine($"  clock atual = {atual:F0} MHz ({atual / 1000.0:F2} GHz)");
        Check(baseMhz > 500, $"a velocidade base é plausível ({baseMhz:F0} MHz)");
        Check(pct > 0 && pct <= 400, $"\"% Processor Performance\" devolveu um valor sane ({pct:F2}%)");
        Check(atual > 0, "o clock atual foi medido");
        if (baseMhz > 0 && pct > 0 && atual > 0)
            Check(Math.Abs(atual - baseMhz * pct / 100.0) <= baseMhz * 0.02,
                  $"clock atual == nominal x % ({atual:F0} vs {baseMhz * pct / 100.0:F0} MHz)");

        // ─────────────────────────────────────────────────────────────────
        // 2) PROVA DO BUG: sob carga o clock sobe (a fonte antiga ficava fixa)
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 2) Sob carga: o clock sai do base fixo ==");
        double legado = ClockLegado();
        Console.WriteLine($"  CallNtPowerInformation (fonte antiga) = {legado:F0} MHz");
        var cts = new CancellationTokenSource();
        var tasks = new Task[Environment.ProcessorCount];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                double x = 0;
                while (!cts.IsCancellationRequested) { for (int k = 0; k < 200000; k++) x += Math.Sqrt(k + x % 7); }
            });
        }
        double maxAtual = 0, maxPct = 0;
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(700);
            double a = NativeMetricsHelper.GetCpuFrequencyMhz();
            double p = NativeMetricsHelper.GetCpuProcessorPerformancePercent();
            if (a > maxAtual) maxAtual = a;
            if (p > maxPct) maxPct = p;
        }
        cts.Cancel();
        try { Task.WaitAll(tasks, 3000); } catch { }
        Console.WriteLine($"  pico com carga: {maxAtual:F0} MHz ({maxAtual / 1000.0:F2} GHz) / {maxPct:F1}%");
        Check(maxPct > 100, $"a CPU passou do clock nominal sob carga ({maxPct:F1}% > 100%)");
        Check(maxAtual > baseMhz, $"o clock medido subiu acima do base ({maxAtual:F0} > {baseMhz:F0} MHz)");
        if (legado > 0 && baseMhz > 0)
            Check(maxAtual > legado + 50,
                  $"o clock novo ({maxAtual:F0}) está acima do valor FIXO da fonte antiga ({legado:F0}) — o bug do 3,50 GHz congelado morreu");

        // ─────────────────────────────────────────────────────────────────
        // 3) INTERFACE REAL: janela WPF fora da tela
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 3) Janela real (Resumo + Desempenho) ==");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
        }
        catch { }

        app.DispatcherUnhandledException += (_, e) =>
        {
            Console.WriteLine($"   [excecao na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };

        var janela = new KitTaskManagerWindow
        {
            Width = 1400,
            Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        try { janela.Show(); } catch (Exception ex) { Console.WriteLine($"AVISO: Show() falhou: {ex.Message}"); }

        Pump(1500);
        try { janela.SwitchTabByTag("Summary"); } catch { }
        Pump(2500);

        // ── 3.1) vital "Relógio" do Resumo ────────────────────────────────
        string relogio = Texto(janela, "TxtSumClock") ?? "";
        string detalhe = Texto(janela, "TxtSumClockDetail") ?? "";
        Console.WriteLine($"  vital Relógio: \"{relogio}\" | detalhe: \"{detalhe}\"");
        double ghz = ParseGhz(relogio);
        Check(ghz > 0, $"o vital Relógio mostra um valor em GHz (obtido: \"{relogio}\")");
        Check(ghz > 0 && baseMhz > 0 && ghz * 1000.0 > baseMhz,
              $"o vital mostra o clock ATUAL, não o base fixo ({ghz:F2} GHz > {baseMhz / 1000.0:F2} GHz)");
        Check(ghz > 0 && maxAtual > 0 && Math.Abs(ghz * 1000.0 - maxAtual) <= maxAtual * 0.25,
              $"o valor na tela é da mesma ordem do medido na API ({ghz:F2} GHz vs {maxAtual / 1000.0:F2} GHz)");
        Check(detalhe.Contains("base") && detalhe.Contains("pico") && Regex.IsMatch(detalhe, @"[\d.,]+"),
              $"o detalhe mostra base e pico (obtido: \"{detalhe}\")");

        // ── 3.2) aba Desempenho: card da CPU + painel de detalhes ────────
        try { janela.SwitchTabByTag("Performance"); } catch { }
        Pump(1500);
        object? cpuDev = null;
        for (int tent = 0; tent < 40 && cpuDev == null; tent++)
        {
            Pump(400);
            cpuDev = Dispositivos(janela).FirstOrDefault(d => (Prop(d, "Key") as string) == "cpu");
        }
        Check(cpuDev != null, "o card de CPU existe na lista de dispositivos");
        if (cpuDev != null)
        {
            var lista = (ListBox?)Campo(janela, "LstPerfDevices");
            if (lista != null) { lista.SelectedItem = cpuDev; Pump(400); }

            string summary = "";
            for (int tent = 0; tent < 20; tent++)
            {
                Pump(500);
                summary = Prop(cpuDev, "Summary") as string ?? "";
                if (summary.Contains("GHz")) break;
            }
            Console.WriteLine($"  card da CPU (Summary): \"{summary}\"");
            Check(Regex.IsMatch(summary, @"^\d+%\s*[·•]?\s*[\d.,]+\s*GHz$"),
                  $"o card mostra \"% + GHz\" no estilo do TM (obtido: \"{summary}\")");
            double cardGhz = ParseGhz(summary);
            Check(cardGhz > 0 && baseMhz > 0 && cardGhz * 1000.0 > baseMhz,
                  $"o card da CPU mostra o clock ATUAL ({cardGhz:F2} GHz)");

            // painel de detalhes: linha viva "Velocidade atual:"
            string linhaAtual = "";
            string linhaBase = "";
            for (int tent = 0; tent < 20; tent++)
            {
                Pump(500);
                var linhas = LinhasDetalhes(janela);
                linhaAtual = linhas.FirstOrDefault(l => l.StartsWith("Velocidade atual:", StringComparison.Ordinal)) ?? "";
                linhaBase = linhas.FirstOrDefault(l => l.StartsWith("Velocidade base:", StringComparison.Ordinal)) ?? "";
                if (linhaAtual.Contains("GHz")) break;
            }
            Console.WriteLine($"  painel: \"{linhaAtual}\" | \"{linhaBase}\"");
            Console.WriteLine("  linhas do painel: " + string.Join(" || ", LinhasDetalhes(janela)));
            foreach (var tb in BlocosDetalhes(janela))
                Console.WriteLine($"    [Text='{tb.Text}' inlines={tb.Inlines.Count}] " +
                                  string.Join(" + ", tb.Inlines.Select(i => $"'{(i as Run)?.Text}'")));
            Check(linhaAtual.Contains("GHz"), "o painel da CPU tem a linha viva \"Velocidade atual:\" com o clock");
            double painelGhz = ParseGhz(linhaAtual);
            Check(painelGhz > 0 && baseMhz > 0 && painelGhz * 1000.0 > baseMhz,
                  $"a linha do painel mostra o clock ATUAL ({painelGhz:F2} GHz)");
            Check(linhaBase.Contains("MHz") || linhaBase.Contains("GHz"),
                  $"o painel continua mostrando a \"Velocidade base\" ({linhaBase})");
        }

        // ── 3.3) barra superior da aba Processos ────────────────────────
        string topo = Texto(janela, "TxtCpuFreq") ?? "";
        Console.WriteLine($"  barra superior: Frequência \"{topo}\"");
        Check(ParseGhz(topo) > 0, $"a barra superior mostra a frequência em GHz (obtido: \"{topo}\")");

        // ── 3.4) print da janela real ────────────────────────────────────
        try
        {
            try { janela.SwitchTabByTag("Performance"); } catch { }
            Pump(1200);
            SalvarPrint(janela, "kitlugia_clock_harness_perf.png");
            try { janela.SwitchTabByTag("Summary"); } catch { }
            Pump(1200);
            SalvarPrint(janela, "kitlugia_clock_harness.png");
        }
        catch (Exception ex) { Console.WriteLine("  (print falhou: " + ex.Message + ")"); }

        try { janela.Close(); } catch { }

        Console.WriteLine(_fail == 0 ? "\n== ALL-PASS ==" : $"\n== {_fail} FALHA(S) ==");
        return _fail == 0 ? 0 : 1;
    }

    // ── helpers ────────────────────────────────────────────────────────────
    private static void SalvarPrint(Window janela, string nome)
    {
        int w = (int)Math.Max(1, janela.ActualWidth);
        int h = (int)Math.Max(1, janela.ActualHeight);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(janela);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), nome);
        using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        Console.WriteLine($"  print da janela: {path}");
    }

    /// <summary>Extrai o número em GHz de textos como "4,95 GHz" (pt-BR) ou "4.95 GHz".</summary>
    private static double ParseGhz(string txt)
    {
        if (string.IsNullOrWhiteSpace(txt)) return -1;
        var m = Regex.Match(txt, @"([\d.,]+)\s*GHz");
        if (!m.Success) return -1;
        string s = m.Groups[1].Value.Replace(".", "").Replace(',', '.');
        // "4.95" -> "495" quando o ponto era separador de milhar; se sobrou ponto, normaliza.
        if (s.Count(c => c == '.') > 1) s = s.Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : -1;
    }

    private static List<string> LinhasDetalhes(object janela)
    {
        var res = new List<string>();
        foreach (var tb in BlocosDetalhes(janela))
        {
            // Lê os RUNS (o que a tela mostra): o .Text de um TextBlock montado só com
            // Inlines pode vir vazio (quirk do WPF medido neste harness).
            string txt = tb.Inlines.Count > 0
                ? string.Concat(tb.Inlines.Select(i => (i as Run)?.Text ?? ""))
                : tb.Text;
            res.Add(txt.Trim());
        }
        return res;
    }

    private static List<TextBlock> BlocosDetalhes(object janela)
    {
        var res = new List<TextBlock>();
        var painel = Campo(janela, "PnlPerfDetails") as Panel;
        if (painel == null) return res;
        foreach (var child in painel.Children)
        {
            if (child is TextBlock tb) res.Add(tb);
        }
        return res;
    }

    private static List<object> Dispositivos(object janela)
    {
        var col = Campo(janela, "_perfDevices");
        if (col is IEnumerable en)
        {
            var lista = new List<object>();
            foreach (var o in en) lista.Add(o!);
            return lista;
        }
        return new List<object>();
    }

    private static object? Campo(object obj, string nome) =>
        obj.GetType().GetField(nome, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(obj);

    private static string? Texto(object obj, string nome) =>
        Campo(obj, nome) is TextBlock tb ? tb.Text : null;

    private static object? Prop(object obj, string nome) =>
        obj.GetType().GetProperty(nome, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(obj);

    private static void Pump(int ms)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < limite)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
