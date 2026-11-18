using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KitLugia.Core;

namespace KitLugia.GUI.Helpers
{
    public static class ProgramIconHelper
    {
        [DllImport("shell32.dll", EntryPoint = "ExtractAssociatedIcon", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractAssociatedIcon(IntPtr hInst, StringBuilder iconPath, ref int index);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_SYSICONINDEX = 0x4000;   // devolve o índice na lista de imagens do sistema

        // ───────── FIX HARD ERROR BOX ("Exception Processing Message 0xC0000005") ─────────
        // Quando SHGetFileInfo/LoadLibrary tenta ler ícone de arquivo em drive de rede
        // desconectado ou ausente, o Windows mostra um box FATAL fora do processo.
        // SetErrorMode + SEM_FAILCRITICALERRORS suprime esses boxes para ESTE processo:
        // as APIs retornam erro normal (que já tratamos) em vez de abrir o diálogo.
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [System.Runtime.InteropServices.PreserveSig]
        private static extern uint SetErrorMode(uint uMode);

        private const uint SEM_FAILCRITICALERRORS = 0x0001;
        private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
        private static bool _errorModeSet;

        /// <summary>Suprime hard-error boxes do Windows neste processo (chamar 1× no startup).</summary>
        public static void SuppressHardErrorDialogs()
        {
            if (_errorModeSet) return;
            try
            {
                // OR: adiciona flags sem limpar as existentes do processo
                var current = SetErrorMode(0);
                SetErrorMode(current | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
                _errorModeSet = true;
                Logger.Log("[ICON HELPER] Hard error dialogs suprimidos (SEM_FAILCRITICALERRORS)");
            }
            catch { /* não crítico */ }
        }

        // Cache temporário para evitar reprocessar caminhos
        private static readonly Dictionary<string, BitmapSource?> PathCache = new(200, StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> PathCacheAccess = new(200, StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new();

        // Cache PERSISTENTE em disco: na 1ª execução extrai do shell32 e grava PNG;
        // nas seguintes carrega do disco (~1ms) sem tocar em shell32 — deixa os ícones
        // do Task Manager praticamente instantâneos em cada abertura do app.
        private static readonly string IconCacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "IconCache");
        private static readonly object DiskCacheLock = new();

        private static string DiskCachePath(string filePath, int? index)
        {
            long mtime = 0;
            try { if (File.Exists(filePath)) mtime = File.GetLastWriteTimeUtc(filePath).Ticks; } catch { }
            // V2 = ícone extraído em 256px (jumbo). Muda o prefixo para os PNGs gravados pelo
            // modo antigo (32px, borrados) serem ignorados — senão o cache em disco serviria
            // para sempre o ícone pequeno que já estava lá.
            return Path.Combine(IconCacheDir, $"v3_{StableHash($"{filePath}|{index ?? -1}|{mtime}")}.png");
        }

        private static string StableHash(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(b).Substring(0, 20).ToLowerInvariant();
        }

        private static BitmapSource? LoadIconFromDisk(string filePath, int? index)
        {
            try
            {
                string p = DiskCachePath(filePath, index);
                if (!File.Exists(p)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(p);
                bmp.DecodePixelWidth = IconDecodeSize;   // 64: nítido em DPI 200% sem inflar memória
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static void SaveIconToDisk(string filePath, int? index, BitmapSource? icon)
        {
            if (icon == null) return;
            try
            {
                string p = DiskCachePath(filePath, index);
                if (File.Exists(p)) return;
                Directory.CreateDirectory(IconCacheDir);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(icon));
                string tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
                lock (DiskCacheLock)
                {
                    using (var fs = File.Create(tmp)) enc.Save(fs);
                    if (File.Exists(p)) { try { File.Delete(tmp); } catch { } return; }
                    File.Move(tmp, p);
                }
            }
            catch { }
        }

        /// <summary>
        /// Obtém ícone de um arquivo tratando formato "caminho,indice"
        /// </summary>
        public static BitmapSource? GetIconFromFile(string? filePath)
        {
            return GetIconFromFile(filePath, null);
        }

        /// <summary>
        /// Tenta obter ícone testando múltiplos caminhos em ordem até encontrar
        /// </summary>
        public static BitmapSource? GetIconFromFiles(params string?[] paths)
        {
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var icon = GetIconFromFile(p);
                if (icon != null) return icon;
            }
            return null;
        }

        /// <summary>
        /// Enumeração que NUNCA lança UnauthorizedAccessException/DirectoryNotFoundException:
        /// o overload legado com SearchOption.AllDirectories aborta TUDO na primeira
        /// subpasta protegida (centenas de exceções no load dos 1100+ apps — o ruído
        /// no debugger desde a v1 — e apps ficando sem ícone à toa). Com
        /// IgnoreInaccessible a enumeração continua pastando as pastas protegidas.
        /// </summary>
        private static EnumerationOptions SafeRecurse { get; } = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint // junction: não duplicar/loopar
        };

        private static EnumerationOptions SafeTopOnly { get; } = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0 // legado NÃO pulava hidden/system/reparse (sem risco de loop: sem recursão)
        };

        /// <summary>
        /// Procura ícone em um diretório (recursivamente) e arredores
        /// </summary>
        public static BitmapSource? GetIconFromDirectory(string? installDir)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return null;

            try
            {
                // .exe na raiz
                var exes = Directory.GetFiles(installDir, "*.exe", SafeTopOnly);
                if (exes.Length > 0)
                {
                    var icon = GetIconFromFile(exes[0]);
                    if (icon != null) return icon;
                }

                // .exe em subpastas comuns
                foreach (var sub in new[] { "bin", "app", "program", "client", "core" })
                {
                    string subDir = Path.Combine(installDir, sub);
                    if (Directory.Exists(subDir))
                    {
                        exes = Directory.GetFiles(subDir, "*.exe", SafeTopOnly);
                        if (exes.Length > 0)
                        {
                            var icon = GetIconFromFile(exes[0]);
                            if (icon != null) return icon;
                        }
                    }
                }

                // .ico no diretório
                var icos = Directory.GetFiles(installDir, "*.ico", SafeRecurse);
                if (icos.Length > 0)
                {
                    var icon = GetIconFromFile(icos[0]);
                    if (icon != null) return icon;
                }

                // .exe em qualquer subnível
                exes = Directory.GetFiles(installDir, "*.exe", SafeRecurse);
                if (exes.Length > 0)
                {
                    var icon = GetIconFromFile(exes[0]);
                    if (icon != null) return icon;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return null;
        }

        private static BitmapSource? GetIconFromFile(string? filePath, int? overrideIndex)
        {
            if (string.IsNullOrEmpty(filePath)) return null;

            // Separa "caminho,indice" (ex: "imageres.dll,3") ANTES de qualquer cache,
            // para o cache em disco indexar pelo arquivo real + índice correto.
            int iconIndex = overrideIndex ?? 0;
            string cleanPath = filePath;
            if (!overrideIndex.HasValue && cleanPath.Contains(','))
            {
                var parts = cleanPath.Split(',');
                cleanPath = parts[0].Trim();
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int idx))
                    iconIndex = idx;
            }

            // Verifica cache
            string cacheKey = $"{filePath}_{overrideIndex ?? -1}";
            lock (CacheLock)
            {
                if (PathCache.TryGetValue(cacheKey, out var cached))
                {
                    PathCacheAccess[cacheKey] = DateTime.UtcNow;
                    return cached;
                }
            }

            // Cache persistente em disco (evita shell32 a cada abertura do app)
            if (File.Exists(cleanPath))
            {
                var fromDisk = LoadIconFromDisk(cleanPath, iconIndex);
                if (fromDisk != null)
                {
                    lock (CacheLock)
                    {
                        if (!PathCache.ContainsKey(cacheKey))
                        {
                            PathCache[cacheKey] = fromDisk;
                            PathCacheAccess[cacheKey] = DateTime.UtcNow;
                        }
                    }
                    return fromDisk;
                }
            }

            BitmapSource? result = null;

            try
            {
                // .ico direto
                if (cleanPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanPath))
                    result = LoadIcoFile(cleanPath);

                // PNG/JPG direto: logos de pacotes UWP e assets de app. SHGetFileInfo nesses
                // arquivos devolve o ícone genérico de "imagem", então decodificamos o arquivo.
                if (result == null && IsRasterImage(cleanPath) && File.Exists(cleanPath))
                    result = LoadRasterImage(cleanPath);

                // FIX HARD ERROR 0xC0000005: caminhos de rede/UNC/drive ausentes fazem o
                // loader do Windows disparar "Exception Processing Message" (hard error box).
                // Nunca passar esses caminhos para SHGetFileInfo/ExtractAssociatedIcon:
                // usar apenas SHGFI_USEFILEATTRIBUTES (não abre o arquivo) ou pular.
                bool isUnsafePath =
                    cleanPath.StartsWith(@"\\", StringComparison.Ordinal) ||                 // UNC
                    cleanPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||       // URL
                    (!File.Exists(cleanPath) && !cleanPath.Contains(':'));                     // sem drive

                // Ícone em ALTA resolução: o shell expõe a lista jumbo (256x256) do Vista em
                // diante. SHGetFileInfo com SHGFI_ICON|SHGFI_LARGEICON devolve 32x32 — daí os
                // ícones borrados/errados na tela. SYSICONINDEX + SHIL_JUMBO é o caminho
                // documentado para se obter o ícone grande que o Explorer usa.
                if (result == null && File.Exists(cleanPath) && !isUnsafePath)
                    result = TryJumboIcon(cleanPath);

                // Fallback de alta resolução para arquivos que o shell não indexa
                // (exe solto, dll com ícone, .ico multi-frame): pede 256x256 direto ao
                // extrator de recursos.
                if (result == null && File.Exists(cleanPath) && !isUnsafePath &&
                    (cleanPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                     cleanPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    result = TryPrivateExtractIcon(cleanPath, iconIndex);

                // SHGetFileInfo (só com arquivo local existente; senão, modo atributos-only)
                if (result == null && !isUnsafePath)
                {
                    try
                    {
                        bool exists2 = File.Exists(cleanPath);
                        uint flags2 = SHGFI_ICON | SHGFI_LARGEICON;
                        if (!exists2) flags2 |= SHGFI_USEFILEATTRIBUTES;
                        var shfi = new SHFILEINFO();
                        IntPtr hr = SHGetFileInfo(cleanPath, 0, ref shfi, (uint)Marshal.SizeOf(shfi), flags2);
                        if (hr != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                        {
                            using (var icon = System.Drawing.Icon.FromHandle(shfi.hIcon))
                            {
                                var bmp = IconToBitmapSource(icon);
                                DestroyIcon(shfi.hIcon);
                                if (bmp != null) result = bmp;
                            }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                if (result == null && File.Exists(cleanPath))
                    result = TryExtractIconEx(cleanPath, iconIndex);

                // ExtractAssociatedIcon com índice específico (só arquivo LOCAL existente)
                if (result == null && File.Exists(cleanPath) && !isUnsafePath)
                {
                    try
                    {
                        var sb = new StringBuilder(260);
                        sb.Append(cleanPath);
                        int index = iconIndex;
                        var handle = ExtractAssociatedIcon(IntPtr.Zero, sb, ref index);
                        if (handle != IntPtr.Zero)
                        {
                            using (var icon = System.Drawing.Icon.FromHandle(handle))
                            result = IconToBitmapSource(icon);
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                // Se não achou e o caminho não existe, tenta localizar o arquivo
                if (result == null && !File.Exists(cleanPath))
                {
                    string? foundPath = ResolveFilePath(cleanPath);
                    if (foundPath != null)
                        result = GetIconFromFile(foundPath, iconIndex);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Corte da margem transparente: o shell entrega alguns ícones desenhados dentro de um
            // canvas grande e vazio (ex.: cute-borders, wscript), e na coluna de 34px o desenho
            // ficava com 5px. Recortar na tinta deixa o glifo preencher o espaço.
            result = CortarMargemTransparente(result);

            lock (CacheLock)
            {
                if (!PathCache.ContainsKey(cacheKey))
                {
                    // Cap LRU: remove o menos acessado quando o cache lota (evita crescimento sem limite)
                    if (PathCache.Count >= 200)
                    {
                        DateTime oldest = DateTime.MaxValue;
                        string? evictKey = null;
                        foreach (var kv in PathCacheAccess)
                            if (kv.Value < oldest) { oldest = kv.Value; evictKey = kv.Key; }
                        if (evictKey != null)
                        {
                            PathCache.Remove(evictKey);
                            PathCacheAccess.Remove(evictKey);
                        }
                    }
                    PathCache[cacheKey] = result;
                    PathCacheAccess[cacheKey] = DateTime.UtcNow;
                }
            }

            if (result != null && File.Exists(cleanPath))
                SaveIconToDisk(cleanPath, iconIndex, result);
            return result;
        }

        /// <summary>
        /// Tenta localizar um arquivo que não foi encontrado no caminho original
        /// </summary>
        private static string? ResolveFilePath(string originalPath)
        {
            try
            {
                string fileName = Path.GetFileName(originalPath);
                if (string.IsNullOrEmpty(fileName)) return null;

                // Procura no System32
                string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string candidate = Path.Combine(sys32, fileName);
                if (File.Exists(candidate)) return candidate;

                // Procura no SystemWOW64
                candidate = Path.Combine(sys32, "..", "SysWOW64", fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                // Procura no Windows
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                candidate = Path.Combine(winDir, fileName);
                if (File.Exists(candidate)) return candidate;

                // Nota: busca em ProgramFiles removida (lenta, 30-50 IOs por falha) — só System32/Windows relevantes para processos
                // Se precisar de ProgramFiles, use cache de disco ou busca lazy sob demanda
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        private static BitmapSource? TryExtractIconEx(string path, int index)
        {
            try
            {                    IntPtr[] large = new IntPtr[1];
                    IntPtr[] small = new IntPtr[1];
                    uint cnt = ExtractIconEx(path, index, large, small, 1);
                    IntPtr hIcon = large[0] != IntPtr.Zero ? large[0] : small[0];
                if (hIcon != IntPtr.Zero)
                {
                    try
                    {
                        using (var icon = System.Drawing.Icon.FromHandle(hIcon))
                            return IconToBitmapSource(icon);
                    }
                    finally
                    {
                        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                        if (large[0] != IntPtr.Zero && large[0] != hIcon) DestroyIcon(large[0]);
                        if (small[0] != IntPtr.Zero && small[0] != hIcon) DestroyIcon(small[0]);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        #region Corte da margem transparente

        /// <summary>
        /// Recorta o padding transparente do ícone.
        ///
        /// Motivo: o shell (lista jumbo) e alguns .ico de terceiros entregam o desenho dentro de um
        /// canvas grande e vazio — o cute-borders e o wscript.exe ocupavam 19% do quadrado, então na
        /// coluna de 34px o glifo saía com ~6px ao lado de Discord/Steam que preenchem tudo. Como a
        /// Image usa Stretch=Uniform, recortar equivale a ampliar o desenho até preencher o espaço.
        ///</n>
        /// Só age quando a tinta ocupa menos de 88% do lado (ícones já cheios não são tocados) e
        /// mantém 6% de respiro, para não colar o glifo na borda.
        /// </summary>
        private static BitmapSource? CortarMargemTransparente(BitmapSource? icone)
        {
            if (icone == null) return null;

            try
            {
                int w = icone.PixelWidth, h = icone.PixelHeight;
                if (w < 8 || h < 8) return icone;

                var px = new byte[w * h * 4];
                new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    icone, PixelFormats.Bgra32, null, 0).CopyPixels(px, w * 4, 0);

                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++)
                {
                    int baseLinha = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        if (px[baseLinha + x * 4 + 3] <= 20) continue;
                        if (x < x0) x0 = x;
                        if (y < y0) y0 = y;
                        if (x > x1) x1 = x;
                        if (y > y1) y1 = y;
                    }
                }

                if (x1 < 0) return icone;                       // totalmente transparente

                int largura = x1 - x0 + 1, altura = y1 - y0 + 1;
                if (largura >= w * 0.88 && altura >= h * 0.88) return icone;   // já preenche

                // Respiro de 6% do lado maior, sem passar do canvas original.
                int folga = (int)Math.Round(Math.Max(largura, altura) * 0.06);
                x0 = Math.Max(0, x0 - folga);
                y0 = Math.Max(0, y0 - folga);
                x1 = Math.Min(w - 1, x1 + folga);
                y1 = Math.Min(h - 1, y1 + folga);

                var recorte = new System.Windows.Media.Imaging.CroppedBitmap(
                    icone, new Int32Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
                recorte.Freeze();
                return recorte;
            }
            catch
            {
                return icone;   // qualquer falha: mantém o ícone original
            }
        }

        #endregion

        #region Ícone em alta resolução (jumbo 256px do shell + PrivateExtractIcons)

        private const int SHIL_JUMBO = 0x4;
        private const uint ILD_TRANSPARENT = 0x1;

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

        [DllImport("comctl32.dll")]
        private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex,
            int cxIcon, int cyIcon, IntPtr[]? phicon, uint[]? piconid, uint nIcons, uint flags);

        /// <summary>
        /// Tamanho em que os ícones são decodificados. O DataGrid mostra 26px e as listas 32px;
        /// decodificar em 64 dá margem para DPI 200% sem inflar memória (256px por ícone ×
        /// 1100 apps passaria de 250 MB).
        /// </summary>
        private const int IconDecodeSize = 64;

        /// <summary>
        /// Ícone 256x256 do shell: SHGetFileInfo com SHGFI_SYSICONINDEX dá o índice do ícone na
        /// lista de imagens do sistema, e SHGetImageList(SHIL_JUMBO) devolve essa lista em 256px.
        /// É o caminho recomendado pela documentação para não ficar preso no 32x32 do
        /// SHGFI_ICON (fonte dos ícones borrados). Reduzimos para IconDecodeSize antes de devolver.
        /// </summary>
        private static BitmapSource? TryJumboIcon(string path)
        {
            IntPtr himl = IntPtr.Zero;
            try
            {
                var sfi = new SHFILEINFO();
                IntPtr ok = SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(sfi),
                    SHGFI_SYSICONINDEX);
                if (ok == IntPtr.Zero || sfi.iIcon == 0) return null;

                var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IID_IImageList
                int hrLista = SHGetImageList(SHIL_JUMBO, ref iid, out himl);
                if (hrLista != 0 || himl == IntPtr.Zero) return null;

                IntPtr hIcon = ImageList_GetIcon(himl, sfi.iIcon, ILD_TRANSPARENT);
                if (hIcon == IntPtr.Zero) return null;

                try { return FromHIcon(hIcon); }
                finally { DestroyIcon(hIcon); }
            }
            catch { return null; }
            finally
            {
                // A lista é compartilhada pelo shell: só soltamos a referência, sem destruir.
                if (himl != IntPtr.Zero) { try { Marshal.Release(himl); } catch { } }
            }
        }

        /// <summary>
        /// Pede o ícone em 256x256 direto ao extrator de recursos (funciona para exe/dll/ico).
        /// Usado quando a lista jumbo não tem o arquivo indexado.
        /// </summary>
        private static BitmapSource? TryPrivateExtractIcon(string path, int index)
        {
            try
            {
                var hicons = new IntPtr[1];
                uint extraidos = PrivateExtractIcons(path, index, 256, 256, hicons, null, 1, 0);
                if (extraidos == 0 || hicons[0] == IntPtr.Zero) return null;

                try { return FromHIcon(hicons[0]); }
                finally { DestroyIcon(hicons[0]); }
            }
            catch { return null; }
        }

        /// <summary>
        /// HICON -&gt; BitmapSource preservando alfa (CreateBitmapSourceFromHIcon), reduzido para
        /// IconDecodeSize. Evita o ToBitmap() do System.Drawing, que perdia transparência.
        /// </summary>
        private static BitmapSource? FromHIcon(IntPtr hIcon)
        {
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                // Recorta ANTES de reduzir: em 256px o glifo tem resolução de sobra, então o corte
                // preserva detalhe. Se deixássemos para depois do downscale, um glifo de 48px no
                // canvas de 256 viraria 12px e aí sim ficaria borrado ao ser ampliado na tela.
                bmp = CortarMargemTransparente(bmp) ?? bmp;

                if (bmp.PixelWidth > IconDecodeSize)
                {
                    double escala = (double)IconDecodeSize / bmp.PixelWidth;
                    var reduzido = new TransformedBitmap(bmp, new ScaleTransform(escala, escala));
                    reduzido.Freeze();
                    return reduzido;
                }

                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        #endregion

        private static BitmapSource? LoadIcoFile(string icoPath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(icoPath);                    bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        /// <summary>
        /// Extensões de imagem que decodificamos direto (logos de pacote UWP, assets de app).
        /// </summary>
        private static bool IsRasterImage(string? path) =>
            !string.IsNullOrEmpty(path) &&
            (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Decodifica um PNG/JPG como ícone, reduzindo na decodificação (DecodePixelWidth 64) —
        /// sem isso um Square44x44Logo.scale-400 (176x176) viraria bitmap grande em memória.
        /// </summary>
        private static BitmapSource? LoadRasterImage(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = 64;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { Logger.LogWarning("ProgramIconHelper", "Falha ao decodificar imagem de ícone"); return null; }
        }

        private static BitmapSource? IconToBitmapSource(System.Drawing.Icon icon)        {
            try
            {
                using (var bitmap = icon.ToBitmap())
                using (var memory = new MemoryStream())
                {
                    bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                    memory.Position = 0;

                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = memory;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        public static BitmapSource? GetGenericIcon()
        {
            try
            {
                string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string imageres = Path.Combine(system32, "imageres.dll");
                if (File.Exists(imageres))
                {
                    var shfi = new SHFILEINFO();
                    IntPtr result = SHGetFileInfo(imageres, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
                    if (result != IntPtr.Zero && shfi.hIcon != IntPtr.Zero)
                    {
                        using (var icon = System.Drawing.Icon.FromHandle(shfi.hIcon))
                        {
                            var bmp = IconToBitmapSource(icon);
                            DestroyIcon(shfi.hIcon);
                            if (bmp != null) return bmp;
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return null;
        }
    }
}
