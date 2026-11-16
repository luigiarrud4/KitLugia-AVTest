using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class GitHubUpdater
    {
        private static readonly string GitHubRepo = "luigiarrud4/KitLugia-AVTest";
        public static readonly string ApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

        /// <summary>Página do repositório no GitHub (usada pelo botão "Repositório" da tela de Atualizações).</summary>
        public static readonly string RepoUrl = $"https://github.com/{GitHubRepo}";

        /// <summary>Página de releases do repositório (fallback de download manual).</summary>
        public static readonly string ReleasesUrl = $"https://github.com/{GitHubRepo}/releases";

        /// <summary>
        /// Motivo legível da última falha de rede/API/verificação, para a UI mostrar
        /// algo melhor que "Offline" (ex.: rate limit 403, hash inválido). Null = sem erro.
        /// </summary>
        public static string? LastError { get; private set; }
        public static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        // Cache de disco para última release
        private static ReleaseInfo? _cachedLatestRelease;
        private static DateTime _cacheExpiry = DateTime.MinValue;
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia");
        private static readonly string CacheFile = Path.Combine(CacheDir, "latest_release_cache.json");
        private static readonly TimeSpan CacheTTL = TimeSpan.FromHours(1);

        static GitHubUpdater()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater/2.5.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            TryLoadDiskCache();
        }

        private static void TryLoadDiskCache()
        {
            try
            {
                if (File.Exists(CacheFile))
                {
                    var json = File.ReadAllText(CacheFile);
                    var cached = System.Text.Json.JsonSerializer.Deserialize<CachedRelease>(json);
                    if (cached != null && DateTime.Now < cached.Expiry)
                    {
                        _cachedLatestRelease = cached.Release;
                        _cacheExpiry = cached.Expiry;
                        Logger.Log($"📦 Cache de disco carregado ({_cachedLatestRelease?.TagName}, expira {cached.Expiry:HH:mm})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Erro ao carregar cache de disco: {ex.Message}");
            }
        }

        private static void SaveDiskCache()
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                    Directory.CreateDirectory(CacheDir);
                var cached = new CachedRelease { Release = _cachedLatestRelease, Expiry = _cacheExpiry };
                File.WriteAllText(CacheFile, System.Text.Json.JsonSerializer.Serialize(cached));
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Erro ao salvar cache de disco: {ex.Message}");
            }
        }

        private class CachedRelease
        {
            public ReleaseInfo? Release { get; set; }
            public DateTime Expiry { get; set; }
        }

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        public class ReleaseInfo
        {
            public string TagName { get; set; } = "";
            public string Name { get; set; } = "";
            public string Body { get; set; } = "";
            public bool Prerelease { get; set; }
            public DateTime PublishedAt { get; set; }
            public string HtmlUrl { get; set; } = "";
            public Asset[] Assets { get; set; } = Array.Empty<Asset>();
        }

        public class Asset
        {
            public string Name { get; set; } = "";
            public string BrowserDownloadUrl { get; set; } = "";
            public long Size { get; set; }
        }

        public static async Task<bool> CheckForUpdatesAsync()
        {
            try
            {
                Logger.Log("🔍 Verificando atualizações no GitHub...");

                var release = await GetLatestReleaseAsync();

                if (release == null)
                {
                    Logger.Log("❌ Não foi possível obter a última release (offline e sem cache)");
                    return false;
                }

                var latestVersion = ParseVersion(release.TagName);
                var currentVersion = GetCurrentVersion();

                Logger.Log($"📦 Versão atual: {currentVersion}");
                Logger.Log($"🚀 Versão latest: {latestVersion}");

                if (latestVersion > currentVersion)
                {
                    Logger.Log($"✅ Nova versão disponível: {release.TagName}");
                    Logger.Log($"📝 Notas: {release.Name}");
                    return true;
                }

                Logger.Log("✅ KitLugia está atualizado!");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao verificar atualizações: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Obtém a última release do cache ou da API (com fallback offline).
        /// <paramref name="forceRefresh"/> ignora o cache de memória/disco (TTL 1h) e consulta a API:
        /// usado pelo botão "Verificar Agora" — sem isso uma release nova só aparecia após o TTL.
        /// Se a API falhar, ainda cai no último cache conhecido e registra o motivo em <see cref="LastError"/>.
        /// </summary>
        public static async Task<ReleaseInfo?> GetLatestReleaseAsync(bool forceRefresh = false)
        {
            // Cache em memória válido?
            if (!forceRefresh && _cachedLatestRelease != null && DateTime.Now < _cacheExpiry)
            {
                Logger.Log($"📦 Usando cache de memória ({_cachedLatestRelease.TagName})");
                return _cachedLatestRelease;
            }

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await _httpClient.GetAsync(ApiUrl, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    LastError = code switch
                    {
                        403 or 429 => $"GitHub limitou as requisições ({code}). Tente novamente em alguns minutos.",
                        404 => "Nenhuma release publicada no repositório (404).",
                        _ => $"GitHub respondeu {code} ({response.StatusCode})."
                    };
                    Logger.Log($"❌ API GitHub retornou {response.StatusCode} — usando cache de disco");
                    return _cachedLatestRelease;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var release = JsonSerializer.Deserialize<ReleaseInfo>(json, JsonOptions);

                if (release != null)
                {
                    LastError = null;
                    _cachedLatestRelease = release;
                    _cacheExpiry = DateTime.Now.Add(CacheTTL);
                    SaveDiskCache();
                    Logger.Log($"📦 Cache atualizado: {release.TagName}");
                }

                return release;
            }
            catch (Exception ex)
            {
                LastError = ex is TaskCanceledException
                    ? "Tempo esgotado ao consultar o GitHub (15 s)."
                    : $"Falha de conexão com o GitHub: {ex.Message}";
                Logger.Log($"❌ Erro na API: {ex.Message} — usando cache de disco");
                return _cachedLatestRelease;
            }
        }

        public static async Task<bool> DownloadAndInstallUpdateAsync(bool visible = false)
        {
            try
            {
                Logger.Log("🔄 Baixando atualização...");

                var release = await GetLatestReleaseAsync();

                if (release?.Assets == null || release.Assets.Length == 0)
                {
                    Logger.Log("❌ Nenhum arquivo encontrado no release");
                    return false;
                }

                Logger.Log($"📦 Assets encontrados: {release.Assets.Length}");

                var asset = Array.Find(release.Assets, a =>
                    a.Name.Equals("KITLUGIA2.zip", StringComparison.OrdinalIgnoreCase));

                if (asset == null)
                {
                    Logger.Log("❌ Asset KITLUGIA2.zip não encontrado");
                    Logger.Log($"📋 Assets disponíveis: {string.Join(", ", release.Assets.Select(a => a.Name))}");
                    return false;
                }

                var tempDir = Path.GetTempPath();

                // Limpeza de restos de atualizações anteriores: cada update deixava para
                // sempre um KitLugia_Update_<ticks>.zip + um KitLugia_Extract_<ticks>\ com
                // uma cópia COMPLETA do app em %TEMP% (dezenas de MB por atualização).
                try
                {
                    foreach (var stale in Directory.EnumerateFileSystemEntries(tempDir))
                    {
                        try
                        {
                            string staleName = Path.GetFileName(stale);
                            bool isOurs = staleName.StartsWith("KitLugia_Update_", StringComparison.OrdinalIgnoreCase)
                                      || staleName.StartsWith("KitLugia_Extract_", StringComparison.OrdinalIgnoreCase);
                            if (!isOurs) continue;
                            var writeTime = Directory.Exists(stale)
                                ? Directory.GetLastWriteTime(stale)
                                : File.GetLastWriteTime(stale);
                            if (DateTime.Now - writeTime > TimeSpan.FromDays(3))
                            {
                                if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
                                else File.Delete(stale);
                                Logger.Log($"🧹 Temp de update antigo removido: {staleName}");
                            }
                        }
                        catch { /* arquivo em uso/sumiu — segue */ }
                    }
                }
                catch { }

                var updatePath = Path.Combine(tempDir, $"KitLugia_Update_{DateTime.Now.Ticks}.zip");

                Logger.Log($"📥 Baixando {asset.Name} ({asset.Size / 1024 / 1024}MB)...");

                using var downloadClient = KitHttp.CreateClient(TimeSpan.FromMinutes(30));
                downloadClient.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater");
                using (var downloadResponse = await downloadClient.GetAsync(asset.BrowserDownloadUrl))
                {
                    downloadResponse.EnsureSuccessStatusCode();
                    await using (var fileStream = File.Create(updatePath))
                    {
                        await downloadResponse.Content.CopyToAsync(fileStream);
                    }
                }

                Logger.Log("✅ Download concluído!");

                // FAIL-CLOSED: sem o .sha256 (ou com hash inválido/divergente) o binário
                // NÃO pode ser extraído/executado.
                if (!await VerifyAssetHashAsync(asset, updatePath))
                {
                    TryDeleteFile(updatePath);
                    return false;
                }

                // Extract ZIP in C# so the batch doesn't need PowerShell
                var extractDir = Path.Combine(tempDir, $"KitLugia_Extract_{DateTime.Now.Ticks}");
                Logger.Log($"📂 Extraindo arquivos para {extractDir}...");
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(updatePath, extractDir);
                    Logger.Log("✅ Extração concluída!");
                }
                catch (Exception ex)
                {
                    Logger.Log($"❌ Falha ao extrair ZIP: {ex.Message}");
                    File.Delete(updatePath);
                    return false;
                }

                int currentPid = Process.GetCurrentProcess().Id;
                string currentExePath = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(currentExePath))
                    currentExePath = AppPaths.GetRunningExePath();
                if (string.IsNullOrEmpty(currentExePath))
                    currentExePath = AppPaths.ResolveAppExePath(AppContext.BaseDirectory) ?? "";
                if (currentExePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    currentExePath = Path.ChangeExtension(currentExePath, ".exe");
                string currentDir = Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory;

                string currentVersion = GetCurrentVersion().ToString();
                string newVersion = ParseVersion(release.TagName).ToString();

                string? batchPath = GenerateUpdateBatch(extractDir, currentPid, currentExePath, currentVersion, newVersion, updatePath);
                if (batchPath == null)
                {
                    Logger.Log("❌ Falha ao gerar script de atualização!");
                    File.Delete(updatePath);
                    return false;
                }

                Logger.Log($"🚀 Iniciando KitLugia_Update.cmd...");

                var psi = new ProcessStartInfo
                {
                    FileName = batchPath,
                    UseShellExecute = true,
                    WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);

                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro na atualização: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Baixa o .sha256 publicado junto do asset e compara com o SHA-256 do arquivo local.
        /// Fail-closed: qualquer falha (download, formato, divergência) retorna false e preenche
        /// <see cref="LastError"/>. Compartilhado pelos DOIS fluxos de atualização — antes só o
        /// fluxo de linha de comando verificava; o botão "Atualizar Agora" da UI instalava sem checar.
        /// </summary>
        private static async Task<bool> VerifyAssetHashAsync(Asset asset, string filePath)
        {
            string expectedHash;
            try
            {
                var hashUrl = asset.BrowserDownloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? asset.BrowserDownloadUrl + ".sha256"
                    : asset.BrowserDownloadUrl.Replace(".zip", ".zip.sha256");
                using var hashResponse = await _httpClient.GetAsync(hashUrl);
                hashResponse.EnsureSuccessStatusCode();
                var hashContent = (await hashResponse.Content.ReadAsStringAsync()).Trim();
                // Formato sha256sum ("<hash>  <arquivo>"): fica só com o primeiro token.
                expectedHash = hashContent.Split(new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                Logger.Log($"🔐 SHA256 baixado: {expectedHash}");
            }
            catch (Exception ex)
            {
                LastError = "Não foi possível baixar o arquivo .sha256 da release.";
                Logger.LogError("Updater", "Nao foi possivel baixar o SHA256: " + ex.Message);
                return false;
            }

            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                LastError = "O .sha256 da release é inválido.";
                Logger.LogError("Updater", "SHA256 do release invalido - atualizacao abortada.");
                return false;
            }

            string actualHash = ComputeSha256(filePath);
            Logger.Log($"🔐 Hash calculado: {actualHash}");
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                LastError = "O hash do arquivo baixado não confere com o da release (download corrompido ou adulterado).";
                Logger.Log($"❌ Hash mismatch! Esperado: {expectedHash}, Calculado: {actualHash}");
                return false;
            }

            Logger.Log("✅ Hash verificado com sucesso!");
            return true;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* em uso — será limpo pela rotina de temporários antigos */ }
        }

        private static Version GetCurrentVersion()
        {
            try
            {
                // A versao que importa e' a do APP (exe), nao a desta classe.
                // O KitLugia.Core.csproj NAO tem <Version> proprio: o kit publicado pelo
                // Deploy.ps1 recebe -p:Version (propriedade global, vale para o Core),
                // mas um "Publicar" do Visual Studio NAO passa nada -> o Core ficaria
                // em 1.0.0.0 e o updater acharia TODA release mais nova (nag eterno).
                // SmartVersionDetector.GetCurrentAssemblyVersion ja usa esta ordem.
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version ?? new Version("1.0.0");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return new Version("1.0.0"); }
        }

        private static Version ParseVersion(string tag)
        {
            try
            {
                var cleanTag = tag.StartsWith("v") ? tag.Substring(1) : tag;
                return Version.Parse(cleanTag);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return new Version("1.0.0"); }
        }

        private static string ComputeSha256(string filePath)
        {
            return NativeSha256.ComputeHash(filePath) ?? "";
        }

        public static async Task StartAutoUpdateCheck()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(24));
                    if (await CheckForUpdatesAsync())
                    {
                        Logger.Log("🔄 Atualização disponível! Use a opção 'Atualizar' no menu.");
                    }
                }
                catch (Exception ex)
                {
                    // Exceção pontual (rede/API) NAO pode matar o loop - loga e continua
                    Logger.Log($"❌ Erro no auto-update check (tentativa continuará em 24h): {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Baixa o asset ZIP com relatório de progresso (bytesReceived, totalBytes)
        /// </summary>
        public static async Task<string?> DownloadUpdateFileAsync(Asset asset, IProgress<(long bytesReceived, long totalBytes)>? progress = null, CancellationToken ct = default)
        {
            try
            {
                LastError = null;
                var tempDir = Path.GetTempPath();
                var zipPath = Path.Combine(tempDir, "KitLugia_Update.zip");

                using var client = KitHttp.CreateClient(TimeSpan.FromMinutes(30));
                client.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                using var response = await client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
                // Bloco próprio: os streams precisam estar FECHADOS antes de calcular o hash.
                {
                    await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = File.Create(zipPath);

                    var buffer = new byte[81920];
                    long bytesRead = 0;
                    int bytesJustRead;
                    while ((bytesJustRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesJustRead, ct);
                        bytesRead += bytesJustRead;
                        progress?.Report((bytesRead, totalBytes));
                    }
                }

                Logger.Log($"✅ Download concluído via DownloadUpdateFileAsync: {zipPath}");

                if (!await VerifyAssetHashAsync(asset, zipPath))
                {
                    TryDeleteFile(zipPath);
                    return null;
                }

                return zipPath;
            }
            catch (Exception ex)
            {
                LastError = $"Falha no download: {ex.Message}";
                Logger.Log($"❌ Erro no download com progresso: {ex.Message}");
                return null;
            }
        }

        public static string? GenerateUpdateBatch(string extractDir, int pid, string exePath, string oldVersion, string newVersion, string? zipPath = null)
        {
            try
            {
                string appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                string batchPath = Path.Combine(appDir, "KitLugia_Update.cmd");

                // Limpeza dos temporários DEPOIS da instalação bem-sucedida (o zip
                // baixado e a extração completa do app não precisam sobreviver).
                string cleanup = string.IsNullOrEmpty(zipPath) ? "" : $@"echo Limpando temporarios...
rd /s /q ""{extractDir}"" >nul 2>&1
del /q ""{zipPath}"" >nul 2>&1
echo.
";

                // Remove o executavel com o NOME ANTIGO (KitLugia.GUI.exe) quando o
                // app em uso ja tem o nome novo (KitLugia.App.exe) — senao a
                // atualizacao deixa 54 MB do executavel velho jogados na pasta.
                // Se o app em uso AINDA e' o nome antigo, nao apaga nada.
                string legacyCleanup = "";
                try
                {
                    string currentFileName = Path.GetFileName(exePath);
                    if (!string.Equals(currentFileName, AppPaths.LegacyAppExeFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        legacyCleanup = $"if exist \"%~dp0{AppPaths.LegacyAppExeFileName}\" del /q \"%~dp0{AppPaths.LegacyAppExeFileName}\" >nul 2>&1\r\n";
                    }
                }
                catch { }

                string batch = $@"@echo off
title KitLugia - Atualizando...
color 0E
cls
echo ================================================
echo          KIT LUGIA - ATUALIZACAO
echo ================================================
echo.
echo [1/3] Aguardando fechamento do KitLugia...
set /a KL_TRIES=0
:wait
tasklist /fi ""PID eq {pid}"" 2>nul | findstr /i ""{pid}"" >nul
if errorlevel 1 goto closed
set /a KL_TRIES+=1
if %KL_TRIES% GEQ 120 goto waittimeout
timeout /t 1 /nobreak >nul
goto wait
:waittimeout
echo  AVISO - KitLugia nao fechou em 120 s. NADA foi alterado; abortando.
echo %date% %time% ATUALIZACAO ABORTADA (app nao fechou) old={oldVersion} new={newVersion} >> ""%LOCALAPPDATA%\KitLugia\Logs\update.log"" 2>nul
timeout /t 10 >nul
exit /b 2
:closed
echo  OK - KitLugia fechado.
echo.
echo [2/3] Instalando arquivos...
xcopy ""{extractDir}"" ""{appDir}"" /E /Y /Q
if errorlevel 1 (
    echo  ERRO - Falha ao copiar arquivos. A versao antiga continua intacta.
    echo %date% %time% ATUALIZACAO FALHOU xcopy old={oldVersion} new={newVersion} >> ""%LOCALAPPDATA%\KitLugia\Logs\update.log"" 2>nul
    timeout /t 10 >nul
    exit /b 1
)
echo  OK - Arquivos instalados.
{legacyCleanup}echo.
{cleanup}echo [3/3] Iniciando nova versao...
start """" ""{exePath}""
echo.
echo ================================================
echo     ATUALIZACAO CONCLUIDA!
echo ================================================
echo %date% %time% ATUALIZACAO OK old={oldVersion} new={newVersion} >> ""%LOCALAPPDATA%\KitLugia\Logs\update.log"" 2>nul
timeout /t 5 >nul
exit /b 0
";

                File.WriteAllText(batchPath, batch);
                Logger.Log($"✅ Script de atualização gerado: {batchPath}");
                return batchPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao gerar script de atualização: {ex.Message}");
                return null;
            }
        }
    }
}
