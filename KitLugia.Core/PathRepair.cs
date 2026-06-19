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
            catch { return false; }
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
            var repaired = new List<string>();
            var actions = new List<string>();

            foreach (var entry in entries)
            {
                switch (entry.Problem)
                {
                    case PathEntryProblem.None:
                        repaired.Add(entry.CleanValue);
                        break;
                    case PathEntryProblem.Missing:
                        if (IsDotnetTools(entry))
                        {
                            try
                            {
                                Directory.CreateDirectory(entry.ExpandedValue);
                                repaired.Add(entry.CleanValue);
                                actions.Add($"Criada pasta: {entry.CleanValue}");
                            }
                            catch
                            {
                                actions.Add($"FALHA ao criar pasta: {entry.CleanValue}");
                            }
                        }
                        else
                        {
                            // Não remover, apenas manter e adicionar ao log
                            repaired.Add(entry.CleanValue);
                            actions.Add($"Mantido (não existe): {entry.CleanValue}");
                        }
                        break;
                    case PathEntryProblem.WrongLocation:
                        // Não remover caminhos de local errado, apenas manter
                        repaired.Add(entry.CleanValue);
                        if (pathType == "User")
                        {
                            actions.Add($"Mantido (caminho de sistema no User): {entry.CleanValue}");
                        }
                        else
                        {
                            actions.Add($"Mantido (caminho de usuário no System): {entry.CleanValue}");
                        }
                        break;
                    case PathEntryProblem.Duplicate:
                        // Manter apenas a primeira ocorrência
                        repaired.Add(entry.CleanValue);
                        actions.Add($"Mantido (duplicado): {entry.CleanValue}");
                        break;
                    case PathEntryProblem.Junk:
                        // Manter lixo de desenvolvimento
                        repaired.Add(entry.CleanValue);
                        actions.Add($"Mantido (lixo): {entry.CleanValue}");
                        break;
                    case PathEntryProblem.Orphan:
                        // Manter órfãos
                        repaired.Add(entry.CleanValue);
                        actions.Add($"Mantido (órfão): {entry.CleanValue}");
                        break;
                    case PathEntryProblem.SyntaxError:
                        // Manter mesmo com erro de sintaxe
                        repaired.Add(entry.CleanValue);
                        actions.Add($"Mantido (sintaxe inválida): {entry.CleanValue}");
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
                bool found = false;
                foreach (var c in currentEntries)
                {
                    string cExpanded = Environment.ExpandEnvironmentVariables(c).TrimEnd('\\');
                    if (cExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                }
                if (!found) result.Add(m);
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

            foreach (var kvp in installedPaths)
            {
                string pathToAdd = kvp.Value;
                string expanded = pathToAdd.TrimEnd('\\');
                bool found = false;

                foreach (var c in currentEntries)
                {
                    string cExpanded = Environment.ExpandEnvironmentVariables(c).TrimEnd('\\');
                    if (cExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
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

        public static Dictionary<string, string> GetInstalledProgramPaths()
        {
            var paths = new Dictionary<string, string>();
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

            return paths;
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
    }
}