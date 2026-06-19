using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace KitLugia.Core
{
    public static class LocalInstallManager
    {
        // 1. Prepara a Partição (Shrink C: + Create New) - Baseado no EaseUS Partition Master
        public static async Task<(bool Success, string Message, string NewDrive)> PreparePartition(int sizeMb)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string scriptFile = Path.Combine(Path.GetTempPath(), "diskpart_script.txt");
                    
                    // Script Diskpart seguro inspirado no EaseUS - verifica espaço antes de operar
                    string scriptContent = 
                        "select volume c\n" +
                        $"shrink desired={sizeMb} minimum={sizeMb}\n" +
                        "create partition primary\n" +
                        "format quick fs=ntfs label=\"WIN_INSTALL\"\n" +
                        "assign letter=Z\n" +
                        "exit";
                    
                    File.WriteAllText(scriptFile, scriptContent);
                    
                    string output = SystemUtils.RunExternalProcess("diskpart.exe", $"/s \"{scriptFile}\"", hidden: true);
                    File.Delete(scriptFile);

                    // Validação robusta do resultado
                    if (output.Contains("sucesso") || output.Contains("successfully") || output.Contains("DiskPart successfully"))
                        return (true, "Partição Z: criada com sucesso.", "Z:\\");
                    else
                        return (false, "Erro no Diskpart (verifique espaço livre). Saída: " + output, "");
                }
                catch (Exception ex) { return (false, ex.Message, ""); }
            });
        }

        // 2. Copia arquivos da ISO montada para a nova partição
        public static async Task CopyInstallFiles(string sourceDrive, string targetDrive)
        {
            await Task.Run(() =>
            {
                // Usa Robocopy para cópia robusta em Modo Turbo (/MT:128 /J Unbuffered)
                SystemUtils.RunExternalProcess("robocopy.exe", $"\"{sourceDrive}\" \"{targetDrive}\" /E /ZB /J /DCOPY:T /FFT /R:0 /W:0 /MT:128", hidden: true);
            });
        }

        // 3. Configura o Boot (BCD) - Baseado no BCDBoot da Microsoft
        public static void SetupBootEntry(string targetDrive)
        {
            try
            {
                // Detecta se é UEFI ou BIOS de forma robusta
                bool isUefi = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Panther")) ||
                               Directory.Exists("C:\\EFI");

                // Usa BCDBoot (método recomendado pela Microsoft)
                string bcdBootCommand = $"bcdboot {targetDrive}Windows /s {targetDrive.Substring(0, 2)} /f {(isUefi ? "UEFI" : "BIOS")}";
                
                SystemUtils.RunExternalProcess("bcdboot.exe", bcdBootCommand, hidden: true);

                // Configura descrição e timeout (opcional)
                SystemUtils.RunExternalProcess("bcdedit.exe", "/timeout 10", hidden: true);
                SystemUtils.RunExternalProcess("bcdedit.exe", "/set {bootmgr} description \"Windows Boot Manager\"", hidden: true);
            }
            catch (Exception ex)
            {
                // Log do erro para debugging
                System.Diagnostics.Debug.WriteLine($"Erro em SetupBootEntry: {ex.Message}");
            }
        }

        // 4. Remove partição de boot (função de limpeza)
        public static async Task<(bool Success, string Message)> RemoveBootPartition()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string scriptFile = Path.Combine(Path.GetTempPath(), "diskpart_remove.txt");
                    string scriptContent = 
                        "select volume Z\n" +
                        "remove letter=Z\n" +
                        "delete partition override\n" +
                        "exit";
                    
                    File.WriteAllText(scriptFile, scriptContent);
                    
                    string output = SystemUtils.RunExternalProcess("diskpart.exe", $"/s \"{scriptFile}\"", hidden: true);
                    File.Delete(scriptFile);

                    if (output.Contains("sucesso") || output.Contains("successfully"))
                        return (true, "Partição de boot removida com sucesso.");
                    else
                        return (false, "Erro ao remover partição: " + output);
                }
                catch (Exception ex) { return (false, ex.Message); }
            });
        }

        // 5. Verifica espaço disponível (função de segurança)
        public static async Task<(bool Success, long FreeSpaceMB)> CheckFreeSpace()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var drive = new DriveInfo("C:");
                    long freeSpaceMB = drive.AvailableFreeSpace / (1024 * 1024);
                    return (true, freeSpaceMB);
                }
                catch { return (false, 0); }
            });
        }

        // 6. Detecção UEFI/BIOS robusta
        public static bool IsUEFI()
        {
            try
            {
                // Método 1: Verificar firmware
                string firmware = SystemUtils.RunExternalProcess("bcdedit", "", true);
                if (firmware.Contains("winload.efi")) return true;
                if (firmware.Contains("winload.exe")) return false;

                // Método 2: Verificar pasta EFI
                if (Directory.Exists("C:\\EFI")) return true;

                // Método 3: Verificar registro
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (key != null) return true;
                }

                return false;
            }
                catch (Exception ex)
            {
                Logger.Log($"Erro ao detectar UEFI/BIOS: {ex.Message}");
                return false; // Assume BIOS em caso de erro
            }
        }
    }
}
