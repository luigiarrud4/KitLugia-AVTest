// NetPageVis — auditoria VISUAL isolada da NetworkPage (mesmo padrão do TmVisualBench).
//
// A página é renderizada FORA da tela (janela em -32000), sem elevação e sem UIA.
// O que ele responde, com PIXELS e não só propriedades:
//   1) o placeholder aparece nos dois campos de DNS Customizado logo após o load?
//   2) em que estados ele desaparece: Text null, Text "", Text " ", texto preenchido, foco?
//
// Modos:
//   audit   (padrão) — estado inicial + PNGs (página inteira, cada campo, área inteira)
//   states           — força cada estado e diz em qual o placeholder some
//   page             — salva só o PNG da página inteira
//
// Opções: --size WxH (padrão 1000x900)  --dpi N (padrão 96)  --out DIR
//
// IMPORTANTE (lição do primeiro run): a análise usa o render da PRÓPRIA PÁGINA, porque
// RenderTargetBitmap.Render() de um elemento filho inclui o offset dele no pai — recortar
// pelas coordenadas relativas ao filho dava um recorte deslocado ~30px (falso veredito).
//
// Read-only: a página apenas lê adaptadores/DNS.

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int _w = 1000;
    private static int _h = 900;
    private static int _dpi = 96;
    private static string _outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");

    private static double Escala => _dpi / 96.0;

    [STAThread]
    private static int Main(string[] args)
    {
        bool modoEstados = args.Contains("states");
        bool modoPagina = args.Contains("page");
        bool modoCaret = args.Contains("caret");

        int iSize = Array.IndexOf(args, "--size");
        if (iSize >= 0 && iSize + 1 < args.Length)
        {
            var partes = args[iSize + 1].Split('x');
            if (partes.Length == 2 && int.TryParse(partes[0], out int w) && int.TryParse(partes[1], out int h))
            { _w = w; _h = h; }
        }
        int iDpi = Array.IndexOf(args, "--dpi");
        if (iDpi >= 0 && iDpi + 1 < args.Length && int.TryParse(args[iDpi + 1], out int d)) _dpi = d;
        int iOut = Array.IndexOf(args, "--out");
        if (iOut >= 0 && iOut + 1 < args.Length) _outDir = args[iOut + 1];

        Directory.CreateDirectory(_outDir);
        Console.WriteLine("=== NetPageVis — auditoria visual da NetworkPage ===");
        Console.WriteLine($"janela {_w}x{_h} · dpi {_dpi} · saida {Path.GetFullPath(_outDir)}");
        Console.WriteLine();

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
                });
            }
            catch (Exception ex) { Console.WriteLine($"AVISO: Generic.xaml nao carregou ({ex.Message})"); }

            app.DispatcherUnhandledException += (_, e) =>
            {
                Console.WriteLine($"   [excecao na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var pagina = new KitLugia.GUI.Pages.NetworkPage();

            // TextBox NATIVO (template padrao do WPF, sem Style) = grupo de controle.
            // Fica fora da tela (x negativo) mas COM layout real (Opacity 0 nao remove
            // layout), para servir de referencia do recuo que o WPF aplica de verdade.
            // Isso substitui "chutar" uma constante: a pergunta passa a ser "o campo DNS
            // insere no mesmo lugar que um TextBox normal?", que e a pergunta do usuario.
            var refBox = new TextBox
            {
                Padding = new Thickness(8, 6, 8, 6),
                Height = 32,
                FontSize = 11,
                Text = "8.8.8.8",
            };
            var refHost = new Border
            {
                Width = 220,
                Height = 32,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-500, 0, 0, 0),                Child = refBox,
            };

            var janela = new Window
            {
                Width = _w,
                Height = _h,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = pagina,
            };

            // A referencia fica numa JANELA propria: Page so aceita Window/Frame como pai,
            // entao nao da para embrulhar a pagina + referencia no mesmo Grid.
            var janelaRef = new Window
            {
                Width = 240,
                Height = 60,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = refHost,
                Opacity = 0,
            };

            bool mostrou = false;
            try { janela.Show(); mostrou = true; }
            catch (Exception ex) { Console.WriteLine($"AVISO: Show() falhou ({ex.Message}) — modo offscreen."); }
            if (!mostrou)
            {
                janela.Measure(new Size(_w, _h));
                janela.Arrange(new Rect(0, 0, _w, _h));
                janela.UpdateLayout();
            }
            // A janela da referencia PRECISA de Show para ter layout real (medir/arranjar
            // nao aciona o template do TextBox da mesma forma que um layout de verdade).
            try { janelaRef.Show(); } catch (Exception ex) { Console.WriteLine($"AVISO: janela da referencia nao abriu ({ex.Message})"); }
            Console.WriteLine(mostrou ? "janela criada fora da tela (Loaded disparou)" : "pagina apenas medida/arranjada");

            // Deixa o Loaded/auto-detect (e o teste automatico de DNS, quando roda) assentar.
            Pump(4000);

            var prim = pagina.FindName("TxtCustomDnsPrimary") as TextBox;
            var sec = pagina.FindName("TxtCustomDnsSecondary") as TextBox;
            if (prim == null || sec == null)
            {
                Console.WriteLine("!! TxtCustomDnsPrimary/TxtCustomDnsSecondary nao encontrados");
                return 1;
            }

            // Garante a área DNS dentro da viewport (rola se preciso) antes de renderizar.
            try { prim.BringIntoView(); } catch { }
            Pump(500);
            Console.WriteLine($"pagina na tela: {pagina.ActualWidth:F0}x{pagina.ActualHeight:F0} (viewport)");
            Console.WriteLine();

            int falhas;
            if (modoEstados)
            {
                falhas = ModoEstados(pagina, prim, sec);
            }
            else if (modoCaret)
            {
                falhas = ModoCaret(pagina, prim, sec, refBox);
            }
            else
            {
                falhas = ModoAudit(pagina, prim, sec, salvarPagina: true, modoPagina);
            }

            try { janela.Close(); } catch { }
            try { janelaRef.Close(); } catch { }

            Console.WriteLine();
            Console.WriteLine(falhas == 0 ? "=== OK: nenhuma falha de placeholder ===" : $"=== {falhas} FALHA(S) ===");
            return falhas == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERRO: {ex}");
            return 1;
        }
    }

    // ───────────────────────── AUDITORIA ─────────────────────────

    private static int ModoAudit(FrameworkElement pagina, TextBox prim, TextBox sec, bool salvarPagina, bool soPagina)
    {
        var bmp = Render(pagina);
        if (salvarPagina || soPagina)
        {
            string arq = Path.Combine(_outDir, "net_pagina.png");
            SalvarPng(bmp, arq);
            Console.WriteLine($"PNG da pagina (viewport): {arq} ({bmp.PixelWidth}x{bmp.PixelHeight})");
            try
            {
                var bmpInteira = Render(pagina is Page pg && pg.Content is ScrollViewer sv && sv.Content is FrameworkElement conteudo
                    ? conteudo : pagina);
                string arq2 = Path.Combine(_outDir, "net_pagina_inteira.png");
                SalvarPng(bmpInteira, arq2);
                Console.WriteLine($"PNG da pagina inteira : {arq2} ({bmpInteira.PixelWidth}x{bmpInteira.PixelHeight})");
            }
            catch (Exception ex) { Console.WriteLine($"   !! render da pagina inteira: {ex.Message}"); }
        }

        int falhas = 0;
        Console.WriteLine();

        var caixas = new List<(string Nome, TextBox Tb, Rect Caixa)>();
        foreach (var (nome, tb) in new[] { ("DNS PRIMARIO", prim), ("DNS SECUNDARIO", sec) })
        {
            var caixa = Analisar(pagina, tb, bmp, nome, out bool placeholderVisivel);
            caixas.Add((nome, tb, caixa));
            Console.WriteLine();
            SalvarCropZoom(bmp, caixa, Path.Combine(_outDir, nome == "DNS PRIMARIO" ? "net_dns_primario.png" : "net_dns_secundario.png"), 4);
            if (!placeholderVisivel) falhas++;
        }

        // Recorte da área inteira "DNS CUSTOMIZADO" (rótulo + os dois campos + botão).
        var a = caixas[0].Caixa; var b = caixas[1].Caixa;
        double x = Math.Min(a.X, b.X) - 8;
        double y = a.Y - 30;
        double w = (Math.Max(a.Right, b.Right) - x) + 110;   // + botão Aplicar
        var bloco = new Rect(x, y, w, a.Height + 38);
        SalvarCropZoom(bmp, bloco, Path.Combine(_outDir, "net_dns_customizado.png"), 3);
        Console.WriteLine();
        Console.WriteLine($"recorte da area: {Path.Combine(_outDir, "net_dns_customizado.png")}");
        return falhas;
    }

    private static int ModoEstados(FrameworkElement pagina, TextBox prim, TextBox sec)
    {
        var estados = new (string Nome, Action Aplicar, bool EsperaPlaceholder)[]
        {
            ("inicial (como a pagina carrega)", () => { }, true),
            ("Text = \"\" (vazio explicito)", () => { prim.Text = ""; sec.Text = ""; }, true),
            ("Text = null", () => { prim.Text = null; sec.Text = null; }, true),
            ("Text = \" \" (um espaco)", () => { prim.Text = " "; sec.Text = " "; }, false),
            ("espaco + saiu do campo (normaliza)", () =>
            {
                prim.Text = " "; sec.Text = " ";
                prim.Focus(); Pump(120);
                sec.Focus(); Pump(120);   // prim perde o foco -> whitespace vira ""
                prim.Focus(); Pump(120);   // sec perde o foco -> whitespace vira ""
                sec.Focus();
            }, true),
            ("Text = \"8.8.8.8\" (preenchido)", () => { prim.Text = "8.8.8.8"; sec.Text = "8.8.4.4"; }, false),
            ("de volta a vazio", () => { prim.Text = ""; sec.Text = ""; }, true),
            ("foco no primario", () => { prim.Focus(); }, true),
        };

        int falhas = 0;
        foreach (var (nome, aplicar, espera) in estados)
        {
            aplicar();
            Pump(300);

            var bmp = Render(pagina);
            Analisar(pagina, prim, bmp, "  primario", out bool visPrim, silencioso: true);
            Analisar(pagina, sec, bmp, "  secundario", out bool visSec, silencioso: true);

            bool ok = espera ? visPrim : true;
            string extra = prim.IsKeyboardFocusWithin ? $"  [foco=sim borda={CorBorda(prim)}]" : "";
            Console.WriteLine($"[{(ok ? " ok " : "FALHA")}] {nome,-38} placeholder: primario={(visPrim ? "sim" : "NAO")} secundario={(visSec ? "sim" : "nao")}{extra}");
            if (!ok) falhas++;
        }
        return falhas;
    }

    /// <summary>Posição X do placeholder (TextBlock "ph") no sistema do proprio TextBox.</summary>
    private static double PhXRel(TextBox tb)
    {
        tb.ApplyTemplate();
        var ph = tb.Template?.FindName("ph", tb) as TextBlock;
        if (ph == null) { Console.WriteLine("      [ph] NAO ENCONTRADO no template"); return double.NaN; }
        try
        {
            var r = ph.TransformToAncestor(tb).TransformBounds(new Rect(0, 0, ph.ActualWidth, ph.ActualHeight));
            return r.X;
        }
        catch (Exception ex) { Console.WriteLine($"      [ph] transform falhou: {ex.GetType().Name}: {ex.Message}"); return double.NaN; }
    }

    /// <summary>
    /// Posição X do caret no sistema do proprio TextBox, para o indice pedido.
    /// GetRectFromCharacterIndex e' API publica de TextBox e devolve o retangulo REAL do
    /// caractere (nao e' chute de pixel), entao responde exatamente a pergunta "onde o
    /// cursor e o texto estao sendo desenhados dentro da caixa".
    /// </summary>
    private static double CaretX(TextBox tb, int indice)
    {
        try
        {
            tb.CaretIndex = indice;
            tb.UpdateLayout();
            return tb.GetRectFromCharacterIndex(indice).X;
        }
        catch { return double.NaN; }
    }

    /// <summary>
    /// MODO CARET — mede a correcao do texto "corrompido".
    /// BUG ORIGINAL: o PART_ContentHost usava Margin="{TemplateBinding Padding}", mas o
    /// TextBox JA aplica o Padding no host de edicao. O padding ficava DUPLICADO, entao o
    /// caret e o texto digitado nasciam em ~2x o padding e o placeholder (que usa o
    /// padding simples) ficava desalinhado — dois "textos" sobrepostos/errados.
    /// Aqui medimos em DIPs reais, sem pixel:
    ///   1) placeholder alinhado com o texto real
    ///   2) recuo igual ao Padding.Left (1x, nao 2x)
    ///   3) caret cresce monotonicamente com o indice (insercao no lugar certo)
    /// </summary>
    private static int ModoCaret(FrameworkElement pagina, TextBox prim, TextBox sec, TextBox refBox)
    {
        Console.WriteLine("--- MODO CARET: onde o texto/caret realmente e desenhado ---");
        Console.WriteLine("    Referencia = TextBox NATIVO do WPF, mesmo Padding/Height/FontSize,");
        Console.WriteLine("    hospedado fora da tela mas com layout real (grupo de controle).");
        Console.WriteLine();

        // Referencia: caret no indice 0 com o mesmo texto.
        refBox.Text = "8.8.8.8";
        refBox.Focus();
        Pump(150);
        double refX0 = CaretX(refBox, 0);
        Console.WriteLine($"REFERENCIA (TextBox nativo)  caret[0] = {refX0:F2}   <- o que o WPF faz de verdade");
        Console.WriteLine();

        int falhas = 0;

        foreach (var (nome, tb) in new[] { ("DNS PRIMARIO", prim), ("DNS SECUNDARIO", sec) })
        {
            Console.WriteLine($"{nome}:");

            // (a) X do placeholder com o campo vazio
            tb.Text = ""; Pump(200);
            double phX = PhXRel(tb);

            // (b) X do caret no inicio, com o campo preenchido
            tb.Text = "8.8.8.8"; Pump(200);
            tb.Focus(); Pump(120);
            double c0 = CaretX(tb, 0);

            Console.WriteLine($"   Padding.Left     = {tb.Padding.Left}");
            Console.WriteLine($"   X placeholder    = {phX:F2}");
            Console.WriteLine($"   X caret[0]       = {c0:F2}");
            Console.WriteLine($"   X referencia[0]  = {refX0:F2}");

            // (1) BUG ORIGINAL: padding DUPLICADO no host de edicao. O sintoma e o caret
            // nascer ~2x o Padding.Left e o placeholder (padding simples) ficar fora de
            // alinhamento -> dois "textos" sobrepostos. Se fossil == 0 agora, o bug voltou.
            double fossil = Math.Abs(c0 - tb.Padding.Left * 2);
            if (fossil < 1.0)
            {
                Console.WriteLine($"   FALHA  padding DUPLICADO: caret em {c0:F2} ~ 2x Padding.Left ({tb.Padding.Left * 2})");
                falhas++;
            }

            // (2) o campo DNS tem de inserir no MESMO lugar que o TextBox nativo
            if (double.IsNaN(c0) || double.IsNaN(refX0) || Math.Abs(c0 - refX0) > 1.5)
            {
                Console.WriteLine($"   FALHA  dif vs referencia = {Math.Abs(c0 - refX0):F2} (tol 1.5)");
                falhas++;
            }
            else
            {
                Console.WriteLine($"   [ ok ]  mesmo recuo do TextBox nativo (dif {Math.Abs(c0 - refX0):F2})");
            }

            // (3) placeholder alinhado com o texto/caret real
            if (double.IsNaN(phX) || double.IsNaN(c0) || Math.Abs(phX - c0) > 1.5)
            {
                Console.WriteLine($"   FALHA  alinhamento: |placeholder - caret| = {(Math.Abs(phX - c0)):F2} (tol 1.5)");
                falhas++;
            }
            else
            {
                Console.WriteLine($"   [ ok ]  placeholder na MESMA coluna do texto (dif {Math.Abs(phX - c0):F2})");
            }

            // (4) caret cresce com o indice -> clique/insercao mapeia posicao certa
            double c3 = CaretX(tb, 3);
            double c7 = CaretX(tb, 7);
            Console.WriteLine($"   caret[3]={c3:F2}  caret[7]={c7:F2}  (referencia: {CaretX(refBox, 3):F2} / {CaretX(refBox, 7):F2})");
            if (double.IsNaN(c3) || double.IsNaN(c7) || !(c0 < c3 && c3 < c7))
            {
                Console.WriteLine("   FALHA  caret nao cresce monotonamente com o indice");
                falhas++;
            }
            else
            {
                Console.WriteLine("   [ ok ]  caret cresce com o indice -> insercao no lugar certo");
            }

            // restore
            tb.Text = ""; tb.CaretIndex = 0; Pump(120);
            Console.WriteLine();
        }

        Console.WriteLine(falhas == 0 ? "=== OK: recuo/caret/placeholder corretos ===" : $"=== {falhas} FALHA(S) ===");
        return falhas;
    }

    /// <summary>Cor atual do anel da caixa (o border do template) — usada para provar que o
    /// foco vence o hover (dourado #FFD700) e que o placeholder ainda é o #666.</summary>
    private static string CorBorda(TextBox tb)
    {
        tb.ApplyTemplate();
        var bd = tb.Template?.FindName("bd", tb) as Border;
        return (bd?.BorderBrush as SolidColorBrush)?.Color.ToString() ?? "?";
    }

    // ───────────────────────── ANÁLISE ─────────────────────────

    /// <summary>
    /// Lê o estado REAL do TextBlock "ph" do template e confere nos PIXELS se o texto do
    /// placeholder foi desenhado dentro da caixa. Pixels de brilho 90..160 contam como o
    /// #666 do placeholder; >160 = texto digitado/caret/borda acesa (separar evita confundir
    /// caret com placeholder).
    /// </summary>
    private static Rect Analisar(FrameworkElement pagina, TextBox tb, RenderTargetBitmap bmp,
                                 string rotulo, out bool placeholderVisivel, bool silencioso = false)
    {
        tb.ApplyTemplate();
        var ph = tb.Template?.FindName("ph", tb) as TextBlock;
        var caixa = BoundsRel(pagina, tb);

        var (escuroClaro, claros, minX, maxX, minY, maxY) = ContarPixels(bmp, caixa);
        placeholderVisivel = escuroClaro > 20;

        if (!silencioso)
        {
            string textoVal = tb.Text == null ? "null" : $"\"{tb.Text}\"";
            Console.WriteLine($"{rotulo}:");
            Console.WriteLine($"   Text          = {textoVal} (len {tb.Text?.Length ?? -1})");
            Console.WriteLine($"   Tag           = \"{tb.Tag}\"");
            Console.WriteLine($"   caixa         = {caixa.Width:F0}x{caixa.Height:F0} @ ({caixa.X:F0},{caixa.Y:F0})");
            Console.WriteLine($"   ph (template) = {(ph == null ? "NAO ENCONTRADO" : $"Visibility={ph.Visibility} Text=\"{ph.Text}\" Foreground={ph.Foreground}")}");
            Console.WriteLine($"   pixels #666 no render = {escuroClaro}  (texto claro = {claros})");
            if (escuroClaro > 0)
                Console.WriteLine($"   faixa do placeholder  = x[{minX}..{maxX}] y[{minY}..{maxY}] (relativo a caixa)");
            Console.WriteLine($"   veredito      = {(placeholderVisivel ? "PLACEHOLDER VISIVEL" : "SEM PLACEHOLDER")}");
        }
        return caixa;
    }

    /// <summary>Conta, dentro do recorte da caixa (inset de 3px para ignorar a borda), os
    /// pixels de brilho 90..160 (texto #666 do placeholder) e >160 (texto branco/caret).</summary>
    private static (int EscuroClaro, int Claros, int MinX, int MaxX, int MinY, int MaxY)
        ContarPixels(RenderTargetBitmap bmp, Rect caixa)
    {
        int x0 = (int)Math.Round((caixa.X + 3) * Escala);
        int y0 = (int)Math.Round((caixa.Y + 3) * Escala);
        int w = (int)Math.Round((caixa.Width - 6) * Escala);
        int h = (int)Math.Round((caixa.Height - 6) * Escala);
        if (w <= 2 || h <= 2) return (0, 0, 0, 0, 0, 0);

        if (x0 < 0) { w += x0; x0 = 0; }
        if (y0 < 0) { h += y0; y0 = 0; }
        if (x0 + w > bmp.PixelWidth) w = bmp.PixelWidth - x0;
        if (y0 + h > bmp.PixelHeight) h = bmp.PixelHeight - y0;
        if (w <= 2 || h <= 2) return (0, 0, 0, 0, 0, 0);

        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var buf = new byte[conv.PixelWidth * 4 * conv.PixelHeight];
        conv.CopyPixels(buf, conv.PixelWidth * 4, 0);

        int escuroClaro = 0, claros = 0;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int i = (y * conv.PixelWidth + x) * 4;
                int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                int lum = (r + g + b) / 3;
                if (lum > 160) claros++;
                else if (lum > 90)
                {
                    escuroClaro++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0) return (0, 0, 0, 0, 0, 0);
        return (escuroClaro, claros, minX - x0, maxX - x0, minY - y0, maxY - y0);
    }

    // ───────────────────────── RENDER ─────────────────────────

    private static RenderTargetBitmap Render(FrameworkElement raiz)
    {
        raiz.UpdateLayout();
        double w = raiz.ActualWidth > 1 ? raiz.ActualWidth : _w;
        double h = raiz.ActualHeight > 1 ? raiz.ActualHeight : _h;
        int pw = Math.Max(1, (int)Math.Ceiling(w * Escala));
        int ph = Math.Max(1, (int)Math.Ceiling(h * Escala));

        var bmp = new RenderTargetBitmap(pw, ph, _dpi, _dpi, PixelFormats.Pbgra32);
        // Fundo escuro: a página é translúcida; sem isto o PNG sai com alpha.
        var fundo = new DrawingVisual();
        using (var dc = fundo.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), null, new Rect(0, 0, w, h));
        bmp.Render(fundo);
        bmp.Render(raiz);
        bmp.Freeze();
        return bmp;
    }

    private static Rect BoundsRel(FrameworkElement raiz, FrameworkElement el)
    {
        try { return el.TransformToAncestor(raiz).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
        catch { return Rect.Empty; }
    }

    private static void SalvarCropZoom(BitmapSource bmp, Rect area, string caminho, int zoom)
    {
        try
        {
            int x = (int)Math.Floor(area.X * Escala), y = (int)Math.Floor(area.Y * Escala);
            int w = (int)Math.Ceiling(area.Width * Escala), h = (int)Math.Ceiling(area.Height * Escala);
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > bmp.PixelWidth) w = bmp.PixelWidth - x;
            if (y + h > bmp.PixelHeight) h = bmp.PixelHeight - y;
            if (w <= 1 || h <= 1) return;

            var crop = new CroppedBitmap(bmp, new Int32Rect(x, y, w, h));
            BitmapSource saida = crop;
            if (zoom > 1)
            {
                var t = new TransformedBitmap(crop, new ScaleTransform(zoom, zoom));
                t.Freeze();
                saida = t;
            }
            SalvarPng(saida, caminho);
            Console.WriteLine($"   PNG: {caminho} ({saida.PixelWidth}x{saida.PixelHeight})");
        }
        catch (Exception ex) { Console.WriteLine($"   !! crop: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void SalvarPng(BitmapSource bmp, string caminho)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(caminho);
        enc.Save(fs);
    }

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
