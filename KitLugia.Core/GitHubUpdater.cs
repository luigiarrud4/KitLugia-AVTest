using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO.Compression;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class GitHubUpdater
    {
        private static readonly string GitHubRepo = "luigiarrud4/KitLugia-AVTest";
        public static readonly string ApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
        public static readonly HttpClient _httpClient = new();

        static GitHubUpdater()
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "KitLugia-Updater/2.5.0");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
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

                var response = await _httpClient.GetAsync(ApiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Log($"❌ Erro ao buscar release: {response.StatusCode}");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<ReleaseInfo>(json, JsonOptions);

                if (release == null)
                {
                    Logger.Log("❌ Erro ao desserializar release");
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

        public static async Task<bool> DownloadAndInstallUpdateAsync(bool visible = false)
        {
            try
            {
                Logger.Log("🔄 Baixando atualização...");

                var response = await _httpClient.GetAsync(ApiUrl);
                var json = await response.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<ReleaseInfo>(json, JsonOptions);

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
                var updatePath = Path.Combine(tempDir, $"KitLugia_Update_{DateTime.Now.Ticks}.zip");
                var hashPath = updatePath + ".sha256";

                Logger.Log($"📥 Baixando {asset.Name} ({asset.Size / 1024 / 1024}MB)...");

                var downloadResponse = await _httpClient.GetAsync(asset.BrowserDownloadUrl);
                await using (var fileStream = File.Create(updatePath))
                {
                    await downloadResponse.Content.CopyToAsync(fileStream);
                }

                Logger.Log("✅ Download concluído!");

                // Try to download SHA256 hash file
                string expectedHash = "";
                try
                {
                    var hashUrl = asset.BrowserDownloadUrl.Replace(".zip", ".zip.sha256");
                    var hashContent = await _httpClient.GetStringAsync(hashUrl);
                    expectedHash = hashContent.Trim();
                    Logger.Log($"🔐 SHA256 baixado: {expectedHash}");
                }
                catch
                {
                    Logger.Log("⚠️ Arquivo .sha256 não encontrado no release — pulando verificação de hash");
                }

                // Verify hash if available
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    string actualHash = ComputeSha256(updatePath);
                    Logger.Log($"🔐 Hash calculado: {actualHash}");
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"❌ Hash mismatch! Esperado: {expectedHash}, Calculado: {actualHash}");
                        File.Delete(updatePath);
                        return false;
                    }
                    Logger.Log("✅ Hash verificado com sucesso!");
                }

                int currentPid = Process.GetCurrentProcess().Id;
                string currentExePath = Environment.ProcessPath
                    ?? Path.ChangeExtension(Assembly.GetEntryAssembly()?.Location ?? "", ".exe")
                    ?? AppContext.BaseDirectory.TrimEnd('\\') + "\\KitLugia.GUI.exe";
                if (currentExePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    currentExePath = Path.ChangeExtension(currentExePath, ".exe");
                string currentDir = Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory;

                string currentVersion = GetCurrentVersion().ToString();
                string newVersion = ParseVersion(release.TagName).ToString();

                string batchPath = GenerateUpdateBatch(updatePath, currentPid, currentExePath, currentVersion, newVersion);
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

        private static Version GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version ?? new Version("1.0.0");
            }
            catch
            {
                return new Version("1.0.0");
            }
        }

        private static Version ParseVersion(string tag)
        {
            try
            {
                var cleanTag = tag.StartsWith("v") ? tag.Substring(1) : tag;
                return Version.Parse(cleanTag);
            }
            catch
            {
                return new Version("1.0.0");
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return Convert.ToHexStringLower(hash);
        }

        public static async Task StartAutoUpdateCheck()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(24));
                    if (await CheckForUpdatesAsync())
                    {
                        Logger.Log("🔄 Atualização disponível! Use a opção 'Atualizar' no menu.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro no auto-update check: {ex.Message}");
            }
        }

        public static string GenerateUpdateBatch(string zipPath, int pid, string exePath, string oldVersion, string newVersion)
        {
            try
            {
                string appDir = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                string batchPath = Path.Combine(appDir, "KitLugia_Update.cmd");

                string batch = $@"@echo off
title KitLugia - Atualizando...
color 0E
cls
echo ================================================
echo          KIT LUGIA - ATUALIZACAO
echo ================================================
echo.
echo [1/4] Aguardando fechamento do KitLugia...
:wait
tasklist /fi ""PID eq {pid}"" 2>nul | findstr /i ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
echo  OK - KitLugia fechado.
echo.
echo [2/4] Extraindo arquivos...
powershell -NoProfile -ExecutionPolicy Bypass -Command ""try {{ Expand-Archive -Path '{zipPath.Replace("'", "''")}' -DestinationPath '{appDir.Replace("'", "''")}' -Force; exit 0 }} catch {{ echo $_; pause; exit 1 }}""
if errorlevel 1 (
    echo  ERRO - Falha ao extrair arquivos.
    pause
    exit /b 1
)
echo  OK - Arquivos extraidos.
echo.
echo [3/4] Limpando temporarios...
if exist ""%~dp0KitLugia.Updater.exe"" del /q ""%~dp0KitLugia.Updater.exe"" 2>nul
if exist ""%~dp0KitLugia.Updater.dll"" del /q ""%~dp0KitLugia.Updater.dll"" 2>nul
if exist ""%~dp0update.log"" del /q ""%~dp0update.log"" 2>nul
echo  OK - Temporarios removidos.
echo.
rem Write UPDATE_COMPLETE.txt
echo {{""OldVersion"":""{oldVersion}"",""NewVersion"":""{newVersion}""}} > ""%~dp0UPDATE_COMPLETE.txt""
echo [4/4] Iniciando nova versao...
start """" ""%~dp0KitLugia.GUI.exe""
echo.
echo ================================================
echo     ATUALIZACAO CONCLUIDA!
echo ================================================
timeout /t 3 /nobreak >nul
del ""%~f0""
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
