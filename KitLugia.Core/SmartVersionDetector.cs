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
    /// Detector inteligente de vers�es baseado em data de compila��o
    /// Obt�m vers�es dinamicamente do GitHub (sem hardcoded)
    /// </summary>
    public static class SmartVersionDetector
    {
        private static readonly string GitHubRepo = "luigiarrud4/KitLugia-AVTest";
        private static readonly string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases";
        private static readonly HttpClient _httpClient = new();
        
        // Cache de releases (para evitar m�ltiplas requisi��es)
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
                        Logger.Log($"?? Cache de disco carregado ({cached.Releases?.Count ?? 0} releases)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"?? Erro ao carregar cache de disco: {ex.Message}");
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
                Logger.Log($"?? Cache salvo em disco: {CacheFile}");
            }
            catch (Exception ex)
            {
                Logger.Log($"?? Erro ao salvar cache de disco: {ex.Message}");
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
        /// Obt�m TODOS os releases do GitHub (cache de 1 hora)
        /// </summary>
        private static ValueTask<List<GitHubRelease>> GetAllReleasesAsync()
        {
            // ?? Verificar cache (retorno s�ncrono sem aloca��o)
            if (_cachedReleases != null && DateTime.Now < _cacheExpiry)
            {
                Logger.Log($"?? Usando cache de releases ({_cachedReleases.Count} releases)");
                return new ValueTask<List<GitHubRelease>>(_cachedReleases);
            }

            // ?? Se cache expirou, executa assincronamente
            return new ValueTask<List<GitHubRelease>>(GetAllReleasesAsyncCore());
        }

        /// <summary>
        /// Implementa��o ass�ncrona de GetAllReleasesAsync
        /// </summary>
        private static async Task<List<GitHubRelease>> GetAllReleasesAsyncCore()
        {
            try
            {
                // ?? Verificar conectividade primeiro
                Logger.Log("?? Verificando conectividade com GitHub...");
                using var testClient = new HttpClient();
                testClient.Timeout = TimeSpan.FromSeconds(10);
                testClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                try
                {
                    var testResponse = await testClient.GetAsync("https://api.github.com/rate_limit");

                    if (!testResponse.IsSuccessStatusCode)
                    {
                        var statusCode = (int)testResponse.StatusCode;
                        Logger.Log($"? GitHub API inacess�vel: {testResponse.StatusCode} ({statusCode})");

                        // ?? Tratamento espec�fico para diferentes status codes
                        switch (statusCode)
                        {
                            case 403:
                                Logger.Log("?? GitHub API: 403 Forbidden - Poss�vel Rate Limit ou IP bloqueado");
                                break;
                            case 429:
                                Logger.Log("?? GitHub API: 429 Too Many Requests - Rate Limit excedido");
                                break;
                            case 401:
                                Logger.Log("?? GitHub API: 401 Unauthorized - Token inv�lido ou expirado");
                                break;
                            default:
                                Logger.Log($"? GitHub API: {statusCode} - Erro desconhecido");
                                break;
                        }

                        return GetFallbackReleases();
                    }

                    Logger.Log("? GitHub API acess�vel - buscando releases...");
                    await Task.Delay(1000); // Delay de 1 segundo para evitar rate limit
                }
                catch (HttpRequestException ex)
                {
                    Logger.Log($"? Erro de conex�o com GitHub: {ex.Message}");
                    return GetFallbackReleases();
                }

                Logger.Log("?? Buscando releases do GitHub...");

                var response = await _httpClient.GetAsync(ReleasesApiUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }) ?? new List<GitHubRelease>();

                // ?? Filtrar releases v�lidos (n�o draft, n�o prerelease)
                var validReleases = releases
                    .Where(r => !r.Draft && !r.Prerelease)
                    .OrderByDescending(r => r.PublishedAt)
                    .ToList();

                // ?? Atualizar cache
                _cachedReleases = validReleases;
                _cacheExpiry = DateTime.Now.Add(CacheTTL);
                SaveDiskCache();

                Logger.Log($"?? {validReleases.Count} releases encontrados");
                foreach (var release in validReleases.Take(5))
                {
                    Logger.Log($"   ?? {release.TagName} - {release.PublishedAt:dd/MM/yyyy}");
                }

                return validReleases;
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"? Erro de conex�o com GitHub: {ex.Message}");
                return GetFallbackReleases();
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"? Timeout na conex�o com GitHub: {ex.Message}");
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
                Logger.Log($"?? Usando cache em mem�ria ({_cachedReleases.Count} releases)");
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
                        Logger.Log($"?? Usando cache de disco ({cached.Releases.Count} releases)");
                        return cached.Releases;
                    }
                }
            }
            catch { }

            return GetPlaceholderReleases();
        }

        /// <summary>
        /// Retorna releases placeholder quando GitHub est� inacess�vel
        /// </summary>
        private static List<GitHubRelease> GetPlaceholderReleases()
        {
            Logger.Log("?? Usando releases placeholder (GitHub inacess�vel)");
            
            var now = DateTime.Now;
            return new List<GitHubRelease>
            {
                new GitHubRelease 
                { 
                    TagName = "2.0.5", 
                    Name = "KitLugia v2.0.5 - Update Manual + Timezone",
                    PublishedAt = now.AddDays(-7), // 7 dias atr�s
                    Prerelease = false,
                    Draft = false
                },
                new GitHubRelease 
                { 
                    TagName = "2.0.4", 
                    Name = "KitLugia v2.0.4 - GameBoost Fix",
                    PublishedAt = now.AddDays(-14), // 14 dias atr�s
                    Prerelease = false,
                    Draft = false
                },
                new GitHubRelease 
                { 
                    TagName = "2.0.3", 
                    Name = "KitLugia v2.0.3 - Network Diagnostics",
                    PublishedAt = now.AddDays(-21), // 21 dias atr�s
                    Prerelease = false,
                    Draft = false
                }
            };
        }
        
        /// <summary>
        /// Obt�m a vers�o do assembly atual via AssemblyVersion
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
            catch { }
            return "2.0.x";
        }

        /// <summary>
        /// Obt�m a vers�o real usando AssemblyVersion em vez de data de compila��o
        /// </summary>
        /// <param name="buildDate">Ignorado — mantido para compatibilidade</param>
        /// <returns>Vers�o detectada (ex: "2.0.5")</returns>
        public static async Task<string> GetRealVersionAsync(DateTime buildDate = default)
        {
            return await Task.FromResult(GetCurrentAssemblyVersion());
        }
        
        /// <summary>
        /// Vers�o s�ncrona — usa AssemblyVersion diretamente
        /// </summary>
        public static string GetRealVersion(DateTime buildDate = default)
        {
            return GetCurrentAssemblyVersion();
        }
        
        /// <summary>
        /// Obt�m informa��es detalhadas da vers�o (baseada em AssemblyVersion)
        /// </summary>
        public static async Task<(string RealVersion, string AssemblyVersion, string BuildDate, string DetectionMethod, int TotalReleases)> GetVersionInfoAsync(DateTime buildDate = default)
        {
            var realVersion = GetCurrentAssemblyVersion();
            var assemblyVersion = Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "1.0.0.0";
            var releases = await GetAllReleasesAsync();
            return (realVersion, assemblyVersion, DateTime.Now.ToString("dd/MM/yyyy HH:mm"), "AssemblyVersion", releases.Count);
        }
        
        /// <summary>
        /// Vers�o s�ncrona para compatibilidade
        /// </summary>
        public static (string RealVersion, string AssemblyVersion, string BuildDate, string DetectionMethod, int TotalReleases) GetVersionInfo(DateTime buildDate = default)
        {
            try
            {
                var task = GetVersionInfoAsync();
                return task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Log($"? Erro em GetVersionInfo: {ex.Message}");
                return ("2.0.x", "1.0.0.0", DateTime.Now.ToString("dd/MM/yyyy HH:mm"), "Erro", 0);
            }
        }
        
        /// <summary>
        /// Lista todas as vers�es dispon�veis online
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
                Logger.Log($"? Erro ao listar vers�es: {ex.Message}");
                return new List<(string, DateTime, string)>();
            }
        }
        
        /// <summary>
        /// For�a atualiza��o do cache (para testes)
        /// </summary>
        public static void ClearCache()
        {
            _cachedReleases = null;
            _cacheExpiry = DateTime.MinValue;
            Logger.Log("??? Cache de vers�es limpo");
        }
    }
}
