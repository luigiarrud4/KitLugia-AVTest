using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class EmergencyBcdBootManager
    {
        private static string AntiXSourceDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "BootGoodies", "antix"
        );

        private static string RefindSourceDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "BootGoodies", "refind"
        );

        private static string AntiXIsoPath => Path.Combine(AntiXSourceDir, "antiX-26_x64-core.iso");
        private static string RefindSource => Path.Combine(RefindSourceDir, "refind_x64.efi");

        private const string PARTITION_LABEL = "KITLUGIA";
        private const string ESP_KITLUGIA_DIR = "KitLugia";
        private const string MARKER_FILE = "preboot_complete.txt";
        private const string BOOTMGFW_ORIGINAL = "bootmgfw.original.efi";

        /// <summary>
        /// Deploys antiX + rEFInd for interactive shrink.
        ///
        /// 1. WinbootManager creates a KITLUGIA partition (shrinks source, creates primary, formats)
        /// 2. Extracts antiX ISO to the KITLUGIA partition (includes linuxfs squashfs)
        /// 3. Hijacks bootmgfw.efi with rEFInd on ESP
        /// 4. rEFInd shows menu: "antiX Live" and "Windows" (timeout 20s, user chooses)
        ///
        /// The user reboots, picks antiX, and runs the shrink manually/automatically.
        /// </summary>
        public static async Task<(bool Success, string Message)> DeployAntiXAsync(
            string sourceDriveLetter,
            int shrinkSizeMb,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                if (!File.Exists(AntiXIsoPath))
                    return (false, $"ISO antiX não encontrada em:\n{AntiXIsoPath}");

                if (!File.Exists(RefindSource))
                    return (false, $"rEFInd não encontrado em:\n{RefindSource}");

                // Step 1: Create KITLUGIA partition via WinbootManager
                progressCallback?.Invoke(10, "Criando partição KITLUGIA...");
                Logger.Log("[BCDBOOT] Criando partição KITLUGIA via WinbootManager...");

                bool partOk = await WinbootManager.CreateBootPartition(
                    sourceDriveLetter,
                    shrinkSizeMb,
                    PARTITION_LABEL,
                    multiIso: false,
                    safeMode: false,
                    isoPath: AntiXIsoPath,
                    progressCallback: (pct, msg) =>
                        progressCallback?.Invoke(10 + pct * 0.3, $"Partição: {msg}")
                );

                if (!partOk)
                {
                    Logger.Log("[BCDBOOT] Falha ao criar partição KITLUGIA.");
                    return (false, "Falha ao criar partição KITLUGIA.\nVerifique se há espaço suficiente (mínimo 2GB).");
                }

                // Find the drive letter of the newly created partition
                progressCallback?.Invoke(40, "Localizando partição KITLUGIA...");
                string? kitlugiaDrive = null;
                var disks = WinbootManager.GetDisks(false, false);
                foreach (var disk in disks)
                {
                    foreach (var part in disk.Partitions)
                    {
                        if (part.Label.Equals(PARTITION_LABEL, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(part.DriveLetter))
                        {
                            kitlugiaDrive = part.DriveLetter.Replace(":", "");
                            break;
                        }
                    }
                    if (kitlugiaDrive != null) break;
                }

                if (string.IsNullOrEmpty(kitlugiaDrive))
                {
                    Logger.Log("[BCDBOOT] Partição KITLUGIA criada mas sem letra detectada.");
                    return (false, "Partição KITLUGIA criada mas não foi possível detectar a letra.");
                }

                Logger.Log($"[BCDBOOT] Partição KITLUGIA em {kitlugiaDrive}:");

                // Step 2: Extract antiX ISO to KITLUGIA partition
                progressCallback?.Invoke(50, "Extraindo antiX ISO para partição KITLUGIA...");
                Logger.Log($"[BCDBOOT] Extraindo ISO para {kitlugiaDrive}:\\...");

                var bootInfo = await WinbootManager.ExtractFiles(AntiXIsoPath, $"{kitlugiaDrive}:\\");

                if (bootInfo == null)
                {
                    Logger.Log("[BCDBOOT] Falha na extração da ISO.");
                    return (false, "Falha ao extrair a ISO antiX para a partição KITLUGIA.");
                }

                Logger.Log("[BCDBOOT] ISO antiX extraída com sucesso.");

                // Step 3: Hijack bootmgfw.efi with rEFInd on ESP
                progressCallback?.Invoke(75, "Instalando rEFInd no ESP...");
                Logger.Log("[BCDBOOT] Instalando rEFInd no ESP...");

                string espDrive = await MountEspAsync();
                if (espDrive == null)
                {
                    Logger.Log("[BCDBOOT] Falha ao montar ESP.");
                    return (false, "Não foi possível montar a partição ESP para instalar o rEFInd.");
                }

                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                string kitlugiaEspDir = Path.Combine(espDrive, "EFI", ESP_KITLUGIA_DIR);
                Directory.CreateDirectory(kitlugiaEspDir);

                // Backup original bootmgfw.efi
                string backupPath = Path.Combine(kitlugiaEspDir, BOOTMGFW_ORIGINAL);
                if (!File.Exists(backupPath) && File.Exists(bootmgfwPath))
                {
                    File.Copy(bootmgfwPath, backupPath, true);
                    Logger.Log($"[BCDBOOT] bootmgfw.efi salvo como {backupPath}");
                }

                // Overwrite bootmgfw.efi with rEFInd
                File.Copy(RefindSource, bootmgfwPath, true);
                Logger.Log("[BCDBOOT] bootmgfw.efi substituído por rEFInd.");

                // Step 4: Write refind.conf pointing to KITLUGIA partition
                progressCallback?.Invoke(85, "Criando configuração do rEFInd...");
                Logger.Log("[BCDBOOT] Escrevendo refind.conf...");

                string refindConfig = $@"
timeout 20
default_selection antiX
hideui banner,badges
dont_scan_files bootmgfw.efi,bootmgfw.original.efi,refind_x64.efi,grubx64.efi,shellx64.efi,BOOTX64.EFI
dont_scan_volumes ""SYSTEM"",""RECOVERY""

menuentry ""antiX Live (KitLugia Shrink)"" {{
    icon     /EFI/refind/icons/os_linux.png
    volume   ""{PARTITION_LABEL}""
    loader   /antiX/vmlinuz
    initrd   /antiX/initrd.gz
    options  ""from=hd nomodeset loglevel=3""
}}

menuentry ""Windows"" {{
    icon     /EFI/refind/icons/os_win.png
    loader   /EFI/KitLugia/bootmgfw.original.efi
    ostype   Windows
}}
";
                File.WriteAllText(Path.Combine(msBootDir, "refind.conf"), refindConfig);

                await DismountEspAsync(espDrive);

                Logger.Log("[BCDBOOT] rEFInd configurado: timeout=20s, 2 opções.");

                return (true,
                    $"antiX + rEFInd implantado com sucesso!\n\n" +
                    $"Partição KITLUGIA criada em {kitlugiaDrive}: ({shrinkSizeMb}MB)\n" +
                    $"ISO antiX extraída para {kitlugiaDrive}:\\\n" +
                    $"rEFInd substituiu o Windows Boot Manager no ESP\n\n" +
                    $"REINICIE e selecione \"antiX Live\" no menu do rEFInd.\n" +
                    $"No antiX, execute o gparted ou ntfsresize manualmente.\n\n" +
                    $"Para restaurar o boot: clique em \"Desinstalar rEFInd\".");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCDBOOT] ERRO: {ex.Message}");
                return (false, $"Erro ao implantar: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores original bootmgfw.efi, removes refind.conf from ESP,
        /// and optionally removes the KITLUGIA partition.
        /// </summary>
        public static async Task<(bool Success, string Message)> CleanupAsync(bool removePartition = false)
        {
            try
            {
                string espDrive = await MountEspAsync();
                if (espDrive == null)
                    return (false, "Não foi possível montar ESP para limpeza.");

                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                string kitlugiaEspDir = Path.Combine(espDrive, ESP_KITLUGIA_DIR);
                string backupPath = Path.Combine(kitlugiaEspDir, BOOTMGFW_ORIGINAL);
                string refindConfPath = Path.Combine(msBootDir, "refind.conf");

                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, bootmgfwPath, true);
                    Logger.Log("[BCDBOOT] bootmgfw.efi restaurado do backup.");
                }
                else
                    Logger.Log("[BCDBOOT] ALERTA: backup não encontrado!");

                if (File.Exists(refindConfPath))
                    File.Delete(refindConfPath);

                if (Directory.Exists(kitlugiaEspDir))
                    Directory.Delete(kitlugiaEspDir, true);

                await DismountEspAsync(espDrive);

                if (removePartition)
                {
                    Logger.Log("[BCDBOOT] Removendo partição KITLUGIA...");
                    WinbootManager.RemoveWinboot();
                }

                return (true, "Boot restaurado. rEFInd removido.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[BCDBOOT] Erro no cleanup: {ex.Message}");
                return (false, $"Erro ao limpar: {ex.Message}");
            }
        }

        public static async Task<(bool Success, string Message)> InstallRefindOnlyAsync()
        {
            try
            {
                string? espDrive = MountEspSync();
                if (espDrive == null)
                    return (false, "Não foi possível montar ESP.");

                if (!File.Exists(RefindSource))
                    return (false, "refind_x64.efi não encontrado.");

                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                string kitlugiaEspDir = Path.Combine(espDrive, "EFI", ESP_KITLUGIA_DIR);
                Directory.CreateDirectory(kitlugiaEspDir);

                string backupPath = Path.Combine(kitlugiaEspDir, BOOTMGFW_ORIGINAL);
                if (!File.Exists(backupPath) && File.Exists(bootmgfwPath))
                    File.Copy(bootmgfwPath, backupPath, true);

                File.Copy(RefindSource, bootmgfwPath, true);

                string refindConfig = $@"
timeout 10
default_selection Windows
dont_scan_files bootmgfw.efi,bootmgfw.original.efi,refind_x64.efi,grubx64.efi,shellx64.efi,BOOTX64.EFI
dont_scan_volumes ""SYSTEM"",""RECOVERY""

menuentry ""Windows"" {{
    icon     /EFI/refind/icons/os_win.png
    loader   /EFI/KitLugia/bootmgfw.original.efi
    ostype   Windows
}}

menuentry ""EFI Shell"" {{
    icon     /EFI/refind/icons/tool_shell.png
    loader   /EFI/refind/shellx64.efi
}}
";
                File.WriteAllText(Path.Combine(msBootDir, "refind.conf"), refindConfig);

                return (true, "rEFInd instalado. Menu com Windows + EFI Shell.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        public static bool IsPreBootCompleted()
        {
            try
            {
                string? espDrive = MountEspSync();
                if (espDrive == null) return false;
                string markerPath = Path.Combine(espDrive, ESP_KITLUGIA_DIR, MARKER_FILE);
                return File.Exists(markerPath);
            }
            catch { return false; }
        }

        public static async Task TriggerReboot()
        {
            await RunProcessCaptured("shutdown", "/r /t 3 /c \"KitLugia: Reinicie e selecione antiX Live no rEFInd\"", 10000);
        }

        private static async Task<string?> MountEspAsync()
        {
            for (char letter = 'S'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                try { if (new DriveInfo(drive).IsReady) continue; } catch { }

                var (exit, _) = await RunProcessCaptured("mountvol", $"{drive} /S");
                if (exit != 0) continue;

                if (Directory.Exists($"{drive}\\EFI"))
                {
                    Logger.Log($"[BCDBOOT] ESP montada em {drive}");
                    return drive;
                }
            }
            return null;
        }

        private static async Task DismountEspAsync(string drive)
        {
            await RunProcessCaptured("mountvol", $"{drive} /D");
        }

        public static string? MountEspSync()
        {
            for (char letter = 'S'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                try { if (new DriveInfo(drive).IsReady) continue; } catch { }

                var psi = new System.Diagnostics.ProcessStartInfo("mountvol", $"{drive} /S")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(10000);

                if (Directory.Exists($"{drive}\\EFI"))
                    return drive;
            }
            return null;
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

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) outputWaitHandle.Set();
                    else outputBuilder.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) errorWaitHandle.Set();
                    else errorBuilder.AppendLine(e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return (-1, "TIMEOUT");
                }

                proc.WaitForExitAsync().Wait(timeoutMs);
                outputWaitHandle.WaitOne(timeoutMs);
                errorWaitHandle.WaitOne(timeoutMs);

                string output = outputBuilder.ToString().Trim();
                string error = errorBuilder.ToString().Trim();
                if (!string.IsNullOrEmpty(error)) output += "\n" + error;

                return (proc.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }
    }
}
