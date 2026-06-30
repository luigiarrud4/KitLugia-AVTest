using System;
using System.IO;
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

        private static string AntiXIsoPath => Path.Combine(AntiXSourceDir, "antiX-26_x64-core.iso");

        private const string PARTITION_LABEL = "KITLUGIA";

        public static async Task<(bool Success, string Message)> DeployAntiXAsync(
            string sourceDriveLetter,
            int shrinkSizeMb,
            Action<double, string>? progressCallback = null)
        {
            try
            {
                if (!File.Exists(AntiXIsoPath))
                    return (false, $"ISO antiX não encontrada em:\n{AntiXIsoPath}\n\nBaixe manualmente e coloque no diretório Resources\\BootGoodies\\antix\\.");

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

                // Step 3-4: Install rEFInd on ESP with antiX config
                progressCallback?.Invoke(75, "Instalando rEFInd no ESP...");
                Logger.Log("[BCDBOOT] Instalando rEFInd no ESP...");

                string espDrive = await RefindManager.MountEspAsync();
                if (espDrive == null)
                {
                    Logger.Log("[BCDBOOT] Falha ao montar ESP.");
                    return (false, "Não foi possível montar a partição ESP para instalar o rEFInd.");
                }

                string msBootDir = Path.Combine(espDrive, "EFI", "Microsoft", "Boot");
                string kitlugiaEspDir = Path.Combine(espDrive, "KitLugia");
                Directory.CreateDirectory(kitlugiaEspDir);

                string backupPath = Path.Combine(kitlugiaEspDir, "bootmgfw.original.efi");
                string bootmgfwPath = Path.Combine(msBootDir, "bootmgfw.efi");
                if (!File.Exists(backupPath) && File.Exists(bootmgfwPath))
                {
                    File.Copy(bootmgfwPath, backupPath, true);
                    Logger.Log("[BCDBOOT] bootmgfw.efi salvo como backup.");
                }

                string refindSource = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Resources", "BootGoodies", "refind", "refind_x64.efi");
                File.Copy(refindSource, bootmgfwPath, true);
                Logger.Log("[BCDBOOT] bootmgfw.efi substituído por rEFInd.");

                // Write antiX-specific refind.conf
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

                await RefindManager.DismountEspAsync(espDrive);

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

        public static async Task<(bool Success, string Message)> CleanupAsync(bool removePartition = false)
        {
            try
            {
                var refindResult = await RefindManager.CleanupRefindAsync();
                if (!refindResult.Success)
                    return refindResult;

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
    }
}
