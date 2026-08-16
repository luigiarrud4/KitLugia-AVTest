using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Gerenciador avançado de edição de ISOs
    /// Usa IsoManager existente para operações de ISO e DISM para customização
    /// </summary>
    public static class IsoEditorManager
    {
        // ==========================================
        // HELPER METHODS
        // ==========================================

        private static async Task<(int ExitCode, string Output)> RunProcessCaptured(string filename, string args)
        {
            return await RunProcessCapturedWithStdin(filename, args, null);
        }

        /// <summary>
        /// Executa um processo capturando stdout+stderr e, opcionalmente, escrevendo
        /// stdinContent no stdin do processo (usado pelo wimlib update < CMDFILE).
        /// </summary>
        private static async Task<(int ExitCode, string Output)> RunProcessCapturedWithStdin(string filename, string args, string? stdinContent)
        {
            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(filename, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = stdinContent != null,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi)!;
                string output, error;
                if (stdinContent != null)
                {
                    // Leitura assíncrona para evitar deadlock de pipe (stdout cheio + stdin bloqueado)
                    var tout = process.StandardOutput.ReadToEndAsync();
                    var terr = process.StandardError.ReadToEndAsync();
                    process.StandardInput.Write(stdinContent);
                    process.StandardInput.Close();
                    output = tout.GetAwaiter().GetResult();
                    error = terr.GetAwaiter().GetResult();
                    process.WaitForExit();
                }
                else
                {
                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                }

                return (process.ExitCode, output + (string.IsNullOrEmpty(error) ? "" : $"\n[ERROR]: {error}"));
            });
        }
        // ==========================================
        // WIMLIB (SEM MONTAR) - motor rápido
        // wimlib-imagex embutido modifica o WIM em 1-2s, sem DISM mount/commit.
        // ==========================================

        private static string? WimlibExe => WinpeBuilder.FindBundledWimlib();

        /// <summary>
        /// Lista as edições (indices + nomes) de um install.wim/esd via wimlib-imagex info
        /// (rápido, sem montar). Detecta se a imagem é ESD (solid) pela extensão.
        /// </summary>
        public static async Task<(bool Success, string Message, List<WimEdition> Editions)> AnalyzeWimAsync(string wimPath)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.", new List<WimEdition>());
                    string? wimlib = WimlibExe;
                    if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.", new List<WimEdition>());

                    var (code, output) = await RunProcessCaptured(wimlib, $"info \"{wimPath}\"");
                    if (code != 0) return (false, $"wimlib info falhou (código {code}): {output.Trim()}", new List<WimEdition>());

                    var editions = new List<WimEdition>();
                    int currentIndex = 0;
                    foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
                    {
                        var line = rawLine.Trim();
                        // wimlib imprime "Index: N" (sem "Image"); DISM/outros podem usar "Image Index: N".
                        var idxMatch = Regex.Match(line, @"^(?:Image\s+)?Index\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase);
                        if (idxMatch.Success)
                        {
                            currentIndex = int.Parse(idxMatch.Groups[1].Value);
                            editions.Add(new WimEdition { Index = currentIndex });
                            continue;
                        }
                        if (currentIndex == 0 || editions.Count == 0) continue;
                        var cur = editions[^1];
                        var nameMatch = Regex.Match(line, @"^(?:Name|Nome|Nazwa)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                        if (nameMatch.Success) cur.Name = nameMatch.Groups[1].Value.Trim();
                        var descMatch = Regex.Match(line, @"^Description\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                        if (descMatch.Success) cur.Description = descMatch.Groups[1].Value.Trim();
                    }

                    bool isEsd = wimPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)
                                 || output.Contains("Solid compression", StringComparison.OrdinalIgnoreCase)
                                 || output.Contains("Compression: LZMS", StringComparison.OrdinalIgnoreCase);
                    foreach (var e in editions) e.IsEsd = isEsd;

                    if (editions.Count == 0) return (false, "Nenhuma edição encontrada no WIM/ESD.", editions);
                    return (true, $"Analisada: {FormatBytes(new FileInfo(wimPath).Length)}, {editions.Count} edição(ões)" + (isEsd ? " (ESD comprimida)" : ""), editions);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao analisar WIM: {ex.Message}");
                    return (false, $"Erro ao analisar: {ex.Message}", new List<WimEdition>());
                }
            });
        }

        /// <summary>
        /// Exporta UMA edição do WIM/ESD para um novo install.wim, recomprimindo.
        /// - ESD (solid) -> WIM normal (permite tweaks depois, sem runtime DISM)
        /// - strip de ISO multi-edição (deixa só a escolhida), reduz tamanho
        /// - compress: "lzms" (máx) ou "lzx" (padrão/balanceado)
        /// - markBootable: adiciona --boot ao export (marca a imagem exportada como bootable
        ///   no header do WIM, BootIndex=1) - necessario para boot ramdisk (bootmgr so
        ///   carrega imagens marcadas; sem o flag o WIM exportado falha com 0xc0000487)
        /// </summary>
        public static async Task<(bool Success, string Message, string OutputPath)> ExportSingleEditionAsync(
            string wimPath, int index, string destWim, string compress = "lzms", bool markBootable = false)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    string? wimlib = WimlibExe;
                    if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.", wimPath);
                    if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.", wimPath);

                    long sizeBefore = new FileInfo(wimPath).Length;
                    bool sameFile = Path.GetFullPath(destWim).Equals(Path.GetFullPath(wimPath), StringComparison.OrdinalIgnoreCase);

                    // Destino igual à origem (ex: exportar edição N do próprio install.wim): exporta
                    // para um .tmp e substitui o original no fim. Destino existente (rodada anterior):
                    // apaga primeiro, senão o wimlib aborta com "already an image named ...".
                    string targetWim = destWim;
                    if (sameFile)
                    {
                        targetWim = destWim + ".kitltmp";
                        try { if (File.Exists(targetWim)) { File.SetAttributes(targetWim, FileAttributes.Normal); File.Delete(targetWim); } } catch { }
                    }
                    else if (File.Exists(destWim))
                    {
                        try { File.SetAttributes(destWim, FileAttributes.Normal); File.Delete(destWim); } catch { }
                    }

                    if (compress != "lzms" && compress != "lzx") compress = "lzms";
                    string bootFlag = markBootable ? " --boot" : "";
                    var (code, output) = await RunProcessCaptured(wimlib, $"export \"{wimPath}\" {index} \"{targetWim}\" --compress={compress}{bootFlag}");
                    if (code != 0)
                    {
                        try { File.Delete(targetWim); } catch { }
                        return (false, $"wimlib export falhou (código {code}): {output.Trim()}", wimPath);
                    }

                    if (sameFile)
                    {
                        try { File.SetAttributes(wimPath, FileAttributes.Normal); File.Delete(wimPath); } catch { }
                        File.Move(targetWim, destWim);
                    }

                    long sizeAfter = new FileInfo(destWim).Length;
                    long saved = sizeBefore - sizeAfter;
                    string msg = $"Exportada edição {index} como install.wim (compressão {compress}). " +
                                 $"Economia: {FormatBytes(saved)} (antes {FormatBytes(sizeBefore)} -> depois {FormatBytes(sizeAfter)})";
                    return (true, msg, destWim);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Erro ao exportar edição: {ex.Message}");
                    return (false, $"Erro ao exportar: {ex.Message}", wimPath);
                }
            });
        }

        /// <summary>
        /// Injeta arquivos no WIM (index específico) via wimlib update --command-file,
        /// sem montar. Mesmo padrão do WinpeBuilder.InjectWimlibIntoWimAsync.
        /// </summary>
        public static async Task<bool> InjectFilesIntoWimAsync(string wimPath, int index, IEnumerable<(string LocalPath, string WimTarget)> files)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return false;
                if (!File.Exists(wimPath)) return false;

                var list = files.Where(f => File.Exists(f.LocalPath)).ToList();
                if (list.Count == 0) return false;
                File.SetAttributes(wimPath, FileAttributes.Normal);

                string tmpDir = Path.Combine(Path.GetTempPath(), "KitLugia_IsoWimlib");
                Directory.CreateDirectory(tmpDir);
                var sb = new List<string>();
                foreach (var (local, target) in list)
                {
                    string targetNorm = target.Replace('\\', '/');
                    if (!targetNorm.StartsWith("/")) targetNorm = "/" + targetNorm;
                    sb.Add($"add \"{local}\" {targetNorm}");
                }
                string commands = string.Join("\n", sb);

                string args = $"update \"{wimPath}\" {index}";
                var (code, output) = await RunProcessCapturedWithStdin(wimlib, args, commands);
                try { Directory.Delete(tmpDir, true); } catch { }

                if (code != 0)
                {
                    Logger.Log($"wimlib update falhou (código {code}): {output.Trim()}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao injetar arquivos no WIM: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Instala um startnet.cmd + winpeshl.ini na imagem (index) do WIM de Setup:
        /// SEM winpeshl.ini o winpeshl.exe tenta lancar %SystemDrive%\$Windows.~BT\sources\setup.exe
        /// e %SystemDrive%\setup.exe ANTES do fallback cmd /k startnet.cmd - e a imagem de Setup
        /// da midia TEM setup.exe na raiz (o shim, 333 KB), entao o nosso startnet.cmd NUNCA
        /// rodava (o winpeshl lancava o shim, que abre o Setup cru, sem /installfrom e sem o
        /// ambiente de enumeracao de discos). Com winpeshl.ini presente, o winpeshl lanca SO o
        /// que o [LaunchApps] mandar - cmd.exe /k startnet.cmd - e o fluxo fica sob nosso controle.
        /// </summary>
        public static async Task<bool> InstallSetupStartnetAsync(string wimPath, int index, string startnetLocalPath)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return false;
                if (!File.Exists(wimPath) || !File.Exists(startnetLocalPath)) return false;
                File.SetAttributes(wimPath, FileAttributes.Normal);

                // 1. winpeshl.ini local: [LaunchApps] -> cmd /k startnet.cmd (ASCII; o
                //    winpeshl le AppPath + args separados por virgula - formato da midia)
                string iniLocal = Path.Combine(Path.GetTempPath(), "kitlugia_winpeshl.ini");
                File.WriteAllText(iniLocal,
                    "[LaunchApps]\r\n" +
                    "%SystemRoot%\\system32\\cmd.exe, /k startnet.cmd\r\n",
                    System.Text.Encoding.ASCII);

                // 2. Command file via stdin: add startnet.cmd + add/substituir winpeshl.ini
                var cmds = new List<string>
                {
                    $"add \"{startnetLocalPath}\" /Windows/System32/startnet.cmd",
                    $"add \"{iniLocal}\" /Windows/System32/winpeshl.ini"
                };

                var (uCode, uOut) = await RunProcessCapturedWithStdin(wimlib, $"update \"{wimPath}\" {index}", string.Join("\n", cmds));
                try { File.Delete(iniLocal); } catch { }
                if (uCode != 0)
                {
                    Logger.Log($"wimlib update do startnet/winpeshl.ini falhou (codigo {uCode}): {uOut.Trim()}");
                    return false;
                }
                Logger.Log($"startnet.cmd + winpeshl.ini instalados na imagem {index} (winpeshl.ini controla o launch do startnet.cmd).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao instalar startnet.cmd/winpeshl.ini no WIM: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Lista as pastas de nível superior de Program Files\WindowsApps de um WIM via wimlib dir
        /// (nome completo do pacote = o nome da pasta; só filhos DIRETOS da raiz de WindowsApps).
        /// </summary>
        public static async Task<List<string>> ListWindowsAppsFoldersAsync(string wimPath, int index)
        {
            var result = new List<string>();
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null || !File.Exists(wimPath)) return result;

                // dir lista a subárvore inteira (o próprio dir + pacotes + arquivos); o filtro abaixo
                // mantém só os filhos diretos (pacotes) - 1 nível abaixo do prefixo.
                var (code, output) = await RunProcessCaptured(wimlib, $"dir \"{wimPath}\" {index} --path=\"Program Files/WindowsApps/\"");
                if (code != 0) return result;

                const string prefix = @"\Program Files\WindowsApps\";
                foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
                {
                    string p = rawLine.Trim();
                    if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = p.Substring(prefix.Length);
                    if (string.IsNullOrEmpty(name) || name.Contains('\\')) continue; // só filhos diretos
                    if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
                }
            }
            catch (Exception ex) { Logger.Log($"Erro ao listar WindowsApps: {ex.Message}"); }
            return result;
        }

        /// <summary>
        /// Remove AppX provisionados SEM DISM e SEM montar (método nativo, espelha o que o
        /// Remove-AppxProvisionedPackage faz por baixo - ver AppxAllUserStore::CleanupPackageFromPerMachineStore):
        /// 1. wimlib ls lista as pastas de Program Files\WindowsApps
        /// 2. wimlib update DELETE remove as pastas cujo nome começa com algum prefixo
        /// 3. Hive SOFTWARE (extract -> reg load -> delete Applications + add Deprovisioned -> unload -> re-inject)
        ///    Deprovisioned é o marcador documentado (MS Learn) que impede o re-provisionamento em updates.
        /// </summary>
        public static async Task<(bool Success, string Message)> RemoveProvisionedAppsNoMountAsync(
            string wimPath, int index, IEnumerable<string> namePrefixes, Action<string>? log = null)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.");
                if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.");

                var prefixes = namePrefixes.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (prefixes.Count == 0) return (false, "Nenhum app informado para remover.");

                log?.Invoke("Listando pastas de WindowsApps no WIM (wimlib ls)...");
                var folders = await ListWindowsAppsFoldersAsync(wimPath, index);
                var toRemove = folders
                    .Where(f => prefixes.Any(p => f.StartsWith(p + "_", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (toRemove.Count == 0)
                {
                    log?.Invoke("Nenhum app provisionado correspondente encontrado no WIM (já removidos?).");
                    return (true, "Nenhum AppX provisionado encontrado para os prefixos informados.");
                }

                string tmpDir = Path.Combine(Path.GetTempPath(), $"KitLugia_IsoAppx_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(tmpDir);
                try
                {
                    // 1) wimlib update: delete das pastas (--recursive p/ pastas; comandos via stdin)
                    var cmds = toRemove.Select(f => $"delete \"Program Files/WindowsApps/{f}\"").ToList();
                    log?.Invoke($"Removendo {toRemove.Count} pasta(s) de WindowsApps via wimlib update...");
                    var (uCode, uOut) = await RunProcessCapturedWithStdin(wimlib, $"update \"{wimPath}\" {index} --recursive", string.Join("\n", cmds));
                    if (uCode != 0)
                    {
                        log?.Invoke($"Aviso: wimlib update delete falhou (código {uCode}): {uOut.Trim()} - continua com o registro.");
                    }

                    // 2) hive SOFTWARE: delete Applications/<fullname> + add Deprovisioned/<fullname>
                    string softwareLocal = Path.Combine(tmpDir, "software");
                    log?.Invoke("Extraindo hive SOFTWARE do WIM...");
                    var (xCode, xOut) = await RunProcessCaptured(wimlib, $"extract \"{wimPath}\" {index} \"Windows/System32/config/software\" --dest-dir=\"{tmpDir}\"");
                    if (xCode != 0 || !File.Exists(softwareLocal))
                    {
                        log?.Invoke($"Aviso: não foi possível extrair SOFTWARE (código {xCode}): {xOut.Trim()} - registro não editado.");
                        return (true, $"{toRemove.Count} AppX removidos (pastas), registro SOFTWARE não editado.");
                    }

                    var unloadPsi = new ProcessStartInfo("reg.exe", "unload HKLM\\zSOFTWARE")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using (var pu = Process.Start(unloadPsi)) pu?.WaitForExit(10000);

                    var (lCode, lOut) = await RunProcessCaptured("reg.exe", $"load HKLM\\zSOFTWARE \"{softwareLocal}\"");
                    if (lCode != 0)
                    {
                        log?.Invoke($"Aviso: reg load de SOFTWARE falhou (código {lCode}): {lOut.Trim()} - registro não editado.");
                        return (true, $"{toRemove.Count} AppX removidos (pastas), registro SOFTWARE não editado.");
                    }

                    const string appxRoot = @"HKLM\zSOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore";
                    foreach (var fullName in toRemove)
                    {
                        // Remove a entrada de provisionamento (a pasta já foi deletada)
                        await RunProcessCaptured("reg.exe", $"delete \"{appxRoot}\\Applications\\{fullName}\" /f");
                        await RunProcessCaptured("reg.exe", $"delete \"{appxRoot}\\Application\\{fullName}\" /f");
                        // Marcador Deprovisioned: impede o re-provisionamento em feature updates (MS Learn)
                        var (dCode, dOut) = await RunProcessCaptured("reg.exe", $"add \"{appxRoot}\\Deprovisioned\\{fullName}\" /f");
                        if (dCode != 0) log?.Invoke($"Deprovisioned {fullName} -> ({dCode}) {dOut.Trim()}");
                    }

                    var (uCode2, uOut2) = await RunProcessCaptured("reg.exe", "unload HKLM\\zSOFTWARE");
                    if (uCode2 != 0) log?.Invoke($"reg unload SOFTWARE -> ({uCode2}) {uOut2.Trim()}");

                    log?.Invoke("Reinjetando hive SOFTWARE no WIM via wimlib...");
                    bool ok = await InjectFilesIntoWimAsync(wimPath, index, new[] { (softwareLocal, "/Windows/System32/config/software") });
                    if (!ok) return (false, "Falha ao re-injetar SOFTWARE (wimlib update).");

                    return (true, $"{toRemove.Count} AppX provisionados removidos sem montar: {string.Join(", ", toRemove.Take(8))}" +
                                 (toRemove.Count > 8 ? $" (+{toRemove.Count - 8})" : ""));
                }
                finally
                {
                    try { Directory.Delete(tmpDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro no RemoveProvisionedAppsNoMountAsync: {ex.Message}");
                return (false, $"Erro ao remover AppX sem montar: {ex.Message}");
            }
        }

        /// <summary>
        /// Deleta arquivos de scheduled tasks de dentro do WIM via wimlib update delete (sem montar).
        /// tasks: caminhos relativos a Windows\System32\Tasks (ex: "Microsoft\Windows\UpdateOrchestrator").
        /// Versões novas (24H2/25H2) NÃO incluem mais a maioria das tasks de telemetria no WIM:
        /// lista antes com dir e só deleta o que EXISTE (o update aborta no 1º path ausente com
        /// código 49 e nada é deletado).
        /// </summary>
        public static async Task<(bool Success, string Message)> DeleteScheduledTaskFilesNoMountAsync(
            string wimPath, int index, IEnumerable<string> tasks, Action<string>? log = null)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.");
                if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.");

                var list = tasks.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                if (list.Count == 0) return (false, "Nenhuma task informada.");

                // Lista o que existe de verdade em Tasks (1 chamada dir, barata - 25H2 tem ~10 linhas)
                var (lsCode, lsOutput) = await RunProcessCaptured(wimlib, $"dir \"{wimPath}\" {index} --path=\"/Windows/System32/Tasks\"");
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (lsCode == 0)
                {
                    foreach (var rawLine in lsOutput.Replace("\r\n", "\n").Split('\n'))
                    {
                        var line = rawLine.Trim().Replace('\\', '/');
                        if (!line.StartsWith("/Windows/System32/Tasks/", StringComparison.OrdinalIgnoreCase)) continue;
                        existing.Add(line);
                    }
                }

                var toDelete = list
                    .Select(t => "Windows/System32/Tasks/" + t.TrimStart('/').Replace('\\', '/'))
                    .Where(p => existing.Any(e => e.Equals("/" + p, StringComparison.OrdinalIgnoreCase)
                                               || e.StartsWith("/" + p + "/", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (toDelete.Count == 0)
                    return (true, $"Nenhuma das {list.Count} scheduled task(s) alvo existe no WIM (esta versão já não as inclui). Nada a deletar.");

                string tmpDir = Path.Combine(Path.GetTempPath(), $"KitLugia_IsoTasks_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(tmpDir);
                try
                {
                    var cmds = toDelete.Select(p => $"delete \"{p}\"").ToList();
                    log?.Invoke($"Deletando {toDelete.Count} scheduled task(s) existente(s) via wimlib update ({list.Count - toDelete.Count} já ausentes nesta versão)...");
                    var (code, output) = await RunProcessCapturedWithStdin(wimlib, $"update \"{wimPath}\" {index} --recursive", string.Join("\n", cmds));
                    if (code != 0) return (false, $"wimlib update delete de tasks falhou (código {code}): {output.Trim()}");
                    return (true, $"{toDelete.Count} scheduled task(s) deletadas sem montar.");
                }
                finally
                {
                    try { Directory.Delete(tmpDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro no DeleteScheduledTaskFilesNoMountAsync: {ex.Message}");
                return (false, $"Erro ao deletar tasks sem montar: {ex.Message}");
            }
        }

        /// <summary>
        /// Otimiza o WIM via wimlib optimize SEM recompressão: remove os "holes" deixados
        /// pelos updates (appends/deletes) reutilizando os dados comprimidos existentes.
        /// Equivalente no-mount ao DISM /StartComponentCleanup /ResetBase em mídia nova.
        /// NUNCA passar --compress= aqui: ele implica --recompress (wimlib docs) e recomprime
        /// o WIM INTEIRO do zero (minutos em 6GB). A compressão já foi escolhida no export.
        /// </summary>
        public static async Task<(bool Success, string Message)> OptimizeWimAsync(string wimPath)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.");
                if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.");

                long before = new FileInfo(wimPath).Length;
                var (code, output) = await RunProcessCaptured(wimlib, $"optimize \"{wimPath}\"");
                if (code != 0) return (false, $"wimlib optimize falhou (código {code}): {output.Trim()}");
                long after = new FileInfo(wimPath).Length;
                return (true, $"WIM otimizado: {FormatBytes(before)} -> {FormatBytes(after)} (economia {FormatBytes(before - after)})");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro no OptimizeWimAsync: {ex.Message}");
                return (false, $"Erro ao otimizar: {ex.Message}");
            }
        }

        /// <summary>
        /// Aplica registry tweaks SEM montar o WIM:
        /// extrai as hives (SOFTWARE/SYSTEM/NTUSER/DEFAULT) via wimlib, reg load,
        /// reg add, reg unload, e re-injeta as hives via wimlib update.
        /// edits: (Hive, SubKeyCompleto, ValorName, Tipo, Valor) - Hive sem o prefixo HKLM\z.
        /// </summary>
        public static async Task<(bool Success, string Message)> ApplyRegistryEditsNoMountAsync(
            string wimPath, int index,
            IEnumerable<(string Hive, string Key, string Name, string Type, string Value)> edits,
            Action<string>? log = null)
        {
            try
            {
                string? wimlib = WimlibExe;
                if (wimlib == null) return (false, "wimlib-imagex.exe não encontrado no kit.");
                if (!File.Exists(wimPath)) return (false, "Arquivo WIM/ESD não encontrado.");

                var hiveWimPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SOFTWARE"] = "/Windows/System32/config/software",
                    ["SYSTEM"] = "/Windows/System32/config/system",
                    ["DEFAULT"] = "/Windows/System32/config/default",
                    ["NTUSER"] = "/Users/Default/ntuser.dat",
                };

                var groups = edits
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                    .GroupBy(e => e.Hive, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (groups.Count == 0) return (false, "Nenhum tweak de registro para aplicar.");

                string tmpDir = Path.Combine(Path.GetTempPath(), $"KitLugia_IsoHives_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(tmpDir);

                try
                {
                    var reInject = new List<(string Local, string Wim)>();
                    var applied = new List<string>();
                    var reInjectLock = new object();
                    var appliedLock = new object();

                    // Processa cada hive EM PARALELO (extract + reg load + tweaks + unload):
                    // hives diferentes = chaves HKLM\z{...} diferentes + arquivos locais diferentes
                    // (software/system/default/ntuser.dat) - sem colisao entre si.
                    async Task ProcessHiveAsync(IGrouping<string, (string Hive, string Key, string Name, string Type, string Value)> g)
                    {
                        string hive = g.Key.ToUpperInvariant();
                        if (!hiveWimPath.ContainsKey(hive))
                        {
                            log?.Invoke($"Aviso: hive desconhecido '{g.Key}' ignorado.");
                            return;
                        }

                        string wimInternal = hiveWimPath[hive];
                        string hiveLocal = Path.Combine(tmpDir, wimInternal.Substring(wimInternal.LastIndexOf('/') + 1));

                        log?.Invoke($"Extraindo hive {hive} do WIM (edição {index})...");
                        var (xCode, xOut) = await RunProcessCaptured(wimlib, $"extract \"{wimPath}\" {index} \"{wimInternal.TrimStart('/')}\" --dest-dir=\"{tmpDir}\"");
                        if (xCode != 0 || !File.Exists(hiveLocal))
                        {
                            log?.Invoke($"Aviso: não foi possível extrair {hive} (código {xCode}): {xOut.Trim()}");
                            return;
                        }

                        // Liberar hive caso já esteja carregado (reg load exige key inexistente)
                        var unloadPsi = new ProcessStartInfo("reg.exe", $"unload HKLM\\z{hive}")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        };
                        using (var pu = Process.Start(unloadPsi)) pu?.WaitForExit(10000);

                        log?.Invoke($"Carregando hive {hive} (reg load)...");
                        var (lCode, lOut) = await RunProcessCaptured("reg.exe", $"load HKLM\\z{hive} \"{hiveLocal}\"");
                        if (lCode != 0)
                        {
                            log?.Invoke($"Aviso: reg load de {hive} falhou (código {lCode}): {lOut.Trim()} - tweaks deste hive pulados.");
                            return;
                        }

                        foreach (var e in g)
                        {
                            var (aCode, aOut) = await RunProcessCaptured("reg.exe",
                                $"add \"HKLM\\z{hive}\\{e.Key.TrimStart('\\')}\" /v {e.Name} /t {e.Type} /d \"{e.Value}\" /f");
                            if (aCode == 0) { lock (appliedLock) applied.Add(hive); }
                            else log?.Invoke($"reg add {e.Key}\\{e.Name} -> ({aCode}) {aOut.Trim()}");
                        }

                        var (uCode, uOut) = await RunProcessCaptured("reg.exe", $"unload HKLM\\z{hive}");
                        if (uCode != 0) log?.Invoke($"reg unload {hive} -> ({uCode}) {uOut.Trim()}");

                        lock (reInjectLock) reInject.Add((hiveLocal, wimInternal.TrimStart('/')));
                    }

                    await Task.WhenAll(groups.Select(g => Task.Run(() => ProcessHiveAsync(g))));

                    if (reInject.Count > 0)
                    {
                        log?.Invoke($"Reinjeta {reInject.Count} hive(s) no WIM via wimlib...");
                        bool ok = await InjectFilesIntoWimAsync(wimPath, index, reInject.Select(r => (r.Local, r.Wim)));
                        if (!ok) return (false, "Falha ao re-injetar hives no WIM (wimlib update).");
                    }

                    return (applied.Count > 0
                        ? (true, $"Registry tweaks aplicados sem montar ({string.Join(", ", applied.Distinct())}).")
                        : (false, "Nenhum tweak de registro foi aplicado (veja o log)."));
                }
                finally
                {
                    try { Directory.Delete(tmpDir, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro no ApplyRegistryEditsNoMountAsync: {ex.Message}");
                return (false, $"Erro ao aplicar tweaks sem montar: {ex.Message}");
            }
        }

        // ==========================================
        // ISO MANAGEMENT (Usando IsoManager existente)
        // ==========================================

        /// <summary>
        /// Cria uma ISO a partir de um diretório (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateIso(string sourceDir, string targetIso)
        {
            return await IsoManager.CreateIso(sourceDir, targetIso);
        }

        /// <summary>
        /// Monta uma ISO (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message, string DriveLetter)> MountIso(string isoPath)
        {
            return await IsoManager.MountIso(isoPath);
        }

        /// <summary>
        /// Desmonta uma ISO (usa IsoManager existente)
        /// </summary>
        public static async Task<(bool Success, string Message)> DismountIso(string isoPath)
        {
            return await IsoManager.DismountIso(isoPath);
        }

        // ==========================================
        // HELPER METHODS (Parsing & Utilities)
        // ==========================================

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    // ==========================================
    // DATA MODELS
    // ==========================================

    /// <summary>
    /// Uma edição (imagem) dentro de um install.wim/esd, listada pelo wimlib info.
    /// </summary>
    public class WimEdition
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsEsd { get; set; } = false;
        public override string ToString() => $"{Index}. {(string.IsNullOrEmpty(Name) ? "Edição" : Name)}".TrimEnd();
    }
}
