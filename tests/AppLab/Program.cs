// AppLab - controle do app REAL (paginas do KitLugia) fora do processo principal.
// NAO substitui o app: cria a pagina de verdade, mede, renderiza PNG e
// devolve veredito objetivo. Read-only (nao toca registro nem services).
//
// Por que um harness e nao "olhar a tela":
//  - o TextBox do XAML tem Text="2048" mas no print do usuario aparece VAZIO.
//    Sao 2 hipoteses: (a) o LoadSettings limpou o texto, (b) o texto esta
//    la mas o estilo implicito do tema pinta em cor invisivel.
//  - Ler .Text responde (a). Contar pixel CLARO dentro do retangulo do
//    TextBox responde (b). As duas juntas fecham o diagnostico.
//
// Quirks herdados de docs/TASKMANAGER_UI_DEBUG.md e tests/NetPageVis:
//  - StaticResource do App.xaml exige Themes/Generic.xaml em Application.Resources
//  - RenderTargetBitmap.Render(child) inclui o offset do filho no pai: para
//    recortar, renderiza-se a PAGINA e usa-se TransformToAncestor(page)
//  - RenderTargetBitmap reaproveita tiles: Invalidar Measure/Arrange antes
//  - Window.Loaded dispara DENTRO de Show(): handler antes do Show
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.GUI.Pages;

// O projeto do kit tem UseWindowsForms, entao os usings globais trazem
// System.Windows.Forms e AMBIGUAM com WPF (CS0104) - padrao do AGENTS.md.
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

class Program
{
    static readonly string[] Noiseiras = new[]
    {
        "MONITORAMENTO", "MONITORAMENTO AVAN", "AVAN"
    };

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "out";
        Directory.CreateDirectory(outDir);

        // A varredura de XAML le o FONTE: o repo e ../../ a partir do .csproj
        // (regra 1 do AGENTS.md - nada de caminho absoluto na máquina).
        string repoRoot = FindRepoRoot();

        int fail = 0, ok = 0;
        void Check(string what, bool cond, string detail = "")
        {
            if (cond) { ok++; Console.WriteLine($"  OK   {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
        }

        Console.WriteLine("AppLab - SettingsPage: limiares de RAM/CPU por processo");
        Console.WriteLine(new string('=', 70));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        int antes = app.Resources.MergedDictionaries.Count;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml", UriKind.Absolute)
            });
            Check("Generic.xaml carregado", app.Resources.MergedDictionaries.Count > antes);
        }
        catch (Exception ex)
        {
            Check("Generic.xaml carregado", false, ex.GetType().Name + ": " + ex.Message);
        }

        SettingsPage page = null;
        try
        {
            page = new SettingsPage();
            Check("SettingsPage instanciou (XAML parseou)", true);
        }
        catch (Exception ex)
        {
            Check("SettingsPage instanciou (XAML parseou)", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex);
            Environment.Exit(1);
            return 1;
        }

        // A pagina carrega as preferencias de forma fire-and-forget no ctor.
        // Sem MainWindow/TrayService o LoadSettings aborta cedo e o XAML
        // mantem Text="2048"/Text="80" - que e o cenario deste harness.
        page.UpdateLayout();
        Pump(600);

        var ram = Find(page, "TxtHighRamThreshold") as TextBox;
        var cpu = Find(page, "TxtHighCpuThreshold") as TextBox;
        Check("TxtHighRamThreshold existe como TextBox", ram != null);
        Check("TxtHighCpuThreshold existe como TextBox", cpu != null);

        foreach (var (tb, nome) in new[] { (ram, "RAM"), (cpu, "CPU") })
        {
            if (tb == null) continue;

            // --- hipotese (a): o texto sumiu ---
            Check($"{nome}: .Text nao esta vazio",
                  !string.IsNullOrWhiteSpace(tb.Text),
                  $"valor='{tb.Text}'");

            // --- hipotese (b): o texto esta la mas e invisivel ---
            // Este e O teste do bug do print: o default do WPF para Foreground
            // de um TextBox sem estilo e PRETO (#FF000000) e some no card
            // escuro. Exigimos que a cor resolvida seja CLARA.
            var fg = tb.Foreground as SolidColorBrush;
            Check($"{nome}: Foreground CLARO (texto visivel no tema escuro)",
                  fg != null && Luminancia(fg.Color) > 140,
                  fg != null ? fg.Color.ToString() : (tb.Foreground?.ToString() ?? "null"));

            Console.WriteLine($"        {nome}: IsEnabled={tb.IsEnabled} Opacity={tb.Opacity} " +
                              $"FontSize={tb.FontSize} Width={tb.Width} Text='{tb.Text}'");
        }

        // Status abaixo do titulo: preenchido por UpdateThresholdStatus(), que le
        // TrayService. Sem MainWindow/TrayService neste harness ele nao roda
        // (e o codigo trata tray==null sem estourar). No app real o print
        // mostra "Avisa acima de 2048 MB..." preenchido. Aqui exigimos so
        // que o TextBlock EXISTA (a ligacao XAML <-> code-behind).
        var stRam = Find(page, "TxtHighRamStatus") as TextBlock;
        var stCpu = Find(page, "TxtHighCpuStatus") as TextBlock;
        Check("TxtHighRamStatus existe (ligacao XAML ok)", stRam != null);
        Check("TxtHighCpuStatus existe (ligacao XAML ok)", stCpu != null);
        if (stRam != null && stCpu != null)
            Console.WriteLine($"        status RAM='{stRam.Text}' CPU='{stCpu.Text}' " +
                              $"(vazio aqui = sem TrayService; preenchido no app real)");

        // ---------- RENDER: prova visual ----------
        // Janela alta o bastante para o card inteiro existir no layout (sem
        // ScrollViewer cortando), e o render da PAGINA (nao do TextBox) para
        // as coordenadas de recorte baterem.
        int W = 1500, H = 4200;
        var window = new Window
        {
            Width = W,
            Height = H,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            Content = page
        };
        window.Show();
        Pump(700);
        page.UpdateLayout();
        Pump(300);

        var png = Path.Combine(outDir, "settings_thresholds.png");
        try
        {
            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            Invalidate(page);
            rtb.Render(page);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(png)) enc.Save(fs);
            Check("PNG da pagina gerado", File.Exists(png), $"{new FileInfo(png).Length} bytes");
        }
        catch (Exception ex)
        {
            Check("PNG da pagina gerado", false, ex.GetType().Name + ": " + ex.Message);
        }

        // Recorte 3x de cada campo: se o digito NAO pinta, a faixa fica vazia
        // mesmo com .Text preenchido (prova a hipotese (b) com pixels).
        foreach (var (tb, nome) in new[] { (ram, "RAM"), (cpu, "CPU") })
        {
            if (tb == null) continue;
            try
            {
                var box = tb.TransformToAncestor(page).TransformBounds(
                    new Rect(new Point(0, 0), tb.RenderSize));
                int x = (int)box.X - 6, y = (int)box.Y - 6;
                int w = (int)box.Width + 12, h = (int)box.Height + 12;
                Check($"{nome}: campo tem geometria laid-out",
                      box.Width > 20 && box.Height > 8,
                      $"{box.Width:F0}x{box.Height:F0} em ({box.X:F0},{box.Y:F0})");

                var crop = new CroppedBitmap(LoadPng(png), new Int32Rect(
                    Math.Max(0, x), Math.Max(0, y), Math.Max(1, w), Math.Max(1, h)));
                // Assinatura real: (source, format, palette, alphaThreshold).
                var esc = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, (BitmapPalette)null, 0.0);
                var esc3 = new TransformedBitmap(esc, new ScaleTransform(3, 3));
                var cp = Path.Combine(outDir, $"campo_{nome.ToLowerInvariant()}.png");
                var e3 = new PngBitmapEncoder();
                e3.Frames.Add(BitmapFrame.Create(esc3));
                using (var fs = File.Create(cp)) e3.Save(fs);

                int claros = ContarPixelsClaros(esc);
                Check($"{nome}: texto PINTA dentro do campo (pixels claros)",
                      claros >= 8,
                      $"{claros} pixel(s) claro(s) no recorte 3x");
            }
            catch (Exception ex)
            {
                Check($"{nome}: recorte", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------- VARREDURA GLOBAL DO XAML ----------
        // O TextBox sem Style e sem Foreground herda o default do WPF =
        // PRETO (#FF000000) e some no card escuro. Foi exatamente o que o
        // print do usuario mostrou (os limiares "vazios" com o dado certo
        // em .Text). Este bloco impede a regressao em QUALQUER pagina nova.
        Console.WriteLine();
        Console.WriteLine("Varredura global: TextBox sem Style e sem Foreground");
        Console.WriteLine(new string('-', 70));
        var ruins = FindTextBoxesSemEstilo(repoRoot);
        Check("nenhum TextBox com texto preto invisivel",
              ruins.Count == 0,
              ruins.Count == 0 ? "todos tem Style ou Foreground"
                               : $"{ruins.Count} ACHADO(S): " + string.Join(", ",
                                   ruins.Take(6).Select(r => $"{r.File}:{r.Line} {r.Name}")));

        Console.WriteLine();
        Console.WriteLine("Politica de log: Logger.LogOnce (fala so na mudanca)");
        Console.WriteLine(new string('-', 70));
        {
            // Conta SO as mensagens com o marcador unico: contar todos os
            // OnLogReceived era flaky (qualquer log paralelo do processo
            // deslocava a contagem -> 2 falhas em 1 de 3 execucoes).
            const string MARCA = "kl-applab-marca-9f3c";
            int recebidas = 0;
            System.Action<object> handler = m =>
            {
                if (m is string s && s.Contains(MARCA)) recebidas++;
            };
            KitLugia.Core.Logger.OnLogReceived += handler;
            string key = "applab|teste";
            KitLugia.Core.Logger.ResetLogState(key);

            // 1) primeira sempre passa
            KitLugia.Core.Logger.LogOnce(key, MARCA + " estado A", 3600);
            Check("LogOnce: 1a chamada passa", recebidas == 1, $"recebidas={recebidas}");

            // 2) 500 repeticoes IDENTICAS com heartbeat de 1 h = nada passa
            for (int i = 0; i < 500; i++) KitLugia.Core.Logger.LogOnce(key, MARCA + " estado A", 3600);
            Check("LogOnce: 500 repeticoes identicas nao viram log", recebidas == 1,
                  $"recebidas={recebidas} (era 501 antes da correcao)");

            // 3) mudanca de estado passa
            KitLugia.Core.Logger.LogOnce(key, MARCA + " estado B", 3600);
            Check("LogOnce: mudanca de estado passa", recebidas == 2, $"recebidas={recebidas}");

            // 4) heartbeat curto reemite com a contagem
            KitLugia.Core.Logger.ResetLogState(key);
            recebidas = 0;
            KitLugia.Core.Logger.LogOnce(key, MARCA + " estado C", 0); // 0 vira minimo de 5 s
            System.Threading.Thread.Sleep(5200);
            KitLugia.Core.Logger.LogOnce(key, MARCA + " estado C", 0);
            Check("LogOnce: heartbeat reemite com contagem", recebidas == 2,
                  $"recebidas={recebidas}");

            KitLugia.Core.Logger.OnLogReceived -= handler;
            KitLugia.Core.Logger.ResetLogState(key);
        }

        Console.WriteLine(new string('=', 70));
        Console.WriteLine($"RESULTADO: {ok} OK / {fail} FALHA");
        Console.WriteLine($"PNGs em: {Path.GetFullPath(outDir)}");

        window.Close();
        Environment.Exit(fail == 0 ? 0 : 1);
        return fail == 0 ? 0 : 1;
    }

    static string FindRepoRoot()
    {
        // sobe ate achar a pasta que contem KitLugia.sln
        var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "KitLugia.GUI")) &&
                File.Exists(Path.Combine(d.FullName, "KitLugia.sln")))
                return d.FullName;
            d = d.Parent;
        }
        // fallback: sobe do CWD
        d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "KitLugia.GUI")))
                return d.FullName;
            d = d.Parent;
        }
        return "";
    }

    static object Find(object root, string name)
    {
        var f = root.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(root);
    }

    /// <summary>
    /// Acha TextBox sem Style= e sem Foreground= (texto preto invisivel no
    /// tema escuro). Cuidado com dois falsos negativos ja vistos na sessao:
    ///  - <TextBox.Style> como ELEMENTO FILHO conta como estilo valido;
    ///  - procurar "TextBox.Style" em N linhas a frente pega o do proximo
    ///    campo -> por isso a profundidade e contada de verdade.
    /// </summary>
    static List<(string File, int Line, string Name)> FindTextBoxesSemEstilo(string repoRoot)
    {
        var out_ = new List<(string, int, string)>();
        var gui = Path.Combine(repoRoot, "KitLugia.GUI");
        if (!Directory.Exists(gui)) return out_;

        var tagRe = new System.Text.RegularExpressions.Regex(@"<TextBox\b", RegexOptions.IgnoreCase);
        var openRe = new System.Text.RegularExpressions.Regex(@"<([A-Za-z][\w.]*)\b[^>]*?/?>");
        var closeRe = new System.Text.RegularExpressions.Regex(@"</([A-Za-z][\w.]*)\s*>");

        foreach (var file in Directory.EnumerateFiles(gui, "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file, System.Text.Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!tagRe.IsMatch(lines[i])) continue;

                // junta a tag de abertura ate fechar
                var buf = new System.Text.StringBuilder();
                int j = i; bool closed = false;
                for (; j < lines.Length && j < i + 25; j++)
                {
                    buf.AppendLine(lines[j]);
                    if (System.Text.RegularExpressions.Regex.IsMatch(buf.ToString(), @"[\s]/?>"))
                    { closed = true; break; }
                }
                if (!closed) continue;

                string tag = buf.ToString();
                bool selfClose = System.Text.RegularExpressions.Regex.IsMatch(tag, @"/>\s*$");
                string head = tag.Split('>')[0];

                bool childStyle = false;
                if (!selfClose)
                {
                    int depth = 0;
                    for (int k = j; k < Math.Min(j + 60, lines.Length); k++)
                    {
                        depth += openRe.Matches(lines[k]).Count - closeRe.Matches(lines[k]).Count;
                        if (lines[k].Contains("TextBox.Style")) { childStyle = true; break; }
                        if (depth <= 0 && k > j) break;
                    }
                }

                bool styled = head.Contains("Style=\"") || head.Contains("Foreground=\"") || childStyle;
                if (styled) { i = j; continue; }

                var nm = System.Text.RegularExpressions.Regex.Match(head, "x:Name=\"([^\"]+)\"");
                string name = nm.Success ? nm.Groups[1].Value
                                         : System.Text.RegularExpressions.Regex
                                           .Match(head, "Name=\"([^\"]+)\"").Groups[1].Value;
                out_.Add((Path.GetFileName(file), i + 1,
                          string.IsNullOrEmpty(name) ? "(sem nome)" : name));
                i = j;
            }
        }
        return out_;
    }

    static BitmapSource LoadPng(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }

    static int Luminancia(Color c)
    {
        // mesma formula doContarPixelsClaros (rec. 30/59/11)
        return (c.R * 30 + c.G * 59 + c.B * 11) / 100;
    }

    static int ContarPixelsClaros(BitmapSource bmp)
    {
        var conv = bmp.Format == PixelFormats.Bgra32
            ? bmp
            : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, (BitmapPalette)null, 0.0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        int n = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            // "claro" = luminancia alta (texto branco/cinza sobre fundo escuro)
            if ((r * 30 + g * 59 + b * 11) / 100 > 140) n++;
        }
        return n;
    }

    static void Invalidate(DependencyObject root)
    {
        if (root == null) return;
        // InvalidateVisual fica em Visual/UIElement (Visual3D nao tem - CS1061).
        if (root is UIElement ue) ue.InvalidateVisual();
        if (root is UIElement u2) { u2.InvalidateMeasure(); u2.InvalidateArrange(); }
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) Invalidate(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            System.Threading.Thread.Sleep(15);
        }
    }
}