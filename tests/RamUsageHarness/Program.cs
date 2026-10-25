// Harness da ABA DESEMPENHO: quanto de RAM está EM USE, além do %.
//
// O que prova (pela interface REAL, janela WPF fora da tela):
//   1) Core: TryGetPhysicalMemoryUsage devolve bytes coerentes com o % oficial;
//   2) o CARD "Memória" mostra o quanto usado (antes mostrava só "80% de 32 GB");
//   3) a linha "Em uso" do painel de detalhes mostra o quanto usado;
//   4) a barra superior da aba Processos mostra "X GB de Y GB" sob o %;
//   5) o número mostrado bate com a RAM real da máquina (não é texto decorativo).
//
// A janela e aberta FORA DA TELA (Left/Top = -32000): o usuario nao ve nada.
// Nada aqui altera o sistema.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

    [STAThread]
    private static int Main(string[] args)
    {
        Console.WriteLine("=== Uso de RAM em bytes (aba Desempenho) ===");

        // ─────────────────────────────────────────────────────────────────
        // 1) CORE: bytes em uso/total coerentes com o % oficial
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 1) Core: NativeMetricsHelper.TryGetPhysicalMemoryUsage ==");
        bool okBytes = NativeMetricsHelper.TryGetPhysicalMemoryUsage(out ulong used, out ulong total);
        Check(okBytes, "a API devolveu os bytes de RAM");
        if (okBytes)
        {
            double pctBytes = used * 100.0 / Math.Max(1UL, total);
            double pctApi = NativeMetricsHelper.GetMemoryUsagePercent();
            Console.WriteLine($"  usado   = {used} bytes ({used / 1024.0 / 1024.0 / 1024.0:F2} GB)");
            Console.WriteLine($"  total   = {total} bytes ({total / 1024.0 / 1024.0 / 1024.0:F2} GB)");
            Console.WriteLine($"  % pelos bytes = {pctBytes:F1}%   % da API = {pctApi:F1}%");
            Check(total > 0, "o total de RAM é maior que zero");
            Check(used > 0 && used <= total, "os bytes em uso estão entre 1 e o total");
            Check(Math.Abs(pctBytes - pctApi) <= 2.0,
                  $"o % derivado dos bytes bate com a API ({pctBytes:F1}% vs {pctApi:F1}%)");
        }

        // ─────────────────────────────────────────────────────────────────
        // 2) INTERFACE REAL: janela WPF, aba Desempenho, card de Memória
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 2) Janela real (aba Desempenho) ==");
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
        try { janela.SwitchTabByTag("Performance"); } catch { }
        Pump(500);

        // Espera a lista de dispositivos montar (BuildPerfDevicesAsync é async).
        object? memDev = null;
        for (int tent = 0; tent < 40 && memDev == null; tent++)
        {
            Pump(400);
            memDev = Dispositivos(janela).FirstOrDefault(d => (Prop(d, "Key") as string) == "mem");
        }
        Check(memDev != null, "o card de Memória existe na lista de dispositivos");
        if (memDev == null) { Console.WriteLine("== FIM (sem card de memória) =="); return 1; }

        // Seleciona o card (dispara RenderPerfDetails) e espera o tick de 1s
        // popular o resumo e a linha "Em uso".
        var lista = (ListBox?)Campo(janela, "LstPerfDevices");
        Check(lista != null, "a lista de dispositivos foi encontrada");
        if (lista != null)
        {
            lista.SelectedItem = memDev;
            Pump(300);
        }

        string summary = "";
        for (int tent = 0; tent < 20; tent++)
        {
            Pump(500);
            summary = Prop(memDev, "Summary") as string ?? "";
            if (summary.Contains("GB")) break;
        }
        Console.WriteLine($"  card de Memória (Summary): \"{summary}\"");

        // O texto precisa ter % + quanto usado + total: "80% · 25,6 GB de 32 GB".
        // O decimal pode vir com "," (pt-BR) ou "." (invariante) — aceita os dois.
        var re = new Regex(@"^(?<pct>\d+)%\s*[·•]\s*(?<usado>[\d.,]+)\s*GB\s+de\s+(?<total>[\d.,]+)\s*GB$",
                           RegexOptions.Compiled);
        var m = re.Match(summary);
        Check(m.Success, $"o card mostra % + quanto usado + total (formato \"80% · 25,6 GB de 32 GB\") — obtido: \"{summary}\"");
        if (m.Success)
        {
            double pct = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
            // O texto pode vir com "," (pt-BR) ou "." (invariante): normaliza para "." e parseia.
            double usado = double.Parse(m.Groups["usado"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            double totalTxt = double.Parse(m.Groups["total"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            Console.WriteLine($"  interpretado: {pct}% | usado {usado} GB | total {totalTxt} GB");
            Check(usado > 0, "o card mostra um valor de RAM em uso maior que zero");
            Check(usado <= totalTxt, "o valor em uso não passa do total");
            Check(Math.Abs(pct - (used * 100.0 / Math.Max(1UL, total))) <= 3.0,
                  $"o % do card bate com a RAM real da máquina ({pct}% vs {used * 100.0 / Math.Max(1UL, total):F1}%)");
            Check(Math.Abs(totalTxt - total / 1024.0 / 1024.0 / 1024.0) <= 1.0,
                  $"o total do card bate com a RAM instalada ({totalTxt} GB vs {total / 1024.0 / 1024.0 / 1024.0:F1} GB)");
        }

        // ── 2.1) linha "Em uso" do painel de detalhes ────────────────────
        string uso = "";
        for (int tent = 0; tent < 10; tent++)
        {
            Pump(400);
            uso = LinhaUso(janela);
            if (uso.Contains("GB")) break;
        }
        Console.WriteLine($"  linha \"Em uso\": \"{uso}\"");
        Check(uso.Contains("GB"), "a linha \"Em uso\" do painel mostra quantos GB estão em uso");
        Check(re.IsMatch(uso.Trim()), $"a linha \"Em uso\" tem o formato % + GB usado + total — obtido: \"{uso}\"");

        // ── 2.2) barra superior da aba Processos ─────────────────────────
        string topo = Texto(janela, "TxtMemUsageDetail") ?? "";
        string pctTopo = Texto(janela, "TxtMemUsage") ?? "";
        Console.WriteLine($"  barra superior: Memória {pctTopo} / \"{topo}\"");
        Check(pctTopo.Contains("%"), "a barra superior continua mostrando o % de RAM");
        Check(topo.Contains("GB") && Regex.IsMatch(topo, @"[\d.,]+\s*GB\s+de\s+[\d.,]+\s*GB"),
              $"a barra superior mostra \"X GB de Y GB\" — obtido: \"{topo}\"");

        // ── 2.3) Prova VISUAL: print da janela real (card + painel) ──────
        try
        {
            int w = (int)Math.Max(1, janela.ActualWidth);
            int h = (int)Math.Max(1, janela.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(janela);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kitlugia_ram_harness.png");
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  print da janela: {path}");
        }
        catch (Exception ex) { Console.WriteLine("  (print falhou: " + ex.Message + ")"); }

        try { janela.Close(); } catch { }

        Console.WriteLine(_fail == 0 ? "\n== ALL-PASS ==" : $"\n== {_fail} FALHA(S) ==");
        return _fail == 0 ? 0 : 1;
    }

    // ── helpers ────────────────────────────────────────────────────────────
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

    /// <summary>Texto do valor da linha "Em uso" (o Run guardado no Tag do TextBlock).</summary>
    private static string LinhaUso(object janela)
    {
        var tb = Campo(janela, "_perfUsageLine") as TextBlock;
        if (tb == null) return "";
        if (tb.Tag is Run r) return r.Text;
        return tb.Text;
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
            System.Threading.Thread.Sleep(10);
        }
    }
}
