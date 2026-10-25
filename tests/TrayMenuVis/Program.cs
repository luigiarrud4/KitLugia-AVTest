using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Harness VISUAL + COMPORTAMENTAL do menu de bandeja.
//
// O que PROVA:
//  - a raiz esta enxuta (poucas acoes, poucos separadores);
//  - o que nao e' diario caiu dentro do submenu "Mais";
//  - "Reiniciar como Usuario Normal" sumiu;
//  - Config de RAM e Config do Kit sao itens diferentes;
//  - GameBoost mostra ❌/✅ e o motor, e so o Auto ganha destaque azul;
//  - clicar em cada atalho dispara o evento certo (prova de cablagem, nao so build);
//  - o menu e' escuro (nada de fundo branco do Windows) e os cantos sao
//    REALLY arredondados (Region), nao "falsos".
//
// O que NAO PROVA: o app completo rodando. E' o menu real do TrayIconService.

internal static class TrayMenuVis
{
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    static int _ok, _fail;

    static void Check(string what, bool cond, string det = "")
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {what}{(det.Length > 0 ? "  [" + det + "]" : "")}"); }
        else { _fail++; Console.WriteLine($"  FALHA {what}{(det.Length > 0 ? "  [" + det + "]" : "")}"); }
    }

    /// <summary>Compara textos ignorando acento (remove QUALQUER marca combinante).</summary>
    static string Norm(string? x)
    {
        if (string.IsNullOrEmpty(x)) return "";
        string d = x.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    static bool IsLabel(ToolStripItem i)
    {
        string t = i.Tag as string ?? "";
        return t.StartsWith("group:") || t.StartsWith("title:") || t.StartsWith("subtitle:");
    }

    /// <summary>Todos os itens, incluindo os dentro de submenus.</summary>
    static List<ToolStripMenuItem> Walk(ToolStripItemCollection items)
    {
        var list = new List<ToolStripMenuItem>();
        foreach (ToolStripItem i in items)
        {
            if (i is not ToolStripMenuItem mi) continue;
            list.Add(mi);
            if (mi.HasDropDownItems) list.AddRange(Walk(mi.DropDownItems));
        }
        return list;
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "traymenu_out");
        Directory.CreateDirectory(outDir);

        var t = Type.GetType("KitLugia.GUI.Services.TrayIconService, KitLugia.GUI");
        Check("TrayIconService carregado", t != null);
        if (t == null) return 1;

        var svc = Activator.CreateInstance(t, nonPublic: true)!;
        var mBuild = t.GetMethod("BuildTrayContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        Check("BuildTrayContextMenu existe", mBuild != null);
        if (mBuild == null) return 1;

        var menu = (ContextMenuStrip)mBuild.Invoke(svc, null)!;
        var items = menu.Items.Cast<ToolStripItem>().ToList();
        var todos = Walk(menu.Items);

        Console.WriteLine("--- Raiz ---");
        foreach (var it in items)
        {
            string mark = it is ToolStripMenuItem mi2 && mi2.Checked ? "[x]" : "   ";
            string kind = it is ToolStripSeparator ? "sep" :
                          IsLabel(it) ? "rot" : "item";
            Console.WriteLine($"  {mark} {kind,-5} {it.Text}");
        }
        var subMais = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => Norm(x.Text).Contains("mais"));
        if (subMais != null)
        {
            Console.WriteLine("--- Submenu Mais ---");
            foreach (ToolStripItem c in subMais.DropDownItems)
                if (c is ToolStripMenuItem cm)
                    Console.WriteLine($"       {cm.Text}{(cm.HasDropDownItems ? "  ▸" : "")}");
        }
        Console.WriteLine();

        // ---- RAIZ ENXUTA ----
        int acoesRaiz = items.Count(i => i is ToolStripMenuItem && !IsLabel(i) && i.Enabled);
        int seps = items.Count(i => i is ToolStripSeparator);
        Check("raiz enxuta (<= 10 acoes)", acoesRaiz <= 10, $"{acoesRaiz}");
        Check("poucos separadores na raiz (<= 5)", seps <= 5, $"{seps}");
        Check("existe o submenu 'Mais'", subMais != null);

        var dentroMais = subMais != null
            ? subMais.DropDownItems.OfType<ToolStripMenuItem>().Select(i => Norm(i.Text)).ToList()
            : new List<string>();
        foreach (var it in subMais?.DropDownItems.OfType<ToolStripMenuItem>() ?? Enumerable.Empty<ToolStripMenuItem>())
            if (it.HasDropDownItems)
                dentroMais.AddRange(it.DropDownItems.OfType<ToolStripMenuItem>().Select(x => Norm(x.Text)));

        foreach (string s2 in new[] { "busca global", "diagnostico", "aplicativos", "reparos", "tweaks", "turbo explorer", "reiniciar como admin" })
            Check($"'{s2}' foi para dentro de 'Mais'", dentroMais.Any(x => x.Contains(s2)));

        var textosRaiz = items.Select(i => Norm(i.Text)).ToList();
        Check("'Reiniciar como Usuario Normal' saiu do menu",
              !textosRaiz.Any(x => x.Contains("reiniciar como usuario normal")) &&
              !dentroMais.Any(x => x.Contains("reiniciar como usuario normal")));

        // ---- CONFIGURAÇÕES DIVIDIDAS ----
        var itemCfgRam = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => Norm(x.Text).Contains("configuracoes de ram"));
        var itemCfgKit = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => Norm(x.Text).Contains("configuracoes do kit"));
        Check("item 'Configurações de RAM' existe", itemCfgRam != null);
        Check("item 'Configurações do Kit' existe", itemCfgKit != null);
        Check("as duas configurações são itens DIFERENTES",
              itemCfgRam != null && itemCfgKit != null && !ReferenceEquals(itemCfgRam, itemCfgKit));

        // ---- GAMEBOOST: estado + motor ----
        var itemGb = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => Norm(x.Text).Contains("gameboost"));
        Check("item GameBoost existe", itemGb != null);

        var mApply = t.GetMethod("ApplyGameBoostMenuState", BindingFlags.Instance | BindingFlags.NonPublic);
        var pEnabled = t.GetProperty("GamePriorityEnabled");
        var pEngine = t.GetProperty("CurrentEngine", BindingFlags.Static | BindingFlags.Public);
        Check("GameBoost tem ApplyGameBoostMenuState", mApply != null);
        Check("GameBoost tem GamePriorityEnabled", pEnabled != null);
        Check("GameBoost tem CurrentEngine", pEngine != null);

        string Simula(string engine, bool on)
        {
            pEnabled?.SetValue(svc, on);
            if (pEngine != null && engine.Length > 0)
                pEngine.SetValue(null, System.Enum.Parse(pEngine.PropertyType, engine));
            var probe = new ToolStripMenuItem("x");
            mApply?.Invoke(svc, new object[] { probe });
            return probe.Text + "  |tag=" + (probe.Tag ?? "null");
        }

        if (mApply != null && pEnabled != null && pEngine != null)
        {
            string off = Simula("Auto", false);
            string onAuto = Simula("Auto", true);
            string onV4 = Simula("V4_ExtremePro", true);
            string onV1 = Simula("V1_Balanced", true);
            Console.WriteLine($"  desligado     : {off}");
            Console.WriteLine($"  Auto ligado   : {onAuto}");
            Console.WriteLine($"  V4 ligado     : {onV4}");
            Console.WriteLine($"  V1 ligado     : {onV1}");

            Check("desligado mostra ❌ e NAO mostra motor", off.Contains("\u274c") && !off.Contains("Automático"));
            Check("Auto ligado mostra ✅ + 🤖 Automático", onAuto.Contains("\u2705") && onAuto.Contains("\U0001F916"));
            Check("Auto ligado com destaque azul (tag accent)", onAuto.Contains("accent"));
            Check("V4 ligado mostra o motor e NÃO tem destaque", onV4.Contains("V4") && !onV4.Contains("accent"));
            Check("V1 ligado mostra o motor", onV1.Contains("V1"));
        }

        // ---- CABLAGEM: cada atalho dispara o evento certo ----
        var evTM = t.GetEvent("OnOpenTaskManager");
        var evNav = t.GetEvent("OnNavigateTo");
        Check("evento OnOpenTaskManager existe", evTM != null);
        Check("evento OnNavigateTo existe", evNav != null);

        bool abriuTM = false;
        string? tagRecebida = null;
        evTM?.AddEventHandler(svc, new Action(() => abriuTM = true));
        evNav?.AddEventHandler(svc, new Action<string>(tg => tagRecebida = tg));

        foreach (var it in Walk(menu.Items))
        {
            string n = Norm(it.Text);
            if (n.Contains("kit task manager")) it.PerformClick();
            if (n.Contains("diagnostico")) { it.PerformClick(); }
        }
        Application.DoEvents();

        Check("clicar em 'Kit Task Manager' abre o gerenciador", abriuTM);
        Check("clicar em 'Diagnostico' envia tag 'diagnostico'", tagRecebida == "diagnostico", $"tag='{tagRecebida}'");

        // as duas configurações devem enviar tags DIFERENTES
        itemCfgRam?.PerformClick();
        string? t1 = tagRecebida;
        itemCfgKit?.PerformClick();
        string? t2 = tagRecebida;
        Check("Config RAM e Config Kit enviam tags diferentes",
              t1 != null && t2 != null && t1 != t2, $"ram='{t1}' kit='{t2}'");

        // ---- PIXEL: o ✅ tem que sair VERDE de verdade (GDI+) ----
        // Um por estado: mostra o menu com GameBoost desligado e ligado, mede
        // a linha do item e conta pixels verdes/vermelhos.
        long ContaCor(string pngFile, int y0, int y1, Func<int, int, int, bool> pred)
        {
            using var b = new Bitmap(pngFile);
            long n = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (pred(c.R, c.G, c.B)) n++;
                }
            return n;
        }

        if (itemGb != null && pEnabled != null && pEngine != null && mApply != null)
        {
            void Medir(bool on, string engine, string rotulo)
            {
                pEnabled.SetValue(svc, on);
                pEngine.SetValue(null, System.Enum.Parse(pEngine.PropertyType, engine));

                // Aplica o estado pelo MESMO metodo que o menu usa em Opening.
                // (Reabrir o menu nao redesenha de forma confiavel neste harness
                //  — o Opening nao re-dispara e a medicao dava o mesmo numero
                //  nos 3 estados, o que mascararia a falha.)
                mApply.Invoke(svc, new object[] { itemGb });

                // PrintWindow nao pegava o item (buffer velho do drop-down).
                // DrawToBitmap forca um render novo do controle inteiro.
                menu.Show(new Point(40, 40));
                Application.DoEvents();
                for (int k=0;k<5;k++){ Application.DoEvents(); Thread.Sleep(120); }
                Application.DoEvents();

                string f = Path.Combine(outDir, $"gb_{rotulo}.png");
                if (menu.Handle != IntPtr.Zero)
                {
                    using var bmp = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height));
                    menu.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(f, ImageFormat.Png);

                    // mede SÓ a linha do GameBoost (evita contar o resto do menu)
                    int linhaTop = itemGb.Bounds.Top, linhaBot = itemGb.Bounds.Bottom;
                    long verde = ContaCor(f, linhaTop, linhaBot, (r, g2, b2) => g2 > 120 && g2 > r + 40 && g2 > b2 + 40);
                    long vermelho = ContaCor(f, linhaTop, linhaBot, (r, g2, b2) => r > 150 && r > g2 + 50 && r > b2 + 50);
                    long azul = ContaCor(f, linhaTop, linhaBot, (r, g2, b2) => b2 > 140 && b2 > r + 45 && g2 > 110);
                    Console.WriteLine($"  {rotulo,-8}: linha[{linhaTop}..{linhaBot}] verde={verde} vermelho={vermelho} azul={azul}");

                    if (!on)
                    {
                        Check("GameBoost DESLIGADO pinta a linha de VERMELHO", vermelho > 60, $"{vermelho}px");
                        Check("GameBoost desligado não pinta verde", verde < 20, $"{verde}px");
                        Check("GameBoost desligado não pinta azul", azul < 20, $"{azul}px");
                    }
                    else if (engine == "Auto")
                    {
                        Check("GameBoost com motor AUTOMATICO pinta a linha de AZUL (destaque)", azul > 60, $"{azul}px");
                        Check("motor Auto não é vermelho", vermelho < 20, $"{vermelho}px");
                    }
                    else
                    {
                        Check("GameBoost ligado (motor manual) pinta a linha de VERDE", verde > 60, $"{verde}px");
                        Check("motor manual não é vermelho", vermelho < 20, $"{vermelho}px");
                    }
                }
            }

            Medir(false, "Auto", "off");
            Medir(true, "Auto", "on_auto");
            Medir(true, "V4_ExtremePro", "on_v4");
        }

        // ---- HOVER: so item clicavel reage ----
        // Reproduz o que o user viu: passar o mouse pela linha de RAM
        // (NAO clicavel) desenhava um retangulo azul de "parece clicavel".
        var sub = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => (x.Tag as string ?? "").StartsWith("subtitle:"));
        var clic = items.OfType<ToolStripMenuItem>().FirstOrDefault(x => x.Enabled && !IsLabel(x));
        Check("existe uma linha NAO-clicavel de cabecalho", sub != null && !sub.Enabled);
        Check("existe uma linha clicavel para comparar", clic != null && clic.Enabled);

        if (sub != null && clic != null)
        {
            void FotoHover(ToolStripMenuItem alvo, string nome)
            {
                // Selected e' somente leitura -> Select(), que e' o que o ToolStrip
                // chama quando o mouse entra no item.
                foreach (var x in Walk(menu.Items)) x.Select();
                alvo.Select();
                Application.DoEvents();
                Thread.Sleep(220);
                Application.DoEvents();

                string f = Path.Combine(outDir, nome + ".png");
                using var bmp = new Bitmap(Math.Max(1, menu.Width), Math.Max(1, menu.Height));
                menu.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(f, ImageFormat.Png);

                int t = alvo.Bounds.Top, bo = alvo.Bounds.Bottom;
                // Mede SO na faixa sem texto (lado direito da linha). Contar a
                // linha inteira media a anti-aliasing do texto cinza e dava
                // falso positivo em item nao-clicavel (118 px de "azul" que
                // eram apenas as bordas dos glifos).
                int x0 = Math.Max(0, bmp.Width - 40), x1 = Math.Max(1, bmp.Width - 6);
                long azul = 0, total = 0;
                for (int y = Math.Max(0, t + 2); y < Math.Min(bmp.Height, bo - 2); y++)
                    for (int x = x0; x < x1; x++)
                    {
                        total++;
                        var c = bmp.GetPixel(x, y);
                        if (c.B > 62 && c.B > c.R + 18 && c.B >= c.G + 8) azul++;
                    }
                double pct = 100.0 * azul / Math.Max(1, total);
                Console.WriteLine($"  {nome,-22} faixa[{x0}..{x1}]y[{t}..{bo}] azuis={azul}/{total} ({pct:0.0}%)");
                if (nome == "hover_clicavel")
                    Check("hover em item CLICAVEL preenche a linha", pct > 70, $"{pct:0.0}%");
                else
                    Check("hover em item NAO-CLICAVEL nao preenche a linha", pct < 20, $"{pct:0.0}%");
                Console.WriteLine($"  {nome,-22} linha[{t}..{bo}] pixels_azuis={azul}");
                if (nome == "hover_clicavel")
                    Check("hover em item CLICAVEL mostra realce", azul > 200, $"{azul}px");
                else
                    Check("hover em item NAO-CLICAVEL nao mostra realce", azul < 60, $"{azul}px");
            }

            FotoHover(clic, "hover_clicavel");
            FotoHover(sub, "hover_naoclicavel");
        }

        menu.Show(new Point(40, 40));
        Application.DoEvents();
        Thread.Sleep(450);
        Application.DoEvents();

        var h = menu.Handle;
        string png = Path.Combine(outDir, "tray_menu.png");
        bool captured = false;
        int W = 0, H = 0;
        if (h != IntPtr.Zero && GetWindowRect(h, out var rc))
        {
            W = rc.R - rc.L; H = rc.B - rc.T;
            using var bmp = new Bitmap(Math.Max(1, W), Math.Max(1, H));
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dc = g.GetHdc();
                captured = PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
            }
            bmp.Save(png, ImageFormat.Png);
        }
        Check("captura gravada", captured && File.Exists(png), $"{W}x{H}");

        // cantos arredondados DE VERDADE => Region recortada (a "borda falsa"
        // que o user viu era so o desenho; sem Region sobra a tela de baixo)
        Check("menu tem Region (cantos recortados de verdade)", menu.Region != null);

        if (File.Exists(png))
        {
            using var bmp = new Bitmap(png);
            long claro = 0, escuro = 0, dourado = 0, azul = 0, total = 0;
            for (int y = 0; y < bmp.Height; y += 2)
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    total++;
                    int lum = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                    if (lum > 200) claro++;
                    else if (lum < 90) escuro++;
                    if (c.R > 180 && c.G > 140 && c.B < 110) dourado++;
                    if (c.B > 150 && c.B > c.R + 40 && c.G > 120) azul++;
                }
            double pc = 100.0 * claro / Math.Max(1, total);
            double pe = 100.0 * escuro / Math.Max(1, total);
            Console.WriteLine($"  pixels: claro={pc:0.0}%  escuro={pe:0.0}%  dourado={dourado}  azul={azul}");
            Check("sem fundo branco (claro < 25%)", pc < 25.0, $"{pc:0.0}%");
            Check("predomina escuro (>= 45%)", pe >= 45.0, $"{pe:0.0}%");
            Check("dourado presente (titulo)", dourado > 0, $"{dourado}px");
        }

        menu.Close();
        (svc as IDisposable)?.Dispose();

        Console.WriteLine();
        Console.WriteLine($"RESULTADO: OK={_ok} FALHAS={_fail}");
        return _fail == 0 ? 0 : 1;
    }
}