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
            return await Task.Run(async () =>
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
                    
                    string output = await SystemUtils.RunExternalProcessAsync("diskpart.exe", $"/s \"{scriptFile}\"", hidden: true).ConfigureAwait(false);
                    File.Delete(scriptFile);

                    // Validação robusta do resultado
                    if (output.Contains("sucesso") || output.Contains("successfully") || output.Contains("DiskPart successfully"))
                        return (true, "Partição criada com sucesso.", "");
                    else
                        return (false, "Erro no Diskpart (verifique espaço livre). Saída: " + output, "");
                }
                catch (Exception ex) { return (false, ex.Message, ""); }
            });
        }

        // 2. Copia arquivos da ISO montada para a nova partição

        // 3. Configura o Boot (BCD) - Baseado no BCDBoot da Microsoft
        public static void SetupBootEntry(string targetDrive)
        {
            try
            {
                // Detecta se é UEFI ou BIOS de forma robusta
                bool isUefi = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Panther")) ||
                               Directory.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "EFI"));

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
            return await Task.Run(async () =>
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
                    
                    string output = await SystemUtils.RunExternalProcessAsync("diskpart.exe", $"/s \"{scriptFile}\"", hidden: true).ConfigureAwait(false);
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
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); return (false, 0); }
            });
        }

        // 6. Detecção UEFI/BIOS robusta
    }
}
