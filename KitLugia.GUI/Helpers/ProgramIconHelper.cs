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
        /// Procura ícone em um diretório (recursivamente) e arredores
        /// </summary>
        public static BitmapSource? GetIconFromDirectory(string? installDir)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return null;

            try
            {
                // .exe na raiz
                var exes = Directory.GetFiles(installDir, "*.exe", SearchOption.TopDirectoryOnly);
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
                        exes = Directory.GetFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly);
                        if (exes.Length > 0)
                        {
                            var icon = GetIconFromFile(exes[0]);
                            if (icon != null) return icon;
                        }
                    }
                }

                // .ico no diretório
                var icos = Directory.GetFiles(installDir, "*.ico", SearchOption.AllDirectories);
                if (icos.Length > 0)
                {
                    var icon = GetIconFromFile(icos[0]);
                    if (icon != null) return icon;
                }

                // .exe em qualquer subnível
                exes = Directory.GetFiles(installDir, "*.exe", SearchOption.AllDirectories);
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

            BitmapSource? result = null;

            try
            {
                int iconIndex = overrideIndex ?? 0;
                string cleanPath = filePath;

                // Separa "caminho,indice" (ex: "imageres.dll,3")
                if (!overrideIndex.HasValue && cleanPath.Contains(','))
                {
                    var parts = cleanPath.Split(',');
                    cleanPath = parts[0].Trim();
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int idx))
                        iconIndex = idx;
                }

                // .ico direto
                if (cleanPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanPath))
                    result = LoadIcoFile(cleanPath);

                // FIX HARD ERROR 0xC0000005: caminhos de rede/UNC/drive ausentes fazem o
                // loader do Windows disparar "Exception Processing Message" (hard error box).
                // Nunca passar esses caminhos para SHGetFileInfo/ExtractAssociatedIcon:
                // usar apenas SHGFI_USEFILEATTRIBUTES (não abre o arquivo) ou pular.
                bool isUnsafePath =
                    cleanPath.StartsWith(@"\\", StringComparison.Ordinal) ||                 // UNC
                    cleanPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||       // URL
                    (!File.Exists(cleanPath) && !cleanPath.Contains(':'));                     // sem drive

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

        private static BitmapSource? IconToBitmapSource(System.Drawing.Icon icon)
        {
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
