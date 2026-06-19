using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KitLugia.Core;

namespace KitLugia.GUI.Helpers
{
    public static class AppIconHelper
    {
        private static readonly Dictionary<string, BitmapSource?> IconCache = new Dictionary<string, BitmapSource?>(500, StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        public static BitmapSource? GetAppIcon(string packageName, int size = 32)
        {
            string cacheKey = $"{packageName}_{size}";
            lock (CacheLock)
            {
                if (IconCache.ContainsKey(cacheKey))
                    return IconCache[cacheKey];
            }

            BitmapSource? icon = null;
            try
            {
                string? iconPath = GetUwpIconPathNative(packageName);
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    icon = LoadImageFromFile(iconPath, size);
                }
            }
            catch { }

            lock (CacheLock)
            {
                if (!IconCache.ContainsKey(cacheKey))
                    IconCache[cacheKey] = icon;
            }
            return icon;
        }

        /// <summary>
        /// Procura o ícone UWP em várias fontes: registro HKCU, HKLM, manifest, e busca recursiva
        /// </summary>
        private static string? GetUwpIconPathNative(string packageName)
        {
            // 1) Tenta HKCU Repository
            string? path = TryRegistryPath(packageName, Microsoft.Win32.Registry.CurrentUser,
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (path != null) return path;

            // 2) Tenta HKLM Repository
            path = TryRegistryPath(packageName, Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (path != null) return path;

            // 3) Tenta HKLM StateRepository (Windows 11+)
            try
            {
                using (var srKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateRepository\Cache\Package"))
                {
                    if (srKey != null)
                    {
                        foreach (string sub in srKey.GetSubKeyNames())
                        {
                            try
                            {
                                using (var pkgKey = srKey.OpenSubKey(sub))
                                {
                                    if (pkgKey == null) continue;
                                    string? pfn = pkgKey.GetValue("PackageFullName") as string;
                                    if (string.IsNullOrEmpty(pfn) ||
                                        pfn.IndexOf(packageName, StringComparison.OrdinalIgnoreCase) < 0)
                                        continue;

                                    string? installLoc = pkgKey.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                                    {
                                        string? icon = FindIconInLocation(installLoc);
                                        if (icon != null) return icon;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static string? TryRegistryPath(string packageName, Microsoft.Win32.RegistryKey baseKey, string subKeyPath)
        {
            try
            {
                using (var key = baseKey.OpenSubKey(subKeyPath))
                {
                    if (key == null) return null;

                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        if (subkeyName.IndexOf(packageName, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        using (var pkgKey = key.OpenSubKey(subkeyName))
                        {
                            if (pkgKey == null) continue;

                            string? installLocation = pkgKey.GetValue("PackageRootFolder") as string ??
                                                      pkgKey.GetValue("InstallLocation") as string;

                            if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                            {
                                string? iconPath = FindIconInLocation(installLocation);
                                if (!string.IsNullOrEmpty(iconPath))
                                    return iconPath;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Tenta encontrar ícone em várias pastas, com busca recursiva e múltiplos padrões
        /// </summary>
        private static string? FindIconInLocation(string installLocation)
        {
            // 1) Manifesto AppxManifest.xml (fonte oficial)
            string? logoFromManifest = GetLogoFromManifest(installLocation);
            if (logoFromManifest != null) return logoFromManifest;

            // 2) Busca recursiva na pasta Assets
            string assetsFolder = Path.Combine(installLocation, "Assets");
            if (Directory.Exists(assetsFolder))
            {
                string? found = FindIconRecursive(assetsFolder);
                if (found != null) return found;
            }

            // 3) Busca recursiva na raiz da instalação
            string? foundRoot = FindIconRecursive(installLocation);
            if (foundRoot != null) return foundRoot;

            // 4) Último recurso: qualquer .png ou .ico na pasta Assets
            try
            {
                if (Directory.Exists(assetsFolder))
                {
                    foreach (var file in Directory.GetFiles(assetsFolder, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLower();
                        if (ext == ".png" || ext == ".ico")
                            return file;
                    }
                }
            }
            catch { }

            // 5) Último recurso: qualquer .png ou .ico na raiz
            try
            {
                foreach (var file in Directory.GetFiles(installLocation, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string ext = Path.GetExtension(file).ToLower();
                    if (ext == ".png" || ext == ".ico")
                        return file;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Busca recursiva por arquivos de ícone com múltiplos padrões de nome
        /// </summary>
        private static string? FindIconRecursive(string searchPath)
        {
            var keywords = new[] { "logo", "icon", "storelogo", "tile", "square", "wide",
                                   "small", "splash", "badge", "app", "windows" };
            var extensions = new[] { ".png", ".jpg", ".jpeg", ".ico" };

            try
            {
                foreach (var file in Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                    string ext = Path.GetExtension(file).ToLower();

                    if (!extensions.Contains(ext)) continue;

                    foreach (var kw in keywords)
                    {
                        if (fileName.Contains(kw))
                            return file;
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (PathTooLongException) { }

            return null;
        }

        private static string? GetLogoFromManifest(string installLocation)
        {
            try
            {
                string manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) return null;

                var doc = System.Xml.Linq.XDocument.Load(manifestPath);
                var root = doc.Root;
                if (root == null) return null;

                var ns = root.GetDefaultNamespace();
                var logoElem = root
                    .Element(ns + "Properties")?
                    .Element(ns + "Logo");

                if (logoElem == null || string.IsNullOrWhiteSpace(logoElem.Value))
                    return null;

                string logoPath = logoElem.Value.Replace('/', '\\');
                string logoDir = Path.GetDirectoryName(logoPath) ?? "";
                string logoName = Path.GetFileNameWithoutExtension(logoPath);
                string logoExt = Path.GetExtension(logoPath);
                string fullDir = Path.Combine(installLocation, logoDir);

                if (!Directory.Exists(fullDir)) return null;

                string? best = FindBestScaledLogo(fullDir, logoName, logoExt);
                if (best != null) return best;

                string exact = Path.Combine(installLocation, logoPath);
                if (File.Exists(exact)) return exact;
            }
            catch { }

            return null;
        }

        private static string? FindBestScaledLogo(string directory, string baseName, string ext)
        {
            string pattern = $"{baseName}*{ext}";
            string? best = null;
            int bestScale = 0;

            try
            {
                foreach (var file in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    int scale = 100;

                    var scaleMatch = System.Text.RegularExpressions.Regex.Match(name, @"\.scale-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (scaleMatch.Success)
                        int.TryParse(scaleMatch.Groups[1].Value, out scale);

                    bool isContrastWhite = name.Contains(".contrast-white", StringComparison.OrdinalIgnoreCase);

                    if (scale > bestScale && !isContrastWhite)
                    {
                        bestScale = scale;
                        best = file;
                    }
                }
            }
            catch { }

            return best;
        }

        private static BitmapSource? LoadImageFromFile(string filePath, int size)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath);
                bitmap.DecodePixelWidth = size;
                bitmap.DecodePixelHeight = size;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
        }

        private static BitmapSource? _cachedGenericIcon;
        private static readonly object _genericIconLock = new();

        public static BitmapSource? GetGenericStoreIcon()
        {
            if (_cachedGenericIcon != null) return _cachedGenericIcon;

            lock (_genericIconLock)
            {
                if (_cachedGenericIcon != null) return _cachedGenericIcon;

                try
                {
                    var app = System.Windows.Application.Current;
                    if (app.Dispatcher.CheckAccess())
                        _cachedGenericIcon = CreateGenericStoreIcon();
                    else
                        _cachedGenericIcon = app.Dispatcher.Invoke(CreateGenericStoreIcon);
                }
                catch { return null; }
            }

            return _cachedGenericIcon;
        }

        private static BitmapSource? CreateGenericStoreIcon()
        {
            var drawing = new DrawingVisual();
            using (var ctx = drawing.RenderOpen())
            {
                ctx.DrawRoundedRectangle(
                    System.Windows.Media.Brushes.DarkBlue, null,
                    new System.Windows.Rect(0, 0, 48, 48), 6, 6);

                var typeface = new Typeface("Segoe UI Emoji");
                var text = new FormattedText(
                    "\U0001F6D2",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    typeface, 28,
                    System.Windows.Media.Brushes.White, 1.0);
                ctx.DrawText(text, new System.Windows.Point(10, 10));
            }

            var bitmap = new RenderTargetBitmap(48, 48, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
