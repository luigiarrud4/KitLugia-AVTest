using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        public static (long TotalBytesFreed, List<string> Log) CleanTemporaryFiles(Action<string>? progressCallback = null)
        {
            Logger.Log("Iniciando varredura de arquivos temporários...");
            long totalBytes = 0;
            var log = new List<string>(10);

            progressCallback?.Invoke("Limpando arquivos temporários nativamente...");


            var tempPaths = new[]
            {
                Environment.GetEnvironmentVariable("TEMP"), // %TEMP% do usuário atual
                Environment.GetEnvironmentVariable("TMP"),  // %TMP% do usuário atual
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") // C:\Windows\Temp
            };

            foreach (var tempPath in tempPaths)
            {
                if (string.IsNullOrEmpty(tempPath) || !Directory.Exists(tempPath))
                {
                    log.Add($"'{tempPath ?? "null"}': Não encontrado. Pulando.");
                    continue;
                }


                var dirName = tempPath.ToLower();
                if (!dirName.Contains("temp") && !dirName.Contains("tmp"))
                {
                    log.Add($"'{tempPath}': Não é um diretório temporário. Pulando por segurança.");
                    continue;
                }

                progressCallback?.Invoke($"Limpando {tempPath}...");
                var result = CleanDirectory(tempPath, $"Temp ({Path.GetFileName(tempPath)})", progressCallback, minimumAge: TimeSpan.FromHours(1));
                totalBytes += result.BytesFreed;
                log.Add(result.Message);
                log.AddRange(result.BlockedFiles);
            }

            string sizeMb = (totalBytes / (1024.0 * 1024.0)).ToString("N2");
            log.Add($"Total limpo: {sizeMb} MB de arquivos temporários");

            return (totalBytes, log);
        }

        public static (long TotalBytesFreed, List<string> Log) CleanWindowsUpdateCache(Action<string>? progressCallback = null)
        {
            Logger.Log("Verificando cache do Windows Update...");

            // Típico: 1-5 entradas de log
            var log = new List<string>(5);
            string wuCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");

            progressCallback?.Invoke("Limpando Cache do Windows Update...");
            var result = CleanDirectory(wuCachePath, "Cache do Windows Update", progressCallback, minimumAge: TimeSpan.FromDays(1));
            log.Add(result.Message);
            log.AddRange(result.BlockedFiles);

            return (result.BytesFreed, log);
        }

        public static (long TotalBytesFreed, List<string> Log) CleanShaderCaches(Action<string>? progressCallback = null)
        {
            Logger.Log("Verificando caches de shader (GPU)...");
            long totalBytes = 0;

            // Típico: 3-10 entradas de log
            var log = new List<string>(10);

            progressCallback?.Invoke("Limpando Cache de Shader NVIDIA...");
            var resultNvidia = CleanDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"NVIDIA\GLCache"), "Cache de Shader NVIDIA", progressCallback, minimumAge: TimeSpan.FromHours(1));
            totalBytes += resultNvidia.BytesFreed;
            log.Add(resultNvidia.Message);
            log.AddRange(resultNvidia.BlockedFiles);

            progressCallback?.Invoke("Limpando Cache de Shader AMD (DX)...");
            var resultAmdDx = CleanDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AMD\DxCache"), "Cache de Shader AMD (DX)", progressCallback, minimumAge: TimeSpan.FromHours(1));
            totalBytes += resultAmdDx.BytesFreed;
            log.Add(resultAmdDx.Message);
            log.AddRange(resultAmdDx.BlockedFiles);

            progressCallback?.Invoke("Limpando Cache de Shader AMD (OpenGL)...");
            var resultAmdGl = CleanDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"AMD\GLCache"), "Cache de Shader AMD (OpenGL)", progressCallback, minimumAge: TimeSpan.FromHours(1));
            totalBytes += resultAmdGl.BytesFreed;
            log.Add(resultAmdGl.Message);
            log.AddRange(resultAmdGl.BlockedFiles);

            return (totalBytes, log);
        }

        public static (long TotalBytesFreed, List<string> Log) RunFullCleanup(Action<string>? progressCallback = null)
        {
            Logger.Log("=== INICIANDO LIMPEZA COMPLETA DO SISTEMA ===");
            long totalBytes = 0;
            var fullLog = new List<string>();

            progressCallback?.Invoke("Limpando Arquivos Temporários...");
            var tempResult = CleanTemporaryFiles(progressCallback);
            totalBytes += tempResult.TotalBytesFreed;
            fullLog.AddRange(tempResult.Log);

            progressCallback?.Invoke("Limpando Cache do Windows Update...");
            var updateResult = CleanWindowsUpdateCache(progressCallback);
            totalBytes += updateResult.TotalBytesFreed;
            fullLog.AddRange(updateResult.Log);

            progressCallback?.Invoke("Limpando Caches de Shader...");
            var shaderResult = CleanShaderCaches(progressCallback);
            totalBytes += shaderResult.TotalBytesFreed;
            fullLog.AddRange(shaderResult.Log);

            string mbFreed = (totalBytes / 1024.0 / 1024.0).ToString("N2");
            Logger.Log($"[RESUMO] Limpeza finalizada. Total liberado: {mbFreed} MB");

            return (totalBytes, fullLog);
        }

        public static void CompactOS()
        {
            Logger.Log("Iniciando CompactOS (Compressão do Sistema)...");
            Logger.LogProcess("compact.exe", "/CompactOS:always");
            // Abre janela externa pois demora muito
            SystemUtils.RunExternalProcess("cmd.exe", "/c compact.exe /CompactOS:always & pause", hidden: false, waitForExit: false);
        }

        /// <summary>
        /// Corrige HD em 100% de uso limpando arquivos de sistema, logs e caches
        /// </summary>
        public static (long TotalBytesFreed, List<string> Log) FixDiskFullUsage(Action<string>? progressCallback = null)
        {
            Logger.Log("=== INICIANDO CORREÇÃO DE HD EM 100% DE USO ===");
            long totalBytes = 0;
            var fullLog = new List<string>();

            // 1. Limpar arquivos temporários
            progressCallback?.Invoke("Limpando Arquivos Temporários...");
            var tempResult = CleanTemporaryFiles(progressCallback);
            totalBytes += tempResult.TotalBytesFreed;
            fullLog.AddRange(tempResult.Log);

            // 2. Limpar cache do Windows Update
            progressCallback?.Invoke("Limpando Cache do Windows Update...");
            var updateResult = CleanWindowsUpdateCache(progressCallback);
            totalBytes += updateResult.TotalBytesFreed;
            fullLog.AddRange(updateResult.Log);

            // 3. Limpar caches de shader
            progressCallback?.Invoke("Limpando Caches de Shader...");
            var shaderResult = CleanShaderCaches(progressCallback);
            totalBytes += shaderResult.TotalBytesFreed;
            fullLog.AddRange(shaderResult.Log);

            // 4. Limpar logs do Windows (CBS, DISM, etc.)
            progressCallback?.Invoke("Limpando Logs do Windows...");
            var logResult = CleanWindowsLogs(progressCallback);
            totalBytes += logResult.BytesFreed;
            fullLog.Add(logResult.Message);

            // 5. Limpar cache de thumbnails
            progressCallback?.Invoke("Limpando Cache de Thumbnails...");
            var thumbResult = CleanThumbnailCache(progressCallback);
            totalBytes += thumbResult.BytesFreed;
            fullLog.Add(thumbResult.Message);

            // 6. Limpar cache de DNS
            progressCallback?.Invoke("Limpando Cache de DNS...");
            string dnsResult = CleanDnsCache();
            fullLog.Add(dnsResult);

            // 7. Limpar Prefetch
            progressCallback?.Invoke("Limpando Prefetch...");
            var prefetchResult = CleanPrefetch(progressCallback);
            totalBytes += prefetchResult.BytesFreed;
            fullLog.Add(prefetchResult.Message);

            // 8. Limpar Recycle Bin
            progressCallback?.Invoke("Limpando Lixeira...");
            var recycleResult = CleanRecycleBin(progressCallback);
            totalBytes += recycleResult.BytesFreed;
            fullLog.Add(recycleResult.Message);

            string gbFreed = (totalBytes / 1024.0 / 1024.0 / 1024.0).ToString("N2");
            Logger.Log($"[RESUMO] Correção de HD finalizada. Total liberado: {gbFreed} GB");

            return (totalBytes, fullLog);
        }

        private static (long BytesFreed, string Message) CleanWindowsLogs(Action<string>? progressCallback = null)
        {
            long totalBytes = 0;
            var blockedFiles = new List<string>();
            var logPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "DISM"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Panther")
            };

            foreach (var path in logPaths)
            {
                progressCallback?.Invoke($"Limpando Logs Windows ({Path.GetFileName(path)})...");
                var result = CleanDirectory(path, $"Logs Windows ({Path.GetFileName(path)})", progressCallback);
                totalBytes += result.BytesFreed;
                blockedFiles.AddRange(result.BlockedFiles);
            }

            string sizeMb = (totalBytes / (1024.0 * 1024.0)).ToString("N2");
            var message = $"Logs do Windows: {sizeMb} MB liberados";
            if (blockedFiles.Count > 0)
            {
                message += $" ({blockedFiles.Count} arquivos travados)";
            }
            return (totalBytes, message);
        }

        private static (long BytesFreed, string Message) CleanThumbnailCache(Action<string>? progressCallback = null)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer");
            progressCallback?.Invoke("Limpando Cache de Thumbnails...");
            var result = CleanDirectory(path, "Cache de Thumbnails", progressCallback);
            string sizeMb = (result.BytesFreed / (1024.0 * 1024.0)).ToString("N2");
            var message = $"Cache de Thumbnails: {sizeMb} MB liberados";
            if (result.BlockedFiles.Count > 0)
            {
                message += $" ({result.BlockedFiles.Count} arquivos travados)";
            }
            return (result.BytesFreed, message);
        }

        private static string CleanDnsCache()
        {
            try
            {
                SystemUtils.RunExternalProcess("ipconfig.exe", "/flushdns", true);
                return "Cache DNS limpo com sucesso";
            }
            catch (Exception ex)
            {
                return $"Erro ao limpar DNS: {ex.Message}";
            }
        }

        private static (long BytesFreed, string Message) CleanPrefetch(Action<string>? progressCallback = null)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            progressCallback?.Invoke("Limpando Prefetch...");
            var result = CleanDirectory(path, "Prefetch", progressCallback);
            string sizeMb = (result.BytesFreed / (1024.0 * 1024.0)).ToString("N2");
            var message = $"Prefetch: {sizeMb} MB liberados";
            if (result.BlockedFiles.Count > 0)
            {
                message += $" ({result.BlockedFiles.Count} arquivos travados)";
            }
            return (result.BytesFreed, message);
        }

        public static (long BytesFreed, string Message) CleanRecycleBin(Action<string>? progressCallback = null)
        {
            try
            {
                progressCallback?.Invoke("Calculando tamanho da Lixeira...");
                long beforeBytes = GetRecycleBinSize();

                progressCallback?.Invoke("Esvaziando Lixeira pelo Shell do Windows...");
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                if (hr != 0)
                {
                    return (0, $"Erro ao limpar lixeira: código 0x{hr:X8}");
                }

                long afterBytes = GetRecycleBinSize();
                long freedBytes = Math.Max(0, beforeBytes - afterBytes);
                string sizeMb = (freedBytes / (1024.0 * 1024.0)).ToString("N2");
                return (freedBytes, $"Lixeira: {sizeMb} MB liberados");
            }
            catch (Exception ex)
            {
                return (0, $"Erro ao limpar lixeira: {ex.Message}");
            }
        }

        private static long GetRecycleBinSize()
        {
            long totalBytes = 0;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                    var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                    if (Directory.Exists(recyclePath))
                    {
                        totalBytes += GetDirectorySizeSafe(recyclePath);
                    }
                }
                catch { }
            }
            return totalBytes;
        }

        private static long GetDirectorySizeSafe(string path)
        {
            long size = 0;
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(filePath).Length; } catch { }
                }
            }
            catch { }
            return size;
        }

        /// <summary>
        /// Helper para limpar diretórios.
        /// </summary>
        public static (long BytesFreed, int FilesDeleted, string Message, List<string> BlockedFiles) CleanDirectory(
            string path,
            string name,
            Action<string>? progressCallback = null,
            TimeSpan? minimumAge = null)
        {
            if (!Directory.Exists(path))
            {
                return (0, 0, $"'{name}': Não encontrado. Pulando.", new List<string>());
            }

            long totalSize = 0;
            int fileCount = 0;
            int errorCount = 0;
            int processedCount = 0;
            var blockedFiles = new List<string>();
            int blockedCount = 0;
            int skippedFreshCount = 0;
            const int MAX_BLOCKED_TO_SHOW = 10;
            const int PROGRESS_INTERVAL = 250;
            const int YIELD_INTERVAL = 200;
            const int MAX_DEPTH = 50; // Limita profundidade para evitar loops infinitos
            int currentDepth = 0;
            DateTime? newestAllowedWriteTime = minimumAge.HasValue
                ? DateTime.Now.Subtract(minimumAge.Value)
                : null;

            void Traverse(string dirPath, int depth)
            {
                if (depth > MAX_DEPTH)
                {
                    Logger.Log($"[LIMPEZA] Profundidade máxima atingida em: {dirPath}");
                    return;
                }

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(dirPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var fileInfo = new FileInfo(filePath);
                            if (newestAllowedWriteTime.HasValue && fileInfo.LastWriteTime > newestAllowedWriteTime.Value)
                            {
                                skippedFreshCount++;
                                continue;
                            }

                            long fileSize = fileInfo.Length;
                            if ((fileInfo.Attributes & FileAttributes.ReadOnly) != 0)
                            {
                                fileInfo.Attributes &= ~FileAttributes.ReadOnly;
                            }

                            File.Delete(filePath);
                            totalSize += fileSize;
                            fileCount++;
                            processedCount++;

                            if (progressCallback != null && processedCount % PROGRESS_INTERVAL == 0)
                            {
                                progressCallback($"Limpando {name}: {fileCount} arquivos...");
                            }

                            if (processedCount % YIELD_INTERVAL == 0)
                            {
                                Thread.Sleep(1);
                            }
                        }
                        catch (UnauthorizedAccessException)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"🔒 Sem permissão: {Path.GetFileName(filePath)}");
                            }
                        }
                        catch (IOException)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"🔒 Em uso: {Path.GetFileName(filePath)}");
                            }
                        }
                        catch (Exception)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"⚠️ Erro: {Path.GetFileName(filePath)}");
                            }
                        }
                    }

                    foreach (var subDirPath in Directory.EnumerateDirectories(dirPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(subDirPath);
                            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            Traverse(subDirPath, depth + 1);
                            Directory.Delete(subDirPath, recursive: false);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"🔒 Diretório sem permissão: {Path.GetFileName(subDirPath)}");
                            }
                        }
                        catch (IOException)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"🔒 Diretório em uso: {Path.GetFileName(subDirPath)}");
                            }
                        }
                        catch (Exception)
                        {
                            errorCount++;
                            blockedCount++;
                            if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                            {
                                blockedFiles.Add($"⚠️ Erro diretório: {Path.GetFileName(subDirPath)}");
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    errorCount++;
                    blockedCount++;
                    if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                    {
                        blockedFiles.Add($"🔒 Acesso negado: {Path.GetFileName(dirPath)}");
                    }
                }
                catch (IOException)
                {
                    errorCount++;
                    blockedCount++;
                    if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                    {
                        blockedFiles.Add($"🔒 IO erro: {Path.GetFileName(dirPath)}");
                    }
                }
                catch (Exception)
                {
                    errorCount++;
                    blockedCount++;
                    if (blockedCount <= MAX_BLOCKED_TO_SHOW)
                    {
                        blockedFiles.Add($"⚠️ Erro: {Path.GetFileName(dirPath)}");
                    }
                }
            }

            try
            {
                progressCallback?.Invoke($"Iniciando limpeza de {name}...");
                Traverse(path, currentDepth);
            }
            catch (Exception ex)
            {
                Logger.LogError("Limpeza", $"Erro ao limpar '{name}': {ex.Message}");
                progressCallback?.Invoke($"⚠️ Erro ao acessar '{name}': {ex.Message}");
            }

            if (blockedCount > MAX_BLOCKED_TO_SHOW)
            {
                blockedFiles.Add($"... e mais {blockedCount - MAX_BLOCKED_TO_SHOW} arquivos/diretórios travados");
                progressCallback?.Invoke($"... e mais {blockedCount - MAX_BLOCKED_TO_SHOW} arquivos/diretórios travados");
            }

            string msg;
            if (fileCount > 0)
            {
                string sizeMb = (totalSize / (1024.0 * 1024.0)).ToString("N2");
                msg = $"'{name}': {fileCount} arquivos removidos ({sizeMb} MB liberados).";
                if (errorCount > 0)
                {
                    msg += $" ({errorCount} arquivos/pastas ignorados por permissão ou uso)";
                }
                if (skippedFreshCount > 0)
                {
                    msg += $" ({skippedFreshCount} arquivos recentes preservados)";
                }
                Logger.Log($"[LIMPEZA] {msg}");
            }
            else if (errorCount > 0)
            {
                msg = $"'{name}': {errorCount} arquivos/pastas ignorados por permissão ou uso.";
                Logger.Log($"[LIMPEZA] {msg}");
            }
            else
            {
                msg = $"'{name}': Nenhuma limpeza necessária.";
                Logger.Log($"[LIMPEZA] {name}: Nada a limpar.");
            }

            return (totalSize, fileCount, msg, blockedFiles);
        }
    }
}
