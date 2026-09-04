using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KitLugia.Core
{
    public enum PathEntryProblem
    {
        None = 0,
        Missing = 1,
        WrongLocation = 2,
        Duplicate = 3,
        Junk = 4,
        Orphan = 5,
        SyntaxError = 6
    }

    public class PathEntry
    {
        public string RawValue { get; set; }
        public string CleanValue { get; set; }
        public string ExpandedValue { get; set; }
        public bool Exists { get; set; }
        public PathEntryProblem Problem { get; set; }
        public string ProblemDetail { get; set; }
        public string RecommendedAction { get; set; }

        public PathEntry(string raw)
        {
            RawValue = raw;
            CleanValue = raw.Trim().Trim('"').Trim();
            ExpandedValue = Environment.ExpandEnvironmentVariables(CleanValue);
            Exists = TestExists();
            Problem = PathEntryProblem.None;
            ProblemDetail = "";
            RecommendedAction = "Manter";
        }

        private bool TestExists()
        {
            if (string.IsNullOrWhiteSpace(CleanValue)) return false;
            if (CleanValue.Contains('%') && CleanValue.IndexOf('%') < CleanValue.LastIndexOf('%')) return true;
            try { return Directory.Exists(ExpandedValue); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }
    }

    public static class PathRepair
    {
        private static bool IsWindowsSystemPath(PathEntry entry)
        {
            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLower().TrimEnd('\\');
            string expanded = entry.ExpandedValue.ToLower().TrimEnd('\\');
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToLower().TrimEnd('\\');
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).ToLower().TrimEnd('\\');

            // Caminhos válidos do System PATH
            string[] systemPaths = {
                sysRoot,
                $"{sysRoot}\\system32",
                $"{sysRoot}\\system32\\wbem",
                $"{sysRoot}\\system32\\windowspowershell\\v1.0",
                $"{sysRoot}\\system32\\openssh",
                $"{sysRoot}\\system32\\inetsrv",
                $"{sysRoot}\\syswow64",
                $"{sysRoot}\\system32\\drivers\\etc",
                $"{programFiles}\\dotnet",
                $"{programFiles}\\powershell\\7",
                $"{programFiles}\\windows kits",
                $"{programFiles}\\microsoft sdks",
                $"{programFiles}\\microsoft sql server",
                $"{programFilesX86}\\windows kits",
                $"{programFilesX86}\\microsoft sdks",
                $"{programFilesX86}\\microsoft sql server"
            };

            // Verifica se começa com algum dos caminhos do sistema
            foreach (var sysPath in systemPaths)
            {
                if (expanded.StartsWith(sysPath))
                    return true;
            }

            return false;
        }

        private static bool IsDotnetSdkJunk(PathEntry entry)
        {
            return entry.CleanValue.Contains("\\dotnet\\sdk\\", StringComparison.OrdinalIgnoreCase) &&
                   entry.CleanValue.EndsWith("\\sdks", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDotnetTools(PathEntry entry)
        {
            return entry.CleanValue.EndsWith("\\.dotnet\\tools", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSyntaxError(PathEntry entry)
        {
            string v = entry.RawValue;
            if (v.Contains(',')) return true;
            if (v.Contains("\"\"")) return true;
            if (v.Contains("\\\\\\")) return true;
            if (!string.IsNullOrEmpty(v) && !char.IsLetter(v[0]) && v[0] != '%' && v[0] != '\\') return true;
            return false;
        }

        private static bool IsOrphan(PathEntry entry)
        {
            string[] orphanPatterns = {
                "\\(uninstall|remove|old|backup|temp|tmp)\\",
                "\\(node_modules|vendor|\\.git|\\.svn)\\",
                "\\(x86|x64)\\.*\\(old|bak|backup)"
            };
            foreach (var p in orphanPatterns)
            {
                if (entry.CleanValue.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static List<PathEntry> DiagnosePath(string pathString, string pathType)
        {
            var entries = new List<PathEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rawEntries = pathString.Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in rawEntries)
            {
                var entry = new PathEntry(raw);

                if (string.IsNullOrWhiteSpace(entry.CleanValue))
                {
                    entry.Problem = PathEntryProblem.Junk;
                    entry.ProblemDetail = "Entrada vazia";
                    entry.RecommendedAction = "Remover";
                    entries.Add(entry);
                    continue;
                }

                if (IsSyntaxError(entry))
                {
                    entry.Problem = PathEntryProblem.SyntaxError;
                    entry.ProblemDetail = "Sintaxe malformada";
                    entry.RecommendedAction = "Remover ou corrigir";
                    entries.Add(entry);
                    continue;
                }

                if (!seen.Add(entry.CleanValue))
                {
                    entry.Problem = PathEntryProblem.Duplicate;
                    entry.ProblemDetail = "Duplicado (case-insensitive)";
                    entry.RecommendedAction = "Remover duplicata";
                    entries.Add(entry);
                    continue;
                }

                if (IsDotnetSdkJunk(entry))
                {
                    entry.Problem = PathEntryProblem.Junk;
                    entry.ProblemDetail = "Caminho de SDK interno do .NET";
                    entry.RecommendedAction = "Remover";
                    entries.Add(entry);
                    continue;
                }

                if (pathType == "User" && IsWindowsSystemPath(entry))
                {
                    entry.Problem = PathEntryProblem.WrongLocation;
                    entry.ProblemDetail = "Caminho de sistema no User PATH";
                    entry.RecommendedAction = "Mover para System PATH";
                    entries.Add(entry);
                    continue;
                }

                if (pathType == "System" && !IsWindowsSystemPath(entry) && !entry.CleanValue.StartsWith("%"))
                {
                    // Não remover caminhos que usam variáveis de ambiente do sistema
                    if (entry.CleanValue.StartsWith("%SystemRoot%", StringComparison.OrdinalIgnoreCase) ||
                        entry.CleanValue.StartsWith("%ProgramFiles%", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.RecommendedAction = "Manter";
                        entries.Add(entry);
                        continue;
                    }

                    entry.Problem = PathEntryProblem.WrongLocation;
                    entry.ProblemDetail = "Caminho de usuário no System PATH";
                    entry.RecommendedAction = "Mover para User PATH";
                    entries.Add(entry);
                    continue;
                }

                if (!entry.Exists)
                {
                    if (IsDotnetTools(entry))
                    {
                        entry.Problem = PathEntryProblem.Missing;
                        entry.ProblemDetail = "Pasta .dotnet\\tools não existe";
                        entry.RecommendedAction = "Criar pasta";
                    }
                    else if (IsOrphan(entry))
                    {
                        entry.Problem = PathEntryProblem.Orphan;
                        entry.ProblemDetail = "Resíduo de desinstalação";
                        entry.RecommendedAction = "Remover";
                    }
                    else
                    {
                        entry.Problem = PathEntryProblem.Missing;
                        entry.ProblemDetail = "Pasta não existe";
                        entry.RecommendedAction = "Remover ou verificar instalação";
                    }
                    entries.Add(entry);
                    continue;
                }

                entry.RecommendedAction = "Manter";
                entries.Add(entry);
            }

            return entries;
        }

        public static (string Path, List<string> Actions) RepairPathEntries(List<PathEntry> entries, string pathType)
        {
            // FIX: o reparo antigo "mantinha" TUDO (duplicados, órfãos, lixo, sintaxe inválida),
            // então o botão CORRIGIR nunca mudava nada além de reordenar. Agora remove
            // DE VERDADE o que é inútil/quebrado e preserva o que é seguro.
            var repaired = new List<string>();
            var actions = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                switch (entry.Problem)
                {
                    case PathEntryProblem.None:
                        if (seen.Add(entry.CleanValue)) repaired.Add(entry.CleanValue);
                        else actions.Add($"Removida duplicata: {entry.CleanValue}");
                        break;

                    case PathEntryProblem.Missing:
                        if (IsDotnetTools(entry))
                        {
                            try
                            {
                                Directory.CreateDirectory(entry.ExpandedValue);
                                actions.Add($"Criada pasta: {entry.CleanValue}");
                                repaired.Add(entry.CleanValue); // pasta criada — mantém
                            }
                            catch
                            {
                                // Sem permissão para criar: mantém (nunca quebra PATH do usuário)
                                repaired.Add(entry.CleanValue);
                                actions.Add($"[AVISO] FALHA ao criar pasta (mantida no PATH): {entry.CleanValue}");
                            }
                        }
                        else
                        {
                            // Pasta não existe = entrada morta que atrasa resolução de executáveis.
                            // REMOVIDA (era "Mantida (não existe)" antes — reparo não fazia nada).
                            actions.Add($"Removida (diretório não existe): {entry.CleanValue}");
                        }
                        break;

                    case PathEntryProblem.WrongLocation:
                        // Caminho válido em local errado (sistema no User ou vice-versa):
                        // NÃO remover — pode ser intencional. Apenas registra.
                        repaired.Add(entry.CleanValue);
                        actions.Add(pathType == "User"
                            ? $"Mantido (caminho de sistema no User): {entry.CleanValue}"
                            : $"Mantido (caminho de usuário no System): {entry.CleanValue}");
                        break;

                    case PathEntryProblem.Duplicate:
                        // Duplicado: DiagnosePath já marca a 2ª+ ocorrência; aqui garante
                        // que só a primeira entra no resultado final.
                        if (seen.Add(entry.CleanValue))
                        {
                            repaired.Add(entry.CleanValue);
                            actions.Add($"Mantida 1ª ocorrência: {entry.CleanValue}");
                        }
                        else
                        {
                            actions.Add($"REMOVIDA duplicata: {entry.CleanValue}");
                        }
                        break;

                    case PathEntryProblem.Junk:
                        // Lixo de desenvolvimento (SDK internals, node_modules etc.): REMOVE.
                        actions.Add($"REMOVIDO lixo de desenvolvimento: {entry.CleanValue}");
                        break;

                    case PathEntryProblem.Orphan:
                        // Resíduo de desinstalação: REMOVE.
                        actions.Add($"REMOVIDO resíduo de desinstalação: {entry.CleanValue}");
                        break;

                    case PathEntryProblem.SyntaxError:
                        // Sintaxe inválida (aspas sobrando, ;;, caminho relativo): REMOVE —
                        // o Windows ignora entradas malformadas, mas elas poluem e podem confundir.
                        actions.Add($"REMOVIDA entrada com sintaxe inválida: {entry.RawValue}");
                        break;
                }
            }

            return (string.Join(";", repaired), actions);
        }

        public static string EnsureSystemPathMinimum(string currentSystemPath)
        {
            string[] minimal = {
                "%SystemRoot%\\system32",
                "%SystemRoot%",
                "%SystemRoot%\\System32\\Wbem",
                "%SYSTEMROOT%\\System32\\WindowsPowerShell\\v1.0\\",
                "%SYSTEMROOT%\\System32\\OpenSSH\\",
                "%ProgramFiles%\\dotnet",
                "%ProgramFiles%\\PowerShell\\7\\"
            };

            var currentEntries = currentSystemPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in minimal)
            {
                string expanded = Environment.ExpandEnvironmentVariables(m).TrimEnd('\\');

                // NUNCA adicionar um "essencial" cuja pasta nao existe no disco
                // (ex: OpenSSH so existe com o recurso opcional instalado;
                //  %ProgramFiles%\\PowerShell\\7 so existe com o PS7 instalado).
                // Adicionar pasta morta recria o flag "diretorio inexistente" no
                // proximo scan -> loop eterno no Guardian (corrigir -> re-adiciona
                // -> corrigir...).
                if (!Directory.Exists(expanded)) continue;

                bool found = false;
                foreach (var c in currentEntries)
                {
                    string cExpanded = Environment.ExpandEnvironmentVariables(c).TrimEnd('\\');
                    if (cExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (!found)
                {
                    // Marca como visto para evitar duplicar com entradas atuais equivalentes
                    seen.Add(m);
                    result.Add(m);
                }
            }

            foreach (var c in currentEntries)
            {
                if (seen.Add(c)) result.Add(c);
            }

            return string.Join(";", result);
        }

        public static (string Path, List<string> AddedPaths) EnsureUserPathMinimum(string currentUserPath, Dictionary<string, string> installedPaths)
        {
            var currentEntries = currentUserPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addedPaths = new List<string>();

            // Instalacoes de maquina (git, node, dotnet, 7z, winget...) ja vivem no
            // System PATH - duplicar no User PATH e poluicao que o Guardian voltava
            // a exigir (falso "PATH do Usuario Incompleto") e o kit a re-adicionar.
            var sysEntries = GetSystemPathValue().Split(';', StringSplitOptions.RemoveEmptyEntries);

            foreach (var kvp in installedPaths)
            {
                string pathToAdd = kvp.Value;
                if (string.IsNullOrWhiteSpace(pathToAdd)) continue;

                // Comparacao SEMPRE expandida: sem isso, %USERPROFILE%\.dotnet\tools nunca
                // casa com C:\Users\X\.dotnet\tools ja presente e o kit re-adicionava a
                // mesma pasta a CADA execucao (duplicatas acumuladas no User PATH).
                string expanded = Environment.ExpandEnvironmentVariables(pathToAdd).TrimEnd('\\');
                // Nunca adicionar caminho que nao existe (exceto variaveis de ambiente)
                if (!pathToAdd.Contains('%') && !Directory.Exists(expanded))
                {
                    addedPaths.Add($"Ignorado (diretorio nao existe): {pathToAdd}");
                    continue;
                }
                bool found = false;

                foreach (var c in currentEntries)
                {
                    string cExpanded = Environment.ExpandEnvironmentVariables(c).TrimEnd('\\');
                    if (cExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }

                if (!found)
                {
                    foreach (var s in sysEntries)
                    {
                        try
                        {
                            string sExpanded = Environment.ExpandEnvironmentVariables(s).TrimEnd('\\');
                            if (sExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                        }
                        catch { }
                    }
                }

                if (!found)
                {
                    result.Add(pathToAdd);
                    seen.Add(pathToAdd);
                    addedPaths.Add($"Adicionado {kvp.Key}: {pathToAdd}");
                }
            }

            foreach (var c in currentEntries)
            {
                if (seen.Add(c)) result.Add(c);
            }

            return (string.Join(";", result), addedPaths);
        }

        private static readonly object _scanCacheLock = new();
        private static Dictionary<string, string>? _scanCache;
        private static DateTime _scanCacheTime;
        private static readonly TimeSpan ScanCacheTtl = TimeSpan.FromMinutes(5);

        public static Dictionary<string, string> RecoverFromExecutableScan(IEnumerable<string>? onlyTargets = null, Action<string>? progress = null)
        {
            // SINGLE-FLIGHT: o cache era checado sob lock mas o scan rodava FORA -
            // scans concorrentes (Integridade + busca global + toggles) disparavam
            // varreduras DFS em PARALELO, saturando o disco e congelando o app.
            // Agora o lock cobre a varredura inteira: o 2o chamador espera o 1o
            // terminar (chamadores sao background threads) e reusa o cache.
            lock (_scanCacheLock)
            {
                // Cache com TTL: a varredura de disco custa ~30s e o PATH de programas
                // instalados nao muda entre scans (Integrity chama isto ate 2x por scan).
                if (_scanCache != null && DateTime.UtcNow - _scanCacheTime < ScanCacheTtl)
                    return new Dictionary<string, string>(_scanCache, StringComparer.OrdinalIgnoreCase);

            // Recuperacao baseada no disco: procura .exe conhecidos e retorna dir pai
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] searchRoots = new[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            // Nome de arquivo -> nome do alvo (1 passada por raiz em vez de 1 por alvo:
            // o antigo fazia GetFiles(AllDirectories) por alvo = 32 varreduras completas ~30s).
            // pwsh.exe fora: instalacao MSIX e so um stub (reparse) inacessivel fora do
            // WindowsApps - nunca encontrariavel; o pwsh classico ja tem check proprio.
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["winget.exe"] = "winget", ["node.exe"] = "node", ["npm.cmd"] = "npm",
                ["git.exe"] = "git", ["7z.exe"] = "7z",
                ["dotnet.exe"] = "dotnet", ["cargo.exe"] = "cargo"
            };
            if (onlyTargets != null)
            {
                var requested = new HashSet<string>(onlyTargets, StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in wanted.ToList())
                {
                    if (!requested.Contains(kvp.Value)) wanted.Remove(kvp.Key);
                }
            }
            // Subpastas que provavelmente NAO sao o local de instalacao principal do programa
            string[] avoidSubstrings = new[] {
                "node_modules", "\\.git\\", "\\.svn\\", "\\sdk\\", "\\examples\\",
                "\\test\\", "\\tests\\", "\\cache\\", "\\scratch\\", "\\resources\\app\\"
            };
            // Se o filtro onlyTargets excluiu tudo, nada foi escaneado - nao tocar no cache
            // (um scan vazio aqueceria o cache com um dicionario vazio por 5 min).
            bool didScan = wanted.Count > 0;
            // Indexador nativo USN/MFT embutido (requer elevacao): resolve os
            // alvos lendo a Master File Table em ~1-3s; o DFS cobre apenas o
            // que sobrar. Sem elevacao, FindFileDirectories retorna vazio e o
            // comportamento e exatamente o antigo (DFS completo).
            if (wanted.Count > 0)
            {
                try
                {
                    var viaIndex = NativeUsn.FindFileDirectories(wanted.Keys.ToList(), maxPerName: 8);
                    foreach (var kvp in viaIndex)
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0) continue;
                        string candidate = kvp.Value[0]; // ja ordenado: 7-Zip primeiro, depois mais raso
                        string lower = candidate.ToLowerInvariant();
                        // Preferencia do 7z: dir chamado 7-Zip/7zip ganha de 7z.exe
                        // interno de outros apps (ex: NVIDIA App). So remove do wanted
                        // quando o candidato e bom - senao o 1o 7z.exe encontrado (e.g.
                        // NVIDIA) impediria o DFS de achar o 7-Zip real depois.
                        if (kvp.Key == "7z")
                        {
                            bool curGood = lower.Contains("7-zip") || lower.Contains("7zip");
                            if (found.TryGetValue("7z", out string? prev7z))
                            {
                                string prevLower = prev7z.ToLowerInvariant();
                                bool prevGood = prevLower.Contains("7-zip") || prevLower.Contains("7zip");
                                if (prevGood && !curGood) continue;
                                if (!curGood && !prevGood &&
                                    lower.Count(c => c == '\\') > prevLower.Count(c => c == '\\'))
                                    continue;
                            }
                            found["7z"] = candidate;
                            if (curGood) wanted.Remove(kvp.Key);
                        }
                        else
                        {
                            found[kvp.Key] = candidate;
                            wanted.Remove(kvp.Key);
                        }
                    }
                    if (viaIndex.Count > 0)
                        Logger.Log($"PathRepair: {viaIndex.Count} alvo(s) resolvido(s) pelo indexador nativo USN (MFT).");
                }
                catch { }
            }
            var dfsSw = System.Diagnostics.Stopwatch.StartNew();
            const int DfsTimeoutMs = 15000; // 15s max for DFS scan (prevents 30s+ hangs)
            int rootIdx = 0;
            string[] rootNames = { "Program Files", "Program Files (x86)", "AppData\\Local", "UserProfile" };
            foreach (var root in searchRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                if (wanted.Count == 0) break;
                if (dfsSw.ElapsedMilliseconds > DfsTimeoutMs)
                {
                    Logger.Log($"PathRepair: DFS scan timeout after {dfsSw.ElapsedMilliseconds}ms ({found.Count} targets found so far)");
                    break;
                }
                progress?.Invoke($"Escaneando {rootNames[Math.Min(rootIdx, rootNames.Length - 1)]}...");
                rootIdx++;
                try
                {
                    // Uma unica passada DFS por raiz (pulando subpastas pesadas), filtrando
                    // por nome de arquivo - corta ~32 varreduras completas para 4.
                    foreach (var file in EnumerateFilesSkippingHeavy(root, dfsSw, DfsTimeoutMs))
                    {
                        string fileName = Path.GetFileName(file);
                        if (wanted.TryGetValue(fileName, out string? targetName))
                        {
                            var dir = Path.GetDirectoryName(file);
                            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                            string lower = dir.ToLowerInvariant();
                            if (avoidSubstrings.Any(s => lower.Contains(s))) continue;
                            // Preferencia do 7z: dir chamado 7-Zip/7zip ganha de 7z.exe interno
                            // de outros apps (ex: NVIDIA App); entre genericos, o mais raso.
                            // So remove do wanted quando o candidato e bom - senao o 1o 7z.exe
                            // encontrado (e.g. NVIDIA App) impediria o DFS de achar o 7-Zip real.
                            if (targetName == "7z")
                            {
                                bool curGood = lower.Contains("7-zip") || lower.Contains("7zip");
                                if (found.TryGetValue("7z", out string? prev7z))
                                {
                                    string prevLower = prev7z.ToLowerInvariant();
                                    bool prevGood = prevLower.Contains("7-zip") || prevLower.Contains("7zip");
                                    if (prevGood && !curGood) continue;
                                    if (!curGood && !prevGood &&
                                        lower.Count(c => c == '\\') > prevLower.Count(c => c == '\\'))
                                        continue;
                                }
                                found["7z"] = dir;
                                if (curGood) wanted.Remove(fileName);
                            }
                            else
                            {
                                found[targetName] = dir;
                                wanted.Remove(fileName);
                            }
                            if (wanted.Count == 0) break;
                        }
                    }
                }
                catch
                {
                    // Sem acesso (ou erro transiente): continua para as outras raizes
                }
            }
            if (didScan)
            {
                // Merge no cache compartilhado: um scan de fallback nao descarta o que
                // outro scan ja encontrou antes (TTL 5 min cobre a vida util do PATH).
                if (_scanCache == null)
                    _scanCache = new Dictionary<string, string>(found, StringComparer.OrdinalIgnoreCase);
                else
                    foreach (var kvp in found) _scanCache[kvp.Key] = kvp.Value;
                _scanCacheTime = DateTime.UtcNow;
            }
            return found;
            }
        }

        public static Dictionary<string, string> GetInstalledProgramPaths(Action<string>? progress = null)
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // winget
            string wingetPath = Path.Combine(localAppData, "Microsoft", "WindowsApps");
            if (Directory.Exists(wingetPath)) paths["winget"] = wingetPath;

            // dotnet
            string[] dotnetPaths = {
                Path.Combine(programFiles, "dotnet"),
                Path.Combine(programFiles, "dotnet", "tools"),
                Path.Combine(userProfile, ".dotnet"),
                Path.Combine(userProfile, ".dotnet", "tools")
            };
            foreach (var p in dotnetPaths)
            {
                if (Directory.Exists(p)) { paths["dotnet"] = p; break; }
            }

            // PowerShell 7
            string[] pwshPaths = {
                Path.Combine(programFiles, "PowerShell", "7"),
                Path.Combine(programFilesX86, "PowerShell", "7")
            };
            foreach (var p in pwshPaths)
            {
                if (Directory.Exists(p)) { paths["pwsh"] = p; break; }
            }

            // Git
            string[] gitPaths = {
                Path.Combine(programFiles, "Git", "cmd"),
                Path.Combine(programFilesX86, "Git", "cmd")
            };
            foreach (var p in gitPaths)
            {
                if (Directory.Exists(p)) { paths["git"] = p; break; }
            }

            // Node.js
            string[] nodePaths = {
                Path.Combine(programFiles, "nodejs"),
                Path.Combine(programFilesX86, "nodejs")
            };
            foreach (var p in nodePaths)
            {
                if (Directory.Exists(p)) { paths["node"] = p; break; }
            }

            // npm
            string npmPath = Path.Combine(appData, "npm");
            if (Directory.Exists(npmPath)) paths["npm"] = npmPath;

            // Cargo
            string cargoPath = Path.Combine(userProfile, ".cargo", "bin");
            if (Directory.Exists(cargoPath)) paths["cargo"] = cargoPath;

            // 7-Zip (unico alvo sem local padrao garantido)
            string[] zipPaths = {
                Path.Combine(programFiles, "7-Zip"),
                Path.Combine(programFilesX86, "7-Zip"),
                Path.Combine(localAppData, "Programs", "7-Zip")
            };
            foreach (var p in zipPaths)
            {
                if (Directory.Exists(p)) { paths["7z"] = p; break; }
            }

            // Scan de disco como fallback para os alvos ainda nao cobertos pelos
            // caminhos conhecidos (so os ausentes - na pratica, 7z quando nao ha 7-Zip;
            // com tudo coberto o scan nem roda, custo ~0).
            string[] allTargets = { "winget", "node", "npm", "git", "7z", "pwsh", "dotnet", "cargo" };
            var missing = allTargets.Where(t => !paths.ContainsKey(t)).ToList();
            var recovered = missing.Count > 0
                ? RecoverFromExecutableScan(missing, progress)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in recovered)
            {
                if (!paths.ContainsKey(kvp.Key)) paths[kvp.Key] = kvp.Value;
            }

            return paths;
        }

        private static readonly string[] _skipScanDirs = new[]
        {
            "node_modules", ".git", ".svn", "temp", "cache", "caches", "logs",
            "$recycle.bin", "downloads", "onedrive", "winsxs", "installer",
            "webcache", "history", "cookies", "codelldb", "explorercache",
            // Junctions classicas do perfil (apontam para AppData, ja varrido)
            "Application Data", "Local Settings", "My Documents", "NetHood",
            "PrintHood", "Recent", "SendTo", "Templates", "Start Menu",
            // Arvores gigantes que ja sao cobertas pelos checks rapidos (ou nao sao alvo):
            // nenhum alvo (winget/dotnet/git/node/npm/7z/cargo/pwsh) vive em *\Microsoft\*
            // ou em AppData\Roaming (npm ja tem check proprio) ou em packages UWP.
            "Microsoft Visual Studio", "Windows Kits", "WindowsApps", "dotnet",
            "Git", "nodejs", "PowerShell", "Microsoft Edge", "Common Files",
            "Microsoft", "AppData", "Roaming", "Packages", "ProgramData"
        };

        private static IEnumerable<string> EnumerateFilesSkippingHeavy(string root, System.Diagnostics.Stopwatch? dfsWatch = null, int timeoutMs = 0)
        {
            var stack = new Stack<(string Dir, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                // Enforce timeout per-directory (not just per-root) to prevent
                // a single huge tree (e.g. LocalAppData) from blocking for minutes.
                if (dfsWatch != null && timeoutMs > 0 && dfsWatch.ElapsedMilliseconds > timeoutMs)
                    yield break;
                var (dir, depth) = stack.Pop();
                if (!visited.Add(dir)) continue;
                // Programas instalados ficam em profundidade <= 4 (ex: LocalAppData\Programs\App\bin);
                // profundidade maior e so lixo (packages UWP, extensoes, caches) - o cap tambem
                // encerra qualquer ciclo de junction residual.
                if (depth < 4)
                {
                    IEnumerable<string> subdirs;
                    try { subdirs = Directory.EnumerateDirectories(dir); }
                    catch { continue; }
                    foreach (var sub in subdirs)
                    {
                        string name = Path.GetFileName(sub);
                        if (name.Length > 0 && Array.IndexOf(_skipScanDirs, name) >= 0)
                            continue;
                        stack.Push((sub, depth + 1));
                    }
                }
                // Filtro no kernel (FindFirstFile com mascara) em vez de listar todos os nomes.
                foreach (var pattern in new[] { "*.exe", "*.cmd" })
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir, pattern); }
                    catch { continue; }
                    foreach (var f in files)
                        yield return f;
                }
            }
        }

        /// <summary>
        /// Analisa e repara a variável PATH, removendo entradas inválidas, duplicadas e perigosas.
        /// </summary>
        public static (bool Changed, string NewPath, string LogMessage) RepairPath(string originalPath)
        {
            if (string.IsNullOrWhiteSpace(originalPath))
                return (false, originalPath, "PATH vazio, nenhuma ação necessária.");

            var entries = DiagnosePath(originalPath, "User");
            var (newPath, actions) = RepairPathEntries(entries, "User");
            bool changed = !originalPath.Equals(newPath, StringComparison.OrdinalIgnoreCase);
            string logMessage = changed
                ? $"PATH reformatado. Ações: {string.Join("; ", actions)}"
                : "PATH já está limpo.";

            return (changed, newPath, logMessage);
        }

        /// <summary>
        /// Verifica se o PATH está saudável (sem problemas críticos).
        /// </summary>
        public static bool IsPathHealthy(string pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue)) return false;

            var entries = DiagnosePath(pathValue, "User");
            return entries.All(e => e.Problem == PathEntryProblem.None);
        }

        // --- API de leitura/escrita do PATH (usada pelo Explorador de PATH) ----

        public const string SystemPathRegistryKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        public const string UserPathRegistryKey = @"Environment";

        public static string GetSystemPathValue()
        {
            try
            {
                return Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(SystemPathRegistryKey)?.GetValue("Path")?.ToString() ?? "";
            }
            catch { return ""; }
        }

        public static string GetUserPathValue()
        {
            try
            {
                return Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(UserPathRegistryKey)?.GetValue("Path")?.ToString() ?? "";
            }
            catch { return ""; }
        }

        public static bool SetSystemPathValue(string newPath)
            => SetPathValue(Microsoft.Win32.Registry.LocalMachine, SystemPathRegistryKey, newPath);

        public static bool SetUserPathValue(string newPath)
            => SetPathValue(Microsoft.Win32.Registry.CurrentUser, UserPathRegistryKey, newPath);

        private static bool SetPathValue(Microsoft.Win32.RegistryKey baseKey, string subKeyPath, string newPath)
        {
            try
            {
                // hint Unknown: o tipo e decidido por auto-deteccao (%VAR% -> REG_EXPAND_SZ)
                bool ok = RegistryOwnership.TrySetValueWithOwnershipFallback(baseKey, subKeyPath, "Path", newPath, Microsoft.Win32.RegistryValueKind.Unknown);
                if (ok) BroadcastEnvironmentChange();
                return ok;
            }
            catch { return false; }
        }

        /// <summary>
        /// Adiciona UMA entrada ao PATH (System ou User) com dedup por caminho
        /// expandido, preservando REG_EXPAND_SZ. Retorna true se adicionou (ou
        /// se ja existia - idempotente).
        /// </summary>
        public static bool AddSinglePathEntry(string pathType, string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return false;
            string target = Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\');

            string current = pathType.Equals("User", StringComparison.OrdinalIgnoreCase)
                ? GetUserPathValue() : GetSystemPathValue();

            var entries = new List<string>();
            foreach (var raw in current.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                entries.Add(raw);
                try
                {
                    if (Environment.ExpandEnvironmentVariables(raw).TrimEnd('\\')
                        .Equals(target, StringComparison.OrdinalIgnoreCase))
                        return true; // ja presente (idempotente)
                }
                catch { }
            }

            entries.Add(entry);
            string newPath = string.Join(";", entries);

            return pathType.Equals("User", StringComparison.OrdinalIgnoreCase)
                ? SetUserPathValue(newPath) : SetSystemPathValue(newPath);
        }

        /// <summary>
        /// Avisa o Explorer (e todos os processos) que as variaveis de ambiente mudaram.
        /// Roda em background (fire-and-forget): o broadcast para HWND_BROADCAST pode
        /// bloquear ate o timeout por janela travada - na UI thread isso congelaria o app.
        /// </summary>
        private static void BroadcastEnvironmentChange()
        {
            try
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _ = NativeEnv.SendMessageTimeout(
                            new IntPtr(0xffff), 0x001a, IntPtr.Zero, "Environment", 0x0002, 2000, out _);
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static class NativeEnv
        {
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
            public static extern IntPtr SendMessageTimeout(
                IntPtr hWnd, int Msg, IntPtr wParam, string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);
        }

        /// <summary>
        /// Candidato a entrada ausente do PATH (essencial de sistema ou programa instalado).
        /// </summary>
        public class PathEntryCandidate
        {
            public string Label { get; set; } = "";
            public string Path { get; set; } = "";
            public string Detail { get; set; } = "";
            public bool CanAdd { get; set; }
        }

        /// <summary>
        /// Entradas minimas do System PATH que ainda nao estao presentes (comparadas
        /// por caminho expandido). CanAdd=true sempre - o usuario pode querer adicionar
        /// mesmo sem a pasta existir (ex: %ProgramFiles%\PowerShell\7 com PS7 via MSIX);
        /// a UI confirma quando a pasta nao existe.
        /// </summary>
        public static List<PathEntryCandidate> GetMissingSystemEntries(string currentSystemPath)
        {
            var result = new List<PathEntryCandidate>();
            string[] minimal = {
                "%SystemRoot%\\system32",
                "%SystemRoot%",
                "%SystemRoot%\\System32\\Wbem",
                "%SYSTEMROOT%\\System32\\WindowsPowerShell\\v1.0\\",
                "%SYSTEMROOT%\\System32\\OpenSSH\\",
                "%ProgramFiles%\\dotnet",
                "%ProgramFiles%\\PowerShell\\7\\"
            };

            var currentEntries = currentSystemPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var m in minimal)
            {
                bool present = false;
                string expanded = Environment.ExpandEnvironmentVariables(m).TrimEnd('\\');
                foreach (var c in currentEntries)
                {
                    try
                    {
                        if (Environment.ExpandEnvironmentVariables(c).TrimEnd('\\')
                            .Equals(expanded, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                    }
                    catch { }
                }
                if (present) continue;

                bool exists;
                try { exists = Directory.Exists(expanded); }
                catch { exists = false; }

                result.Add(new PathEntryCandidate
                {
                    Label = m,
                    Path = m,
                    Detail = exists
                        ? "Essencial do sistema (pasta existe - recomendado adicionar)"
                        : "Essencial do sistema (pasta nao existe no PC - confirmar para adicionar)",
                    CanAdd = true
                });
            }
            return result;
        }

        /// <summary>
        /// Programas instalados detectados (winget/dotnet/pwsh/git/node/npm/cargo/7z)
        /// cujo diretorio NAO esta no User PATH - o que o kit ve que falta.
        /// </summary>
        public static List<PathEntryCandidate> GetMissingInstalledEntries(string currentUserPath, Action<string>? progress = null)
        {
            var result = new List<PathEntryCandidate>();
            var installed = GetInstalledProgramPaths(progress);
            if (installed.Count == 0) return result;

            var currentEntries = currentUserPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var kvp in installed)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value)) continue;
                string target = kvp.Value.TrimEnd('\\');
                bool present = false;
                foreach (var c in currentEntries)
                {
                    try
                    {
                        if (Environment.ExpandEnvironmentVariables(c).TrimEnd('\\')
                            .Equals(target, StringComparison.OrdinalIgnoreCase)) { present = true; break; }
                    }
                    catch { }
                }
                if (present) continue;

                result.Add(new PathEntryCandidate
                {
                    Label = kvp.Key,
                    Path = kvp.Value,
                    Detail = "Programa instalado detectado pelo kit",
                    CanAdd = true
                });
            }
            return result;
        }
    }
}