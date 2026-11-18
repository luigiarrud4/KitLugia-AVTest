// IconProbe — mede a qualidade do ícone que o KitLugia extrai:
//   1. exe/dll: prova que a fonte é a lista jumbo do shell (HICON 256x256) e que sai em 64x64;
//   2. UWP: mostra logo do pacote x ícone genérico da Store, e diagnostica o shell:AppsFolder.
// Uso: dotnet run --project tests/IconProbe -c Release
using System.IO;
using System.Windows.Media.Imaging;
using KitLugia.GUI.Helpers;

namespace IconProbe;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var caminhos = new (string Rotulo, string? Caminho)[]
        {
            ("Discord Update.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe")),
            ("Discord.exe",        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Discord.exe")),
            ("notepad.exe",        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe")),
            ("explorer.exe",       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
            ("shell32.dll",        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll")),
            ("opera.exe",          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Opera GX", "opera.exe")),
            ("steam.exe",          @"C:\Program Files (x86)\Steam\steam.exe"),
        };

        Console.WriteLine($"{"ITEM",-22} {"TAMANHO",-10} {"HICON JUMBO",-12} CORES");
        Console.WriteLine(new string('-', 62));

        foreach (var (rotulo, caminho) in caminhos)
        {
            if (string.IsNullOrWhiteSpace(caminho) || !File.Exists(caminho))
            {
                Console.WriteLine($"{rotulo,-22} (não existe nesta máquina)");
                continue;
            }

            var icone = ProgramIconHelper.GetIconFromFile(caminho);
            var (jw, jh) = Nativo.Jumbo(caminho);

            string tamanho = icone is BitmapSource bs ? $"{bs.PixelWidth}x{bs.PixelHeight}" : "NULL";
            int cores = icone is BitmapSource b2 ? ContarCores(b2) : -1;
            Console.WriteLine($"{rotulo,-22} {tamanho,-10} {jw + "x" + jh,-12} {cores}");
        }

        Console.WriteLine();
        Console.WriteLine("Preenchimento do ícone (quanto do box o conteúdo ocupa):");
        foreach (var caminhoSolto in new[] { @"\Users\Lugia\Downloads\cute-borders.exe", @"\Users\Lugia\.cuteborders\cute-borders.exe", @"\Windows\System32\wscript.exe", @"\Program Files (x86)\Steam\steam.exe" })
        {
            if (!File.Exists(caminhoSolto)) { Console.WriteLine($"  {caminhoSolto} (não existe)"); continue; }
            var ic = ProgramIconHelper.GetIconFromFile(caminhoSolto);
            if (ic is not BitmapSource b) { Console.WriteLine($"  {caminhoSolto} NULL"); continue; }
            var (bx, by, bw, bh) = BoundingBox(b);
            Console.WriteLine($"  {Path.GetFileName(caminhoSolto),-20} fonte={b.PixelWidth}x{b.PixelHeight} conteudo={bw}x{bh} em ({bx},{by}) preenche={100.0 * bw / b.PixelWidth:0}%");
        }

        Console.WriteLine();
        Console.WriteLine("Dump dos ícones (ampliados 4x para inspeção):");
        Directory.CreateDirectory("out_icons");
        foreach (var caminhoDump in new[] { @"\Users\Lugia\Downloads\cute-borders.exe", @"\Windows\System32\wscript.exe" })
        {
            if (!File.Exists(caminhoDump)) { Console.WriteLine($"  {caminhoDump} (não existe)"); continue; }
            var ic = ProgramIconHelper.GetIconFromFile(caminhoDump);
            if (ic is not BitmapSource b) { Console.WriteLine($"  {caminhoDump} NULL"); continue; }
            var ampliado = new TransformedBitmap(b, new System.Windows.Media.ScaleTransform(4, 4));
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(ampliado));
            string saida = Path.Combine("out_icons", Path.GetFileNameWithoutExtension(caminhoDump) + ".png");
            using (var fs = File.Create(saida)) enc.Save(fs);
            foreach (int limite in new[] { 8, 32, 128 })
            {
                var (cx, cy, cw, ch) = BoundingBox(b, limite);
                Console.WriteLine($"  {Path.GetFileName(caminhoDump),-18} alfa>{limite,-3} conteudo={cw}x{ch} em ({cx},{cy}) preenche={100.0 * cw / b.PixelWidth:0}%");
            }
            Console.WriteLine($"  → {saida}");
        }

        Console.WriteLine();
        Console.WriteLine("Cache em disco (PNG gravado por último):");
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "IconCache");
            if (Directory.Exists(cache))
            {
                var arquivos = Directory.GetFiles(cache, "*.png");
                Console.WriteLine($"  {arquivos.Length} arquivos em {cache}");
                foreach (var alvoCache in new[] { @"\Users\Lugia\.cuteborders\cute-borders.exe", @"\Windows\System32\wscript.exe" })
                {
                    var iconeAoVivo = ProgramIconHelper.GetIconFromFile(alvoCache);
                    if (iconeAoVivo is BitmapSource vivo)
                    {
                        var (vx, vy, vw, vh) = BoundingBox(vivo);
                        Console.WriteLine($"  {Path.GetFileName(alvoCache),-20} AO VIVO {vivo.PixelWidth}x{vivo.PixelHeight} conteudo={vw}x{vh} preenche={100.0 * vw / vivo.PixelWidth:0}%");
                    }
                    else Console.WriteLine($"  {Path.GetFileName(alvoCache),-20} AO VIVO null");
                }
                // O PNG mais recente da pasta (primeiros bytes do nome = hash do caminho)
                foreach (var arq in arquivos.OrderByDescending(File.GetLastWriteTime).Take(3))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(arq); bmp.EndInit(); bmp.Freeze();
                        var (cx2, cy2, cw2, ch2) = BoundingBox(bmp);
                        Console.WriteLine($"    {Path.GetFileName(arq)} {bmp.PixelWidth}x{bmp.PixelHeight} conteudo={cw2}x{ch2} preenche={100.0 * cw2 / bmp.PixelWidth:0}%");
                    }
                    catch (Exception ex) { Console.WriteLine($"    {Path.GetFileName(arq)} ilegível: {ex.Message}"); }
                }
            }
            else Console.WriteLine($"  pasta de cache não existe ({cache})");
        }

        var aumids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["505GAMESS.P.A.JourneytotheSavagePlanet-EotMPCGP"] = "505GAMESS.P.A.JourneytotheSavagePlanet-EotMPCGP_tefn33qh9azfc!AppJourneyToTheSavagePlanetShipping",
            ["16902PlaydeadAPS.PlaydeadsLIMBO"] = "16902PlaydeadAPS.PlaydeadsLIMBO_2m6wzp0cmt084!LIMBOTheGame",
            ["A278AB0D.Asphalt9"] = "A278AB0D.Asphalt9_h6adky7gbf63m!Asphalt9",
            ["21090PaddyXu.QuickLook"] = "21090PaddyXu.QuickLook_egxr34yet59cg!QuickLookTask",
        };

        string[] pacotes =
        {
            "505GAMESS.P.A.JourneytotheSavagePlanet-EotMPCGP",
            "16902PlaydeadAPS.PlaydeadsLIMBO",
            "AIToolkit.Inference.Agent",
            "21090PaddyXu.QuickLook",
            "40459File-New-Project.EarTrumpet",
            "EASx.BypassTools",
            "Microsoft.Paint",
            "A278AB0D.Asphalt9",
        };

        Console.WriteLine();
        Console.WriteLine("Ícones UWP (GEN = caiu no ícone genérico da Store):");
        var genericoStore = AppIconHelper.GetGenericStoreIcon();
        foreach (var pkg in pacotes)
        {
            var aumid = aumids.TryGetValue(pkg, out var a) ? a : null;
            var ic = AppIconHelper.GetAppIcon(pkg, 64, aumid);
            bool ehGenerico = ic is BitmapSource bi && genericoStore is BitmapSource bg && Iguais(bi, bg);
            var shell = aumid == null ? "(sem aumid)" : Nativo.ShellAppsFolder(aumid);
            Console.WriteLine($"  {pkg,-52} {(ic is BitmapSource x ? x.PixelWidth + "x" + x.PixelHeight : "NULL"),-9} " +
                              $"{(ehGenerico ? "GEN" : "logo")} shell={shell}");
        }

        Console.WriteLine();
        Console.WriteLine("Pacotes servidos pelo shell:AppsFolder (arquivo mais largo ⇒ 256px):");
        return 0;
    }

    /// <summary>Retângulo do conteúdo não-transparente (alfa &gt; 8).</summary>
    private static (int X, int Y, int W, int H) BoundingBox(BitmapSource bs, int limite = 8)
    {
        var fc = new FormatConvertedBitmap(bs, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int w = fc.PixelWidth, h = fc.PixelHeight;
        var px = new byte[w * h * 4];
        fc.CopyPixels(px, w * 4, 0);
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (px[(y * w + x) * 4 + 3] <= limite) continue;
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
            }
        if (x1 < 0) return (0, 0, w, h);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static int ContarCores(BitmapSource bs)
    {
        try
        {
            var fc = new FormatConvertedBitmap(bs, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = fc.PixelWidth, h = fc.PixelHeight;
            var px = new byte[w * h * 4];
            fc.CopyPixels(px, w * 4, 0);
            var set = new HashSet<uint>();
            for (int i = 0; i < px.Length; i += 4)
                set.Add((uint)(px[i] | px[i + 1] << 8 | px[i + 2] << 16 | px[i + 3] << 24));
            return set.Count;
        }
        catch { return -1; }
    }

    private static bool Iguais(BitmapSource? a, BitmapSource? b)
    {
        if (a == null || b == null) return a == b;
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        try
        {
            var f1 = new FormatConvertedBitmap(a, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var f2 = new FormatConvertedBitmap(b, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int tam = f1.PixelWidth * f1.PixelHeight * 4;
            var p1 = new byte[tam];
            var p2 = new byte[tam];
            f1.CopyPixels(p1, f1.PixelWidth * 4, 0);
            f2.CopyPixels(p2, f2.PixelWidth * 4, 0);
            return p1.AsSpan().SequenceEqual(p2);
        }
        catch { return false; }
    }
}

/// <summary>Chamadas cruas para conferir o que o Windows expõe, sem passar pelo helper.</summary>
internal static class Nativo
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Tamanho { public int Width; public int Height; }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactoryRaw
    {
        [System.Runtime.InteropServices.PreserveSig]
        int GetImage([System.Runtime.InteropServices.In] ref Tamanho size,
                     [System.Runtime.InteropServices.In] uint flags, out IntPtr phbm);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Tamanho do HICON que o shell entrega na lista jumbo.</summary>
    public static (int, int) Jumbo(string path)
    {
        var sfi = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref sfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(sfi), 0x4000) == IntPtr.Zero)
            return (0, 0);

        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        if (SHGetImageList(4, ref iid, out IntPtr himl) != 0 || himl == IntPtr.Zero) return (0, 0);

        try
        {
            IntPtr h = ImageList_GetIcon(himl, sfi.iIcon, 1);
            if (h == IntPtr.Zero) return (0, 0);
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    h, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                return (bmp.PixelWidth, bmp.PixelHeight);
            }
            finally { DestroyIcon(h); }
        }
        finally { System.Runtime.InteropServices.Marshal.Release(himl); }
    }

    /// <summary>O que o shell devolve para shell:AppsFolder\{aumid}.</summary>
    public static string ShellAppsFolder(string aumid)
    {
        var iid = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
        int hr = SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, ref iid, out IntPtr ppv);
        if (hr != 0 || ppv == IntPtr.Zero) return $"SHCreateItem hr=0x{hr:X8}";

        try
        {
            var fabrica = (IShellItemImageFactoryRaw)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(ppv);
            var tam = new Tamanho { Width = 64, Height = 64 };
            int hr2 = fabrica.GetImage(ref tam, 0x101 /* BIGGERSIZEOK|ICONONLY */, out IntPtr hbm);
            if (hr2 != 0 || hbm == IntPtr.Zero) return $"GetImage hr=0x{hr2:X8}";
            DeleteObject(hbm);
            return "OK";
        }
        finally { System.Runtime.InteropServices.Marshal.Release(ppv); }
    }
}
