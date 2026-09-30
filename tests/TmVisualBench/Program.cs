// Auditoria VISUAL do KitTaskManager (janela larga, interface própria).
//
// O que faz, sem abrir nada para o usuário no caminho normal:
//   1) constroi a janela do KitTaskManager;
//   2) percorre TODAS as abas, mede/arranja e salva um PNG de cada uma em out/;
//   3) roda um DETECTOR DE TEXTO SOBREPOSTO: percorre a árvore visual, pega
//      cada TextBlock/TextBlock-like com texto visível, calcula os bounds em
//      coordenadas da janela e reporta pares que se interceptam.
//   4) modo `flicker`: renderiza a mesma aba duas vezes com o dispatcher
//      bombeando no meio e diz QUANTO da tela mudou (delta de pixels).
//      Serve para provar (ou refutar) que "o refresh faz tudo piscar".
//
// Uso:
//   5) modo `sort` (COMPORTAMENTO, não pixel): seleciona cada critério no combo
//      de ordenação da aba Processos — o caminho real do usuário — e confere se a
//      lista ficou de fato ordenada; depois expande um usuário na aba Usuários e
//      confere que o detalhe continua aberto e listado depois do refresh de 1 s.
//      Foi assim que provamos "ordenei por RAM e não funcionou" e "cliquei na seta
//      e o processo sumiu da lista".
//
// Uso:
//   6) modo `gold`: procura DOURADO (#FFD700) renderizado dentro do TM — é assim que
//      se acha um estilo global do Kit que vazou para a janela azul do Task Manager.
//
// Uso:
//   7) modo `load`: mede a PRIMEIRA CARGA — quando o véu de carregamento aparece, quando a
//      1ª lista de processos e o 1º valor do Resumo chegam à tela e quando o véu sai.
//      Salva o print do véu em ação. É assim que "abre mas demora para mostrar os valores"
//      (relato em PCs mais fracos) vira número, e que se prova que o véu não ficou preso.
//
// Uso:
//   dotnet run -- [tabs] [overlap] [flicker] [sort] [gold] [load] [--size WxH] [--tab Nome] [--top N]
//
// Nada aqui altera o sistema (a janela do TM é read-only para o SO).

using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitLugia.GUI.Windows.TaskManager;

internal static class Program
{
    private const int DefaultW = 1680;
    private const int DefaultH = 1000;

    private static int _w = DefaultW;
    private static int _h = DefaultH;
    private static string _outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out");

    // Abas do TM (mesmos Tags do SwitchTabByTag).
    private static readonly string[] Abas =
    {
        "Summary", "Processes", "Performance", "Services", "Startup",
        "Users", "Connections", "Latency", "Storage", "Diagnostic"
    };

    [STAThread]
    private static int Main(string[] args)
    {
        ParseArgs(args);

        bool modoTabs = args.Contains("tabs") || (!args.Contains("overlap") && !args.Contains("flicker") && !args.Contains("load"));
        bool modoOverlap = args.Contains("overlap") || modoTabs;
        bool modoFlicker = args.Contains("flicker");
        bool modoSort = args.Contains("sort");
        bool modoGold = args.Contains("gold");
        bool modoLoad = args.Contains("load");
        int top = TopN(args);

        Directory.CreateDirectory(_outDir);
        Console.WriteLine($"=== KitTaskManager — auditoria visual ({_w}x{_h}) ===");
        Console.WriteLine($"saída: {Path.GetFullPath(_outDir)}");
        Console.WriteLine();

        KitTaskManagerWindow? janela = null;
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
            catch (Exception ex) { Console.WriteLine($"AVISO: Generic.xaml não carregou ({ex.Message})"); }

            // Absorve qualquer exceção de UI: a janela dispara timers/coletas reais.
            app.DispatcherUnhandledException += (_, e) =>
            {
                Console.WriteLine($"   [exceção na UI absorvida] {e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

            janela = new KitTaskManagerWindow
            {
                Width = _w,
                Height = _h,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,          // fora da tela: não aparece para o usuário
                Top = -32000,
                ShowInTaskbar = false,
            };

            // Show() é o que faz o Loaded rodar (e portanto os grids receberem dados).
            // Posicionada fora da tela; se falhar, cai no caminho offscreen.
            bool mostrou = false;
            try { janela.Show(); mostrou = true; }
            catch (Exception ex) { Console.WriteLine($"AVISO: Show() falhou ({ex.Message}) — modo offscreen."); }
            Console.WriteLine(mostrou ? "janela criada e exibida fora da tela (Loaded disparou)"
                                      : "janela apenas medida/arranjada (modo offscreen)");
            Console.WriteLine();

            // Modo `load`: só mede a PRIMEIRA CARGA (linha do tempo + print do véu) e sai.
            if (modoLoad)
            {
                MedirPrimeiraCarga(janela, mostrou);
                try { janela.Close(); } catch { }
                Console.WriteLine("=== fim ===");
                return 0;
            }

            // Deixa o primeiro refresh/coleta inicial terminar.
            Pump(2500);

            var alvos = Abas;
            int iTab = Array.IndexOf(args, "--tab");
            if (iTab >= 0 && iTab + 1 < args.Length) alvos = new[] { args[iTab + 1] };

            foreach (var aba in alvos)
            {
                Console.WriteLine($"── {aba} ─────────────────────────────────────────");
                TrocarAba(janela, aba);
                Pump(900);   // deixa o trabalho do Loaded da aba terminar

                var (bmp, raiz) = Capture(janela, mostrou);
                if (bmp == null)
                {
                    Console.WriteLine("   !! não consegui renderizar (visual sem layout)");
                    continue;
                }

                string arquivo = Path.Combine(_outDir, $"tm_{aba}.png");
                SalvarPng(bmp, arquivo);
                Console.WriteLine($"   print  : {arquivo}  ({bmp.PixelWidth}x{bmp.PixelHeight})");
                Console.WriteLine($"   nós    : {Contar(raiz)} visuais, {ContarTexto(raiz)} textos com conteúdo");

                if (modoOverlap)
                {
                    var colisoes = DetectarSobreposicao(raiz, bmp.PixelWidth, bmp.PixelHeight);
                    if (colisoes.Count == 0)
                        Console.WriteLine("   layout : OK — nenhum texto sobreposto");
                    else
                    {
                        Console.WriteLine($"   layout : {colisoes.Count} texto(s) SOBREPOSTO(S):");
                        foreach (var c in colisoes.Take(Math.Max(1, top)))
                            Console.WriteLine($"            • \"{Rec(c.A)}\"  ✕  \"{Rec(c.B)}\"   (sobrepõe {c.Pct:P0} do menor)");
                        if (colisoes.Count > top) Console.WriteLine($"            ... e {colisoes.Count - top} outros");
                    }
                }

                if (modoGold)
                    RelatarOuro(raiz, top);

                if (modoFlicker)
                {
                    var f = MedirFlicker(janela, mostrou);
                    if (f < 0) Console.WriteLine("   flicker: não medido");
                    else
                        Console.WriteLine($"   flicker: {f:P1} da tela mudou entre dois refreshes " +
                                          (f > 0.35 ? "  << MUITO (repinta a página inteira)"
                                           : f > 0.08 ? "  << moderado (dados + gráficos)"
                                                      : "  << baixo (só os dados)"));
                }
                Console.WriteLine();
            }

            if (modoSort)
            {
                int falhas = TestarOrdenacao(janela);
                Console.WriteLine();
                Console.WriteLine(falhas == 0
                    ? "=== ordenação/detalhe: TUDO OK ==="
                    : $"=== ordenação/detalhe: {falhas} FALHA(S) ===");
            }

            try { janela.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERRO: {ex}");
            return 1;
        }

        Console.WriteLine("=== fim ===");
        return 0;
    }

    // ────────────────────────────────────────────────────────────────────
    //  1ª CARGA — LINHA DO TEMPO (transforma olhômetro em número)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Mede QUANDO cada marco da primeira carga aparece na tela: véu de carregamento,
    /// primeira linha de processo, primeiro valor do Resumo, status com a contagem e
    /// saída do véu. Bombeia o dispatcher em janelas de 25 ms (os timers reais do TM
    /// rodam de verdade) e salva um print do véu em ação.
    ///
    /// É este modo que dá o número para "abre mas demora para mostrar os valores":
    /// antes da correção a lista só aparecia depois do pipeline inteiro (GPU via PDH
    /// incluída, ~1-3 s na primeira chamada do perflib).
    /// </summary>
    private static void MedirPrimeiraCarga(KitTaskManagerWindow janela, bool mostrou)
    {
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        long tVeu = -1, tLista = -1, tResumo = -1, tStatus = -1, tVeuFora = -1;
        long tPrintVeu = -1;
        string etapa = "", decorrido = "";
        int linhas = 0;

        var dg = janela.FindName("DgProcesses") as DataGrid;
        var veu = janela.FindName("LoadOverlay") as FrameworkElement;
        var txtStatus = janela.FindName("TxtStatus") as TextBlock;
        // Marcador do Resumo COM VALOR: o rodapé do cartão de CPU só é escrito pelo tick de
        // desempenho (o subtítulo tem texto estático no XAML — não serve como prova).
        var txtSub = janela.FindName("TxtSumCpuFooter") as TextBlock;
        var dgTop = janela.FindName("DgSumTopCpu") as DataGrid;
        var txtEtapa = janela.FindName("LoadStage") as TextBlock;
        var txtDecorrido = janela.FindName("LoadElapsed") as TextBlock;

        for (int i = 0; i < 600 && relogio.ElapsedMilliseconds < 25000; i++)
        {
            Pump(25);
            long ms = relogio.ElapsedMilliseconds;

            if (veu != null && veu.Visibility == Visibility.Visible)
            {
                if (tVeu < 0) tVeu = ms;
                if (txtEtapa != null && !string.IsNullOrWhiteSpace(txtEtapa.Text)) etapa = txtEtapa.Text;
                if (txtDecorrido != null && !string.IsNullOrWhiteSpace(txtDecorrido.Text)) decorrido = txtDecorrido.Text;
                // Print do véu em ação (~600 ms: já deu tempo de ele ser pintado).
                if (tPrintVeu < 0 && ms >= 600)
                {
                    var (bmpV, raizV) = Capture(janela, mostrou);
                    if (bmpV != null)
                    {
                        string arq = Path.Combine(_outDir, "tm_carregando.png");
                        SalvarPng(bmpV, arq);
                        tPrintVeu = ms;
                        Console.WriteLine($"   print do véu : {arq}");

                        // O cartão do véu tem 4 textos (título, etapa, tempo, rodapé das etapas):
                        // se algum sobrepõe, o véu é justamente o "sinal de carregamento" mal feito.
                        if (raizV != null)
                        {
                            var col = DetectarSobreposicao(raizV, bmpV.PixelWidth, bmpV.PixelHeight);
                            Console.WriteLine(col.Count == 0
                                ? "   layout do véu : OK — nenhum texto sobreposto"
                                : $"   layout do véu : {col.Count} TEXTO(S) SOBREPOSTO(S): " +
                                  string.Join(" | ", col.Take(4).Select(c => $"\"{c.A}\" ✕ \"{c.B}\"")));
                        }
                    }
                }
            }
            else if (tVeuFora < 0 && tVeu >= 0) tVeuFora = ms;

            if (tLista < 0 && dg != null && dg.Items.Count > 0) { tLista = ms; linhas = dg.Items.Count; }
            if (tResumo < 0 && txtSub != null && !string.IsNullOrWhiteSpace(txtSub.Text)
                           && dgTop != null && dgTop.Items.Count > 0) tResumo = ms;
            if (tStatus < 0 && txtStatus != null && (txtStatus.Text ?? "").Contains("processos em")) tStatus = ms;

            if (tVeuFora >= 0 && tLista >= 0 && tResumo >= 0) break;
        }

        Console.WriteLine("── 1ª CARGA ─────────────────────────────────────────");
        Console.WriteLine($"   véu na tela      : {(tVeu >= 0 ? $"t+{tVeu} ms" : "não apareceu")}");
        Console.WriteLine($"   1ª lista         : {(tLista >= 0 ? $"t+{tLista} ms  ({linhas} linhas em DgProcesses)" : "NÃO apareceu em 25 s")}");
        Console.WriteLine($"   Resumo c/ valor  : {(tResumo >= 0 ? $"t+{tResumo} ms  ({dgTop?.Items.Count ?? 0} linhas no Top processos)" : "NÃO apareceu em 25 s")}");
        Console.WriteLine($"   status final     : {(tStatus >= 0 ? $"t+{tStatus} ms" : "não apareceu")}");
        Console.WriteLine($"   véu saiu         : {(tVeuFora >= 0 ? $"t+{tVeuFora} ms" : "NÃO saiu")}");
        Console.WriteLine($"   última etapa     : \"{etapa}\"  ({decorrido})");

        var (bmpF, raizF) = Capture(janela, mostrou);
        if (bmpF != null)
        {
            string arq = Path.Combine(_outDir, "tm_primeiro_conteudo.png");
            SalvarPng(bmpF, arq);
            Console.WriteLine($"   print final      : {arq}  ({bmpF.PixelWidth}x{bmpF.PixelHeight})");
        }
        if (raizF != null)
        {
            var ouro = new List<string>();
            RelatarOuroSilencioso(raizF, ouro);
            Console.WriteLine($"   dourado visível  : {(ouro.Count == 0 ? "0 (ok)" : string.Join(" | ", ouro.Take(4)))}");
        }
        Console.WriteLine($"   véu ainda visível: {(veu != null && veu.Visibility == Visibility.Visible ? "SIM (problema)" : "não")}");
        Console.WriteLine();
    }

    /// <summary>Versão silenciosa do detector de dourado (mesma lógica do modo `gold`).</summary>
    private static void RelatarOuroSilencioso(FrameworkElement raiz, List<string> achados)
    {
        bool EhOuro(Color c) => c.R > 200 && c.G > 195 && c.B < 70 && c.A > 40;

        void Olhar(DependencyObject o, string prop, Brush? b)
        {
            if (b is SolidColorBrush sc && EhOuro(sc.Color))
                achados.Add($"{o.GetType().Name}.{prop} #{sc.Color.R:X2}{sc.Color.G:X2}{sc.Color.B:X2}");
        }

        try
        {
            foreach (var o in Descendentes(raiz))
            {
                if (!IsVisivelNaTela(o)) continue;
                switch (o)
                {
                    case TextBlock tb:
                        Olhar(o, "Foreground", tb.Foreground);
                        Olhar(o, "Background", tb.Background);
                        break;
                    case Control ct:
                        Olhar(o, "Foreground", ct.Foreground);
                        Olhar(o, "BorderBrush", ct.BorderBrush);
                        break;
                    case System.Windows.Shapes.Shape sh:
                        Olhar(o, "Fill", sh.Fill);
                        Olhar(o, "Stroke", sh.Stroke);
                        break;
                }
                if (o is Border bd)
                {
                    Olhar(o, "Background", bd.Background);
                    Olhar(o, "BorderBrush", bd.BorderBrush);
                }
                if (o is Panel pn) Olhar(o, "Background", pn.Background);
            }
        }
        catch { }
    }

    // ────────────────────────────────────────────────────────────────────
    //  CAPTURA / RENDER
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Mede/arranja e devolve o bitmap + a raiz visual usada na análise.</summary>
    private static (RenderTargetBitmap? Bmp, FrameworkElement? Raiz) Capture(KitTaskManagerWindow janela, bool mostrou)
    {
        try
        {
            FrameworkElement? raiz = null;

            if (mostrou)
            {
                raiz = janela.Content as FrameworkElement;
            }
            else
            {
                janela.Measure(new Size(_w, _h));
                janela.Arrange(new Rect(0, 0, _w, _h));
                janela.UpdateLayout();
                raiz = janela.Content as FrameworkElement;
            }

            if (raiz == null) return (null, null);

            raiz.UpdateLayout();
            double w = raiz.ActualWidth > 1 ? raiz.ActualWidth : _w;
            double h = raiz.ActualHeight > 1 ? raiz.ActualHeight : _h;

            var bmp = new RenderTargetBitmap((int)Math.Round(w), (int)Math.Round(h), 96, 96, PixelFormats.Pbgra32);
            // Fundo escuro: a janela é translúcida; sem isto o PNG sai com alpha e fica ilegível.
            var fundo = new DrawingVisual();
            using (var dc = fundo.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
                                 null, new Rect(0, 0, w, h));
            bmp.Render(fundo);
            bmp.Render(raiz);
            bmp.Freeze();
            return (bmp, raiz);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   !! Capture: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    private static void SalvarPng(BitmapSource bmp, string caminho)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(caminho);
        enc.Save(fs);
    }

    /// <summary>Fracção de pixels que mudam entre dois refreshes (mede o "tudo pisca").</summary>
    private static double MedirFlicker(KitTaskManagerWindow janela, bool mostrou)
    {
        try
        {
            var (a, _) = Capture(janela, mostrou);
            if (a == null) return -1;

            // Espera um refresh de verdade acontecer (timers de 1–3 s) e bombeia.
            Pump(2200);

            var (b, _) = Capture(janela, mostrou);
            if (b == null) return -1;

            return Diferenca(a, b);
        }
        catch { return -1; }
    }

    private static double Diferenca(BitmapSource a, BitmapSource b)
    {
        int w = Math.Min(a.PixelWidth, b.PixelWidth);
        int h = Math.Min(a.PixelHeight, b.PixelHeight);
        if (w <= 0 || h <= 0) return 0;

        int stride = w * 4;
        var pa = new byte[stride * h];
        var pb = new byte[stride * h];
        var conv = new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(pa, stride, 0);
        conv = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(pb, stride, 0);

        int mudou = 0;
        for (int i = 0; i < pa.Length; i += 4)
        {
            // Tolerância de 8 por canal: ruído de antialias não conta como "piscou".
            if (Math.Abs(pa[i] - pb[i]) > 8 || Math.Abs(pa[i + 1] - pb[i + 1]) > 8 || Math.Abs(pa[i + 2] - pb[i + 2]) > 8)
                mudou++;
        }
        return (double)mudou / (w * h);
    }

    // ────────────────────────────────────────────────────────────────────
    //  DETECÇÃO DE TEXTO SOBREPOSTO
    // ────────────────────────────────────────────────────────────────────

    private sealed record Colisao(string A, string B, double Pct);

    private static List<Colisao> DetectarSobreposicao(FrameworkElement raiz, int w, int h)
    {
        var textos = new List<(Rect R, string T)>();

        foreach (var tb in Descendentes(raiz).OfType<TextBlock>())
        {
            if (tb.ActualWidth < 1 || tb.ActualHeight < 1) continue;
            if (string.IsNullOrWhiteSpace(tb.Text)) continue;
            if (!IsVisivelNaTela(tb)) continue;

            var r = BoundsNaJanela(tb, raiz, out bool cortado);
            if (r.IsEmpty || r.Width < 4 || r.Height < 4) continue;
            // Texto CORTADO (fora da viewport de um ScrollViewer, ou recortado pela célula)
            // não entra na comparação: dois pedaços cortados do mesmo viewport colapsam para
            // o MESMO retângulo e pareceriam "sobrepostos" sem estarem na tela.
            if (cortado) continue;
            textos.Add((r, Truncar(tb.Text, 44)));
        }

        var achados = new List<Colisao>();
        for (int i = 0; i < textos.Count; i++)
        {
            for (int j = i + 1; j < textos.Count; j++)
            {
                var a = textos[i]; var b = textos[j];
                var inter = Rect.Intersect(a.R, b.R);
                if (inter.IsEmpty || inter.Width <= 0 || inter.Height <= 0) continue;

                double area = inter.Width * inter.Height;
                double menor = Math.Min(a.R.Width * a.R.Height, b.R.Width * b.R.Height);
                if (menor <= 0) continue;

                double pct = area / menor;
                // >35% do menor: não é "encostou", é um em cima do outro de verdade.
                if (pct > 0.35) achados.Add(new Colisao(a.T, b.T, pct));
            }
        }

        // Saída da janela HORIZONTAL é sempre bug de layout (estoura a largura).
        // Vertical para baixo NÃO é: conteúdo num ScrollViewer pode legitimamente estar
        // abaixo da dobra (foi o falso positivo que dominava o relatório). Só acusa
        // vertical para CIMA (coisa posicionada acima do topo da janela).
        foreach (var (r, t) in textos)
        {
            if (r.Right > w + 2 || r.Left < -2)
                achados.Add(new Colisao(t, "(estoura a largura da janela)", 1.0));
            else if (r.Bottom < -2)
                achados.Add(new Colisao(t, "(acima do topo da janela)", 1.0));
        }

        return achados.OrderByDescending(c => c.Pct).ToList();
    }

    /// <summary>
    /// Bounds do elemento em coordenadas da raiz, JÁ RECORTADOS pelo clip dos ancestrais.
    /// Sem isto, um TextBlock dentro de uma célula de DataGrid reporta a largura TOTAL que
    /// gostaria de ter (a célula é quem corta) e o detector acusava "sobreposição" que o
    /// usuário nunca vê.
    /// </summary>
    private static Rect BoundsNaJanela(FrameworkElement el, FrameworkElement raiz, out bool cortado)
    {
        cortado = false;
        try
        {
            var t = el.TransformToAncestor(raiz);
            var r = t.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));

            DependencyObject? cur = VisualTreeHelper.GetParent(el);
            while (cur != null && !ReferenceEquals(cur, raiz))
            {
                // Um ScrollViewer recorta o conteúdo na sua viewport. Sem isto, linhas de
                // DataGrid abaixo da dobra (ou conteúdo rolado do painel de detalhes) eram
                // medidas como se estivessem na tela e "sobrepunham" a barra de status.
                if (cur is ScrollViewer svw)
                {
                    Rect vb;
                    try
                    {
                        var tt = svw.TransformToAncestor(raiz);
                        vb = tt.TransformBounds(new Rect(0, 0, svw.ActualWidth, svw.ActualHeight));
                    }
                    catch { vb = Rect.Empty; }
                    if (!vb.IsEmpty && !vb.Contains(r))
                    {
                        cortado = true;
                        r = Rect.Intersect(r, vb);
                        if (r.IsEmpty) return Rect.Empty;
                    }
                }

                if (cur is FrameworkElement fe)
                {
                    if (fe.ActualWidth < 1 || fe.ActualHeight < 1) return Rect.Empty;
                    if (fe.ClipToBounds || fe.Clip != null)
                    {
                        Rect b;
                        try
                        {
                            var tt = fe.TransformToAncestor(raiz);
                            b = tt.TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                        }
                        catch { b = Rect.Empty; }
                        if (b.IsEmpty) break;
                        if (!b.Contains(r)) cortado = true;
                        r = Rect.Intersect(r, b);
                        if (r.IsEmpty) return Rect.Empty;
                    }
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
            return r;
        }
        catch { return Rect.Empty; }
    }

    private static bool IsVisivelNaTela(DependencyObject o)
    {
        try
        {
            if (o is UIElement ui && ui.Visibility != Visibility.Visible) return false;
            if (o is FrameworkElement fe && fe.Opacity < 0.05) return false;
            var pai = VisualTreeHelper.GetParent(o);
            return pai == null || IsVisivelNaTela(pai);
        }
        catch { return false; }
    }

    private static IEnumerable<DependencyObject> Descendentes(DependencyObject d)
    {
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(d); } catch { yield break; }
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var g in Descendentes(c)) yield return g;
        }
    }

    private static int Contar(DependencyObject? d)
    {
        if (d == null) return 0;
        int c = 1;
        foreach (var _ in Descendentes(d)) c++;
        return c;
    }

    private static int ContarTexto(DependencyObject? d)
    {
        if (d == null) return 0;
        return Descendentes(d).OfType<TextBlock>().Count(t => !string.IsNullOrWhiteSpace(t.Text));
    }

    private static string Truncar(string s, int max)
        => s.Replace('\n', ' ').Replace('\r', ' ').Trim() is var t && t.Length <= max ? t : t[..max] + "…";

    private static string Rec(string s) => s;

    // ────────────────────────────────────────────────────────────────────
    //  MODO `sort` — comportamento (ordenação e detalhe), não pixel
    // ────────────────────────────────────────────────────────────────────

    private static object? Prop(object o, string nome)
    {
        try { return o.GetType().GetProperty(nome)?.GetValue(o); } catch { return null; }
    }
    private static double Num(object o, string nome)
    {
        try { var v = Prop(o, nome); return v == null ? 0 : Convert.ToDouble(v); } catch { return 0; }
    }
    private static string Str(object o, string nome) => Prop(o, nome) as string ?? "";
    private static int CountOf(object? col) => col is System.Collections.ICollection c ? c.Count : 0;

    /// <summary>
    /// Testa o que o usuário reportou: "filtrei pelo maior consumo de RAM e não funcionou"
    /// e "cliquei na seta da aba Usuários e o processo sumiu da lista".
    /// Usa o CAMINHO REAL da UI (combo de ordenação + propriedades das linhas).
    /// </summary>
    private static int TestarOrdenacao(KitTaskManagerWindow janela)
    {
        int falhas = 0;
        var t = janela.GetType();
        var dg = janela.FindName("DgProcesses") as DataGrid;
        var combo = janela.FindName("CmbSortProcesses") as ComboBox;
        if (dg == null || combo == null) { Console.WriteLine("   !! DgProcesses/CmbSortProcesses não encontrados"); return 1; }

        Console.WriteLine("── ordenação — aba Processos (pelo combo, caminho do usuário) ──");
        TrocarAba(janela, "Processes");
        Pump(1600);

        var casos = new (string Tag, bool Numerico)[]
        {
            ("RamValue|Descending", true),
            ("DiskBytesPerSec|Descending", true),
            ("PeakMemValue|Descending", true),
            ("CpuTimeSec|Descending", true),
            ("DisplayName|Ascending", false),
            ("UserName|Ascending", false),
            ("Status|Ascending", false),
            ("Pid|Ascending", false),
            ("Threads|Descending", true),
        };

        foreach (var (tag, numerico) in casos)
        {
            var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.Ordinal));
            if (item == null) { Console.WriteLine($"   {tag,-28} !! ausente no combo"); falhas++; continue; }
            combo.SelectedItem = item;   // dispara CmbSortProcesses_Changed (caminho real)
            Pump(1500);                  // atravessa um ciclo completo do refresh de 1 s

            string col = tag.Split('|')[0];
            bool asc = tag.Split('|')[1] == "Ascending";
            var (ok, info) = ConferirOrdemProcessos(dg, col, asc, numerico);
            Console.WriteLine($"   {tag,-28} {(ok ? "OK   " : "FALHA")} {info}");
            if (!ok) falhas++;
        }

        Console.WriteLine();
        Console.WriteLine("── aba Usuários — seta ▼, detalhe e ordenação ──");
        TrocarAba(janela, "Users");
        Pump(1800);
        var dgU = janela.FindName("DgUsers") as DataGrid;
        if (dgU == null) { Console.WriteLine("   !! DgUsers não encontrado"); return falhas + 1; }

        var linhasU = dgU.Items.OfType<object>().ToList();
        if (linhasU.Count == 0) { Console.WriteLine("   !! nenhuma linha de usuário (a lista não carregou)"); return falhas + 1; }

        // 1) Abre o usuário com mais processos (o que o usuário clicou) e espera 2+ refreshes:
        //    antes o refresh trocava o ItemsSource, a seleção caía e o detalhe SUMIA.
        var alvo = linhasU.OrderByDescending(r => CountOf(Prop(r, "Processes"))).First();
        try { alvo.GetType().GetProperty("IsExpanded")?.SetValue(alvo, true); } catch { }
        try { t.GetMethod("LoadUsersSafeAsync", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, null); } catch { }
        Pump(2700);

        bool expandido = Prop(alvo, "IsExpanded") is true;
        int listados = CountOf(Prop(alvo, "Processes"));
        string vis = Prop(alvo, "DetailsVisibility")?.ToString() ?? "?";
        Console.WriteLine($"   seta ▼ em \"{Str(alvo, "UserName")}\": expandido={expandido} · detalhe={vis} · {listados} processos listados");
        if (!expandido || listados == 0 || vis != "Visible")
        { Console.WriteLine("   FALHA — o detalhe não sobreviveu ao refresh automático"); falhas++; }
        else Console.WriteLine("   OK    — continua aberto e listado depois de 3 refreshes");

        // 2) Ordenação da aba Usuários: antes o clique ia para o sort default do DataGrid
        //    e o refresh de 1 s desfazia. Agora tem de SOBREVIVER ao refresh.
        foreach (var (col, desc) in new (string Col, bool Desc)[] { ("MemValue", true), ("ProcessCount", true), ("UserName", false) })
        {
            try
            {
                t.GetField("_usersSortColumn", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(janela, col);
                t.GetField("_usersSortDirection", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(janela,
                    desc ? ListSortDirection.Descending : ListSortDirection.Ascending);
                t.GetMethod("ApplyUsersSort", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, null);
            }
            catch (Exception ex) { Console.WriteLine($"   {col,-24} !! {ex.Message}"); falhas++; continue; }
            Pump(1600); // > 1 refresh: a ordem escolhida tem de continuar valendo

            var atual = dgU.Items.OfType<object>().ToList();
            int viol = 0;
            for (int i = 1; i < atual.Count; i++)
            {
                int cmp = col == "UserName"
                    ? string.Compare(Str(atual[i - 1], col), Str(atual[i], col), StringComparison.CurrentCultureIgnoreCase)
                    : Num(atual[i - 1], col).CompareTo(Num(atual[i], col));
                if (desc ? cmp < 0 : cmp > 0) viol++;
            }
            Console.WriteLine($"   {col + (desc ? " ↓" : " ↑"),-24} {(viol == 0 ? "OK" : $"FALHA ({viol}/{Math.Max(1, atual.Count - 1)} pares fora de ordem)")}");
            if (viol != 0) falhas++;
        }

        // 3) Painel "Detalhes do Processo" AO VIVO: antes ele só era escrito no
        //    SelectionChanged, então com um processo vivo selecionado o Uptime (e CPU/RAM)
        //    ficavam congelados. Aqui: seleciona, espera 2+ refreshes SEM tocar na seleção
        //    e exige que o painel tenha acompanhado.
        Console.WriteLine();
        Console.WriteLine("── painel de detalhes ao vivo ──");
        TrocarAba(janela, "Processes");
        Pump(1000);

        var txtPid = janela.FindName("DetailPidValue") as TextBlock;
        var txtCpu = janela.FindName("DetailCpu") as TextBlock;
        var txtUptime = janela.FindName("DetailUptime") as TextBlock;
        var txtHandles = janela.FindName("DetailHandles") as TextBlock;
        if (txtPid == null || txtUptime == null)
        { Console.WriteLine("   !! campos do painel não encontrados"); return falhas + 1; }

        var linha = dg.Items.OfType<object>().FirstOrDefault(r => Prop(r, "IsChild") is not true);
        if (linha == null) { Console.WriteLine("   !! nenhuma linha para selecionar"); return falhas + 1; }
        dg.SelectedItem = linha;
        Pump(1400);

        string pidPainel = txtPid.Text;
        string uptime1 = txtUptime.Text;
        string cpu1 = txtCpu?.Text ?? "";
        string handles1 = txtHandles?.Text ?? "";
        Pump(2600);   // 2+ ciclos do refresh automático, sem mexer na seleção
        string uptime2 = txtUptime.Text;
        string cpu2 = txtCpu?.Text ?? "";
        string handles2 = txtHandles?.Text ?? "";

        bool pidBate = pidPainel == Convert.ToString(Prop(linha, "Pid"));
        bool andou = uptime1 != uptime2;
        bool acompanhou = cpu1 != cpu2 || handles1 != handles2 || andou;
        string nomeLinha = Str(linha, "DisplayName");
        string pidLinha = Convert.ToString(Prop(linha, "Pid")) ?? "?";
        Console.WriteLine($"   linha {nomeLinha} (PID {pidLinha}) -> painel PID={pidPainel} · CPU {cpu1}->{cpu2} · Handles {handles1}->{handles2} · Uptime {uptime1}->{uptime2}");
        Console.WriteLine($"   {(pidBate ? "OK" : "FALHA")} PID do painel bate com a seleção | {(acompanhou ? "OK" : "FALHA")} painel acompanhou o refresh");
        if (!pidBate || !acompanhou) falhas++;

        falhas += TestarSubmenu(janela, dg);
        falhas += TestarResumo(janela);
        return falhas;
    }

    /// <summary>
    /// O template de MenuItem precisa de um Popup com os filhos: sem ele o item "Prioridade"
    /// parece um comando comum e o submenu NUNCA abre (era o defeito dos dois templates do TM).
    /// Aqui o menu é aberto de verdade e o submenu é aberto de verdade.
    /// </summary>
    private static int TestarSubmenu(KitTaskManagerWindow janela, DataGrid dg)
    {
        Console.WriteLine();
        Console.WriteLine("── submenu do menu de contexto ──");
        TrocarAba(janela, "Processes");
        Pump(1200);

        var ctx = dg.ContextMenu;
        if (ctx == null) { Console.WriteLine("   !! o grid não tem menu de contexto"); return 1; }

        var pai = ctx.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Prioridade");
        if (pai == null)
        {
            var comFilhos = ctx.Items.OfType<MenuItem>().FirstOrDefault(m => m.Items.Count > 0);
            pai = comFilhos;
        }
        if (pai == null) { Console.WriteLine("   !! nenhum item com submenu no menu de contexto"); return 1; }

        ctx.PlacementTarget = dg;
        ctx.IsOpen = true;
        Pump(600);
        try { pai.IsSubmenuOpen = true; } catch { }
        Pump(700);

        bool aberto = pai.IsSubmenuOpen;
        int itens = pai.Items.OfType<MenuItem>().Count();
        // O Popup do submenu aparece na árvore visual do item QUANDO abre (é ele que prova
        // que o template tem Popup + ItemsPresenter).
        int popupsAbertos = Descendentes(pai).OfType<System.Windows.Controls.Primitives.Popup>().Count(p => p.Child != null && p.IsOpen);
        Console.WriteLine($"   \"{pai.Header}\": submenu aberto={aberto} · {itens} itens · popups abertos na árvore={popupsAbertos}");
        try { pai.IsSubmenuOpen = false; ctx.IsOpen = false; } catch { }
        Pump(300);

        if (itens == 0 || !aberto || popupsAbertos == 0)
        {
            Console.WriteLine("   FALHA — o submenu não abre (o template do MenuItem não tem Popup/ItemsPresenter)");
            return 1;
        }
        Console.WriteLine("   OK    — o submenu abre de verdade");
        return 0;
    }

    /// <summary>
    /// Resumo: (a) "congelar na lista" — o processo fixado continua no Top depois do refresh
    /// (antes a lista era reconstruida a cada segundo e a linha escolhida podia sumir embaixo
    /// do cursor); (b) histórico — clique no gráfico de CPU abre a linha do tempo com quem
    /// estava usando a CPU em cada instante.
    /// </summary>
    private static int TestarResumo(KitTaskManagerWindow janela)
    {
        int falhas = 0;
        var t = janela.GetType();
        Console.WriteLine();
        Console.WriteLine("── Resumo: congelar na lista (fixar) ──");
        TrocarAba(janela, "Summary");
        Pump(2600);

        var dgTop = janela.FindName("DgSumTopCpu") as DataGrid;
        if (dgTop == null) { Console.WriteLine("   !! DgSumTopCpu não encontrado"); return falhas + 1; }

        var linhas = dgTop.Items.OfType<object>().ToList();
        if (linhas.Count < 5) { Console.WriteLine($"   !! poucas linhas no Top ({linhas.Count})"); return falhas + 1; }

        var alvo = linhas[4];                        // alguém do MEIO da lista
        string nomeAlvo = Str(alvo, "Name");
        int pidAlvo = Convert.ToInt32(Prop(alvo, "Pid"));
        dgTop.SelectedItem = alvo;
        try { t.GetMethod("MenuSumPin_Click", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, new object?[] { dgTop, new RoutedEventArgs() }); }
        catch (Exception ex) { Console.WriteLine($"   !! MenuSumPin_Click: {ex.Message}"); }
        Pump(2600);                                  // 2+ refreshes por cima do pin

        // Re-busca por PID: a lista pode ter sido reconstruída (instância nova) — é o que o
        // usuário veria na tela.
        var atual = dgTop.Items.OfType<object>().FirstOrDefault(r => Convert.ToInt32(Prop(r, "Pid")) == pidAlvo);
        bool naLista = atual != null;
        bool fixado = naLista && Prop(atual!, "Pinned") is true;
        int pos = atual == null ? -1 : dgTop.Items.IndexOf(atual);
        string icone = atual == null ? "" : Str(atual, "PinIcon");
        Console.WriteLine($"   fixado: Pinned={fixado} · continua na lista={naLista} (posição {pos}) · ícone=\"{icone}\" ({nomeAlvo})");
        if (!fixado || !naLista || pos > 3 || icone.Length == 0)
        { Console.WriteLine("   FALHA — o processo fixado não se manteve no topo depois do refresh"); falhas++; }
        else Console.WriteLine("   OK    — o fixado ficou no topo e sobreviveu aos refreshes");

        Console.WriteLine();
        Console.WriteLine("── Resumo: histórico (clique no gráfico) ──");
        try { t.GetMethod("SumCpuCanvas_Click", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, new object?[] { janela, null }); }
        catch (Exception ex) { Console.WriteLine($"   !! SumCpuCanvas_Click: {ex.Message}"); }
        Pump(3600);                                  // deixa acumular algumas amostras + redesenhar

        var pnl = janela.FindName("PnlCpuHistory") as FrameworkElement;
        var canvas = janela.FindName("HistCpuCanvas") as Canvas;
        var txtSel = janela.FindName("TxtHistSel") as TextBlock;
        var txtWhat = janela.FindName("TxtHistWhat") as TextBlock;
        var pnlTop = janela.FindName("PnlHistTop") as Panel;

        bool aberto = pnl?.Visibility == Visibility.Visible;
        bool desenhou = (canvas?.Children.Count ?? 0) > 0;
        bool temTexto = (txtSel?.Text ?? "").Contains("amostra");
        Console.WriteLine($"   painel aberto={aberto} · barras={canvas?.Children.Count ?? 0} · \"{Truncar(txtSel?.Text ?? "", 46)}\"");
        Console.WriteLine($"   instante: \"{Truncar(txtWhat?.Text ?? "", 90)}\"");
        Console.WriteLine($"   quem usava: {pnlTop?.Children.Count ?? 0} linha(s) listadas");
        if (!aberto || !desenhou || !temTexto)
        { Console.WriteLine("   FALHA — o histórico não abriu/desenhou"); falhas++; }
        else Console.WriteLine("   OK    — histórico desenhado com as amostras");

        // Print do painel ABERTO: mede, em pixel, o tamanho do grafico e da lista de processos.
        try
        {
            var (bmpH, raizH) = Capture(janela, true);
            if (bmpH != null && raizH != null)
            {
                SalvarPng(bmpH, Path.Combine(_outDir, "tm_SummaryHistory.png"));
                if (pnl != null) Console.WriteLine($"   painel : {pnl.ActualWidth:F0}x{pnl.ActualHeight:F0} px · canvas {canvas?.ActualHeight:F0} px");
                if (pnlTop?.Children.Count > 0 && pnlTop.Children[0] is Grid lg && lg.Children.Count > 0 && lg.Children[0] is TextBlock n0)
                    Console.WriteLine($"   lista  : FontSize {n0.FontSize:F0} · {pnlTop.Children.Count} linhas");
            }
        }
        catch (Exception ex) { Console.WriteLine($"   !! print do histórico: {ex.Message}"); }

        // Seleciona uma amostra antiga (arrastando/clicando à esquerda do gráfico).
        string antes = txtSel?.Text ?? "";
        try { t.GetMethod("SelecionarAmostraPeloX", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, new object?[] { 4.0 }); }
        catch { }
        Pump(400);
        string depois = txtSel?.Text ?? "";
        bool selecionou = depois != antes && !depois.Contains("ao vivo");
        Console.WriteLine($"   seleção de amostra: \"{Truncar(depois, 46)}\" {(selecionou ? "OK" : "FALHA")}");
        if (!selecionou) falhas++;

        // Fecha (o overlay não pode roubar o gráfico ao vivo).
        try { t.GetMethod("BtnHistClose_Click", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(janela, new object?[] { janela, new RoutedEventArgs() }); } catch { }
        Pump(300);
        bool fechou = (janela.FindName("PnlCpuHistory") as FrameworkElement)?.Visibility != Visibility.Visible;
        Console.WriteLine($"   fechou: {(fechou ? "OK" : "FALHA")}");
        if (!fechou) falhas++;

        return falhas;
    }

    /// <summary>Confere a ordem das linhas-PAI da aba Processos (agrupadas: só compara dentro do mesmo grupo).</summary>
    private static (bool Ok, string Info) ConferirOrdemProcessos(DataGrid dg, string coluna, bool asc, bool numerico)
    {
        var linhas = dg.Items.OfType<object>().Where(r => Prop(r, "IsChild") is not true).ToList();
        if (linhas.Count < 5) return (false, $"poucas linhas ({linhas.Count})");

        int viol = 0, pares = 0, grupos = 1, empates = 0;
        // Pior violação em VALOR absoluto: distingue "empate/defasagem de 1 s" (Δ de décimos)
        // de "linha realmente fora de lugar" (Δ de centenas de MB). Sem isto o teste acusa
        // FALHA sem dizer se o usuário veria algo errado na tela.
        double piorDelta = 0;
        string pior = "";
        for (int i = 1; i < linhas.Count; i++)
        {
            if (!string.Equals(Str(linhas[i - 1], "Group"), Str(linhas[i], "Group"), StringComparison.Ordinal)) { grupos++; continue; }
            pares++;
            int cmp = numerico
                ? Num(linhas[i - 1], coluna).CompareTo(Num(linhas[i], coluna))
                : string.Compare(Str(linhas[i - 1], coluna), Str(linhas[i], coluna), StringComparison.CurrentCultureIgnoreCase);
            if (asc ? cmp > 0 : cmp < 0)
            {
                if (numerico)
                {
                    double va = Num(linhas[i - 1], coluna), vb = Num(linhas[i], coluna);
                    // A ordenação do TM arredonda em 1 casa (anti-pisca: métrica que oscila
                    // décimos movia linhas todo segundo). Logo, duas linhas separadas por até
                    // 0,1 são EMPATE de propósito — não é "ordem errada", é valor
                    // indistinguível na tela. Só é violação o que o usuário veria errado.
                    if (Math.Abs(va - vb) <= 0.1001) { empates++; continue; }
                    if (Math.Abs(va - vb) > piorDelta)
                    {
                        piorDelta = Math.Abs(va - vb);
                        pior = $"{Str(linhas[i - 1], "DisplayName")}={va:F1} acima de {Str(linhas[i], "DisplayName")}={vb:F1}";
                    }
                }
                viol++;
            }
        }
        double pct = pares == 0 ? 0 : (double)viol / pares;
        string detalhe = pior.Length > 0 ? $" · pior: {pior} (Δ {piorDelta:F1})" : "";
        string empateTxt = empates > 0 ? $" · {empates} empate(s) até 0,1 (anti-pisca)" : "";
        string info = $"{linhas.Count} linhas · {grupos} grupos · {viol}/{pares} pares fora de ordem ({pct:P0}){detalhe}{empateTxt}";
        // Tolerância de 5%: as métricas mudam entre o refresh e a leitura (defasagem de 1 s).
        return (pct <= 0.05, info);
    }

    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Caça o DOURADO (#FFD700, o azul do TM é #4FC3F7) que sobrou de algum estilo GLOBAL
    /// do Kit que vazou para dentro do TM. Percorre a árvore visual e reporta cada brush
    /// dourado com o tipo do elemento, o texto (quando houver) e os bounds na janela.
    /// </summary>
    private static void RelatarOuro(FrameworkElement raiz, int top)
    {
        var achados = new List<(string onde, string texto, string cor, Rect r)>();

        bool EhOuro(Color c) => c.R > 200 && c.G > 195 && c.B < 70 && c.A > 40;

        void Olhar(DependencyObject o, string prop, Brush? b)
        {
            if (b is not SolidColorBrush sc || !EhOuro(sc.Color)) return;
            string texto = o switch
            {
                TextBlock t => t.Text ?? "",
                ContentControl c => c.Content?.ToString() ?? "",
                _ => "",
            };
            var r = o is FrameworkElement fe ? BoundsNaJanela(fe, raiz, out _) : Rect.Empty;
            achados.Add((o.GetType().Name + "." + prop, texto.Length > 40 ? texto[..40] : texto,
                         $"#{sc.Color.R:X2}{sc.Color.G:X2}{sc.Color.B:X2}", r));
        }

        foreach (var o in Descendentes(raiz))
        {
            if (!IsVisivelNaTela(o)) continue;
            switch (o)
            {
                case TextBlock tb:
                    Olhar(o, "Foreground", tb.Foreground);
                    Olhar(o, "Background", tb.Background);
                    break;
                case Control ct:
                    Olhar(o, "Foreground", ct.Foreground);
                    Olhar(o, "BorderBrush", ct.BorderBrush);
                    break;
                case System.Windows.Shapes.Shape sh:
                    Olhar(o, "Fill", sh.Fill);
                    Olhar(o, "Stroke", sh.Stroke);
                    break;
            }
            if (o is Border bd)
            {
                Olhar(o, "Background", bd.Background);
                Olhar(o, "BorderBrush", bd.BorderBrush);
            }
            if (o is Panel pn) Olhar(o, "Background", pn.Background);
        }

        if (achados.Count == 0) { Console.WriteLine("   ouro  : nenhum (0 elementos dourados)"); return; }
        Console.WriteLine($"   ouro  : {achados.Count} elemento(s) DOURADO(S) nesta aba:");
        foreach (var a in achados.Take(Math.Max(1, top)))
            Console.WriteLine($"            • {a.onde,-22} {a.cor}  \"{a.texto}\"  @{a.r.X:F0},{a.r.Y:F0} {a.r.Width:F0}x{a.r.Height:F0}");
        if (achados.Count > top) Console.WriteLine($"            ... e {achados.Count - top} outros");
    }

    private static void TrocarAba(KitTaskManagerWindow janela, string aba)
    {
        try { janela.SwitchTabByTag(aba); }
        catch (Exception ex) { Console.WriteLine($"   !! SwitchTabByTag({aba}): {ex.Message}"); }
    }

    /// <summary>Bombeia o Dispatcher por <paramref name="ms"/> ms — é o que faz os timers tickarem.</summary>
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

    private static int TopN(string[] args)
    {
        int i = Array.IndexOf(args, "--top");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n > 0) return n;
        return 12;
    }

    private static void ParseArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--size");
        if (i >= 0 && i + 1 < args.Length)
        {
            var p = args[i + 1].Split('x');
            if (p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h))
            { _w = w; _h = h; }
        }
        int o = Array.IndexOf(args, "--out");
        if (o >= 0 && o + 1 < args.Length) _outDir = args[o + 1];
    }
}
