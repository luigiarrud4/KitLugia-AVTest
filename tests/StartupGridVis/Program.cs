// StartupGridVis — verificação de runtime (SOMENTE-LEITURA) das colunas novas da GridStartup.
// Abre a ServicesPage fora da tela, espera a lista carregar, e reporta:
//   - cabeçalhos das colunas do DataGrid de startup,
//   - quantas linhas ILUSTRADAS (Image) têm Source não-nulo da coluna de ícone,
//   - amostra do texto da coluna Fabricante.
// Nada é alterado no sistema.
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.GUI.Pages;

namespace StartupGridVis;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml")
            });
            app.DispatcherUnhandledException += (_, e) =>
            {
                Console.WriteLine($"UI exceção: {e.Exception.Message}");
                e.Handled = true;
            };

            var page = new ServicesPage(0);
            var win = new Window
            {
                Width = 1400,
                Height = 900,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = page,
            };
            win.Show();
            Pump(2500);

            var grid = page.FindName("GridStartup") as DataGrid;
            if (grid == null)
            {
                Console.WriteLine("FALHA: GridStartup não encontrado na página.");
                return 2;
            }

            Console.WriteLine("=== Colunas da GridStartup ===");
            foreach (var c in grid.Columns)
                Console.WriteLine($"  [{c.DisplayIndex}] {c.Header}  (binding: {GetBinding(c)})");

            // Deixa a listagem carregar (fast + full + ícones)
            Pump(6000);

            Console.WriteLine($"\nItens no ItemsSource: {grid.Items.Count}");
            grid.ScrollIntoView(grid.Items[0]);
            Pump(1500);

            int linhasComIcone = 0, linhasVisiveis = 0;
            foreach (var item in grid.Items)
            {
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
                if (row == null) continue;
                linhasVisiveis++;
                var img = FindVisualChild<Image>(row, 0);
                bool temIcone = img?.Source != null;
                if (temIcone) linhasComIcone++;
                if (linhasVisiveis <= 10)
                {
                    var textos = new List<string>();
                    ColetarTextos(row, textos);
                    var detalhe = item as KitLugia.Core.StartupAppDetails;
                    Console.WriteLine(
                        $"  {(temIcone ? "[icon OK]" : "[sem ícone]")} " +
                        $"nome='{Trunc(detalhe?.Name, 24)}' fabricante='{Trunc(detalhe?.Publisher, 28)}' " +
                        $"icone={(img?.Source is System.Windows.Media.Imaging.BitmapSource bs ? bs.PixelWidth + "x" + bs.PixelHeight : "null")} " +
                        $"células={string.Join(" | ", textos.Select(t => Trunc(t, 18)))}");
                }
            }
            Console.WriteLine($"\nLinhas ilustradas com ícone: {linhasComIcone}/{linhasVisiveis}");
            var primeira = grid.Items.Count > 0
                ? grid.ItemContainerGenerator.ContainerFromItem(grid.Items[0]) as DataGridRow
                : null;
            Console.WriteLine($"Altura real de uma linha: {(primeira == null ? 0 : Math.Round(primeira.ActualHeight))} px — cabem mais itens que antes (era 52)");

            // Ícones dos itens UWP (StartupTask): antes caíam no ícone genérico do Windows.
            var generico = KitLugia.GUI.Helpers.ProgramIconHelper.GetGenericIcon();
            var origem = grid.ItemsSource as System.Collections.IEnumerable;
            if (origem != null)
            {
                Console.WriteLine(Environment.NewLine + "Itens UWP / StartupTask:");
                foreach (var item in origem.OfType<KitLugia.Core.StartupAppDetails>()
                             .Where(x => (x.LocationShort ?? "").Contains("UWP", StringComparison.OrdinalIgnoreCase)))
                {
                    var ic = KitLugia.GUI.Helpers.ProgramIconHelper.GetIconFromFiles(item.IconFilePath, item.FilePath, item.ExePath)
                             ?? generico;
                    Console.WriteLine($"  {Trunc(item.Name, 16),-16} arquivo='{Trunc(item.IconFilePath, 56)}' " +
                                      $"{(Iguais(ic, generico) ? "GENÉRICO" : "ícone do app")} " +
                                      $"{(ic == null ? "-" : ic.PixelWidth + "x" + ic.PixelHeight)}");
                }
            }

            // Detalhe dos itens problemáticos: tamanho do Source x tamanho renderizado.
            Console.WriteLine(Environment.NewLine + "Detalhe de ícones (Source x renderizado):");
            foreach (var alvo in new[] { "cute-borders", "HERMES", "Discord", "Opera" })
            {
                foreach (var item in grid.Items.OfType<KitLugia.Core.StartupAppDetails>()
                             .Where(x => (x.Name ?? "").Contains(alvo, StringComparison.OrdinalIgnoreCase)).Take(2))
                {
                    grid.ScrollIntoView(item);
                    Pump(400);
                    if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow linha) continue;
                    var im = FindVisualChild<Image>(linha, 0);
                    string fonte = im?.Source is System.Windows.Media.Imaging.BitmapSource bmp
                        ? bmp.PixelWidth + "x" + bmp.PixelHeight
                        : "null";
                    // Salva o ícone exatamente como ele é pintado na tela (34px), ampliado 8x,
                    // para conferir se o problema é o desenho do ícone ou a renderização.
                    if (im != null && im.ActualWidth > 0)
                    {
                        Directory.CreateDirectory("out_icons");
                        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)im.ActualWidth, (int)im.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        rtb.Render(im);
                        var grande = new System.Windows.Media.Imaging.TransformedBitmap(rtb,
                            new ScaleTransform(8, 8));
                        var codificador = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        codificador.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(grande));
                        string nome = Path.Combine("out_icons", "render_" + alvo.Replace(" ", "_") + "_" + Math.Abs(item.Name.GetHashCode() % 1000) + ".png");
                        using (var fs = File.Create(nome)) codificador.Save(fs);
                        Console.WriteLine($"    → {nome}");
                    }
                    // Salva o BITMAP ligado ao Image (não o visual), para saber exatamente qual
                    // ícone o converter devolveu para esta linha.
                    if (im?.Source is System.Windows.Media.Imaging.BitmapSource bitmapLinha)
                    {
                        var cod = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        cod.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapLinha));
                        string alvoArq = Path.Combine("out_icons", "source_" + alvo.Replace(" ", "_") + ".png");
                        using (var fs2 = File.Create(alvoArq)) cod.Save(fs2);
                    }
                    // Compara cada candidato dentro do MESMO processo da UI.
                    foreach (var cand in new[] { item.IconFilePath, item.FilePath, item.ExePath })
                    {
                        if (string.IsNullOrWhiteSpace(cand)) continue;
                        var iconeCand = KitLugia.GUI.Helpers.ProgramIconHelper.GetIconFromFile(cand);
                        Console.WriteLine($"      candidato={Trunc(cand, 46),-46} tinta={Tinta(iconeCand)}");
                    }
                    var iconeLista = KitLugia.GUI.Helpers.ProgramIconHelper.GetIconFromFiles(item.IconFilePath, item.FilePath, item.ExePath);
                    Console.WriteLine($"      GetIconFromFiles → tinta={Tinta(iconeLista)}   ligado no Image → tinta={Tinta(im?.Source as System.Windows.Media.Imaging.BitmapSource)}");
                    Console.WriteLine($"  {Trunc(item.Name, 20),-20} source={fonte,-10} render={Math.Round(im?.ActualWidth ?? 0)}x{Math.Round(im?.ActualHeight ?? 0)} arquivo={Trunc(item.IconFilePath, 44)}");
                }
            }

            // Comparação crua dentro do processo WPF: qual API devolve o quê.
            Console.WriteLine(Environment.NewLine + "APIs cruas (processo WPF):");
            foreach (var alvoCru in new[] { @"\Users\Lugia\.cuteborders\cute-borders.exe", @"\Windows\System32\wscript.exe" })
            {
                Console.WriteLine($"  {Path.GetFileName(alvoCru)}");
                Console.WriteLine($"    jumbo        → {Tinta(ProgramIconHelperTeste.Jumbo(alvoCru))}");
                Console.WriteLine($"    privateExt   → {Tinta(ProgramIconHelperTeste.Privado(alvoCru))}");
                Console.WriteLine($"    shell 32px   → {Tinta(ProgramIconHelperTeste.Shell32(alvoCru))}");
            }

            win.Close();
            Console.WriteLine(linhasComIcone > 0 ? "\nRESULTADO: OK — colunas e ícones funcionando." : "\nRESULTADO: SEM ÍCONES — investigar.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FALHA: {ex}");
            return 2;
        }
    }

    /// <summary>Quanto do quadrado a tinta ocupa (%), medido no alfa do bitmap.</summary>
    private static string Tinta(System.Windows.Media.Imaging.BitmapSource? src)
    {
        if (src == null) return "null";
        try
        {
            var fc = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = fc.PixelWidth, h = fc.PixelHeight;
            var px = new byte[w * h * 4];
            fc.CopyPixels(px, w * 4, 0);
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (px[i + 3] <= 20) continue;
                    if (x < x0) x0 = x; if (y < y0) y0 = y;
                    if (x > x1) x1 = x; if (y > y1) y1 = y;
                }
            if (x1 < 0) return $"{w}x{h} vazio";
            return $"{w}x{h} conteudo={x1 - x0 + 1}x{y1 - y0 + 1} ({100.0 * (x1 - x0 + 1) / w:0}%)";
        }
        catch (Exception ex) { return "erro: " + ex.Message; }
    }

    private static string GetBinding(DataGridColumn c) =>
        c is DataGridTextColumn tc && tc.Binding is System.Windows.Data.Binding b ? b.Path.Path : "(template)";

    private static string? Trunc(string? s, int n) =>
        s == null ? null : s.Length <= n ? s : s[..(n - 1)] + "…";

    private static void ColetarTextos(DependencyObject parent, List<string> destino)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) destino.Add(tb.Text);
            ColetarTextos(child, destino);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent, int occurrence)
        where T : DependencyObject
    {
        int found = 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && found++ == occurrence) return t;
            var deeper = FindVisualChild<T>(child, occurrence);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ms),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// Compara dois bitmaps pixel a pixel (para provar que o ícone não é o genérico).
    /// </summary>
    private static bool Iguais(System.Windows.Media.Imaging.BitmapSource? a, System.Windows.Media.Imaging.BitmapSource? b)
    {
        if (a == null || b == null) return a == b;
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        try
        {
            var fa = new System.Windows.Media.Imaging.FormatConvertedBitmap(a, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var fb = new System.Windows.Media.Imaging.FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int tam = fa.PixelWidth * fa.PixelHeight * 4;
            var ba = new byte[tam];
            var bb = new byte[tam];
            fa.CopyPixels(ba, fa.PixelWidth * 4, 0);
            fb.CopyPixels(bb, fb.PixelWidth * 4, 0);
            return ba.AsSpan().SequenceEqual(bb);
        }
        catch { return false; }
    }
}

/// <summary>Chamadas cruas, só para diagnóstico do harness.</summary>
internal static class ProgramIconHelperTeste
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetImageList(int i, ref Guid riid, out IntPtr ppv);
    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icones, int[] ids, int n, uint flags);

    public static System.Windows.Media.Imaging.BitmapSource? Jumbo(string caminho)
    {
        var sfi = new SHFILEINFO();
        if (SHGetFileInfo(caminho, 0, ref sfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(sfi), 0x4000) == IntPtr.Zero) return null;
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        if (SHGetImageList(4, ref iid, out IntPtr himl) != 0 || himl == IntPtr.Zero) return null;
        IntPtr h = ImageList_GetIcon(himl, sfi.iIcon, 1);
        System.Runtime.InteropServices.Marshal.Release(himl);
        if (h == IntPtr.Zero) return null;
        try { return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); }
        finally { DestroyIcon(h); }
    }

    public static System.Windows.Media.Imaging.BitmapSource? Privado(string caminho)
    {
        var icones = new IntPtr[1];
        if (PrivateExtractIcons(caminho, 0, 256, 256, icones, new int[1], 1, 0) == 0 || icones[0] == IntPtr.Zero) return null;
        try { return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icones[0], Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); }
        finally { DestroyIcon(icones[0]); }
    }

    public static System.Windows.Media.Imaging.BitmapSource? Shell32(string caminho)
    {
        var sfi = new SHFILEINFO();
        if (SHGetFileInfo(caminho, 0, ref sfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(sfi), 0x100) == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
        try { return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(sfi.hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); }
        finally { DestroyIcon(sfi.hIcon); }
    }
}
