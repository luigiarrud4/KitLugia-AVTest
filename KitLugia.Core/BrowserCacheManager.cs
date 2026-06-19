using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// Gerencia limpeza de cache de navegadores populares.
    /// Suporta Chrome, Edge, Firefox, Opera, Brave e Vivaldi.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class BrowserCacheManager
    {
        public record BrowserCacheResult(string BrowserName, long BytesFreed, int FilesDeleted, bool Found, string Message);

        /// <summary>
        /// Retorna todos os navegadores detectados no sistema com seus tamanhos de cache.
        /// </summary>
        public static List<BrowserInfo> GetDetectedBrowsers()
        {
            var browsers = new List<BrowserInfo>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var definitions = GetBrowserDefinitions(localAppData, appData);

            foreach (var def in definitions)
            {
                long cacheSize = 0;
                bool found = false;

                foreach (var cachePath in def.CachePaths)
                {
                    if (Directory.Exists(cachePath))
                    {
                        found = true;
                        cacheSize += GetDirectorySize(cachePath);
                    }
                }

                if (found)
                {
                    browsers.Add(new BrowserInfo
                    {
                        Name = def.Name,
                        Icon = def.Icon,
                        CacheSizeBytes = cacheSize,
                        CachePaths = def.CachePaths,
                        IsInstalled = found
                    });
                }
            }

            return browsers;
        }

        /// <summary>
        /// Limpa o cache de um navegador específico.
        /// </summary>
        public static BrowserCacheResult CleanBrowserCache(string browserName, Action<string>? progress = null)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var definitions = GetBrowserDefinitions(localAppData, appData);
            var def = definitions.Find(d => d.Name.Equals(browserName, StringComparison.OrdinalIgnoreCase));

            if (def == null)
                return new BrowserCacheResult(browserName, 0, 0, false, $"Navegador '{browserName}' não encontrado.");

            long totalBytes = 0;
            int totalFiles = 0;
            bool anyFound = false;

            foreach (var cachePath in def.CachePaths)
            {
                if (!Directory.Exists(cachePath)) continue;
                anyFound = true;
                progress?.Invoke($"Limpando {def.Name}: {Path.GetFileName(cachePath)}...");

                var result = Toolbox.CleanDirectory(cachePath, $"{def.Name} Cache", progress, minimumAge: TimeSpan.FromHours(1));
                totalBytes += result.BytesFreed;
                totalFiles += result.FilesDeleted;
            }

            if (!anyFound)
                return new BrowserCacheResult(browserName, 0, 0, false, $"{browserName} não está instalado ou cache não encontrado.");

            string sizeMb = (totalBytes / 1024.0 / 1024.0).ToString("N2");
            return new BrowserCacheResult(browserName, totalBytes, totalFiles, true,
                $"{browserName}: {totalFiles} arquivos removidos ({sizeMb} MB liberados).");
        }

        /// <summary>
        /// Limpa o cache de todos os navegadores detectados.
        /// </summary>
        public static (long TotalBytes, int TotalFiles, List<BrowserCacheResult> Results) CleanAllBrowserCaches(Action<string>? progress = null)
        {
            var browsers = GetDetectedBrowsers();
            var results = new List<BrowserCacheResult>();
            long totalBytes = 0;
            int totalFiles = 0;

            foreach (var browser in browsers)
            {
                progress?.Invoke($"Limpando {browser.Name}...");
                var result = CleanBrowserCache(browser.Name, progress);
                results.Add(result);
                totalBytes += result.BytesFreed;
                totalFiles += result.FilesDeleted;
            }

            return (totalBytes, totalFiles, results);
        }

        /// <summary>
        /// Retorna o tamanho total do cache de todos os navegadores instalados.
        /// </summary>
        public static long GetTotalBrowserCacheSize()
        {
            var browsers = GetDetectedBrowsers();
            long total = 0;
            foreach (var b in browsers)
                total += b.CacheSizeBytes;
            return total;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static long GetDirectorySize(string path)
        {
            try
            {
                long size = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                return size;
            }
            catch { return 0; }
        }

        private record BrowserDefinition(string Name, string Icon, List<string> CachePaths);

        private static List<BrowserDefinition> GetBrowserDefinitions(string localAppData, string appData)
        {
            return new List<BrowserDefinition>
            {
                new("Google Chrome", "🌐", new List<string>
                    (GetChromiumCachePaths(Path.Combine(localAppData, @"Google\Chrome\User Data")))),
                new("Microsoft Edge", "🔷", new List<string>
                    (GetChromiumCachePaths(Path.Combine(localAppData, @"Microsoft\Edge\User Data")))),
                new("Mozilla Firefox", "🦊", new List<string>
                    (GetFirefoxCachePaths())),
                new("Opera", "🎭", new List<string>
                    (GetOperaCachePaths(appData))),
                new("Brave", "🦁", new List<string>
                    (GetChromiumCachePaths(Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data")))),
                new("Vivaldi", "🎵", new List<string>
                    (GetChromiumCachePaths(Path.Combine(localAppData, @"Vivaldi\User Data")))),
                new("Internet Explorer", "🌍", new List<string>
                {
                    Path.Combine(localAppData, @"Microsoft\Windows\INetCache"),
                    Path.Combine(localAppData, @"Microsoft\Windows\Temporary Internet Files"),
                }),
            };
        }

        private static List<string> GetChromiumCachePaths(string userDataPath)
        {
            var paths = new List<string>();
            if (!Directory.Exists(userDataPath)) return paths;

            paths.Add(Path.Combine(userDataPath, "ShaderCache"));
            paths.Add(Path.Combine(userDataPath, "GrShaderCache"));

            foreach (var profilePath in Directory.EnumerateDirectories(userDataPath))
            {
                string profileName = Path.GetFileName(profilePath);
                bool isUserProfile =
                    profileName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    profileName.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase) ||
                    profileName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);

                if (!isUserProfile) continue;

                paths.Add(Path.Combine(profilePath, "Cache"));
                paths.Add(Path.Combine(profilePath, "Cache", "Cache_Data"));
                paths.Add(Path.Combine(profilePath, "Code Cache"));
                paths.Add(Path.Combine(profilePath, "GPUCache"));
                paths.Add(Path.Combine(profilePath, "Media Cache"));
                paths.Add(Path.Combine(profilePath, "Service Worker", "CacheStorage"));
            }

            return paths;
        }

        private static List<string> GetOperaCachePaths(string appData)
        {
            var paths = new List<string>();
            string operaRoot = Path.Combine(appData, "Opera Software");
            if (!Directory.Exists(operaRoot)) return paths;

            foreach (var profilePath in Directory.EnumerateDirectories(operaRoot))
            {
                paths.Add(Path.Combine(profilePath, "Cache"));
                paths.Add(Path.Combine(profilePath, "Cache", "Cache_Data"));
                paths.Add(Path.Combine(profilePath, "Code Cache"));
                paths.Add(Path.Combine(profilePath, "GPUCache"));
                paths.Add(Path.Combine(profilePath, "Service Worker", "CacheStorage"));
            }

            return paths;
        }

        private static List<string> GetFirefoxCachePaths()
        {
            var paths = new List<string>();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profilesPath = Path.Combine(localAppData, @"Mozilla\Firefox\Profiles");
            if (!Directory.Exists(profilesPath)) return paths;

            foreach (var dir in Directory.EnumerateDirectories(profilesPath))
            {
                string cachePath = Path.Combine(dir, "cache2");
                string startupCachePath = Path.Combine(dir, "startupCache");
                if (Directory.Exists(cachePath)) paths.Add(cachePath);
                if (Directory.Exists(startupCachePath)) paths.Add(startupCachePath);
            }
            return paths;
        }
    }

    public class BrowserInfo
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "🌐";
        public long CacheSizeBytes { get; set; }
        public List<string> CachePaths { get; set; } = new();
        public bool IsInstalled { get; set; }
        public string CacheSizeFormatted => CacheSizeBytes switch
        {
            >= 1_073_741_824 => $"{CacheSizeBytes / 1_073_741_824.0:N1} GB",
            >= 1_048_576 => $"{CacheSizeBytes / 1_048_576.0:N1} MB",
            >= 1_024 => $"{CacheSizeBytes / 1_024.0:N1} KB",
            _ => $"{CacheSizeBytes} B"
        };
    }
}
