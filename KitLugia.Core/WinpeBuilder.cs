using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public static class WinpeBuilder
    {
        private const string ADK_REG_PATH = @"SOFTWARE\Microsoft\Windows Kits\Installed Roots";
        private const string ADK_REG_VALUE = "KitsRoot10";
        private const string DEFAULT_OUTPUT = @"C:\WinPE_KitLugia";

        // URL do WinPE base (já vem com packages WMI/NetFX/StorageWMI/Scripting pré-instalados).
        // O artefato é um .7z contendo boot.wim + boot.sdi + estrutura EFI.
        // Para criar este artefato uma única vez, use BuildKitLugiaWinpe (com ADK) e publique-o no GitHub.
        private const string WINPE_BASE_URL = "https://github.com/luigiarrud4/KitLugia-WinPE/releases/download/v1.0/WinPE-base.7z";

        // Caminho de cache local persistente (sobrevive entre reinstalações/updates do app)
        private static string WinpeCacheDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "WinPE");

        private static string WinpeBaseWimPath => Path.Combine(WinpeCacheDir, "sources", "boot.wim");
        private static string WinpeBaseSdiPath => Path.Combine(WinpeCacheDir, "sources", "boot.sdi");
        private static string WinpeBaseEfiDir => Path.Combine(WinpeCacheDir, "efi");

        // Evento de progresso (Percent, Message)
        public static event Action<double, string>? ProgressUpdate;

        private static void ReportProgress(double pct, string msg)
        {
            Log(msg);
            ProgressUpdate?.Invoke(pct, msg);
        }

        public static void Log(string message) => WinbootManager.Log(message);
        public static void LogReplace(string message) => WinbootManager.LogReplace(message);

        /// <summary>
        /// Garante que um arquivo não seja read-only e tenha permissão de escrita.
        /// Resolve "Permission denied" / "WIM is read-only" do wimlib e DISM.
        /// </summary>
        public static void EnsureFileWritable(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                    Log($"ReadOnly removido: {path}");
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso: não foi possível ajustar permissões de {path}: {ex.Message}");
            }
        }

        /// <summary>Report de progresso que substitui a última linha (evita flooding em downloads).</summary>
        private static void ReportProgressReplace(double pct, string msg)
        {
            LogReplace(msg);
            ProgressUpdate?.Invoke(pct, msg);
        }

        // ======================================================================
        // === NOVO PIPELINE (SEM ADK) — padrão para usuários finais ===========
        // ======================================================================
        // Fluxo: obter WinPE base (cache/download/winre.wim) → customizar via
        // DISM do System32 (drivers do host + scripts KitLugia) → commit WIM
        // → gerar ISO via oscdimg.exe embutido. Não requer Windows ADK.
        // ======================================================================

        // Resolve o caminho do 7-Zip embutido para extrair o .7z do WinPE base.
        public static string? FindBundled7Zip()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Resources", "App", "7Zip", "7z.exe"),
                Path.Combine(Path.GetDirectoryName(typeof(WinpeBuilder).Assembly.Location) ?? "", "Resources", "App", "7Zip", "7z.exe"),
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };
            foreach (var p in candidates) if (File.Exists(p)) return p;
            return null;
        }

        // Resolve o oscdimg.exe embutido para gerar a ISO final.
        private static string? FindBundledOscdimg()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Resources", "App", "Oscdimg", "oscdimg.exe"),
                Path.Combine(Path.GetDirectoryName(typeof(WinpeBuilder).Assembly.Location) ?? "", "Resources", "App", "Oscdimg", "oscdimg.exe"),
            };
            foreach (var p in candidates) if (File.Exists(p)) return p;
            return null;
        }

        // Resolve o wimlib-imagex.exe embutido (modifica WIM sem montar via DISM).
        private static string? FindBundledWimlib()
        {
            HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase);
            AddIfNotEmpty(dirs, AppDomain.CurrentDomain.BaseDirectory);
            AddIfNotEmpty(dirs, Path.GetDirectoryName(typeof(WinpeBuilder).Assembly.Location));
            AddIfNotEmpty(dirs, Path.GetDirectoryName(Environment.ProcessPath));
            AddIfNotEmpty(dirs, Environment.CurrentDirectory);
            AddIfNotEmpty(dirs, Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()?.Location));
            try { AddIfNotEmpty(dirs, Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory)?.FullName); } catch { }

            Log($"[DIAG] FindBundledWimlib: {string.Join(", ", dirs)}");

            foreach (var d in dirs)
            {
                string p = Path.Combine(d, "Resources", "App", "Wimlib", "wimlib-imagex.exe");
                Log($"[DIAG]   trying: {p} => {File.Exists(p)}");
                if (File.Exists(p)) return p;
            }
            foreach (var d in dirs)
            {
                string p = Path.Combine(d, "wimlib-imagex.exe");
                Log($"[DIAG]   trying (bare): {p} => {File.Exists(p)}");
                if (File.Exists(p)) return p;
            }

            Log("[DIAG]   --- not found in any path, starting recursive search ---");
            foreach (var d in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d, "wimlib-imagex.exe", SearchOption.AllDirectories))
                    {
                        Log($"[DIAG]   recursive FOUND: {f}");
                        return f;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[DIAG]   recursive error in {d}: {ex.Message}");
                }
            }

            Log("[DIAG]   FAILED: wimlib-imagex.exe not found anywhere");
            return null;
        }

        private static void AddIfNotEmpty(HashSet<string> set, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                set.Add(value);
        }

        // Procura recursivamente por boot.wim no cache, retorna o primeiro com tamanho válido.
        private static string? FindBootWimRecursive()
        {
            try
            {
                if (!Directory.Exists(WinpeCacheDir)) return null;
                foreach (var f in Directory.GetFiles(WinpeCacheDir, "boot.wim", SearchOption.AllDirectories))
                    if (new FileInfo(f).Length > 10 * 1024 * 1024) return f;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        // Retorna true se a base WinPE já existe em cache local (boot.wim).
        public static bool IsWinpeBaseCached()
        {
            // Caminho direto (sources\boot.wim)
            if (File.Exists(WinpeBaseWimPath) && new FileInfo(WinpeBaseWimPath).Length > 10 * 1024 * 1024)
                return true;
            // Fallback: procura recursivamente (estrutura do 7z pode variar)
            return FindBootWimRecursive() != null;
        }

        // Garante que o boot.wim esteja no local esperado, procurando recursivamente se necessário.
        // Retorna o caminho final do boot.wim.
        private static string EnsureBootWimAtExpectedPath()
        {
            if (File.Exists(WinpeBaseWimPath) && new FileInfo(WinpeBaseWimPath).Length > 10 * 1024 * 1024)
                return WinpeBaseWimPath;

            var found = FindBootWimRecursive();
            if (found != null && found != WinpeBaseWimPath)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(WinpeBaseWimPath)!);
                    File.Copy(found, WinpeBaseWimPath, true);
                    Log($"boot.wim copiado de {found} para {WinpeBaseWimPath}");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            return WinpeBaseWimPath;
        }

        // Baixa e extrai o WinPE base do GitHub Release (uma única vez; cache persistente).
        // Result.Value é o caminho do boot.wim extraído.
        public static async Task<(bool ok, string message, string? wimPath)> DownloadWinpeBaseAsync()
        {
            try
            {
                Directory.CreateDirectory(WinpeCacheDir);

                // Se já existe boot.wim válido no cache (qualquer subpasta), reutiliza
                if (IsWinpeBaseCached())
                {
                    string cached = EnsureBootWimAtExpectedPath();
                    ReportProgress(100, "WinPE base encontrado em cache local.");
                    return (true, "Cache local", cached);
                }

                string archivePath = Path.Combine(WinpeCacheDir, "WinPE-base.7z");
                string? sevenZip = FindBundled7Zip();
                if (string.IsNullOrEmpty(sevenZip))
                    return (false, "7-Zip não encontrado (Resources/App/7Zip/7z.exe). Reinstale o KitLugia.", null);

                bool needDownload = !File.Exists(archivePath);
                if (needDownload)
                {
                    ReportProgressReplace(0, $"Baixando WinPE base de {WINPE_BASE_URL}...");
                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromMinutes(10);
                    using var resp = await httpClient.GetAsync(WINPE_BASE_URL, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode)
                        return (false, $"GitHub Release não disponível (HTTP {resp.StatusCode}). Verifique a conexão ou use a opção WinRE local.", null);

                    long total = resp.Content.Headers.ContentLength ?? 0;
                    await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    await using (var stream = await resp.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[8 * 1024 * 1024];
                        long read = 0;
                        int n;
                        while ((n = await stream.ReadAsync(buffer.AsMemory())) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n));
                            read += n;
                            if (total > 0)
                                ReportProgressReplace(read * 60.0 / total, $"Baixando WinPE: {read / (1024 * 1024)} MB / {total / (1024 * 1024)} MB");
                        }
                    }
                }
                else
                {
                    ReportProgress(10, "Arquivo .7z já existe no cache. Extraindo...");
                }

                // Extrai (tenta x primeiro, depois e como fallback)
                ReportProgress(70, "Extraindo WinPE base...");
                var (extCode, extOut) = await RunDism(sevenZip, $"x \"{archivePath}\" -o{WinpeCacheDir} -y", 180000);
                if (extCode != 0)
                {
                    Log($"Extração com 'x' falhou (código {extCode}). Tentando 'e' (flat)...");
                    var (extCode2, extOut2) = await RunDism(sevenZip, $"e \"{archivePath}\" -o{WinpeCacheDir} -y", 180000);
                    if (extCode2 != 0)
                        return (false, $"Falha ao extrair .7z (x: {extOut}) (e: {extOut2})", null);
                }

                // Garante que boot.wim esteja no local esperado (busca recursiva)
                string finalWim = EnsureBootWimAtExpectedPath();
                if (!File.Exists(finalWim) || new FileInfo(finalWim).Length <= 10 * 1024 * 1024)
                {
                    try { File.Delete(archivePath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    return (false, $"boot.wim não encontrado ou inválido após extração em: {finalWim}", null);
                }

                // Valida assinatura WIM (4 bytes: "MSWI" para WIM, "wimMS" para ESD/XPRESS)
                try
                {
                    using var fs = new FileStream(finalWim, FileMode.Open, FileAccess.Read);
                    byte[] sig = new byte[4];
                    await fs.ReadAsync(sig);
                    string sigStr = System.Text.Encoding.ASCII.GetString(sig);
                    if (sigStr != "MSWI" && sigStr != "wimMS")
                        Log($"Aviso: boot.wim sem assinatura WIM válida (found: {sigStr}). Pode estar corrompido.");
                    else
                        Log($"boot.wim assinatura OK: \"{sigStr}\".");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Limpa o .7z
                try { File.Delete(archivePath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                ReportProgress(100, "WinPE base pronto em cache local.");
                return (true, "Download concluído", finalWim);
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao baixar WinPE base: {ex.Message}", null);
            }
        }

        // Fallback: usa winre.wim do sistema (já tem WMI/Scripting/StorageWMI built-in).
        // Copia para o cache para não modificar o original.
        public static async Task<(bool ok, string message, string? wimPath)> UseWinreAsBaseAsync()
        {
            try
            {
                string? winreWim = await WinbootManager.LocateWinreWim();
                if (string.IsNullOrEmpty(winreWim) || !File.Exists(winreWim))
                    return (false, "winre.wim não encontrado neste sistema.", null);

                Directory.CreateDirectory(WinpeCacheDir);
                ReportProgress(10, $"Usando WinRE como base: {winreWim}");
                File.Copy(winreWim, WinpeBaseWimPath, true);
                ReportProgress(100, "WinRE copiado para cache local.");
                return (true, "WinRE utilizado como base", WinpeBaseWimPath);
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao usar WinRE: {ex.Message}", null);
            }
        }

        // Procura recursivamente por boot.sdi no cache.
        private static string? FindBootSdiRecursive()
        {
            try
            {
                if (!Directory.Exists(WinpeCacheDir)) return null;
                foreach (var f in Directory.GetFiles(WinpeCacheDir, "boot.sdi", SearchOption.AllDirectories))
                    if (new FileInfo(f).Length > 100 * 1024) return f;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        // Localiza o boot.sdi (RAM disk loader). Tenta cache → ADK → Windows.
        public static string? ResolveBootSdi()
        {
            if (File.Exists(WinpeBaseSdiPath))
                return WinpeBaseSdiPath;

            // Busca recursiva no cache
            var foundSdi = FindBootSdiRecursive();
            if (foundSdi != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(WinpeBaseSdiPath)!);
                    File.Copy(foundSdi, WinpeBaseSdiPath, true);
                    return WinpeBaseSdiPath;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); return foundSdi; }
            }

            string winSdi = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Boot", "DVD", "PCAT", "boot.sdi");
            if (File.Exists(winSdi))
            {
                try
                {
                    Directory.CreateDirectory(WinpeCacheDir);
                    File.Copy(winSdi, WinpeBaseSdiPath, true);
                    return WinpeBaseSdiPath;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); return winSdi; }
            }

            // Tenta a partir do ADK se instalado
            var (installed, adkRoot, _) = DetectAdk();
            if (installed && !string.IsNullOrEmpty(adkRoot))
            {
                string adkSdi = Path.Combine(adkRoot, "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "amd64", "Media", "boot.sdi");
                if (File.Exists(adkSdi)) return adkSdi;
            }

            return null;
        }

        // Gera o conteúdo do startnet.cmd (executado automaticamente pelo WinPE no boot).
        // Documentação Microsoft: startnet.cmd é o lugar correto para scripts batch.
        private static string WinpeConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "KitLugia", "WinPE");

        /// <summary>
        /// Gera startnet.cmd GENÉRICO que lê configuração de shrink_config.ini
        /// Formato do shrink_config.ini:
        ///   Linha 1: letra da unidade alvo (ex: C)
        ///   Linha 2: tamanho em MB (ex: 15000)
        /// </summary>
        public static string GenerateStartnetCmd()
        {
            string cfg = Path.Combine(WinpeConfigDir, "shrink_config.ini");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("wpeinit");
            sb.AppendLine("powercfg /s 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c >nul 2>&1");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo KitLugia WinPE - Shrink Avancado");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Aguardando discos...");
            sb.AppendLine("ping -n 5 127.0.0.1 > nul");
            sb.AppendLine();

            // Lê configuração de shrink_config.ini (mesmo dir do boot.wim)
            sb.AppendLine($"rem --- Ler config de {cfg} ---");
            sb.AppendLine("set TARGET_DRIVE=C");
            sb.AppendLine("set SHRINK_MB=8000");
            sb.AppendLine($"if exist \"{cfg}\" (");
            sb.AppendLine($"  set /p TARGET_DRIVE=<\"{cfg}\"");
            sb.AppendLine($"  for /f \"skip=1 delims=\" %%a in (\"{cfg}\") do set SHRINK_MB=%%a");
            sb.AppendLine("  echo Config lido: drive=!TARGET_DRIVE! size=!SHRINK_MB! MB");
            sb.AppendLine(") else (");
            sb.AppendLine("  echo Aviso: config nao encontrado. Usando C: 8000MB.");
            sb.AppendLine(")");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- Diagnostico ---");
            sb.AppendLine("fsutil fsinfo ntfsinfo !TARGET_DRIVE!:");
            sb.AppendLine("echo.");

            sb.AppendLine("echo --- QueryMax do shrink ---");
            sb.AppendLine("echo select volume !TARGET_DRIVE! > %TEMP%\\q.txt");
            sb.AppendLine("echo shrink querymax >> %TEMP%\\q.txt");
            sb.AppendLine("diskpart /s %TEMP%\\q.txt");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- Executando shrink ---");
            sb.AppendLine("echo select volume !TARGET_DRIVE! > %TEMP%\\s.txt");
            sb.AppendLine("echo shrink desired=!SHRINK_MB! >> %TEMP%\\s.txt");
            sb.AppendLine("diskpart /s %TEMP%\\s.txt");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- Resultado ---");
            sb.AppendLine("echo %DATE% %TIME% > X:\\KitLugiaPE\\result.log");
            sb.AppendLine("echo Status: %ERRORLEVEL% >> X:\\KitLugiaPE\\result.log");
            sb.AppendLine("echo Drive: !TARGET_DRIVE! Shrink: !SHRINK_MB! MB >> X:\\KitLugiaPE\\result.log");
            sb.AppendLine("if exist C:\\ (");
            sb.AppendLine("  echo %DATE% %TIME% > C:\\KitLugia_WinPE_Log.txt");
            sb.AppendLine("  echo Status: %ERRORLEVEL% >> C:\\KitLugia_WinPE_Log.txt");
            sb.AppendLine("  echo Drive: !TARGET_DRIVE! Shrink: !SHRINK_MB! MB >> C:\\KitLugia_WinPE_Log.txt");
            sb.AppendLine("  type X:\\KitLugiaPE\\result.log >> C:\\KitLugia_WinPE_Log.txt");
            sb.AppendLine(")");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Shrink concluido. Reinicie o sistema.");
            sb.AppendLine("pause");
            sb.AppendLine("wpeutil reboot");
            return sb.ToString();
        }

        // Gera o winpeshl.ini correto (WinPE só precisa do startnet.cmd padrão).
        // Documentação Microsoft: [LaunchApps] não suporta scripts batch — usa startnet.cmd.
        // Deixamos vazio para o WinPE rodar o cmd.exe padrão + startnet.cmd.
        public static string GenerateWinpeshlIni()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; winpeshl.ini - KitLugia WinPE");
            sb.AppendLine("; O script de shrink roda em startnet.cmd (executado automaticamente).");
            sb.AppendLine("; Deixe [LaunchApps] vazio para usar o shell padrão (cmd.exe).");
            sb.AppendLine("[LaunchApps]");
            return sb.ToString();
        }

        // Monta o WIM base, injeta drivers do host + scripts KitLugia, e commita.
        public static async Task<(bool ok, string log)> CustomizeWinpeWimAsync(string wimPath, bool includeDrivers = true)
        {
            var sb = new StringBuilder();
            string mountDir = Path.Combine(WinpeCacheDir, "mount");

            try
            {
                // Limpa montagem anterior (se existir)
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    // Tenta forçar via DISM cleanup
                    await RunDism("dism.exe", $"/Cleanup-Mountpoints", 30000);
                    Directory.CreateDirectory(mountDir);
                }
                else
                {
                    Directory.CreateDirectory(mountDir);
                }

                ReportProgress(10, $"Montando {wimPath} em {mountDir}...");
                var (code1, out1) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                sb.AppendLine(out1);
                if (code1 != 0 && !out1.Contains("already mounted") && !out1.Contains("remotely"))
                    return (false, $"Falha ao montar WIM (código {code1}): {out1}");

                // Injeta drivers de storage do host (nívei/RAID/AHCI)
                if (includeDrivers)
                {
                    ReportProgress(40, "Injetando drivers de storage do host...");
                    var (drvOk, drvLog) = await InjectStorageDrivers(mountDir);
                    sb.AppendLine(drvLog);
                }

                // Cria diretório KitLugiaPE no WIM
                string kitDir = Path.Combine(mountDir, "KitLugiaPE");
                Directory.CreateDirectory(kitDir);

                // Cria startnet.cmd no local correto: mounted\Windows\System32\startnet.cmd
                string system32 = Path.Combine(mountDir, "Windows", "System32");
                Directory.CreateDirectory(system32);

                string startnetPath = Path.Combine(system32, "startnet.cmd");
                string startnetContent = GenerateStartnetCmd();
                await File.WriteAllTextAsync(startnetPath, startnetContent, Encoding.ASCII);
                sb.AppendLine($"startnet.cmd escrito: {startnetPath}");
                ReportProgress(60, "startnet.cmd gerado");

                // Cria winpeshl.ini vazio (shell padrão)
                string winpeshlPath = Path.Combine(system32, "winpeshl.ini");
                await File.WriteAllTextAsync(winpeshlPath, GenerateWinpeshlIni(), Encoding.Unicode);
                sb.AppendLine($"winpeshl.ini escrito: {winpeshlPath}");

                // Aumenta scratch space (512MB ajuda em máquinas com pouca RAM)
                var (scratchCode, scratchOut) = await RunDism("dism.exe",
                    $"/Set-ScratchSpace:512 /Image:\"{mountDir}\"", 60000);
                sb.AppendLine($"ScratchSpace: código {scratchCode}");

                // Commit
                ReportProgress(75, "Commitando alterações no WIM...");
                var (code2, out2) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                sb.AppendLine(out2);
                if (code2 != 0)
                    return (false, $"Falha ao desmontar WIM (código {code2}): {out2}");

                ReportProgress(95, "WIM customizado com sucesso.");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                // Tentar desmontar sem commit
                try
                {
                    await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return (false, $"Erro ao customizar WinPE: {ex.Message}");
            }
        }

        /// <summary>
        /// Tenta wimlib-imagex wimupdate primeiro (rápido, sem montar).
        /// Se wimlib não estiver disponível, usa DISM mount/commit como fallback.
        /// </summary>
        public static async Task<bool> CustomizeWinpeWimFlatAsync(string wimPath, string startnetContent)
        {
            // Tenta wimlib primeiro (1-2 segundos, sem montagem)
            if (await WimlibUpdate(wimPath, startnetContent))
                return true;

            // Fallback: DISM mount/commit
            Log("Usando DISM mount/commit (fallback)...");
            string mountDir = Path.Combine(WinpeCacheDir, "mount");
            try
            {
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    await RunDism("dism.exe", "/Cleanup-Mountpoints", 30000);
                }
                Directory.CreateDirectory(mountDir);

                var (mntCode, mntOut) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                if (mntCode != 0 && !mntOut.Contains("already mounted"))
                {
                    Log($"Falha ao montar WIM para flat: {mntOut}");
                    return false;
                }

                string system32 = Path.Combine(mountDir, "Windows", "System32");
                Directory.CreateDirectory(system32);

                string startnetPath = Path.Combine(system32, "startnet.cmd");
                await File.WriteAllTextAsync(startnetPath, startnetContent, Encoding.ASCII);
                Log($"startnet.cmd substituído em boot.wim");

                // Remove default blue background (winpe.jpg)
                string winpeJpg = Path.Combine(system32, "winpe.jpg");
                if (File.Exists(winpeJpg))
                {
                    try { File.Delete(winpeJpg); }
                    catch (Exception ex) { Log($"Aviso: não foi possível remover winpe.jpg: {ex.Message}"); }
                }

                var (cmtCode, cmtOut) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                if (cmtCode != 0)
                {
                    Log($"Falha ao commitar WIM flat: {cmtOut}");
                    return false;
                }

                Log("boot.wim atualizado com startnet.cmd flat (DISM).");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Erro ao customizar boot.wim flat: {ex.Message}");
                try { await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return false;
            }
        }

        /// <summary>
        /// Monta o boot.wim e substitui o startnet.cmd por um novo com valores embutidos.
        /// Mais robusto que config separado: os valores ficam no proprio script.
        /// </summary>
        public static async Task<bool> InjectStartnetCmdIntoWimAsync(string wimPath, string startnetContent, string scriptName = "startnet.cmd")
        {
            string mountDir = Path.Combine(WinpeCacheDir, "mount_cfg");
            try
            {
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    await RunDism("dism.exe", "/Cleanup-Mountpoints", 30000);
                }
                Directory.CreateDirectory(mountDir);

                var (mntCode, mntOut) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                if (mntCode != 0 && !mntOut.Contains("already mounted"))
                {
                    Log($"Falha ao montar WIM para {scriptName}: {mntOut}");
                    return false;
                }

                string system32 = Path.Combine(mountDir, "Windows", "System32");
                Directory.CreateDirectory(system32);
                string scriptPath = Path.Combine(system32, scriptName);
                await File.WriteAllTextAsync(scriptPath, startnetContent, Encoding.ASCII);
                Log($"{scriptName} substituido em boot.wim");

                var (cmtCode, cmtOut) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                if (cmtCode != 0)
                {
                    Log($"Falha ao commitar WIM com {scriptName}: {cmtOut}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Erro ao injetar {scriptName} no boot.wim: {ex.Message}");
                try { await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return false;
            }
        }

        /// <summary>
        /// Monta o boot.wim e injeta shrink_config.ini na raiz (acessível como X:\shrink_config.ini no ramdisk).
        /// </summary>
        public static async Task<bool> InjectConfigIntoWimAsync(string wimPath, string configContent)
        {
            string mountDir = Path.Combine(WinpeCacheDir, "mount_cfg");
            try
            {
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    await RunDism("dism.exe", "/Cleanup-Mountpoints", 30000);
                }
                Directory.CreateDirectory(mountDir);

                var (mntCode, mntOut) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                if (mntCode != 0 && !mntOut.Contains("already mounted"))
                {
                    Log($"Falha ao montar WIM para config: {mntOut}");
                    return false;
                }

                string cfgPath = Path.Combine(mountDir, "shrink_config.ini");
                await File.WriteAllTextAsync(cfgPath, configContent, Encoding.ASCII);
                Log($"shrink_config.ini injetado em boot.wim");

                var (cmtCode, cmtOut) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                if (cmtCode != 0)
                {
                    Log($"Falha ao commitar WIM com config: {cmtOut}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Erro ao injetar config no boot.wim: {ex.Message}");
                try { await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return false;
            }
        }

        /// <summary>
        /// Monta o boot.wim UMA ÚNICA VEZ e injeta TANTO startnet.cmd quanto shrink_config.ini.
        /// Evita duas montagens/commits separados para o mesmo WIM.
        /// </summary>
        public static async Task<bool> InjectBootFilesIntoWimAsync(string wimPath, string startnetContent, string configContent)
        {
            string mountDir = Path.Combine(WinpeCacheDir, "mount_cfg");
            try
            {
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    await RunDism("dism.exe", "/Cleanup-Mountpoints", 30000);
                }
                Directory.CreateDirectory(mountDir);

                var (mntCode, mntOut) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                if (mntCode != 0 && !mntOut.Contains("already mounted"))
                {
                    Log($"Falha ao montar WIM para boot files: {mntOut}");
                    return false;
                }

                // startnet.cmd
                string system32 = Path.Combine(mountDir, "Windows", "System32");
                Directory.CreateDirectory(system32);
                string startnetPath = Path.Combine(system32, "startnet.cmd");
                await File.WriteAllTextAsync(startnetPath, startnetContent, Encoding.ASCII);

                // shrink_config.ini
                string cfgPath = Path.Combine(mountDir, "shrink_config.ini");
                await File.WriteAllTextAsync(cfgPath, configContent, Encoding.ASCII);

                Log($"startnet.cmd + shrink_config.ini injetados em boot.wim (1 montagem)");

                var (cmtCode, cmtOut) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                if (cmtCode != 0)
                {
                    Log($"Falha ao commitar WIM com boot files: {cmtOut}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Erro ao injetar boot files no boot.wim: {ex.Message}");
                try { await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return false;
            }
        }

        // Gera a ISO final do WinPE usando oscdimg.exe embutido.
        // mediaDir deve conter: boot.wim, boot.sdi, estrutura \efi\boot\bootx64.efi
        public static async Task<(bool ok, string log, string? isoPath)> BuildIsoNoAdk(string mediaDir, string outputIsoPath)
        {
            var sb = new StringBuilder();
            try
            {
                string? oscdimg = FindBundledOscdimg();
                if (string.IsNullOrEmpty(oscdimg))
                    return (false, "oscdimg.exe não encontrado (Resources/App/Oscdimg/oscdimg.exe).", null);

                ReportProgress(5, $"oscdimg: {oscdimg}");

                // Verifica arquivos essenciais na estrutura de mídia
                string bootWim = Path.Combine(mediaDir, "sources", "boot.wim");
                if (!File.Exists(bootWim))
                    return (false, $"boot.wim não encontrado em: {bootWim}", null);

                // Para ISO UEFI+BIOS bootável, oscdimg precisa de Etfsboot.com e Efisys.bin
                // Esses vêm junto no .7z do WinPE base (pasta \efi\boot\etfsboot.com, \efi\microsoft\boot\efisys.bin)
                string etfs = Path.Combine(mediaDir, "efi", "microsoft", "boot", "etfsboot.com");
                if (!File.Exists(etfs))
                    etfs = Path.Combine(mediaDir, "boot", "etfsboot.com");
                string efisys = Path.Combine(mediaDir, "efi", "microsoft", "boot", "efisys.bin");

                string args;
                if (File.Exists(efisys) && File.Exists(etfs))
                {
                    // Dual-boot: BIOS (ElTorito) + UEFI
                    args = $"-bootdata:2#p0,e,b\"{etfs}\"#pEF,e,b\"{efisys}\" -o -u2 -udfver102 \"{mediaDir}\" \"{outputIsoPath}\"";
                }
                else
                {
                    // Só BIOS boot
                    args = $"-b\"{etfs}\" -o -u2 -udfver102 \"{mediaDir}\" \"{outputIsoPath}\"";
                }

                ReportProgress(20, "Gerando ISO...");
                var (code, output) = await RunDism(oscdimg, args, 300000);
                sb.AppendLine(output);
                if (code != 0)
                    return (false, $"oscdimg falhou (código {code})", null);

                if (!File.Exists(outputIsoPath))
                    return (false, $"ISO não foi gerada: {outputIsoPath}", null);

                long sizeMB = new FileInfo(outputIsoPath).Length / (1024 * 1024);
                ReportProgress(100, $"ISO gerada: {outputIsoPath} ({sizeMB} MB)");
                return (true, sb.ToString(), outputIsoPath);
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao gerar ISO: {ex.Message}", null);
            }
        }

        // MÉTODO PRINCIPAL (no-ADK): pipeline completo sem Windows ADK.
        // 1) Resolve base (cache → GitHub → winre.wim)
        // 2) Customiza WIM (drivers + scripts)
        // 3) Gera ISO
        public static async Task<(bool ok, string log, string? isoPath, string? mediaDir)> BuildKitLugiaWinpeNoAdkAsync(
            string? outputIsoPath = null,
            bool includeDrivers = true,
            bool preferWinre = false)
        {
            var sb = new StringBuilder();
            string? baseWim = null;
            string? bootSdi = null;

            Log("========== WINPE KITLUGIA (NO-ADK PIPELINE) ==========");

            // ---- Fase 1: Resolver WinPE base ----
            ReportProgress(0, "Fase 1: Resolvendo WinPE base...");
            if (preferWinre)
            {
                var (wrOk, wrMsg, wrWim) = await UseWinreAsBaseAsync();
                if (wrOk) baseWim = wrWim;
                else sb.AppendLine($"WinRE fallback falhou: {wrMsg}");
            }

            if (baseWim == null && IsWinpeBaseCached())
            {
                baseWim = WinpeBaseWimPath;
                ReportProgress(30, "Usando WinPE base do cache local.");
            }

            if (baseWim == null)
            {
                var (dlOk, dlMsg, dlWim) = await DownloadWinpeBaseAsync();
                if (dlOk) baseWim = dlWim;
                else
                {
                    sb.AppendLine($"Download falhou: {dlMsg}. Tentando WinRE...");
                    var (wrOk, wrMsg, wrWim) = await UseWinreAsBaseAsync();
                    if (wrOk) baseWim = wrWim;
                    else
                        return (false, sb.ToString(), null, null);
                }
            }

            if (baseWim == null || !File.Exists(baseWim))
                return (false, "Não foi possível obter um WinPE base.", null, null);

            sb.AppendLine($"WinPE base: {baseWim}");

            // Resolver boot.sdi
            bootSdi = ResolveBootSdi();
            if (bootSdi == null)
                return (false, "boot.sdi não encontrado. Copie de C:\\Windows\\Boot\\DVD\\PCAT\\boot.sdi", null, null);

            // ---- Fase 2: Montar estrutura de mídia ISO ----
            ReportProgress(40, "Fase 2: Preparando estrutura de mídia...");
            string workDir = Path.Combine(WinpeCacheDir, "media");
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            Directory.CreateDirectory(workDir);

            string sourcesDir = Path.Combine(workDir, "sources");
            Directory.CreateDirectory(sourcesDir);

            // Copia boot.wim customizado (a customização acontece primeiro)
            // ---- Fase 3: Customizar WIM ----
            ReportProgress(45, "Fase 3: Customizando WIM (DISM do System32, sem ADK)...");
            var (custOk, custLog) = await CustomizeWinpeWimAsync(baseWim, includeDrivers);
            sb.AppendLine(custLog);
            if (!custOk)
                return (false, sb.ToString(), null, null);

            File.Copy(baseWim, Path.Combine(sourcesDir, "boot.wim"), true);
            File.Copy(bootSdi, Path.Combine(sourcesDir, "boot.sdi"), true);

            // Copia pasta EFI do cache se existir (já vem no .7z base)
            if (Directory.Exists(WinpeBaseEfiDir))
            {
                string efiDest = Path.Combine(workDir, "efi");
                try
                {
                    if (Directory.Exists(efiDest)) Directory.Delete(efiDest, true);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                CopyDirectory(WinpeBaseEfiDir, efiDest);
            }

            // ---- Fase 4: Gerar ISO ----
            ReportProgress(85, "Fase 4: Gerando ISO via oscdimg...");
            string isoPath = outputIsoPath ?? Path.Combine(WinpeCacheDir, "WinPE_KitLugia.iso");
            var (isoOk, isoLog, isoResult) = await BuildIsoNoAdk(workDir, isoPath);
            sb.AppendLine(isoLog);
            if (!isoOk)
                return (false, sb.ToString(), null, null);

            Log("========== WINPE GERADO COM SUCESSO ==========");
            ReportProgress(100, "WinPE ISO pronto.");
            return (true, sb.ToString(), isoResult, workDir);
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                try { File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), true); }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
            }
        }

        // Limpa o cache local do WinPE.
        public static void ClearCache()
        {
            try
            {
                if (Directory.Exists(WinpeCacheDir))
                    Directory.Delete(WinpeCacheDir, true);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // ======================================================================
        // === PIPELINE LEGADO (com ADK) — para usuários avançados =============
        // ======================================================================
        // Os métodos abaixo exigem Windows ADK + WinPE add-on instalados.
        // São mantidos para casos de uso especial e para gerar a WinPE base
        // publicada no GitHub Releases (uma única vez).
        // ======================================================================

        // ======================================================================
        // 1. DETECTAR ADK INSTALADO
        // ======================================================================
        public static (bool installed, string adkRoot, string peRoot) DetectAdk()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ADK_REG_PATH);
                if (key == null) return (false, "", "");

                string? kitsRoot = key.GetValue(ADK_REG_VALUE) as string;
                if (string.IsNullOrEmpty(kitsRoot) || !Directory.Exists(kitsRoot))
                    return (false, "", "");

                string peRoot = Path.Combine(kitsRoot, "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "amd64");

                if (!Directory.Exists(peRoot))
                {
                    // Tenta x86 fallback
                    peRoot = Path.Combine(kitsRoot, "Assessment and Deployment Kit",
                        "Windows Preinstallation Environment", "x86");
                    if (!Directory.Exists(peRoot))
                        return (false, kitsRoot, "");
                }

                return (true, kitsRoot, peRoot);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return (false, "", ""); }
        }

        // ======================================================================
        // 2. CRIAR BASE DO WINPE (copype amd64)
        // ======================================================================
        public static async Task<(bool ok, string log)> CreateBase(string outputPath = DEFAULT_OUTPUT)
        {
            var sb = new StringBuilder();
            try
            {
                if (Directory.Exists(outputPath))
                {
                    sb.AppendLine($"Diretório {outputPath} já existe. Removendo...");
                    Directory.Delete(outputPath, true);
                }

                var (installed, _, peRoot) = DetectAdk();
                if (!installed)
                    return (false, "ADK não encontrado. Instale o Windows ADK primeiro.");

                string copypeCmd = Path.Combine(peRoot, "copype.cmd");
                if (!File.Exists(copypeCmd))
                    return (false, $"copype.cmd não encontrado em: {copypeCmd}");

                sb.AppendLine($"Executando: {copypeCmd} amd64 {outputPath}");
                var (code, output) = await RunDism(copypeCmd, $"amd64 \"{outputPath}\"", 120000);
                sb.AppendLine(output);

                if (code != 0)
                    return (false, $"copype falhou (código {code}): {output}");

                string bootWim = Path.Combine(outputPath, "media", "sources", "boot.wim");
                if (!File.Exists(bootWim))
                    return (false, $"boot.wim não foi gerado em: {bootWim}");

                sb.AppendLine($"WinPE base criado em: {outputPath}");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao criar base WinPE: {ex.Message}");
            }
        }

        // ======================================================================
        // 3. ADICIONAR PACOTES OPCIONAIS AO WINPE
        // ======================================================================
        public static async Task<(bool ok, string log)> AddOptionalPackages(string mountPath)
        {
            var sb = new StringBuilder();
            try
            {
                var (installed, adkRoot, _) = DetectAdk();
                if (!installed)
                    return (false, "ADK não encontrado.");

                string ocsDir = Path.Combine(adkRoot, "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "amd64", "WinPE_OCs");

                if (!Directory.Exists(ocsDir))
                    return (false, $"Diretório de pacotes OC não encontrado: {ocsDir}");

                string[] requiredPackages = {
                    "WinPE-WMI.cab",
                    "WinPE-WMI_ca-ES.cab",
                    "WinPE-StorageWMI.cab",
                    "WinPE-StorageWMI_ca-ES.cab",
                    "WinPE-Scripting.cab",
                    "WinPE-Scripting_ca-ES.cab",
                    "WinPE-NetFX.cab",
                    "WinPE-NetFX_ca-ES.cab",
                    "WinPE-FontSupport-pt-BR.cab",
                };

                var available = Directory.GetFiles(ocsDir, "*.cab")
                    .Select(f => Path.GetFileName(f))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                int added = 0;
                foreach (var pkg in requiredPackages)
                {
                    if (!available.Contains(pkg))
                    {
                        sb.AppendLine($"  [skip] {pkg} não disponível");
                        continue;
                    }

                    string cabPath = Path.Combine(ocsDir, pkg);
                    string pkgArg = $"/Add-Package /Image:\"{mountPath}\" /PackagePath:\"{cabPath}\"";
                    sb.AppendLine($"  Adicionando: {pkg}");
                    var (code, output) = await RunDism("dism.exe", pkgArg, 180000);
                    if (code == 0 || output.Contains("The remote procedure call failed"))
                    {
                        added++;
                        sb.AppendLine($"    OK");
                    }
                    else
                    {
                        sb.AppendLine($"    Aviso (código {code}): {output.Trim().Replace("\n", "; ")}");
                    }
                }

                sb.AppendLine($"\n{added} pacotes adicionados.");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao adicionar pacotes: {ex.Message}");
            }
        }

        // ======================================================================
        // 4. INJETAR DRIVERS DE STORAGE DO SISTEMA ATUAL
        // ======================================================================
        public static async Task<(bool ok, string log)> InjectStorageDrivers(string mountPath)
        {
            var sb = new StringBuilder();
            try
            {
                // Pega drivers de storage do DriverStore do sistema atual
                string driverStore = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "DriverStore", "FileRepository");

                if (!Directory.Exists(driverStore))
                    return (false, $"DriverStore não encontrado: {driverStore}");

                // Categorias de drivers críticos para boot em WinPE
                string[] storagePatterns = {
                    "nvme", "storport", "storahci", "stornvme", "iastor", "iaStorAC",
                    "pciide", "ahci", "msahci", "scsiport", "lsi_", "megasr", "percsas",
                    "vstor", "vhdmp", "nvraid", "nvstor", "chtpe", "amdsata", "amd_sata",
                    "intelpe", "iaLPSS", "SATA", "raid", "nvme", "solidigm"
                };

                var driverDirs = Directory.GetDirectories(driverStore)
                    .Where(d => storagePatterns.Any(p =>
                        Path.GetFileName(d).IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                if (driverDirs.Count == 0)
                {
                    sb.AppendLine("Nenhum driver de storage adicional encontrado (usando os nativos do WinPE).");
                    return (true, sb.ToString());
                }

                // Cria diretório de drivers temporário
                string tempDriverDir = Path.Combine(Path.GetTempPath(), "KitLugia_WinPE_Drivers");
                if (Directory.Exists(tempDriverDir))
                    Directory.Delete(tempDriverDir, true);
                Directory.CreateDirectory(tempDriverDir);

                int copied = 0;
                foreach (var dir in driverDirs)
                {
                    foreach (var inf in Directory.GetFiles(dir, "*.inf"))
                    {
                        try
                        {
                            string dest = Path.Combine(tempDriverDir, Path.GetFileName(inf));
                            File.Copy(inf, dest, true);
                            copied++;
                        }
                        catch { /* skip locked files */ }
                    }
                }

                if (copied == 0)
                {
                    sb.AppendLine("Nenhum driver .inf pôde ser copiado.");
                    return (true, sb.ToString());
                }

                sb.AppendLine($"{copied} arquivos .inf copiados para {tempDriverDir}");

                // Injeta os drivers no WinPE
                string addDriverArg = $"/Add-Driver /Image:\"{mountPath}\" /Driver:\"{tempDriverDir}\" /Recurse";
                var (code, output) = await RunDism("dism.exe", addDriverArg, 300000);

                sb.AppendLine($"DISM /Add-Driver: código {code}");
                if (code != 0)
                    sb.AppendLine($"  Aviso: {output.Trim().Replace("\n", "; ")}");

                // Limpeza
                try { Directory.Delete(tempDriverDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                sb.AppendLine($"Drivers injetados (pelo menos {copied} .inf copiados).");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao injetar drivers: {ex.Message}");
            }
        }

        // ======================================================================
        // 5. CRIAR winpeshl.ini (AUTO-START DO SCRIPT DE SHRINK)
        // ======================================================================
        public static void CreateWinpeshlIni(string mountPath)
        {
            string system32 = Path.Combine(mountPath, "Windows", "System32");
            Directory.CreateDirectory(system32);

            string iniPath = Path.Combine(system32, "winpeshl.ini");
            var ini = new StringBuilder();
            ini.AppendLine("[LaunchApps]");
            ini.AppendLine("%SYSTEMDRIVE%\\KitLugiaPE\\KitLugiaPE.cmd");

            File.WriteAllText(iniPath, ini.ToString(), Encoding.Unicode);
            Log($"winpeshl.ini criado: {iniPath}");
        }

        // ======================================================================
        // 6. CRIAR SCRIPT DE SHRINK QUE RODA DENTRO DO WINPE
        // ======================================================================
        public static string GenerateShrinkScriptContent(string targetDrive, long targetSizeMB, bool deletePartitionA, string? partitionALabel)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("cd /d %SYSTEMDRIVE%\\KitLugiaPE");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo KitLugia WinPE - Shrink Automatizado");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Aguardando discos ficarem disponiveis...");
            sb.AppendLine("ping -n 5 127.0.0.1 > nul");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- Diagnosticando espaco livre ---");
            sb.AppendLine($"fsutil fsinfo ntfsinfo {targetDrive}:");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- QueryMax do shrink ---");
            sb.AppendLine("echo select volume %systemdrive% > %TEMP%\\shrink.txt");
            sb.AppendLine("echo shrink querymax >> %TEMP%\\shrink.txt");
            sb.AppendLine("diskpart /s %TEMP%\\shrink.txt");
            sb.AppendLine("echo.");

            if (deletePartitionA && !string.IsNullOrEmpty(partitionALabel))
            {
                sb.AppendLine($"echo --- Removendo particao A ({partitionALabel}) ---");
                sb.AppendLine("echo list volume > %TEMP%\\del_part.txt");
                sb.AppendLine($"echo select volume {partitionALabel} >> %TEMP%\\del_part.txt");
                sb.AppendLine("echo delete partition override >> %TEMP%\\del_part.txt");
                sb.AppendLine("diskpart /s %TEMP%\\del_part.txt");
                sb.AppendLine("echo.");
                sb.AppendLine("echo --- QueryMax apos remocao da particao A ---");
                sb.AppendLine($"echo select volume {targetDrive} > %TEMP%\\shrink2.txt");
                sb.AppendLine("echo shrink querymax >> %TEMP%\\shrink2.txt");
                sb.AppendLine("diskpart /s %TEMP%\\shrink2.txt");
                sb.AppendLine("echo.");
            }

            sb.AppendLine("echo --- Executando shrink ---");
            sb.AppendLine($"echo select volume {targetDrive} > %TEMP%\\shrink_exec.txt");
            sb.AppendLine($"echo shrink desired={targetSizeMB} >> %TEMP%\\shrink_exec.txt");
            sb.AppendLine("diskpart /s %TEMP%\\shrink_exec.txt");
            sb.AppendLine("echo.");
            sb.AppendLine("echo --- Shrink concluido ---");
            sb.AppendLine("echo Resultado salvo em %SYSTEMDRIVE%\\KitLugiaPE\\shrink_result.log");
            sb.AppendLine("echo %DATE% %TIME% > shrink_result.log");
            sb.AppendLine("echo Status: %ERRORLEVEL% >> shrink_result.log");

            return sb.ToString();
        }

        // ======================================================================
        // 7. MONTAR WIM, CUSTOMIZAR E DESMONTAR
        // ======================================================================
        public static async Task<(bool ok, string log)> MountAndCustomize(string pePath, string targetDrive = "C", long shrinkMB = 7000, bool includeDrivers = true)
        {
            var sb = new StringBuilder();
            string mountDir = Path.Combine(pePath, "mount");

            try
            {
                string bootWim = Path.Combine(pePath, "media", "sources", "boot.wim");
                if (!File.Exists(bootWim))
                    return (false, $"boot.wim não encontrado: {bootWim}");

                // Montar
                sb.AppendLine("Montando boot.wim...");
                var (code1, out1) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{bootWim}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 120000);
                sb.AppendLine(out1);
                if (code1 != 0 && !out1.Contains("remotely"))
                    return (false, $"Falha ao montar WIM (código {code1})");

                // Adicionar pacotes
                sb.AppendLine("\nAdicionando pacotes opcionais...");
                var (pkgOk, pkgLog) = await AddOptionalPackages(mountDir);
                sb.AppendLine(pkgLog);

                // Injetar drivers
                if (includeDrivers)
                {
                    sb.AppendLine("\nInjetando drivers de storage...");
                    var (drvOk, drvLog) = await InjectStorageDrivers(mountDir);
                    sb.AppendLine(drvLog);
                }

                // Criar diretório do script
                string kitLugiaDir = Path.Combine(mountDir, "KitLugiaPE");
                Directory.CreateDirectory(kitLugiaDir);

                // Criar winpeshl.ini
                CreateWinpeshlIni(mountDir);

                // Criar script de shrink
                string scriptContent = GenerateShrinkScriptContent(targetDrive, shrinkMB, true, "A:");
                string scriptPath = Path.Combine(kitLugiaDir, "KitLugiaPE.cmd");
                File.WriteAllText(scriptPath, scriptContent, Encoding.ASCII);
                sb.AppendLine($"Script de shrink criado: {scriptPath}");

                // Desmontar e commitar
                sb.AppendLine("\nDesmontando e commitando WIM...");
                var (code2, out2) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 180000);
                sb.AppendLine(out2);
                if (code2 != 0)
                    return (false, $"Falha ao desmontar WIM (código {code2}): {out2}");

                sb.AppendLine("\nWIM customizado com sucesso!");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                // Tentar desmontar sem commit em caso de erro
                try
                {
                    await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 60000);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                return (false, $"Erro ao customizar WinPE: {ex.Message}");
            }
        }

        // ======================================================================
        // 8. GERAR ISO FINAL
        // ======================================================================
        public static async Task<(bool ok, string log)> BuildIso(string pePath, string outputIsoPath)
        {
            var sb = new StringBuilder();
            try
            {
                string makeWinPEMedia = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ??
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Windows Kits", "10", "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "amd64",
                    "MakeWinPEMedia.cmd");

                if (!File.Exists(makeWinPEMedia))
                {
                    // Tenta encontrar em ProgramFiles
                    makeWinPEMedia = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Windows Kits", "10", "Assessment and Deployment Kit",
                        "Windows Preinstallation Environment", "amd64",
                        "MakeWinPEMedia.cmd");
                }

                if (!File.Exists(makeWinPEMedia))
                {
                    // Fallback: usar oscdimg
                    string osCdImg = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ??
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Windows Kits", "10", "Assessment and Deployment Kit",
                        "Deployment Tools", "amd64", "Oscdimg", "oscdimg.exe");

                    if (!File.Exists(osCdImg))
                        return (false, "MakeWinPEMedia.cmd e oscdimg.exe não encontrados. ADK incompleto.");

                    string etfsboot = Path.Combine(pePath, "media", "efi", "microsoft", "boot", "etfsboot.com");
                    string efisys = Path.Combine(pePath, "media", "efi", "microsoft", "boot", "efisys.bin");

                    string imgArgs = $"-bootdata:2#p0,e,b\"{etfsboot}\"#pEF,e,b\"{efisys}\" " +
                                    $"-o -u2 -udfver102 " +
                                    $"\"{Path.Combine(pePath, "media")}\" \"{outputIsoPath}\"";

                    sb.AppendLine($"Gerando ISO via oscdimg...");
                    var (code, output) = await RunDism(osCdImg, imgArgs, 300000);
                    sb.AppendLine(output);
                    if (code != 0)
                        return (false, $"oscdimg falhou (código {code})");
                }
                else
                {
                    sb.AppendLine($"Gerando ISO via MakeWinPEMedia...");
                    var (code, output) = await RunDism(makeWinPEMedia, $"/ISO \"{pePath}\" \"{outputIsoPath}\"", 300000);
                    sb.AppendLine(output);
                    if (code != 0)
                        return (false, $"MakeWinPEMedia falhou (código {code})");
                }

                if (!File.Exists(outputIsoPath))
                    return (false, $"ISO não foi gerada: {outputIsoPath}");

                sb.AppendLine($"\nISO gerada: {outputIsoPath}");
                long sizeMB = new FileInfo(outputIsoPath).Length / (1024 * 1024);
                sb.AppendLine($"Tamanho: {sizeMB} MB");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao gerar ISO: {ex.Message}");
            }
        }

        // ======================================================================
        // 9. MÉTODO PRINCIPAL: CONSTRUIR WINPE COMPLETO
        // ======================================================================
        public static async Task<(bool ok, string log, string isoPath)> BuildKitLugiaWinpe(
            string? outputPath = null,
            string targetDrive = "C",
            long shrinkMB = 7000,
            bool includeDrivers = true,
            string? customIsoPath = null)
        {
            var sb = new StringBuilder();
            string pePath = outputPath ?? DEFAULT_OUTPUT;
            string isoPath = customIsoPath ?? Path.Combine(pePath, "KitLugiaPE.iso");

            Log("========== INICIANDO CONSTRUCAO DO WINPE KITLUGIA ==========");

            // Fase 1: Verificar ADK
            Log("\n[1/5] Verificando ADK...");
            var (installed, adkRoot, _) = DetectAdk();
            if (!installed)
            {
                Log("ADK nao encontrado. Baixe e instale o Windows ADK + WinPE add-on.");
                return (false, "ADK não encontrado", "");
            }
            Log($"ADK encontrado em: {adkRoot}");

            // Fase 2: Criar base
            Log("\n[2/5] Criando base WinPE (copype)...");
            var (baseOk, baseLog) = await CreateBase(pePath);
            sb.AppendLine(baseLog);
            if (!baseOk)
                return (false, sb.ToString(), "");

            // Fase 3: Montar e customizar
            Log("\n[3/5] Montando e customizando WIM...");
            var (custOk, custLog) = await MountAndCustomize(pePath, targetDrive, shrinkMB, includeDrivers);
            sb.AppendLine(custLog);
            if (!custOk)
                return (false, sb.ToString(), "");

            // Fase 4: Gerar ISO
            Log("\n[4/5] Gerando ISO...");
            var (isoOk, isoLog) = await BuildIso(pePath, isoPath);
            sb.AppendLine(isoLog);
            if (!isoOk)
                return (false, sb.ToString(), "");

            // Fase 5: Limpeza opcional da estrutura de build
            Log("\n[5/5] Limpeza...");
            try
            {
                string mountDir = Path.Combine(pePath, "mount");
                if (Directory.Exists(mountDir))
                    Directory.Delete(mountDir, true);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            Log("\n========== WINPE CONSTRUIDO COM SUCESSO ==========");
            return (true, sb.ToString(), isoPath);
        }

        // ======================================================================
        // UTILITÁRIO: Executar DISM/COMANDO COM LOG
        // ======================================================================
        private static async Task<(int ExitCode, string Output)> RunDism(string filename, string args, int timeoutMs = 180000)
        {
            Log($"  > {filename} {args}");
            var psi = new ProcessStartInfo(filename, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var proc = Process.Start(psi);
            if (proc == null) return (-1, "Falha ao iniciar processo");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var readTask = Task.WhenAll(outputTask, errorTask);

            if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false) != readTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return (-1, $"TIMEOUT após {timeoutMs}ms");
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            string output = outputTask.Result + errorTask.Result;
            return (proc.ExitCode, output);
        }

        // ======================================================================
        // UTILITÁRIO: Executar qualquer processo com log e timeout
        // ======================================================================
        private static async Task<(int ExitCode, string Output)> RunProcess(string filename, string args, int timeoutMs = 180000, string? workingDirectory = null)
        {
            Log($"  > {filename} {args}");
            var psi = new ProcessStartInfo(filename, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(filename) ?? "",
            };

            var proc = Process.Start(psi);
            if (proc == null) return (-1, "Falha ao iniciar processo");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var readTask = Task.WhenAll(outputTask, errorTask);

            if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false) != readTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return (-1, $"TIMEOUT após {timeoutMs}ms");
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            string output = outputTask.Result + errorTask.Result;
            return (proc.ExitCode, output);
        }

        // ======================================================================
        // WIMLIB: modificar WIM sem montar via wimlib-imagex wimupdate
        // ======================================================================

        /// <summary>
        /// Adiciona um script personalizado em Windows\System32\ no WIM via wimlib (sem montar).
        /// Usado pelo Validation OS e outros cenários que precisam injetar startnet*.cmd.
        /// Retorna true se wimlib executou com sucesso; false se não disponível ou falhou.
        /// </summary>
        public static async Task<bool> UpdateWimWithScriptAsync(string wimPath, string scriptContent, string scriptName = "startnet.cmd")
        {
            string? wimlibExe = FindBundledWimlib();
            if (wimlibExe == null)
            {
                Log("wimlib-imagex.exe não encontrado. Usando DISM como fallback.");
                return false;
            }

            Log($"Usando wimlib-imagex para adicionar {scriptName} em {wimPath} sem montar...");
            EnsureFileWritable(wimPath);

            string system32Path = "/Windows/System32";
            string tmpDir = Path.Combine(Path.GetTempPath(), "KitLugia_Wimlib");
            Directory.CreateDirectory(tmpDir);
            string tmpScript = Path.Combine(tmpDir, scriptName);
            await File.WriteAllTextAsync(tmpScript, scriptContent, Encoding.ASCII);

            // Escape spaces in temp path for wimlib's --command parser
            string escapedTmpScript = tmpScript.Contains(' ')
                ? $"\"{tmpScript}\""
                : tmpScript;

            string args = $"update \"{wimPath}\" 1"
                + $" --command=\"add {escapedTmpScript} {system32Path}/{scriptName}\"";

            var (code, output) = await RunProcess(wimlibExe, args, 60000);

            try { File.Delete(tmpScript); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            if (code == 0 && output.Contains("Successfully updated WIM"))
            {
                Log($"{scriptName} adicionado ao WIM via wimlib-imagex com sucesso.");
                return true;
            }

            Log($"wimlib-imagex falhou (código {code}): {output}");
            return false;
        }

        /// <summary>
        /// Tenta usar wimlib-imagex.exe (se embutido) para modificar o boot.wim sem montar.
        /// Comandos: delete winpe.jpg, add startnet.cmd.
        /// Retorna true se wimlib executou com sucesso, false se não disponível ou falhou.
        /// </summary>
        /// <summary>
        /// Tenta usar wimlib-imagex.exe (se embutido) para modificar o boot.wim sem montar.
        /// Comandos: delete winpe.jpg, add startnet.cmd.
        /// Retorna true se wimlib executou com sucesso, false se não disponível ou falhou.
        /// </summary>
        private static async Task<bool> WimlibUpdate(string wimPath, string startnetContent)
        {
            string? wimlibExe = FindBundledWimlib();
            if (wimlibExe == null)
            {
                Log("wimlib-imagex.exe não encontrado. Usando DISM como fallback.");
                return false;
            }

            Log($"Usando wimlib-imagex para modificar {wimPath} sem montar...");

            string system32Path = "/Windows/System32";

            // Cria startnet.cmd temporário
            string tmpDir = Path.Combine(Path.GetTempPath(), "KitLugia_Wimlib");
            Directory.CreateDirectory(tmpDir);
            string tmpStartnet = Path.Combine(tmpDir, "startnet.cmd");
            await File.WriteAllTextAsync(tmpStartnet, startnetContent, Encoding.ASCII);

            // Escape spaces in temp path for wimlib's --command parser
            string escapedTmpStartnet = tmpStartnet.Contains(' ')
                ? $"\"{tmpStartnet}\""
                : tmpStartnet;

            // Monta comando: delete winpe.jpg + add startnet.cmd
            string args = $"update \"{wimPath}\" 1"
                + $" --command=\"delete {system32Path}/winpe.jpg\""
                + $" --command=\"add {escapedTmpStartnet} {system32Path}/startnet.cmd\"";

            var (code, output) = await RunProcess(wimlibExe, args, 60000);

            // Limpeza
            try { File.Delete(tmpStartnet); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            if (code == 0 && output.Contains("Successfully updated WIM"))
            {
                Log("boot.wim atualizado via wimlib-imagex com sucesso.");
                return true;
            }

            Log($"wimlib-imagex falhou (código {code}): {output}");
            return false;
        }

        /// <summary>
        /// Injeta wimlib-imagex.exe + libwim-15.dll dentro do boot.wim em X:\Windows\System32\
        /// para que o WinPE possa usar wimlib apply (2-5x mais rápido que DISM Apply-Image).
        /// </summary>
        public static async Task<bool> InjectWimlibIntoWimAsync(string wimPath)
        {
            string? wimlibExe = FindBundledWimlib();
            if (wimlibExe == null)
            {
                Log("wimlib-imagex.exe não encontrado no host; não será injetado no boot.wim.");
                return false;
            }

            string wimlibDir = Path.GetDirectoryName(wimlibExe)!;
            string dllPath = Path.Combine(wimlibDir, "libwim-15.dll");
            if (!File.Exists(dllPath))
            {
                Log($"libwim-15.dll não encontrado em {wimlibDir}; não será injetado.");
                return false;
            }

            string system32 = "/Windows/System32";

            // Tenta wimlib update injection primeiro (rápido, sem montar)
            string? ownWimlib = FindBundledWimlib();
            if (ownWimlib != null)
            {
                Log($"Injetando wimlib-imagex.exe + libwim-15.dll em {wimPath} via wimlib-imagex update...");
                string args = $"update \"{wimPath}\" 1"
                    + $" --command=\"add {wimlibExe} {system32}/wimlib-imagex.exe\""
                    + $" --command=\"add {dllPath} {system32}/libwim-15.dll\"";

                var (code, output) = await RunProcess(ownWimlib, args, 120000);
                if (code == 0)
                {
                    Log("wimlib-imagex + libwim-15.dll injetados no boot.wim via wimlib.");
                    return true;
                }
                Log($"wimlib update falhou (código {code}): {output}. Tentando DISM mount/commit...");
            }

            // Fallback: DISM mount/commit
            string mountDir = Path.Combine(Path.GetTempPath(), "KitLugia_WimlibInject");
            try
            {
                if (Directory.Exists(mountDir))
                {
                    try { Directory.Delete(mountDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    await RunDism("dism.exe", "/Cleanup-Mountpoints", 30000);
                }
                Directory.CreateDirectory(mountDir);

                var (mntCode, mntOut) = await RunDism("dism.exe",
                    $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\"", 180000);
                if (mntCode != 0)
                {
                    Log($"Falha ao montar WIM para injetar wimlib: {mntOut}");
                    return false;
                }

                string targetDir = Path.Combine(mountDir, "Windows", "System32");
                Directory.CreateDirectory(targetDir);

                File.Copy(wimlibExe, Path.Combine(targetDir, "wimlib-imagex.exe"), true);
                File.Copy(dllPath, Path.Combine(targetDir, "libwim-15.dll"), true);
                Log("wimlib-imagex.exe + libwim-15.dll copiados para o WIM montado.");

                var (cmtCode, cmtOut) = await RunDism("dism.exe",
                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);

                if (cmtCode == 0)
                {
                    Log("WIM desmontado com commit. wimlib injetado via DISM.");
                    return true;
                }
                Log($"Falha ao commitar WIM: {cmtOut}");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Erro ao injetar wimlib via DISM: {ex.Message}");
                try { await RunDism("dism.exe", $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 120000); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return false;
            }
        }
    }
}
