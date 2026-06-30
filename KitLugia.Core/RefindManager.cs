using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class RefindManager
    {
        private static string RefindSourceDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "BootGoodies", "refind"
        );

        private static string RefindSource => Path.Combine(RefindSourceDir, "refind_x64.efi");

        private const string ESP_KITLUGIA_DIR = "KitLugia";
        private const string BOOTMGFW_ORIGINAL = "bootmgfw.original.efi";
        private const string MARKER_FILE = "preboot_complete.txt";

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

        public static async Task<(bool Success, string Message)> CleanupRefindAsync()
        {
            try
            {
                string espDrive = await MountEspAsync();
                if (espDrive == null)
                    return (false, "Não foi possível montar ESP para limpeza.");

                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                string kitlugiaEspDir = Path.Combine(espDrive, "EFI", ESP_KITLUGIA_DIR);
                string backupPath = Path.Combine(kitlugiaEspDir, BOOTMGFW_ORIGINAL);
                string refindConfPath = Path.Combine(msBootDir, "refind.conf");

                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, bootmgfwPath, true);
                    Logger.Log("[REFIND] bootmgfw.efi restaurado do backup.");
                }
                else
                    Logger.Log("[REFIND] ALERTA: backup não encontrado!");

                if (File.Exists(refindConfPath))
                    File.Delete(refindConfPath);

                if (Directory.Exists(kitlugiaEspDir))
                    Directory.Delete(kitlugiaEspDir, true);

                await DismountEspAsync(espDrive);

                return (true, "Boot restaurado. rEFInd removido.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[REFIND] Erro no cleanup: {ex.Message}");
                return (false, $"Erro ao limpar: {ex.Message}");
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

        internal static async Task<string?> MountEspAsync()
        {
            for (char letter = 'S'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                try { if (new DriveInfo(drive).IsReady) continue; } catch { }

                var (exit, _) = await RunProcessCaptured("mountvol", $"{drive} /S");
                if (exit != 0) continue;

                if (Directory.Exists($"{drive}\\EFI"))
                {
                    Logger.Log($"[REFIND] ESP montada em {drive}");
                    return drive;
                }
            }
            return null;
        }

        internal static async Task DismountEspAsync(string drive)
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
