using System;
using System.IO;
using System.Text;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class BootloaderPackager
    {
        private static string BundledRefindPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "BootGoodies", "refind"
        );

        private static string ShimPath => Path.Combine(BundledRefindPath, "shimx64.efi");
        private static string MokPath => Path.Combine(BundledRefindPath, "mmx64.efi");
        private static string ThemePath => Path.Combine(BundledRefindPath, "themes", "regular");

        public static (bool Success, string Message) DeployRefindToPartition(string targetDrive)
        {
            try
            {
                string sourceEfi = Path.Combine(BundledRefindPath, "refind_x64.efi");
                if (!File.Exists(sourceEfi))
                    return (false, $"refind_x64.efi não encontrado em {sourceEfi}.");

                string refindDir = Path.Combine(targetDrive, "EFI", "refind");
                string driversDir = Path.Combine(refindDir, "drivers_x64");
                Directory.CreateDirectory(driversDir);

                File.Copy(sourceEfi, Path.Combine(refindDir, "refind_x64.efi"), true);

                string sourceDriver = Path.Combine(BundledRefindPath, "drivers_x64", "ext4_x64.efi");
                if (File.Exists(sourceDriver))
                    File.Copy(sourceDriver, Path.Combine(driversDir, "ext4_x64.efi"), true);

                string configPath = Path.Combine(refindDir, "refind.conf");
                if (!File.Exists(configPath))
                    File.WriteAllText(configPath, GetRefindConfig(), Encoding.UTF8);

                Logger.Log($"[BOOT] rEFInd implantado em {refindDir}");
                return (true, "rEFInd configurado como gerenciador de boot UEFI.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static (bool Success, string Message) DeployRefindToEsp(string espDrive, string linuxDescription = "Linux", bool useShim = true)
        {
            try
            {
                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string refindDir = Path.Combine(espDrive, "EFI", "refind");
                Directory.CreateDirectory(refindDir);

                string sourceEfi = Path.Combine(BundledRefindPath, "refind_x64.efi");
                if (!File.Exists(sourceEfi))
                    return (false, $"refind_x64.efi não encontrado em {sourceEfi}.");

                // Back up original Windows Boot Manager
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                string bootmgfwBackup = Path.Combine(refindDir, "bootmgfw.original.efi");
                if (File.Exists(bootmgfwPath) && !File.Exists(bootmgfwBackup))
                    File.Copy(bootmgfwPath, bootmgfwBackup);

                if (useShim && File.Exists(ShimPath))
                {
                    // Shim mode: Shim -> rEFInd (as grubx64.efi) -> menu -> Windows (original WBM)
                    File.Copy(ShimPath, bootmgfwPath, true);
                    File.Copy(sourceEfi, Path.Combine(msBootDir, "grubx64.efi"), true);

                    if (File.Exists(MokPath))
                        File.Copy(MokPath, Path.Combine(msBootDir, "mmx64.efi"), true);

                    var grub64Backup = Path.Combine(refindDir, "grubx64.efi");
                    if (!File.Exists(grub64Backup))
                        File.Copy(sourceEfi, grub64Backup);
                }
                else
                {
                    // Direct mode: rEFInd replaces bootmgfw.efi directly
                    File.Copy(sourceEfi, bootmgfwPath, true);
                }

                // Deploy rEFInd support files to \EFI\refind\
                string driversDir = Path.Combine(refindDir, "drivers_x64");
                Directory.CreateDirectory(driversDir);

                string refindEspEfi = Path.Combine(refindDir, "refind_x64.efi");
                if (!File.Exists(refindEspEfi))
                    File.Copy(sourceEfi, refindEspEfi);

                string sourceDriver = Path.Combine(BundledRefindPath, "drivers_x64", "ext4_x64.efi");
                if (File.Exists(sourceDriver))
                    File.Copy(sourceDriver, Path.Combine(driversDir, "ext4_x64.efi"), true);

                // Deploy UEFI Shell for recovery
                string shellSource = Path.Combine(BundledRefindPath, "shellx64.efi");
                string shellDest = Path.Combine(refindDir, "shellx64.efi");
                if (File.Exists(shellSource))
                    File.Copy(shellSource, shellDest, true);

                // Deploy theme
                DeployThemeToEsp(espDrive);

                // Write config
                string refindConfig = GetEspRefindConfig(linuxDescription);
                string configPath1 = Path.Combine(refindDir, "refind.conf");
                File.WriteAllText(configPath1, refindConfig, Encoding.UTF8);

                string configPath2 = Path.Combine(msBootDir, "refind.conf");
                File.WriteAllText(configPath2, refindConfig, Encoding.UTF8);

                string mode = useShim ? "Shim + rEFInd" : "rEFInd (direct)";
                Logger.Log($"[BOOT] {mode} implantado no ESP ({espDrive})");
                return (true, $"{mode} configurado no ESP UEFI.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static void DeployThemeToEsp(string espDrive)
        {
            if (!Directory.Exists(ThemePath))
            {
                Logger.Log("[BOOT] Tema rEFInd não encontrado, pulando.");
                return;
            }

            string themeDir = Path.Combine(espDrive, "EFI", "refind", "themes", "regular");
            if (Directory.Exists(themeDir))
            {
                Logger.Log("[BOOT] Tema já existe no ESP, pulando.");
                return;
            }

            string themeConfigSrc = Path.Combine(ThemePath, "theme.conf");
            if (!File.Exists(themeConfigSrc))
            {
                Logger.Log("[BOOT] theme.conf não encontrado, pulando tema.");
                return;
            }

            // Copy icons and fonts
            string iconsSrc = Path.Combine(ThemePath, "icons");
            if (Directory.Exists(iconsSrc))
                CopyDirectory(iconsSrc, Path.Combine(themeDir, "icons"));

            string fontsSrc = Path.Combine(ThemePath, "fonts");
            if (Directory.Exists(fontsSrc))
                CopyDirectory(fontsSrc, Path.Combine(themeDir, "fonts"));

            // Write theme.conf with corrected paths (relative to binary at \EFI\Microsoft\Boot\)
            string themeConfig = File.ReadAllText(themeConfigSrc, Encoding.UTF8);
            themeConfig = themeConfig.Replace(
                "themes/refind-theme-regular/",
                "../refind/themes/regular/"
            );
            File.WriteAllText(Path.Combine(themeDir, "theme.conf"), themeConfig, Encoding.UTF8);

            Logger.Log($"[BOOT] Tema rEFInd implantado em {themeDir}");
        }

        private static string? MountEsp()
        {
            for (char c = 'S'; c <= 'Z'; c++)
            {
                string drive = $"{c}:";
                try { if (new DriveInfo(drive).IsReady) continue; } catch { }

                using var proc = new System.Diagnostics.Process();
                proc.StartInfo.FileName = "mountvol";
                proc.StartInfo.Arguments = $"{drive} /S";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();
                if (!proc.WaitForExit(10000)) continue;

                if (Directory.Exists($"{drive}\\EFI"))
                {
                    Logger.Log($"[BOOT] ESP montada em {drive}");
                    return drive;
                }
            }
            Logger.Log("[BOOT] Nenhuma letra disponivel para montar o ESP.");
            return null;
        }

        public static (bool Success, string Message) RestoreEspBoot()
        {
            try
            {
                string? espRoot = MountEsp();
                if (espRoot == null)
                    return (false, "Não foi possível montar a partição ESP.");

                string backupPath = Path.Combine(espRoot, "EFI", "refind", "bootmgfw.original.efi");
                string bootmgfwPath = Path.Combine(espRoot, "EFI", "Microsoft", "Boot", "bootmgfw.efi");

                if (!File.Exists(backupPath))
                {
                    System.Diagnostics.Process.Start("mountvol", $"{espRoot} /D")?.WaitForExit(3000);
                    return (false, "Backup do Windows Boot Manager não encontrado no ESP.");
                }

                if (File.Exists(bootmgfwPath))
                    File.Delete(bootmgfwPath);
                File.Copy(backupPath, bootmgfwPath);

                string refindDir = Path.Combine(espRoot, "EFI", "refind");
                if (Directory.Exists(refindDir))
                    Directory.Delete(refindDir, true);

                string msBootConf = Path.Combine(espRoot, "EFI", "Microsoft", "Boot", "refind.conf");
                if (File.Exists(msBootConf))
                    File.Delete(msBootConf);

                // Clean up Shim artifacts if present
                string shimGrub = Path.Combine(espRoot, "EFI", "Microsoft", "Boot", "grubx64.efi");
                if (File.Exists(shimGrub))
                    File.Delete(shimGrub);

                // Clean up UEFI Shell if present
                string shellPath = Path.Combine(espRoot, "EFI", "refind", "shellx64.efi");
                if (File.Exists(shellPath))
                    File.Delete(shellPath);

                System.Diagnostics.Process.Start("mountvol", $"{espRoot} /D")?.WaitForExit(3000);

                Logger.Log("[BOOT] Windows Boot Manager restaurado no ESP.");
                return (true, "Windows Boot Manager restaurado. rEFInd removido do ESP.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static string GetRefindConfig()
        {
            return @"# Configuracao KitLugia - rEFInd Boot Manager
timeout 10
default_selection ""Linux""
dont_scan_volumes ""ESP"",""SYSTEM"",""RECOVERY""
showtools shell,reboot,shutdown,firmware
";
        }

        private static string GetEspRefindConfig(string linuxName = "Linux")
        {
            string safeName = linuxName.Replace("\"", "'");
            return $@"# KitLugia - rEFInd no ESP (UEFI Boot Manager)
timeout 20
hideui hints,label,badges
dont_scan_files refind_x64.efi,bootmgfw.original.efi,mmx64.efi,grubx64.efi,shellx64.efi,BOOTX64.EFI
dont_scan_volumes ""SYSTEM"",""RECOVERY""
dont_scan_firmware ""Windows""
showtools shell,reboot,shutdown,firmware

# Theme regular
icons_dir ../refind/themes/regular/icons/128-48
big_icon_size 128
small_icon_size 48
banner ../refind/themes/regular/icons/128-48/bg.png
selection_big ../refind/themes/regular/icons/128-48/selection-big.png
selection_small ../refind/themes/regular/icons/128-48/selection-small.png
font ../refind/themes/regular/fonts/source-code-pro-extralight-14.png

menuentry ""Windows"" {{
    loader \EFI\refind\bootmgfw.original.efi
    ostype Windows
}}

menuentry ""EFI Shell"" {{
    loader \EFI\refind\shellx64.efi
    icon \EFI\refind\themes\regular\icons\128-48\func_48.png
    ostype Linux
}}

menuentry ""{safeName} (KitLugia)"" {{
    volume ""KITLUGIA""
    loader \EFI\boot\grubx64.efi
    ostype Linux
}}

menuentry ""{safeName} (Fallback)"" {{
    volume ""KITLUGIA""
    loader \EFI\BOOT\BOOTX64.EFI
    ostype Linux
}}
";
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string target = Path.Combine(targetDir, Path.GetFileName(file));
                try { File.Copy(file, target, true); } catch { }
            }
            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string target = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, target);
            }
        }
    }
}
