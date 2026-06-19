using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class EmergencyWinREManager
    {
        private const string WINRE_WORK_DIR = @"C:\KitLugia\WinRE";
        private const string WINRE_SOURCE = @"C:\Windows\System32\Recovery\winre.wim";
        private const string MOUNT_DIR = @"C:\KitLugia\WinRE\Mount";
        private const string MARKER_FILE = "winre_complete.txt";
        private const string NEW_PARTITION_LABEL = "KITLUGIA";

        public static async Task<(bool Success, string Message)> DeployAsync(
            int diskIndex,
            int partitionIndex,
            long partitionSizeBytes,
            string driveLetter,
            long shrinkSizeMb,
            string newPartitionLabel,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                progressCallback?.Invoke(5, "Preparando diretório de trabalho...");
                Directory.CreateDirectory(WINRE_WORK_DIR);
                Directory.CreateDirectory(MOUNT_DIR);

                string targetWim = Path.Combine(WINRE_WORK_DIR, "winre.wim");

                progressCallback?.Invoke(10, "Copiando winre.wim original...");
                if (!File.Exists(WINRE_SOURCE))
                    return (false, $"winre.wim não encontrado em {WINRE_SOURCE}");
                File.Copy(WINRE_SOURCE, targetWim, true);

                progressCallback?.Invoke(20, "Montando imagem WIM via DISM...");
                var (exitMount, _) = await RunDismAsync(
                    $"/Mount-Image /ImageFile:\"{targetWim}\" /Index:1 /MountDir:\"{MOUNT_DIR}\"");
                if (exitMount != 0)
                    return (false, $"Falha ao montar WIM. Código: {exitMount}");

                try
                {
                    progressCallback?.Invoke(40, "Injetando winpeshl.ini...");
                    string system32Dir = Path.Combine(MOUNT_DIR, "Windows", "System32");
                    string winpeshlPath = Path.Combine(system32Dir, "winpeshl.ini");
                    string diskpartScriptPath = Path.Combine(system32Dir, "diskpart_kitlugia.txt");
                    string markerPath = Path.Combine(MOUNT_DIR, MARKER_FILE);

                    string winpeshlContent = $@"[LaunchApps]
%SystemRoot%\System32\diskpart.exe /s %SystemRoot%\System32\diskpart_kitlugia.txt
%SystemRoot%\System32\shutdown.exe /r /t 5
";
                    File.WriteAllText(winpeshlPath, winpeshlContent, Encoding.Unicode);

                    progressCallback?.Invoke(50, "Gerando script diskpart...");
                    string diskpartContent = $@"select disk {diskIndex}
select partition {partitionIndex}
shrink desired={shrinkSizeMb}
create partition primary
format fs=ntfs quick label={NEW_PARTITION_LABEL}
assign letter=K
exit
";
                    File.WriteAllText(diskpartScriptPath, diskpartContent, Encoding.ASCII);

                    File.WriteAllText(markerPath, "OK", Encoding.ASCII);

                    progressCallback?.Invoke(70, "Desmontando WIM com commit...");
                    var (exitDismount, outputDismount) = await RunDismAsync(
                        $"/Unmount-Image /MountDir:\"{MOUNT_DIR}\" /Commit");
                    if (exitDismount != 0)
                        return (false, $"Falha ao commitar WIM: {outputDismount}");
                }
                catch
                {
                    await RunDismAsync($"/Unmount-Image /MountDir:\"{MOUNT_DIR}\" /Discard");
                    throw;
                }

                progressCallback?.Invoke(85, "Registrando WinRE modificado via reagentc...");
                var (exitReagent, outputReagent) = await RunProcessCaptured(
                    "reagentc", $"/setreimage /path \"{WINRE_WORK_DIR}\" /target \"{WINRE_SOURCE}\"");
                if (exitReagent != 0)
                    return (false, $"Falha ao registrar WinRE: {outputReagent}");

                progressCallback?.Invoke(95, "Programando boot único no WinRE...");
                var (exitBoot, _) = await RunProcessCaptured("reagentc", "/boottore");
                if (exitBoot != 0)
                {
                    await RunProcessCaptured("reagentc", $"/setreimage /target \"{WINRE_SOURCE}\"");
                    return (false, "Falha ao programar boot no WinRE (reagentc /boottore).");
                }

                string dl = driveLetter.Replace(":", "");
                Logger.Log($"[EMERGENCY] WinRE modificado implantado em {targetWim}");
                Logger.Log($"[EMERGENCY] shrink {dl}: em {shrinkSizeMb}MB, nova partição: {NEW_PARTITION_LABEL}");

                return (true,
                    $"Ambiente WinRE modificado com sucesso!\n\n" +
                    $"Operação: Reduzir {dl}: em {shrinkSizeMb}MB, criar partição {NEW_PARTITION_LABEL}\n\n" +
                    $"⚠️ O sistema será reiniciado para o Windows Recovery Environment.\n" +
                    $"O WinRE executará o diskpart automaticamente e reiniciará o Windows em seguida.\n" +
                    $"Após o reinício, execute o KitLugia novamente para finalizar.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[EMERGENCY] ERRO: {ex.Message}");
                return (false, $"Erro ao implantar WinRE: {ex.Message}");
            }
        }

        public static async Task<(bool Success, string Message)> CleanupAsync()
        {
            try
            {
                string targetWim = Path.Combine(WINRE_WORK_DIR, "winre.wim");

                if (File.Exists(targetWim))
                {
                    Logger.Log("[EMERGENCY] Restaurando WinRE original...");
                    var (exit, output) = await RunProcessCaptured(
                        "reagentc", $"/setreimage /target \"{WINRE_SOURCE}\"");
                    if (exit != 0)
                        Logger.Log($"[EMERGENCY] Aviso: falha ao restaurar WinRE original: {output}");
                    else
                        Logger.Log("[EMERGENCY] WinRE original restaurado.");
                }

                if (Directory.Exists(WINRE_WORK_DIR))
                {
                    try { Directory.Delete(WINRE_WORK_DIR, true); }
                    catch { Logger.Log("[EMERGENCY] Aviso: não foi possível limpar diretório WinRE."); }
                }

                Logger.Log("[EMERGENCY] Cleanup concluído.");
                return (true, "WinRE restaurado e arquivos temporários removidos.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[EMERGENCY] Erro no cleanup: {ex.Message}");
                return (false, $"Erro na limpeza: {ex.Message}");
            }
        }

        public static async Task<bool> IsPreBootCompleted()
        {
            try
            {
                string result = await Task.Run(() =>
                {
                    using var ps = new System.Diagnostics.Process();
                    ps.StartInfo.FileName = "powershell";
                    ps.StartInfo.Arguments =
                        "-Command \"Get-Partition | Where-Object { $_.PartitionNumber -gt 0 } | " +
                        "Get-Volume | Where-Object { $_.FileSystemLabel -eq '" + NEW_PARTITION_LABEL + "' } | " +
                        "Select-Object -First 1 | Format-Table -AutoSize\"";
                    ps.StartInfo.UseShellExecute = false;
                    ps.StartInfo.RedirectStandardOutput = true;
                    ps.StartInfo.CreateNoWindow = true;
                    ps.Start();
                    string output = ps.StandardOutput.ReadToEnd();
                    ps.WaitForExit(10000);
                    return output;
                });

                bool found = result.Trim().Length > 0;
                Logger.Log($"[EMERGENCY] Verificação de partição {NEW_PARTITION_LABEL}: {(found ? "encontrada" : "não encontrada")}");
                return found;
            }
            catch (Exception ex)
            {
                Logger.Log($"[EMERGENCY] Erro ao verificar conclusão: {ex.Message}");
                return false;
            }
        }

        public static async Task TriggerReboot()
        {
            Logger.Log("[EMERGENCY] Reiniciando para WinRE...");
            await RunProcessCaptured("shutdown", "/r /t 5 /c \"KitLugia: Reiniciando para Windows Recovery Environment\"", 10000);
        }

        private static async Task<(int ExitCode, string Output)> RunDismAsync(string args)
        {
            return await RunProcessCaptured("dism", args, 300000);
        }

        private static async Task<(int ExitCode, string Output)> RunProcessCaptured(string filename, string args, int timeoutMs = 60000)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = filename;
                proc.StartInfo.Arguments = args;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.CreateNoWindow = true;

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                using var outputWaitHandle = new System.Threading.ManualResetEvent(false);
                using var errorWaitHandle = new System.Threading.ManualResetEvent(false);

                proc.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) outputWaitHandle.Set();
                    else outputBuilder.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null) errorWaitHandle.Set();
                    else errorBuilder.AppendLine(e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (timeoutMs > 0)
                {
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        return (-1, "TIMEOUT: Processo excedeu o limite de tempo.");
                    }
                }
                else
                {
                    proc.WaitForExit();
                }

                proc.WaitForExitAsync().Wait(timeoutMs > 0 ? timeoutMs : 30000);
                outputWaitHandle.WaitOne(timeoutMs > 0 ? timeoutMs : 30000);
                errorWaitHandle.WaitOne(timeoutMs > 0 ? timeoutMs : 30000);

                string output = outputBuilder.ToString().Trim();
                string error = errorBuilder.ToString().Trim();

                if (!string.IsNullOrEmpty(error))
                    output += "\n" + error;

                return (proc.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }
    }
}
