using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public static class WinpeBuilder
    {
        private const string ADK_REG_PATH = @"SOFTWARE\Microsoft\Windows Kits\Installed Roots";
        private const string ADK_REG_VALUE = "KitsRoot10";
        private const string DEFAULT_OUTPUT = @"C:\WinPE_KitLugia";

        public static void Log(string message) => WinbootManager.Log(message);

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
            catch
            {
                return (false, "", "");
            }
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
                try { Directory.Delete(tempDriverDir, true); } catch { }

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
                    $"/Mount-Image /ImageFile:\"{bootWim}\" /index:1 /MountDir:\"{mountDir}\"", 120000);
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
                catch { }

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
            catch { }

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
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, $"TIMEOUT após {timeoutMs}ms");
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            string output = outputTask.Result + errorTask.Result;
            return (proc.ExitCode, output);
        }
    }
}
