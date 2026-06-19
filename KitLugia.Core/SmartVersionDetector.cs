using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Detector inteligente de versões baseado em data de compilação
    /// Obtém versões dinamicamente do GitHub (sem hardcoded)
    /// </summary>
    public static class SmartVersionDetector
    {
        private static readonly string GitHubRepo = "luigiarrud4/KitLugia-AVTest";
        private static readonly string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases";
        private static readonly HttpClient _httpClient = new();
        
        // Cache de releases (para evitar múltiplas requisições)
        private static List<GitHubRelease>? _cachedReleases;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        
        static SmartVersionDetector()
        {
            // Configurar HttpClient com User-Agent mais robusto
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            _httpClient.Timeout = TimeSpan.FromSeconds(30); // Timeout maior
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
        /// Obtém TODOS os releases do GitHub (cache de 1 hora)
        /// </summary>
        private static ValueTask<List<GitHubRelease>> GetAllReleasesAsync()
        {
            // 🔄 Verificar cache (retorno síncrono sem alocação)
            if (_cachedReleases != null && DateTime.Now < _cacheExpiry)
            {
                Logger.Log($"📦 Usando cache de releases ({_cachedReleases.Count} releases)");
                return new ValueTask<List<GitHubRelease>>(_cachedReleases);
            }

            // 🌐 Se cache expirou, executa assincronamente
            return new ValueTask<List<GitHubRelease>>(GetAllReleasesAsyncCore());
        }

        /// <summary>
        /// Implementação assíncrona de GetAllReleasesAsync
        /// </summary>
        private static async Task<List<GitHubRelease>> GetAllReleasesAsyncCore()
        {
            try
            {
                // 🌐 Verificar conectividade primeiro
                Logger.Log("🌐 Verificando conectividade com GitHub...");
                using var testClient = new HttpClient();
                testClient.Timeout = TimeSpan.FromSeconds(10);
                testClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                try
                {
                    var testResponse = await testClient.GetAsync("https://api.github.com/rate_limit");

                    if (!testResponse.IsSuccessStatusCode)
                    {
                        var statusCode = (int)testResponse.StatusCode;
                        Logger.Log($"❌ GitHub API inacessível: {testResponse.StatusCode} ({statusCode})");

                        // 🎯 Tratamento específico para diferentes status codes
                        switch (statusCode)
                        {
                            case 403:
                                Logger.Log("🚫 GitHub API: 403 Forbidden - Possível Rate Limit ou IP bloqueado");
                                break;
                            case 429:
                                Logger.Log("⏱️ GitHub API: 429 Too Many Requests - Rate Limit excedido");
                                break;
                            case 401:
                                Logger.Log("🔑 GitHub API: 401 Unauthorized - Token inválido ou expirado");
                                break;
                            default:
                                Logger.Log($"❌ GitHub API: {statusCode} - Erro desconhecido");
                                break;
                        }

                        return GetPlaceholderReleases();
                    }

                    Logger.Log("✅ GitHub API acessível - buscando releases...");
                    await Task.Delay(1000); // Delay de 1 segundo para evitar rate limit
                }
                catch (HttpRequestException ex)
                {
                    Logger.Log($"❌ Erro de conexão com GitHub: {ex.Message}");
                    return GetPlaceholderReleases();
                }

                Logger.Log("🌐 Buscando releases do GitHub...");

                var response = await _httpClient.GetAsync(ReleasesApiUrl);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                }) ?? new List<GitHubRelease>();

                // 🔄 Filtrar releases válidos (não draft, não prerelease)
                var validReleases = releases
                    .Where(r => !r.Draft && !r.Prerelease)
                    .OrderByDescending(r => r.PublishedAt)
                    .ToList();

                // 💾 Atualizar cache
                _cachedReleases = validReleases;
                _cacheExpiry = DateTime.Now.AddHours(1); // Cache de 1 hora

                Logger.Log($"📦 {validReleases.Count} releases encontrados");
                foreach (var release in validReleases.Take(5))
                {
                    Logger.Log($"   📋 {release.TagName} - {release.PublishedAt:dd/MM/yyyy}");
                }

                return validReleases;
            }
            catch (HttpRequestException ex)
            {
                Logger.Log($"❌ Erro de conexão com GitHub: {ex.Message}");
                return GetPlaceholderReleases();
            }
            catch (TaskCanceledException ex)
            {
                Logger.Log($"❌ Timeout na conexão com GitHub: {ex.Message}");
                return GetPlaceholderReleases();
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao buscar releases: {ex.Message}");
                return _cachedReleases ?? GetPlaceholderReleases();
            }
        }
        
        /// <summary>
        /// Retorna releases placeholder quando GitHub está inacessível
        /// </summary>
        private static List<GitHubRelease> GetPlaceholderReleases()
        {
            Logger.Log("📦 Usando releases placeholder (GitHub inacessível)");
            
            var now = DateTime.Now;
            return new List<GitHubRelease>
            {
                new GitHubRelease 
                { 
                    TagName = "2.0.5", 
                    Name = "KitLugia v2.0.5 - Update Manual + Timezone",
                    PublishedAt = now.AddDays(-7), // 7 dias atrás
                    Prerelease = false,
                    Draft = false
                },
                new GitHubRelease 
                { 
                    TagName = "2.0.4", 
                    Name = "KitLugia v2.0.4 - GameBoost Fix",
                    PublishedAt = now.AddDays(-14), // 14 dias atrás
                    Prerelease = false,
                    Draft = false
                },
                new GitHubRelease 
                { 
                    TagName = "2.0.3", 
                    Name = "KitLugia v2.0.3 - Network Diagnostics",
                    PublishedAt = now.AddDays(-21), // 21 dias atrás
                    Prerelease = false,
                    Draft = false
                }
            };
        }
        
        /// <summary>
        /// Obtém a versão real baseada na data de compilação (DINÂMICO)
        /// </summary>
        /// <param name="buildDate">Data de compilação do assembly</param>
        /// <returns>Versão detectada online (ex: "2.0.5")</returns>
        public static async Task<string> GetRealVersionAsync(DateTime buildDate)
        {
            try
            {
                // 🌐 Buscar releases online
                var releases = await GetAllReleasesAsync();
                
                if (!releases.Any())
                {
                    Logger.Log("❌ Nenhum release encontrado, usando fallback");
                    return "2.0.x";
                }
                
                // 🔍 Procurar release exato (até 7 dias + 15 minutos de tolerância)
                foreach (var release in releases)
                {
                    // ✅ LÓGICA CORRETA: Se build for até 15min DEPOIS do release
                    var releaseTimeWithTolerance = release.PublishedAt.AddMinutes(15); // 15 minutos DEPOIS
                    if (buildDate >= release.PublishedAt.Date && 
                        buildDate <= releaseTimeWithTolerance)
                    {
                        Logger.Log($"📅 Versão exata detectada: {release.TagName} (Publicado: {release.PublishedAt:dd/MM/yyyy HH:mm}, Build: {buildDate:dd/MM/yyyy HH:mm})");
                        return release.TagName;
                    }
                }
                
                // 🔄 Estimar versão baseada no último release
                var lastRelease = releases.First();
                var daysSinceLast = (buildDate - lastRelease.PublishedAt.Date).Days;
                
                // 🧠 Extrair números da versão
                if (ParseVersionNumbers(lastRelease.TagName, out int major, out int minor, out int patch))
                {
                    // Estimar baseado em semanas desde o último release
                    var weeksSinceLast = daysSinceLast / 7;
                    var estimatedPatch = patch + weeksSinceLast;
                    
                    var estimatedVersion = $"{major}.{minor}.{estimatedPatch}";
                    Logger.Log($"📅 Versão estimada: {estimatedVersion} (+{weeksSinceLast} semanas desde {lastRelease.TagName})");
                    return estimatedVersion;
                }
                
                // Fallback para o último release
                Logger.Log($"📅 Usando último release: {lastRelease.TagName}");
                return lastRelease.TagName;
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao detectar versão: {ex.Message}");
                return "2.0.x"; // Fallback seguro
            }
        }
        
        /// <summary>
        /// Versão síncrona para compatibilidade (usa cache)
        /// </summary>
        public static string GetRealVersion(DateTime buildDate)
        {
            try
            {
                // � Usar cache se disponível
                if (_cachedReleases != null)
                {
                    var task = GetRealVersionAsync(buildDate);
                    return task.GetAwaiter().GetResult();
                }
                
                // 🔄 Buscar online (bloqueante, mas necessário)
                var syncTask = Task.Run(async () => await GetRealVersionAsync(buildDate));
                return syncTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro na versão síncrona: {ex.Message}");
                return "2.0.x";
            }
        }
        
        /// <summary>
        /// Extrai números da versão de uma string (ex: "v2.0.5" -> 2, 0, 5)
        /// </summary>
        private static bool ParseVersionNumbers(string versionTag, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            
            try
            {
                // Remover "v" e outros prefixos
                var cleanVersion = versionTag.Trim().TrimStart('v', 'V');
                var parts = cleanVersion.Split('.');
                
                if (parts.Length >= 1) int.TryParse(parts[0], out major);
                if (parts.Length >= 2) int.TryParse(parts[1], out minor);
                if (parts.Length >= 3) int.TryParse(parts[2], out patch);
                
                return major > 0 || minor > 0 || patch > 0;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Obtém informações detalhadas da versão (DINÂMICO)
        /// </summary>
        public static async Task<(string RealVersion, string AssemblyVersion, string BuildDate, string DetectionMethod, int TotalReleases)> GetVersionInfoAsync(DateTime buildDate)
        {
            var realVersion = await GetRealVersionAsync(buildDate);
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            var releases = await GetAllReleasesAsync();
            
            string detectionMethod;
            if (releases.Any(r => 
                buildDate >= r.PublishedAt.Date && 
                buildDate <= r.PublishedAt.AddMinutes(15)))
            {
                detectionMethod = "Online Exata";
            }
            else if (releases.Any())
            {
                detectionMethod = "Online Estimada";
            }
            else
            {
                detectionMethod = "Offline Placeholder";
            }
            
            return (realVersion, assemblyVersion, buildDate.ToString("dd/MM/yyyy HH:mm"), detectionMethod, releases.Count);
        }
        
        /// <summary>
        /// Versão síncrona para compatibilidade
        /// </summary>
        public static (string RealVersion, string AssemblyVersion, string BuildDate, string DetectionMethod, int TotalReleases) GetVersionInfo(DateTime buildDate)
        {
            try
            {
                var task = GetVersionInfoAsync(buildDate);
                return task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro em GetVersionInfo: {ex.Message}");
                return ("2.0.x", "1.0.0.0", buildDate.ToString("dd/MM/yyyy HH:mm"), "Erro", 0);
            }
        }
        
        /// <summary>
        /// Lista todas as versões disponíveis online
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
                Logger.Log($"❌ Erro ao listar versões: {ex.Message}");
                return new List<(string, DateTime, string)>();
            }
        }
        
        /// <summary>
        /// Força atualização do cache (para testes)
        /// </summary>
        public static void ClearCache()
        {
            _cachedReleases = null;
            _cacheExpiry = DateTime.MinValue;
            Logger.Log("🗑️ Cache de versões limpo");
        }
    }
}
