using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Detector inteligente de versões baseado em data de compila -  - o
    /// Obt - m versões dinamicamente do GitHub (sem hardcoded)
    /// </summary>
    public static class SmartVersionDetector
    {
        private static readonly string GitHubRepo = "luigiarrud4/KitLugia-AVTest";
        private static readonly string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases";
        private static readonly HttpClient _httpClient = new();
        
        // Cache de releases (para evitar m - ltiplas requisi -  - es)
        private static List<GitHubRelease>? _cachedReleases;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia");
        private static readonly string CacheFile = Path.Combine(CacheDir, "releases_cache.json");
        private static readonly TimeSpan CacheTTL = TimeSpan.FromHours(1);

        static SmartVersionDetector()
        {
            // Configurar HttpClient com User-Agent mais robusto
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            _httpClient.Timeout = TimeSpan.FromSeconds(30); // Timeout maior
            TryLoadDiskCache();
        }

        private static void TryLoadDiskCache()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var json = File.ReadAllText(CacheFile);
                    var cached = System.Text.Json.JsonSerializer.Deserialize<DiskCachedReleases>(json);
                    if (cached != null && DateTime.Now < cached.Expiry)
                    {
                        _cachedReleases = cached.Releases;
                        _cacheExpiry = cached.Expiry;
                        Logger.Log($"Cache de disco carregado ({cached.Releases?.Count ?? 0} releases)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao carregar cache de disco: {ex.Message}");
            }
        }

        private static void SaveDiskCache()
        {
            try
            {
                if (_cachedReleases == null) return;
                if (!Directory.Exists(CacheDir))
                    Directory.CreateDirectory(CacheDir);
                var cached = new DiskCachedReleases { Releases = _cachedReleases, Expiry = _cacheExpiry };
                File.WriteAllText(CacheFile, System.Text.Json.JsonSerializer.Serialize(cached));
                Logger.Log($"Cache salvo em disco: {CacheFile}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar cache de disco: {ex.Message}");
            }
        }

        private class DiskCachedReleases
        {
            public List<GitHubRelease>? Releases { get; set; }
            public DateTime Expiry { get; set; }
        }
        
        /// <summary>
        /// Classe para representar um release do GitHub
        /// </summary>
        public class GitHubRelease
        {
            public string TagName { get; set; } = "";
            public string Name { get; set; } = "";
            public DateTime PublishedAt { get; set; }
            public bool Prerelease { get; set; }
            public bool Draft { get; set; }
        }
        
        /// <summary>
        /// Obt - m TODOS os releases do GitHub (cache de 1 hora)
        /// </summary>
        private static ValueTask<List<GitHubRelease>> GetAllReleasesAsync()
        {
            // ?? Verificar cache (retorno s - ncrono sem aloca -  - o)
            if (_cachedReleases != null && DateTime.Now < _cacheExpiry)
            {
                Logger.Log($"Usando cache de releases ({_cachedReleases.Count} releases)");
                return new ValueTask<List<GitHubRelease>>(_cachedReleases);
            }

            // ?? Se cache expirou, executa assincronamente
            return new ValueTask<List<GitHubRelease>>(GetAllReleasesAsyncCore());
        }

        /// <summary>
        /// Implementa -  - o ass - ncrona de GetAllReleasesAsync
        /// </summary>
        private static async Task<List<GitHubRelease>> GetAllReleasesAsyncCore()
        {
            try
            {
                // Antes: uma requisição extra a /rate_limit + Task.Delay(1000) só para "testar
                // conectividade" antes da requisição real. Isso dobrava as chamadas à API
                // anônima (limite de 60/h por IP) e atrasava a tela em 1 s+. A própria
                // requisição abaixo já trata 403/429/offline via os catch (fallback p/ cache).
                Logger.Log("Buscando releases do GitHub...");

                var response = await _httpClient.GetAsync(ReleasesApiUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }) ?? new List<GitHubRelease>();

                // ?? Filtrar releases v - lidos (não draft, não prerelease)
                var validReleases = releases
                    .Where(r => !r.Draft && !r.Prerelease)
                    .OrderByDescending(r => r.PublishedAt)
                    .ToList();

                // ?? Atualizar cache
                _cachedReleases = validReleases;
                _cacheExpiry = DateTime.Now.Add(CacheTTL);
                SaveDiskCache();

                Logger.Log($"{validReleases.Count} releases encontrados");
                foreach (var release in validReleases.Take(5))
                {
                    Logger.Log($"   📦 {release.TagName} - {release.PublishedAt:dd/MM/yyyy}");
                }

                return validReleases;
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"? Erro de conexão com GitHub: {ex.Message}");
                return GetFallbackReleases();
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"? Timeout na conexão com GitHub: {ex.Message}");
                return GetFallbackReleases();
            }
            catch (Exception ex)
            {
                Logger.Log($"? Erro ao buscar releases: {ex.Message}");
                return GetFallbackReleases();
            }
        }
        
        /// <summary>
        /// Tenta cache de disco primeiro, depois placeholder
        /// </summary>
        private static List<GitHubRelease> GetFallbackReleases()
        {
            if (_cachedReleases != null)
            {
                Logger.Log($"Usando cache em memória ({_cachedReleases.Count} releases)");
                return _cachedReleases;
            }

            try
            {
                if (File.Exists(CacheFile))
                {
                    var json = File.ReadAllText(CacheFile);
                    var cached = System.Text.Json.JsonSerializer.Deserialize<DiskCachedReleases>(json);
                    if (cached?.Releases != null)
                    {
                        _cachedReleases = cached.Releases;
                        Logger.Log($"Usando cache de disco ({cached.Releases.Count} releases)");
                        return cached.Releases;
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return GetPlaceholderReleases();
        }

        /// <summary>
        /// Retorna lista vazia (sem dados inventados) quando GitHub está inacessível e não há cache
        /// </summary>
        private static List<GitHubRelease> GetPlaceholderReleases()
        {
            // Antes devolvia 3 releases FALSAS (2.0.5/2.0.4/2.0.3) quando o GitHub estava
            // inacessível, e a UI mostrava "Releases Online: 3" como se fosse real.
            Logger.Log("GitHub inacessível e sem cache - nenhuma release disponível");
            return new List<GitHubRelease>();
        }
        
        /// <summary>
        /// Obt - m a vers - o do assembly atual via AssemblyVersion
        /// </summary>
        public static string GetCurrentAssemblyVersion()
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version != null)
                    return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return "2.0.x";
        }
        
        /// <summary>
        /// Obt - m informa -  - es detalhadas da vers - o (baseada em AssemblyVersion)
        /// </summary>
        public static async Task<(string RealVersion, string AssemblyVersion, string BuildDate, string DetectionMethod, int TotalReleases)> GetVersionInfoAsync(DateTime buildDate = default)
        {
            var realVersion = GetCurrentAssemblyVersion();
            var assemblyVersion = Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "1.0.0.0";
            var releases = await GetAllReleasesAsync();
            return (realVersion, assemblyVersion, DateTime.Now.ToString("dd/MM/yyyy HH:mm"), "AssemblyVersion", releases.Count);
        }
        
        /// <summary>
        /// Lista todas as versões dispon - veis online
        /// </summary>
        public static async Task<List<(string TagName, DateTime PublishedAt, string Name)>> GetAllVersionsAsync()
        {
            try
            {
                var releases = await GetAllReleasesAsync();
                return releases
                    .Select(r => (r.TagName, r.PublishedAt, r.Name))
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.Log($"? Erro ao listar versões: {ex.Message}");
                return new List<(string, DateTime, string)>();
            }
        }
        
        /// <summary>
        /// For - a atualiza -  - o do cache (para testes)
        /// </summary>
        public static void ClearCache()
        {
            _cachedReleases = null;
            _cacheExpiry = DateTime.MinValue;
            Logger.Log("Cache de versões limpo");
        }
    }
}
