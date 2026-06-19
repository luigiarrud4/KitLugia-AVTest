using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class EmergencyPreBootManager
    {
        private static string AlpineSourceDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "BootGoodies", "alpine"
        );

        private static string VmlinuzSource => Path.Combine(AlpineSourceDir, "vmlinuz");
        private static string InitrdSource => Path.Combine(AlpineSourceDir, "initrd.gz");
        private static string GrubSource => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "LinuxPreOS", "grubx64.efi"
        );

        private const string ESP_KITLUGIA_DIR = "KitLugia";
        private const string MARKER_FILE = "preboot_complete.txt";
        private const string REFIND_BACKUP = "refind.conf.emergency";
        private const string RECOVERY_ENTRY_NAME = "KitLugia Recovery";

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
                progressCallback?.Invoke(5, "Montando ESP...");
                string espDrive = await MountEspAsync();
                if (espDrive == null)
                    return (false, "Não foi possível montar a partição ESP.");

                if (!File.Exists(VmlinuzSource) || !File.Exists(InitrdSource))
                {
                    await DismountEspAsync(espDrive);
                    return (false, $"Arquivos Alpine não encontrados em {AlpineSourceDir}. Execute build.sh em Linux primeiro.");
                }

                progressCallback?.Invoke(15, "Preparando diretórios no ESP...");
                string kitlugiaEspDir = Path.Combine(espDrive, ESP_KITLUGIA_DIR);
                Directory.CreateDirectory(kitlugiaEspDir);

                progressCallback?.Invoke(25, "Copiando kernel, initramfs e GRUB...");
                File.Copy(VmlinuzSource, Path.Combine(kitlugiaEspDir, "vmlinuz"), true);
                File.Copy(InitrdSource, Path.Combine(kitlugiaEspDir, "initrd.gz"), true);
                string grubSource = GrubSource;
                if (File.Exists(grubSource))
                {
                    File.Copy(grubSource, Path.Combine(kitlugiaEspDir, "grubx64.efi"), true);
                    string grubCfg = GenerateGrubCfg(diskIndex, partitionIndex, shrinkSizeMb, newPartitionLabel, driveLetter);
                    File.WriteAllText(Path.Combine(kitlugiaEspDir, "grub.cfg"), grubCfg);
                }

                progressCallback?.Invoke(50, "Configurando rEFInd para auto-boot...");
                bool refindDeployed = await SetupRefindAutoBoot(espDrive, diskIndex, partitionIndex, shrinkSizeMb, newPartitionLabel, driveLetter);

                await DismountEspAsync(espDrive);

                string bootMode = refindDeployed
                    ? "rEFInd configurado para boot automático do ambiente de recuperação Alpine."
                    : "NOTA: rEFInd não encontrado no ESP. Ao reiniciar, pressione F12 e selecione 'KitLugia Recovery' manualmente.";

                Logger.Log($"[EMERGENCY] Ambiente Alpine implantado no ESP.");
                Logger.Log($"[EMERGENCY] shrink {driveLetter.Replace(":", "")}: em {shrinkSizeMb}MB, nova partição: {newPartitionLabel}");

                return (true,
                    $"Ambiente de recuperação Alpine implantado com sucesso!\n\n" +
                    $"Operação: Reduzir {driveLetter}: em {shrinkSizeMb}MB, criar partição {newPartitionLabel}\n\n" +
                    $"{bootMode}\n\n" +
                    $"⚠️ O sistema será reiniciado. O rEFInd iniciará automaticamente o Alpine Linux.\n" +
                    $"A operação será executada e o Windows será reiniciado em seguida.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[EMERGENCY] ERRO: {ex.Message}");
                return (false, $"Erro ao implantar ambiente de recuperação: {ex.Message}");
            }
        }

        private static async Task<bool> SetupRefindAutoBoot(string espDrive, int diskIndex, int partitionIndex, long shrinkSizeMb, string newPartitionLabel, string driveLetter)
        {
            string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
            string refindConfPath = Path.Combine(msBootDir, "refind.conf");
            string backupPath = Path.Combine(msBootDir, REFIND_BACKUP);
            string grubPath = $"\\{ESP_KITLUGIA_DIR}\\grubx64.efi";

            string emergencyConfig = $@"# KitLugia Emergency Pre-Boot (GRUB chainload) - AUTO BOOT
timeout 5
default_selection ""{RECOVERY_ENTRY_NAME}""
hideui hints,label,badges,banner
dont_scan_files refind_x64.efi,bootmgfw.original.efi,mmx64.efi,grubx64.efi,shellx64.efi,BOOTX64.EFI
dont_scan_volumes ""SYSTEM"",""RECOVERY""
showtools reboot,shutdown

menuentry ""{RECOVERY_ENTRY_NAME}"" {{
    loader {grubPath}
    ostype Linux
}}

menuentry ""Windows"" {{
    loader \EFI\refind\bootmgfw.original.efi
    ostype Windows
}}
";

            if (File.Exists(refindConfPath))
            {
                if (!File.Exists(backupPath))
                    File.Copy(refindConfPath, backupPath);

                await File.WriteAllTextAsync(refindConfPath, emergencyConfig, Encoding.UTF8);
                Logger.Log($"[EMERGENCY] rEFInd config temporaria escrita: auto-boot para {RECOVERY_ENTRY_NAME}");
                return true;
            }

            Logger.Log("[EMERGENCY] rEFInd nao encontrado no ESP. Instalando...");
            var (ok, msg) = BootloaderPackager.DeployRefindToEsp(espDrive, "KitLugia Recovery");
            if (!ok)
            {
                Logger.Log($"[EMERGENCY] Falha ao instalar rEFInd: {msg}");
                return false;
            }

            if (!File.Exists(backupPath))
                File.Copy(refindConfPath, backupPath);

            await File.WriteAllTextAsync(refindConfPath, emergencyConfig, Encoding.UTF8);
            Logger.Log($"[EMERGENCY] rEFInd instalado e configurado para auto-boot: {RECOVERY_ENTRY_NAME}");
            return true;
        }

        private static string GenerateGrubCfg(int diskIndex, int partitionIndex, long shrinkSizeMb, string newPartitionLabel, string driveLetter)
        {
            string dl = driveLetter.Replace(":", "");
            string kernelParams = $"kitlugia_disk=PHYSICALDRIVE{diskIndex} kitlugia_part={partitionIndex} kitlugia_shrink_mb={shrinkSizeMb} kitlugia_label={newPartitionLabel} kitlugia_dl={dl} nomodeset console=tty0";
            return $@"set timeout=1
set default=0

menuentry ""KitLugia Recovery"" {{
    linux /{ESP_KITLUGIA_DIR}/vmlinuz {kernelParams}
    initrd /{ESP_KITLUGIA_DIR}/initrd.gz
}}";
        }

        public static async Task<(bool Success, string Message)> CleanupAsync()
        {
            try
            {
                string espDrive = await MountEspAsync();
                if (espDrive == null)
                    return (false, "Não foi possível montar ESP para limpeza.");

                string kitlugiaEspDir = Path.Combine(espDrive, ESP_KITLUGIA_DIR);
                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string markerPath = Path.Combine(kitlugiaEspDir, MARKER_FILE);
                string refindBackup = Path.Combine(msBootDir, REFIND_BACKUP);
                string refindConf = Path.Combine(msBootDir, "refind.conf");

                bool completed = File.Exists(markerPath);

                if (completed)
                {
                    Logger.Log("[EMERGENCY] Marcador de conclusao encontrado! Operacao foi bem-sucedida.");
                    File.Delete(markerPath);
                }

                if (File.Exists(refindBackup))
                {
                    if (File.Exists(refindConf))
                        File.Delete(refindConf);
                    File.Move(refindBackup, refindConf);
                    Logger.Log("[EMERGENCY] Config original do rEFInd restaurada.");
                }

                if (Directory.Exists(kitlugiaEspDir))
                {
                    Directory.Delete(kitlugiaEspDir, true);
                    Logger.Log("[EMERGENCY] Diretorio KitLugia removido do ESP.");
                }

                await DismountEspAsync(espDrive);

                if (completed)
                    return (true, "Operacao de recuperacao concluida com sucesso! Ambiente limpo.");
                else
                    return (true, "Ambiente de recuperacao removido. Nenhum marcador de conclusao encontrado.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[EMERGENCY] Erro na limpeza: {ex.Message}");
                return (false, $"Erro ao limpar ambiente: {ex.Message}");
            }
        }

        public static async Task<bool> IsPreBootCompleted()
        {
            try
            {
                string espDrive = await MountEspAsync();
                if (espDrive == null) return false;

                string markerPath = Path.Combine(espDrive, ESP_KITLUGIA_DIR, MARKER_FILE);
                bool exists = File.Exists(markerPath);

                await DismountEspAsync(espDrive);
                return exists;
            }
            catch
            {
                return false;
            }
        }

        public static async Task TriggerReboot()
        {
            Logger.Log("[EMERGENCY] Reiniciando para ambiente de recuperacao Alpine...");
            await RunProcessCaptured("shutdown", "/r /t 5 /c \"KitLugia: Reiniciando para Emergency Pre-Boot - Alpine Linux\"", 10000);
        }

        private static async Task<string?> MountEspAsync()
        {
            for (char letter = 'S'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                try
                {
                    if (new DriveInfo(drive).IsReady) continue;
                }
                catch { }

                var (exit, output) = await RunProcessCaptured("mountvol", $"{drive} /S");
                if (exit != 0) continue;

                if (Directory.Exists($"{drive}\\EFI"))
                {
                    Logger.Log($"[EMERGENCY] ESP montada em {drive}");
                    return drive;
                }
            }
            Logger.Log("[EMERGENCY] AVISO: Nenhuma letra disponivel para montar o ESP.");
            return null;
        }

        private static async Task DismountEspAsync(string drive)
        {
            await RunProcessCaptured("mountvol", $"{drive} /D");
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
