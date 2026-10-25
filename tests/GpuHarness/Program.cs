// Harness da ABA DESEMPENHO: as MEDIÇÕES DE GPU (estilo Gerenciador de Tarefas).
//
// O que prova (pela interface REAL, janela WPF fora da tela):
//   1) Core: DXGI enumera a GPU (GUID/vtable corrigidos — antes a lista saía VAZIA);
//   2) Core: GpuMetrics junta driver/data/local PCI/VRAM/DirectX/temperatura;
//   3) Core: o PDH devolve engines + VRAM em uso e a chave LUID casa com a do DXGI;
//   4) o CARD da GPU mostra "N% (T °C)" (estilo TM);
//   5) o painel de detalhes tem as linhas VIVAS: 3D / Copy / Video Encode /
//      Video Decode / memória dedicada / memória compartilhada / DirectX / temperatura;
//   6) os números da tela batem com a medição real da máquina.
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
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Medicoes de GPU (aba Desempenho) ===");

        // ─────────────────────────────────────────────────────────────────
        // 1) CORE: DXGI enumera os adaptadores
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 1) Core: GpuInfo.GetAll() (DXGI) ==");
        var dxgi = GpuInfo.GetAll();
        Check(dxgi.Count >= 1, $"DXGI enumerou pelo menos 1 adaptador (obtido: {dxgi.Count})");
        foreach (var d in dxgi)
        {
            Console.WriteLine($"  {d.Name} | ven={d.VendorId:X4} dev={d.DeviceId:X4} | ded={d.DedicatedVideoMemory} | shared={d.SharedSystemMemory} | {d.LuidKey}");
            Check(!string.IsNullOrWhiteSpace(d.Name), $"[{d.Name}] o adaptador tem nome");
            Check(d.VendorId != 0, $"[{d.Name}] o VendorId nao e zero");
            Check(Regex.IsMatch(d.LuidKey, @"^luid_0x[0-9A-F]{8}_0x[0-9A-F]{8}$"),
                  $"[{d.Name}] a chave LUID tem o formato de PDH ({d.LuidKey})");
        }
        if (dxgi.Count == 0) { Console.WriteLine("== FIM (sem GPU) =="); return 1; }

        // ─────────────────────────────────────────────────────────────────
        // 2) CORE: GpuMetrics (identidade + driver + memoria + DirectX)
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 2) Core: GpuMetrics.GetAdapters() ==");
        var ads = GpuMetrics.GetAdapters();
        Check(ads.Count == dxgi.Count, $"a cascata devolveu os mesmos {dxgi.Count} adaptador(es)");
        foreach (var a in ads)
        {
            Console.WriteLine($"  [{a.Index}] {a.Name}");
            Console.WriteLine($"      driver={a.DriverVersion} | data={a.DriverDate} | directx={a.DirectXVersion}");
            Console.WriteLine($"      pci={a.PciLocation}");
            Console.WriteLine($"      vram={a.VramTotalBytes} ({a.VramTotalBytes / 1073741824.0:F2} GiB) | reservada={a.ReservedBytes} | temp={a.TemperatureC}");
            Check(!string.IsNullOrWhiteSpace(a.Name) && a.Name != "—", $"[{a.Name}] o adaptador tem nome comercial");
            Check(a.DriverVersion != "—", $"[{a.Name}] tem versao de driver ({a.DriverVersion})");
            Check(a.DriverDate != "—", $"[{a.Name}] tem data de driver ({a.DriverDate})");
            Check(a.PciLocation.StartsWith("Barramento PCI", StringComparison.OrdinalIgnoreCase),
                  $"[{a.Name}] tem local fisico no formato do TM ({a.PciLocation})");
            Check(a.VramTotalBytes > 0, $"[{a.Name}] tem VRAM total apurada ({a.VramTotalBytes} bytes)");
            Check(a.DirectXVersion.StartsWith("12 (FL ", StringComparison.OrdinalIgnoreCase),
                  $"[{a.Name}] tem versao do DirectX com feature level ({a.DirectXVersion})");
        }

        // ─────────────────────────────────────────────────────────────────
        // 3) CORE: PDH (engines + VRAM em uso) com a chave LUID do DXGI
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 3) Core: GpuMonitor.GetAdapterLive() (PDH) ==");
        GpuMonitor.GetAdapterLive();               // primer: a 1a coleta so arma o contador
        System.Threading.Thread.Sleep(900);
        var live = GpuMonitor.GetAdapterLive();
        Check(live.Count >= 1, $"o PDH devolveu adaptadores com dados (obtido: {live.Count})");
        var principal = ads[0];
        Check(live.ContainsKey(principal.LuidKey),
              $"a chave LUID do PDH casa com a do DXGI ({principal.LuidKey})");
        if (live.TryGetValue(principal.LuidKey, out var lv))
        {
            Console.WriteLine($"  3D={lv.Util3D:F1}% Copy={lv.UtilCopy:F1}% Encode={lv.UtilEncode:F1}% Decode={lv.UtilDecode:F1}%");
            Console.WriteLine($"  dedUsada={lv.DedicatedUsedBytes} ({lv.DedicatedUsedBytes / 1073741824.0:F2} GiB) | sharedUsada={lv.SharedUsedBytes}");
            Check(lv.Util3D >= 0 && lv.UtilCopy >= 0 && lv.UtilEncode >= 0 && lv.UtilDecode >= 0,
                  "as 4 engines tem valor (>= 0)");
            Check(lv.Utilization >= 0 && lv.Utilization <= 100, $"a utilizacao do adaptador esta em 0..100 ({lv.Utilization:F1}%)");
            Check(lv.DedicatedUsedBytes > 0, $"a VRAM dedicada em uso e maior que zero ({lv.DedicatedUsedBytes} bytes)");
            Check(lv.DedicatedUsedBytes <= principal.VramTotalBytes,
                  "a VRAM em uso nao passa da VRAM total");
        }
        // Temperatura (nvidia-smi): medicao independente da UI
        var smi = GpuMetrics.GetNvidiaSmi();
        double smiTemp = smi.Count > 0 ? smi[0].TemperatureC : -1;
        Console.WriteLine($"  nvidia-smi: {smi.Count} linha(s), temperatura = {smiTemp} °C");
        Check(smiTemp > 0, $"o nvidia-smi devolveu temperatura da GPU ({smiTemp:F0} °C)");

        // ─────────────────────────────────────────────────────────────────
        // 4) INTERFACE REAL: janela WPF, aba Desempenho, card da GPU
        // ─────────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 4) Janela real (aba Desempenho) ==");
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
            Width = 1500,
            Height = 1100,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        try { janela.Show(); } catch (Exception ex) { Console.WriteLine($"AVISO: Show() falhou: {ex.Message}"); }

        Pump(1500);
        try { janela.SwitchTabByTag("Performance"); } catch { }
        Pump(500);

        // Espera a lista montar (BuildPerfDevicesAsync e async)
        object? gpuDev = null;
        for (int tent = 0; tent < 40 && gpuDev == null; tent++)
        {
            Pump(400);
            gpuDev = Dispositivos(janela).FirstOrDefault(d =>
                (Prop(d, "Key") as string ?? "").StartsWith("gpu:") &&
                (Prop(d, "Key") as string) != "gpu:-1");
        }
        Check(gpuDev != null, "o card da GPU existe na lista de dispositivos");
        if (gpuDev == null) { Console.WriteLine("== FIM (sem card de GPU) =="); return 1; }

        var lista = (ListBox?)Campo(janela, "LstPerfDevices");
        Check(lista != null, "a lista de dispositivos foi encontrada");
        if (lista != null)
        {
            lista.SelectedItem = gpuDev;
            Pump(400);
        }

        // ── 4.1) o CARD mostra "N% (T °C)" (estilo Gerenciador de Tarefas) ──
        string summary = "";
        for (int tent = 0; tent < 30; tent++)
        {
            Pump(400);
            summary = Prop(gpuDev, "Summary") as string ?? "";
            if (summary.Contains("%")) break;
        }
        Console.WriteLine($"  card da GPU (Summary): \"{summary}\"");
        var reCard = new Regex(@"^(?<pct>\d+)%(\s*\((?<temp>\d+)\s*°C\))?$", RegexOptions.Compiled);
        var mCard = reCard.Match(summary.Trim());
        Check(mCard.Success, $"o card tem o formato \"12% (45 °C)\" — obtido: \"{summary}\"");
        if (mCard.Success && mCard.Groups["temp"].Success)
        {
            double tCard = double.Parse(mCard.Groups["temp"].Value, CultureInfo.InvariantCulture);
            Console.WriteLine($"  card: {mCard.Groups["pct"].Value}% | {tCard:F0} °C");
            Check(Math.Abs(tCard - smiTemp) <= 5,
                  $"a temperatura do card bate com o nvidia-smi ({tCard:F0} °C vs {smiTemp:F0} °C)");
        }

        // ── 4.2) painel de detalhes: as linhas VIVAS do TM ─────────────────
        var linhas = LerLinhas(janela);
        foreach (var kv in linhas) Console.WriteLine($"    painel | {kv.Key} = {kv.Value}");

        // As linhas NAO podem se sobrepor: cada uma ocupa uma celula propria
        // da grade de 2 colunas (regressao: sem Grid.SetRow todas caem na
        // linha 0 e o texto se sobrepoe na tela).
        var celulas = new HashSet<string>();
        var sobrepostos = new List<string>();
        foreach (var child in ((Panel)Campo(janela, "PnlPerfDetails")!).Children)
        {
            if (child is not TextBlock tb) continue;
            string celula = $"{System.Windows.Controls.Grid.GetRow(tb)}:{System.Windows.Controls.Grid.GetColumn(tb)}";
            string txt = string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty));
            if (!celulas.Add(celula)) sobrepostos.Add(txt);
        }
        Check(sobrepostos.Count == 0,
              $"nenhuma linha do painel se sobrepoe (celulas distintas) — repetidas: {string.Join("; ", sobrepostos)}");

        // DIAG: altura real do painel e de cada linha (regressão de layout)
        if (Campo(janela, "PnlPerfDetails") is Grid gP)
        {
            gP.UpdateLayout();
            var itens = gP.Children.OfType<TextBlock>().ToList();
            int rows = itens.Count == 0 ? 0 : itens.Max(t => System.Windows.Controls.Grid.GetRow(t)) + 1;
            Console.WriteLine($"    [diag] painel {gP.ActualWidth}x{gP.ActualHeight} rows={gP.RowDefinitions.Count} (usadas {rows}) filhos={itens.Count}");
            foreach (var tb in itens)
            {
                Console.WriteLine($"      linha r={System.Windows.Controls.Grid.GetRow(tb)} c={System.Windows.Controls.Grid.GetColumn(tb)} " +
                                  $"pos=({tb.ActualWidth}x{tb.ActualHeight}) em ({tb.TranslatePoint(new System.Windows.Point(0, 0), gP).X:F0},{tb.TranslatePoint(new System.Windows.Point(0, 0), gP).Y:F0}) " +
                                  $"\"{string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty))}\"");
            }
            // Regressão: SEM RowDefinitions o WPF limita o Grid.Row em 0 e todas as
            // linhas caem na mesma celula (texto sobreposto na tela).
            Check(gP.RowDefinitions.Count >= rows,
                  $"a grade tem uma linha por par de linhas do painel (definidas {gP.RowDefinitions.Count}, usadas {rows})");
            Check(gP.ActualHeight >= rows * 12,
                  $"o painel tem altura para todas as linhas ({gP.ActualHeight}px para {rows} linhas)");
            foreach (var child in gP.Children)
            {
                if (child is not TextBlock tb) continue;
                Console.WriteLine($"      linha r={System.Windows.Controls.Grid.GetRow(tb)} c={System.Windows.Controls.Grid.GetColumn(tb)} " +
                                  $"pos=({tb.ActualWidth}x{tb.ActualHeight}) em ({tb.TranslatePoint(new System.Windows.Point(0, 0), gP).X:F0},{tb.TranslatePoint(new System.Windows.Point(0, 0), gP).Y:F0}) " +
                                  $"\"{string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? string.Empty))}\"");
            }
        }

        foreach (var chave in new[] { "Utilização", "3D", "Copy", "Video Encode", "Video Decode",
                                       "Memória da GPU dedicada", "Memória da GPU compartilhada",
                                       "Versão do DirectX", "Temperatura", "Versão do driver",
                                       "Data do driver", "Local físico" })
        {
            Check(linhas.ContainsKey(chave), $"o painel tem a linha \"{chave}\"");
        }

        // As engines mostradas batem com o PDH
        if (linhas.TryGetValue("3D", out var l3d))
        {
            double v3d = ParsePct(l3d);
            Check(v3d >= 0 && v3d <= 100, $"a linha 3D mostra um valor em 0..100% (\"{l3d}\")");
        }

        // Memória da GPU dedicada: "usada/total" e o TOTAL bate com a medição real
        if (linhas.TryGetValue("Memória da GPU dedicada", out var lDed))
        {
            var m = Regex.Match(lDed, @"(?<usada>[\d.,]+)\s*GB\s*/\s*(?<total>[\d.,]+)\s*GB");
            Check(m.Success, $"a linha de memoria dedicada tem o formato \"2,1/15,9 GB\" — obtido: \"{lDed}\"");
            if (m.Success)
            {
                double usada = ParseNum(m.Groups["usada"].Value);
                double total = ParseNum(m.Groups["total"].Value);
                double totalReal = principal.VramTotalBytes / 1073741824.0;
                Console.WriteLine($"    dedicada: {usada:F1} GB / {total:F1} GB (real: {totalReal:F1} GB)");
                Check(Math.Abs(total - totalReal) <= 0.3,
                      $"o TOTAL de VRAM da tela bate com a medicao real ({total:F1} GB vs {totalReal:F1} GB)");
                Check(usada >= 0 && usada <= total + 0.3, "a VRAM em uso cabe no total");
            }
        }

        // Temperatura do painel
        if (linhas.TryGetValue("Temperatura", out var lTemp))
        {
            var m = Regex.Match(lTemp, @"(?<t>\d+)\s*°C");
            Check(m.Success, $"a linha de temperatura mostra \"45 °C\" — obtido: \"{lTemp}\"");
            if (m.Success)
            {
                double t = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
                Check(Math.Abs(t - smiTemp) <= 5, $"a temperatura do painel bate com o nvidia-smi ({t:F0} °C vs {smiTemp:F0} °C)");
            }
        }

        // DirectX
        if (linhas.TryGetValue("Versão do DirectX", out var lDx))
            Check(Regex.IsMatch(lDx, @"12\s*\(FL\s*1[0-9]\.[0-9]\)"), $"a linha do DirectX mostra \"12 (FL 12.2)\" — obtido: \"{lDx}\"");

        // ── 4.2b) PAINEL GRANDE da GPU: 4 curvas de engine + 2 barras de VRAM ──
        // (pedido 24/10: "mostrar as outras metricas da GPU, tem espaco")
        Console.WriteLine("\n== 4.2b) Painel grande da GPU (estilo TM): 4 engines + 2 barras ==");
        for (int i = 0; i < 6; i++) Pump(500);   // deixa o historico encher (>= 2 amostras)
        var graficos = Campo(janela, "PnlGpuGraphs") as Grid;
        var canvasUnico = Campo(janela, "PerfBigCanvas") as Canvas;
        Check(graficos != null, "o painel de graficos da GPU existe na janela");
        Check(canvasUnico != null && canvasUnico.Visibility != Visibility.Visible,
              $"o grafico unico fica escondido quando a GPU esta selecionada ({(canvasUnico?.Visibility.ToString() ?? "?")})");
        if (graficos != null)
        {
            graficos.UpdateLayout();
            Pump(200);
            Console.WriteLine($"    painel {graficos.ActualWidth}x{graficos.ActualHeight} | filhos={graficos.Children.Count} | visivel={graficos.Visibility}");
            Check(graficos.Visibility == Visibility.Visible, $"o painel de graficos da GPU esta visivel ({graficos.Visibility})");
            Check(graficos.Children.Count == 6, $"4 curvas de engine + 2 barras de VRAM (obtido: {graficos.Children.Count})");
            Check(graficos.ActualHeight >= 80, $"o painel ocupa a altura do grafico antigo ({graficos.ActualHeight:F0}px)");

            // Cada bloco: 1 TextBlock (rotulo + valor) + 1 Canvas (curva ou trilho)
            var blocos = new List<(string Texto, Canvas Cv)>();
            foreach (var child in graficos.Children.OfType<Panel>())
            {
                var cvs = child.Children.OfType<Canvas>().ToList();
                var tb = child.Children.OfType<TextBlock>().FirstOrDefault();
                if (cvs.Count == 1 && tb != null)
                    blocos.Add((string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? "")), cvs[0]));
            }
            Console.WriteLine($"    blocos detectados: {blocos.Count}");
            Check(blocos.Count == 6, $"os 6 blocos foram montados (obtido: {blocos.Count})");

            foreach (var (texto, cv) in blocos)
                Console.WriteLine($"      blocos | {texto.Trim()} | canvas {cv.ActualWidth:F0}x{cv.ActualHeight:F0} | filhos={cv.Children.Count}");

            var engines = blocos.Where(b => b.Texto.Contains('%')).ToList();
            Check(engines.Count == 4, $"as 4 engines mostram o % no rotulo (obtido: {engines.Count})");
            foreach (var (texto, cv) in engines)
            {
                // DrawLineChart desenha 2 Paths (area preenchida + linha)
                Check(cv.Children.Count >= 2, $"[{texto.Trim()}] a curva foi desenhada ({cv.Children.Count} elemento(s))");
                Check(cv.ActualHeight >= 10, $"[{texto.Trim()}] a curva tem altura ({cv.ActualHeight:F0}px)");
            }

            // Barras de VRAM: o preenchimento e' proporcional (largura > 0 com uso > 0)
            var barras = blocos.Where(b => b.Texto.Contains("Memória da GPU")).ToList();
            Check(barras.Count == 2, $"as 2 barras de VRAM existem (obtido: {barras.Count})");
            foreach (var (texto, cv) in barras)
            {
                var fill = cv.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault();
                Console.WriteLine($"      barra | {texto.Trim()} | trilho {cv.ActualWidth:F0}px | preenchido {fill?.Width:F0}px");
                Check(fill != null, $"[{texto.Trim()}] a barra tem preenchimento");
                if (fill != null)
                {
                    Check(fill.Width >= 1, $"[{texto.Trim()}] o preenchimento e' proporcional ao uso ({fill.Width:F0}px)");
                    Check(fill.Width <= cv.ActualWidth + 1, $"[{texto.Trim()}] o preenchimento cabe no trilho");
                }
            }
        }

        // ── 4.3) o dxdiag (background) sobe o feature level sozinho ──────
        // O fallback D3D11 devolve no máximo 12.1; o dxdiag traz o 12.2 real.
        // A linha é VIVA, então a tela se corrige sozinha quando o dxdiag termina.
        Console.WriteLine("\n== 5) DirectX: o dxdiag em background sobe o FL 12.1 -> 12.2 ==");
        string dx = "";
        for (int tent = 0; tent < 60; tent++)
        {
            Pump(1000);
            dx = LerLinhas(janela).TryGetValue("Versão do DirectX", out var v2) ? v2 : "";
            if (dx.Contains("12.2")) break;
        }
        Console.WriteLine($"  linha do DirectX: \"{dx}\"");
        Check(dx.Contains("12.2"), $"a linha do DirectX chegou a \"12 (FL 12.2)\" (dxdiag) — obtido: \"{dx}\"");

        // ── 4.4) Prova VISUAL: print da janela real (card + painel) ──────
        try
        {
            // O RenderTargetBitmap NAO forca layout: sem UpdateLayout o print sai
            // com as posicoes antigas (linhas sobrepostas).
            janela.UpdateLayout();
            Pump(400);
            janela.UpdateLayout();
            Pump(200);
            int w = (int)Math.Max(1, janela.ActualWidth);
            int h = (int)Math.Max(1, janela.ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(janela);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kitlugia_gpu_harness.png");
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            Console.WriteLine($"  print da janela: {path}");

            // Print do PAINEL DE DETALHES sozinho (o render da janela fora da tela
            // pode sair com o layout antigo; o painel isolado e a prova das linhas).
            if (Campo(janela, "PnlPerfDetails") is FrameworkElement painel)
            {
                painel.UpdateLayout();
                Pump(200);
                painel.UpdateLayout();
                var rp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)painel.ActualWidth), Math.Max(1, (int)painel.ActualHeight),
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rp.Render(painel);
                var ep = new System.Windows.Media.Imaging.PngBitmapEncoder();
                ep.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rp));
                string pp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kitlugia_gpu_painel.png");
                using (var fs2 = System.IO.File.Create(pp)) ep.Save(fs2);
                Console.WriteLine($"  print do painel: {pp} ({painel.ActualWidth}x{painel.ActualHeight})");
            }
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

    /// <summary>Lê as linhas do painel de detalhes ("Rótulo" → texto do valor).</summary>
    private static Dictionary<string, string> LerLinhas(object janela)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Campo(janela, "PnlPerfDetails") is not Panel painel) return res;
        int n = 0;
        foreach (var child in painel.Children)
        {
            n++;
            if (child is not TextBlock tb) { Console.WriteLine($"    [painel] filho {n}: nao e TextBlock ({child.GetType().Name})"); continue; }
            // Lê os INLINES: o TextBlock.Text só materializa o texto das linhas
            // ESTATICAS depois do layout (quirk do WPF) — ler os Runs é sempre certo.
            string texto = string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text ?? ""));
            int sep = texto.IndexOf("  ", StringComparison.Ordinal);
            string rotulo = (sep > 0 ? texto[..sep] : texto).Trim().TrimEnd(':');
            string valor = sep > 0 ? texto[(sep + 2)..].Trim() : "";
            if (rotulo.Length > 0) res[rotulo] = valor;
            else Console.WriteLine($"    [painel] linha vazia: \"{texto}\"");
        }
        Console.WriteLine($"    (painel: {n} filhos, {res.Count} linhas lidas)");
        return res;
    }

    private static double ParsePct(string s)
    {
        var m = Regex.Match(s, @"([\d.,]+)");
        return m.Success ? ParseNum(m.Groups[1].Value) : -1;
    }

    private static double ParseNum(string s)
        => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private static object? Campo(object obj, string nome) =>
        obj.GetType().GetField(nome, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(obj);

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
