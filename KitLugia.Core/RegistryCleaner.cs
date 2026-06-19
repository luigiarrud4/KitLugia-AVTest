using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;

namespace KitLugia.Core
{
    /// <summary>
    /// Auditor de registro conservador — verifica entradas órfãs conhecidas e de baixo risco.
    /// NÃO remove entradas de sistema críticas. Foca em:
    /// - Entradas de startup apontando para arquivos inexistentes
    /// - Extensões de arquivo sem handler
    /// - Entradas de desinstalação de programas removidos
    /// - MRU (Most Recently Used) lists
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class RegistryCleaner
    {
        public record RegistryIssue(
            string Category,
            string Description,
            string RegistryPath,
            string ValueName,
            string CurrentValue,
            bool IsSafe
        );

        public record CleanResult(int IssuesFound, int IssuesCleaned, int IssuesSkipped, List<string> Log);

        public static bool CanCleanIssue(RegistryIssue issue)
        {
            if (issue.IsSafe) return true;

            if (issue.Category == "Programas" &&
                issue.RegistryPath.Contains(@"\Uninstall\", StringComparison.OrdinalIgnoreCase) &&
                issue.RegistryPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (issue.Category.StartsWith("Inicializa", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(issue.ValueName) &&
                IsStartupRunKey(issue.RegistryPath))
            {
                return true;
            }

            return false;
        }

        public static string GetIssueActionLabel(RegistryIssue issue)
        {
            if (CanCleanIssue(issue)) return issue.IsSafe ? "Automático" : "Pode limpar";
            if (issue.Category.StartsWith("Extens", StringComparison.OrdinalIgnoreCase)) return "Bloqueado";
            if (issue.Category.StartsWith("Hist", StringComparison.OrdinalIgnoreCase)) return "Bloqueado";
            return "Revisar";
        }

        public static string GetIssueRiskWarning(RegistryIssue issue)
        {
            if (issue.Category == "Programas")
                return "Remove a entrada da lista de programas instalados. Nao apaga o app/jogo e pode esconder o desinstalador do Windows.";

            if (issue.Category.StartsWith("Inicializa", StringComparison.OrdinalIgnoreCase))
                return "Remove a inicializacao automatica desse app. O programa nao e desinstalado.";

            if (issue.Category.StartsWith("Extens", StringComparison.OrdinalIgnoreCase))
                return "Bloqueado para evitar quebrar associacoes de arquivo, menus de contexto ou integracoes de apps.";

            if (issue.Category.StartsWith("Hist", StringComparison.OrdinalIgnoreCase))
                return "Bloqueado: historico do Explorer nao deve ser apagado pelo limpador de registro.";

            return "Item apenas para auditoria. Pesquise antes de remover manualmente.";
        }

        public static string FormatIssuesForResearch(IEnumerable<RegistryIssue> issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("KitLugia - Relatório de Auditoria do Registro");
            sb.AppendLine($"Gerado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var issue in issues)
            {
                sb.AppendLine($"Categoria: {issue.Category}");
                sb.AppendLine($"Status: {(issue.IsSafe ? "Automático" : "Revisão manual")}");
                sb.AppendLine($"Descrição: {issue.Description}");
                sb.AppendLine($"Caminho: {issue.RegistryPath}");
                sb.AppendLine($"Valor: {issue.ValueName}");
                sb.AppendLine($"Dados atuais: {issue.CurrentValue}");
                sb.AppendLine($"Acao no Kit: {GetIssueActionLabel(issue)}");
                sb.AppendLine($"Aviso: {GetIssueRiskWarning(issue)}");
                sb.AppendLine(new string('-', 72));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escaneia o registro em busca de entradas órfãs seguras para remover.
        /// </summary>
        public static List<RegistryIssue> ScanForIssues(Action<string>? progress = null)
        {
            var issues = new List<RegistryIssue>();

            progress?.Invoke("Verificando entradas de inicialização...");
            issues.AddRange(ScanStartupEntries());

            progress?.Invoke("Auditando programas desinstalados...");
            issues.AddRange(ScanUninstallEntries());

            progress?.Invoke("Verificando caminhos de aplicativos (App Paths)...");
            issues.AddRange(ScanAppPaths());

            progress?.Invoke("Auditando referências COM/ActiveX...");
            issues.AddRange(ScanComReferences());

            progress?.Invoke("Verificando Shared DLLs órfãs...");
            issues.AddRange(ScanSharedDlls());

            progress?.Invoke("Verificando manipuladores de ícone...");
            issues.AddRange(ScanIconHandlers());

            progress?.Invoke("Verificando MRU (arquivos recentes)...");
            issues.AddRange(ScanMruEntries());

            progress?.Invoke("Auditando extensões de arquivo órfãs...");
            issues.AddRange(ScanOrphanedFileExtensions());

            Logger.Log($"[RegistryCleaner] Scan concluído: {issues.Count} problemas encontrados.");
            return issues;
        }

        /// <summary>
        /// Remove apenas entradas explicitamente marcadas como seguras.
        /// No modo atual, o scanner é conservador e deixa achados como revisão manual.
        /// </summary>
        public static CleanResult CleanIssues(List<RegistryIssue> issues, Action<string>? progress = null)
        {
            int cleaned = 0;
            int skipped = 0;
            var log = new List<string>();

            foreach (var issue in issues)
            {
                if (!issue.IsSafe)
                {
                    skipped++;
                    log.Add($"⏭️ Pulado (não seguro): {issue.Description}");
                    continue;
                }

                try
                {
                    progress?.Invoke($"Removendo: {issue.Description}...");

                    // Determina a hive correta
                    RegistryKey? baseKey = issue.RegistryPath.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
                        ? Registry.CurrentUser
                        : issue.RegistryPath.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                            ? Registry.LocalMachine
                            : null;

                    if (baseKey == null)
                    {
                        skipped++;
                        log.Add($"⚠️ Hive desconhecida: {issue.RegistryPath}");
                        continue;
                    }

                    // Remove o prefixo da hive do caminho
                    string subPath = issue.RegistryPath
                        .Replace("HKEY_CURRENT_USER\\", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("HKEY_LOCAL_MACHINE\\", "", StringComparison.OrdinalIgnoreCase);

                    using var key = baseKey.OpenSubKey(subPath, writable: true);
                    if (key == null)
                    {
                        skipped++;
                        log.Add($"⚠️ Chave não encontrada: {issue.RegistryPath}");
                        continue;
                    }

                    if (!string.IsNullOrEmpty(issue.ValueName))
                    {
                        key.DeleteValue(issue.ValueName, throwOnMissingValue: false);
                    }

                    cleaned++;
                    log.Add($"✅ Removido: {issue.Description}");
                }
                catch (Exception ex)
                {
                    skipped++;
                    log.Add($"❌ Erro ao remover '{issue.Description}': {ex.Message}");
                }
            }

            Logger.Log($"[RegistryCleaner] Limpeza: {cleaned} removidos, {skipped} pulados.");
            return new CleanResult(issues.Count, cleaned, skipped, log);
        }

        public static CleanResult CleanSelectedIssues(IEnumerable<RegistryIssue> selectedIssues, Action<string>? progress = null)
        {
            var issues = selectedIssues.ToList();
            int cleaned = 0;
            int skipped = 0;
            var log = new List<string>();

            string backupDir = Path.Combine(
                Path.GetTempPath(),
                "KitLugia",
                "RegistryBackups",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));

            foreach (var issue in issues)
            {
                if (!CanCleanIssue(issue))
                {
                    skipped++;
                    log.Add($"Pulado (bloqueado): {issue.Description}");
                    continue;
                }

                try
                {
                    progress?.Invoke($"Backup: {issue.Description}...");
                    Directory.CreateDirectory(backupDir);
                    BackupIssue(issue, backupDir, cleaned + skipped + 1);

                    progress?.Invoke($"Removendo: {issue.Description}...");
                    DeleteIssue(issue);

                    cleaned++;
                    log.Add($"Removido: {issue.Description}");
                }
                catch (Exception ex)
                {
                    skipped++;
                    log.Add($"Erro ao remover '{issue.Description}': {ex.Message}");
                }
            }

            if (cleaned > 0 || Directory.Exists(backupDir))
            {
                log.Insert(0, $"Backup criado em: {backupDir}");
            }

            Logger.Log($"[RegistryCleaner] Limpeza manual: {cleaned} removidos, {skipped} pulados.");
            return new CleanResult(issues.Count, cleaned, skipped, log);
        }

        // ─── Scanners ────────────────────────────────────────────────────────────

        /// <summary>
        /// Verifica entradas de startup (Run/RunOnce) que apontam para arquivos inexistentes.
        /// </summary>
        private static List<RegistryIssue> ScanStartupEntries()
        {
            var issues = new List<RegistryIssue>();
            var runKeys = new[]
            {
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            };

            foreach (var keyPath in runKeys)
            {
                try
                {
                    RegistryKey? baseKey = keyPath.StartsWith("HKEY_CURRENT_USER")
                        ? Registry.CurrentUser
                        : Registry.LocalMachine;

                    string subPath = keyPath
                        .Replace("HKEY_CURRENT_USER\\", "")
                        .Replace("HKEY_LOCAL_MACHINE\\", "");

                    using var key = baseKey.OpenSubKey(subPath, writable: false);
                    if (key == null) continue;

                    foreach (var valueName in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(valueName)) continue;

                        string? value = key.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(value)) continue;

                        string exePath = ExtractExePath(value);

                        // Verifica se o arquivo existe
                        if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !IsSystemPath(exePath))
                        {
                            issues.Add(new RegistryIssue(
                                Category: "Inicialização",
                                Description: $"Startup órfão: '{valueName}' → arquivo não encontrado",
                                RegistryPath: keyPath,
                                ValueName: valueName,
                                CurrentValue: value,
                                IsSafe: false
                            ));
                        }
                    }
                }
                catch { /* Ignora erros de acesso */ }
            }

            return issues;
        }

        /// <summary>
        /// Verifica entradas de desinstalação de programas que já foram removidos.
        /// </summary>
        private static List<RegistryIssue> ScanUninstallEntries()
        {
            var issues = new List<RegistryIssue>();
            var uninstallPaths = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var path in uninstallPaths)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = key.OpenSubKey(subKeyName, writable: false);
                            if (subKey == null) continue;

                            string? displayName = subKey.GetValue("DisplayName")?.ToString();
                            string? installLocation = subKey.GetValue("InstallLocation")?.ToString();
                            string? uninstallString = subKey.GetValue("UninstallString")?.ToString();

                            if (string.IsNullOrEmpty(displayName)) continue;

                            // Verifica se InstallLocation existe
                            if (!string.IsNullOrEmpty(installLocation) && !Directory.Exists(installLocation) && !IsSystemPath(installLocation))
                            {
                                issues.Add(new RegistryIssue(
                                    Category: "Programas",
                                    Description: $"Entrada órfã: '{displayName}' (pasta removida)",
                                    RegistryPath: $"HKEY_LOCAL_MACHINE\\{path}\\{subKeyName}",
                                    ValueName: "",
                                    CurrentValue: installLocation,
                                    IsSafe: true
                                ));
                                continue;
                            }

                            // Verifica se UninstallString aponta para algo inexistente
                            if (!string.IsNullOrEmpty(uninstallString))
                            {
                                string exePath = ExtractExePath(uninstallString);
                                if (!string.IsNullOrEmpty(exePath) && !File.Exists(exePath) && !IsSystemPath(exePath))
                                {
                                    issues.Add(new RegistryIssue(
                                        Category: "Programas",
                                        Description: $"Desinstalador órfão: '{displayName}'",
                                        RegistryPath: $"HKEY_LOCAL_MACHINE\\{path}\\{subKeyName}",
                                        ValueName: "UninstallString",
                                        CurrentValue: uninstallString,
                                        IsSafe: true
                                    ));
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return issues;
        }

        /// <summary>
        /// Verifica listas MRU (Most Recently Used) de arquivos que não existem mais.
        /// </summary>
        private static List<RegistryIssue> ScanMruEntries()
        {
            var issues = new List<RegistryIssue>();
            var mruPaths = new[]
            {
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
            };

            foreach (var keyPath in mruPaths)
            {
                try
                {
                    string subPath = keyPath.Replace("HKEY_CURRENT_USER\\", "");
                    using var key = Registry.CurrentUser.OpenSubKey(subPath, writable: false);
                    if (key == null) continue;

                    // Conta entradas — se houver muitas, sugere limpeza
                    int count = key.GetValueNames().Length + key.GetSubKeyNames().Length;
                    if (count > 50)
                    {
                        issues.Add(new RegistryIssue(
                            Category: "Histórico",
                            Description: $"Lista MRU grande ({count} entradas): {Path.GetFileName(keyPath)}",
                            RegistryPath: keyPath,
                            ValueName: "MRUListEx",
                            CurrentValue: $"{count} entradas",
                            IsSafe: false
                        ));
                    }
                }
                catch { }
            }

            return issues;
        }

        /// <summary>
        /// Verifica extensões de arquivo que apontam para handlers inexistentes.
        /// Apenas extensões não-sistema são verificadas.
        /// </summary>
        private static List<RegistryIssue> ScanOrphanedFileExtensions()
        {
            var issues = new List<RegistryIssue>();

            // Extensões de sistema que nunca devem ser tocadas
            var systemExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".exe", ".dll", ".sys", ".bat", ".cmd", ".msi", ".inf", ".reg",
                ".lnk", ".url", ".scr", ".cpl", ".msc", ".pif", ".com"
            };

            try
            {
                using var classesRoot = Registry.ClassesRoot;
                foreach (var extName in classesRoot.GetSubKeyNames())
                {
                    if (!extName.StartsWith(".")) continue;
                    if (systemExtensions.Contains(extName)) continue;

                    try
                    {
                        using var extKey = classesRoot.OpenSubKey(extName, writable: false);
                        if (extKey == null) continue;

                        string? progId = extKey.GetValue("")?.ToString();
                        if (string.IsNullOrEmpty(progId)) continue;

                        // Verifica se o ProgID existe
                        using var progIdKey = classesRoot.OpenSubKey(progId, writable: false);
                        if (progIdKey == null)
                        {
                            issues.Add(new RegistryIssue(
                                Category: "Extensões",
                                Description: $"Extensão órfã: '{extName}' → ProgID '{progId}' não encontrado",
                                RegistryPath: $"HKEY_CLASSES_ROOT\\{extName}",
                                ValueName: "",
                                CurrentValue: progId,
                                IsSafe: false // Conservador — não remover automaticamente
                            ));
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return issues;
        }

        /// <summary>
        /// Verifica entradas de App Paths que apontam para executáveis inexistentes.
        /// </summary>
        private static List<RegistryIssue> ScanAppPaths()
        {
            var issues = new List<RegistryIssue>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", writable: false);
                if (key == null) return issues;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var appKey = key.OpenSubKey(subKeyName, writable: false);
                        if (appKey == null) continue;

                        string? exePath = appKey.GetValue("")?.ToString();
                        if (string.IsNullOrEmpty(exePath)) continue;
                        if (IsSystemPath(exePath)) continue;

                        string expanded = Environment.ExpandEnvironmentVariables(exePath);
                        if (!File.Exists(expanded) && !Directory.Exists(expanded))
                        {
                            issues.Add(new RegistryIssue(
                                Category: "Programas",
                                Description: $"App Path inválido: '{subKeyName}' → arquivo não encontrado",
                                RegistryPath: $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{subKeyName}",
                                ValueName: "",
                                CurrentValue: exePath,
                                IsSafe: true
                            ));
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return issues;
        }

        /// <summary>
        /// Verifica referências COM (InprocServer32/LocalServer32) que apontam para DLLs/exe inexistentes.
        /// </summary>
        private static List<RegistryIssue> ScanComReferences()
        {
            var issues = new List<RegistryIssue>();
            var clsidPath = @"HKEY_CLASSES_ROOT\CLSID";

            try
            {
                using var clsidKey = Registry.ClassesRoot.OpenSubKey("CLSID", writable: false);
                if (clsidKey == null) return issues;

                int checkedCount = 0;
                foreach (var guid in clsidKey.GetSubKeyNames())
                {
                    if (checkedCount > 500) break; // Limite para performance
                    checkedCount++;

                    try
                    {
                        using var guidKey = clsidKey.OpenSubKey(guid, writable: false);
                        if (guidKey == null) continue;

                        // Verifica InprocServer32
                        string? dllPath = GetComServerPath(guidKey, "InprocServer32");
                        if (!string.IsNullOrEmpty(dllPath) && !IsSystemPath(dllPath))
                        {
                            string expanded = Environment.ExpandEnvironmentVariables(dllPath);
                            if (!File.Exists(expanded))
                            {
                                issues.Add(new RegistryIssue(
                                    Category: "Extensões",
                                    Description: $"COM inválido: '{guid}' → DLL não encontrada",
                                    RegistryPath: $@"{clsidPath}\{guid}\InprocServer32",
                                    ValueName: "",
                                    CurrentValue: dllPath,
                                    IsSafe: false
                                ));
                                continue;
                            }
                        }

                        // Verifica LocalServer32
                        string? exePath = GetComServerPath(guidKey, "LocalServer32");
                        if (!string.IsNullOrEmpty(exePath) && !IsSystemPath(exePath))
                        {
                            string expanded = Environment.ExpandEnvironmentVariables(exePath);
                            if (!File.Exists(expanded))
                            {
                                issues.Add(new RegistryIssue(
                                    Category: "Extensões",
                                    Description: $"COM inválido: '{guid}' → EXE não encontrado",
                                    RegistryPath: $@"{clsidPath}\{guid}\LocalServer32",
                                    ValueName: "",
                                    CurrentValue: exePath,
                                    IsSafe: false
                                ));
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return issues;
        }

        private static string? GetComServerPath(RegistryKey guidKey, string subKeyName)
        {
            try
            {
                using var serverKey = guidKey.OpenSubKey(subKeyName, writable: false);
                if (serverKey == null) return null;
                string? path = serverKey.GetValue("")?.ToString();
                if (string.IsNullOrEmpty(path)) return null;
                // Extrai apenas o caminho do executável (remove argumentos)
                path = ExtractExePathFromCom(path);
                return path;
            }
            catch { return null; }
        }

        private static string ExtractExePathFromCom(string value)
        {
            if (value.StartsWith("\""))
            {
                int end = value.IndexOf('"', 1);
                if (end > 1) return value.Substring(1, end - 1);
            }
            int spaceIdx = value.IndexOf(' ');
            if (spaceIdx > 0) return value.Substring(0, spaceIdx);
            return value;
        }

        /// <summary>
        /// Verifica SharedDLLs — referências a DLLs que não existem mais no disco.
        /// </summary>
        private static List<RegistryIssue> ScanSharedDlls()
        {
            var issues = new List<RegistryIssue>();
            var paths = new[]
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs",
            };

            foreach (var keyPath in paths)
            {
                try
                {
                    RegistryKey? baseKey = Registry.LocalMachine;
                    string subPath = keyPath.Replace("HKEY_LOCAL_MACHINE\\", "");

                    using var key = baseKey.OpenSubKey(subPath, writable: false);
                    if (key == null) continue;

                    foreach (var valueName in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(valueName)) continue;
                        if (IsSystemPath(valueName)) continue;

                        string expanded = Environment.ExpandEnvironmentVariables(valueName);
                        if (!File.Exists(expanded))
                        {
                            issues.Add(new RegistryIssue(
                                Category: "Programas",
                                Description: $"Shared DLL órfã: '{Path.GetFileName(valueName)}'",
                                RegistryPath: keyPath,
                                ValueName: valueName,
                                CurrentValue: valueName,
                                IsSafe: true
                            ));
                        }
                    }
                }
                catch { }
            }
            return issues;
        }

        /// <summary>
        /// Verifica entries de shell icon handlers que apontam para DLLs inexistentes.
        /// </summary>
        private static List<RegistryIssue> ScanIconHandlers()
        {
            var issues = new List<RegistryIssue>();
            var handlerPaths = new[]
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell IconOverlayIdentifiers",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\*\shellex\IconHandler",
            };

            foreach (var keyPath in handlerPaths)
            {
                try
                {
                    string subPath = keyPath.Replace("HKEY_LOCAL_MACHINE\\", "");
                    using var key = Registry.LocalMachine.OpenSubKey(subPath, writable: false);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var handlerKey = key.OpenSubKey(subKeyName, writable: false);
                            if (handlerKey == null) continue;

                            string? guid = handlerKey.GetValue("")?.ToString();
                            if (string.IsNullOrEmpty(guid) || !guid.StartsWith("{")) continue;

                            // Lookup CLSID
                            using var clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{guid}\InprocServer32", writable: false);
                            if (clsidKey == null)
                            {
                                issues.Add(new RegistryIssue(
                                    Category: "Extensões",
                                    Description: $"Handler de ícone órfão: '{subKeyName}' → GUID '{guid}' não registrado",
                                    RegistryPath: keyPath + "\\" + subKeyName,
                                    ValueName: "",
                                    CurrentValue: guid,
                                    IsSafe: false
                                ));
                                continue;
                            }

                            string? dllPath = clsidKey.GetValue("")?.ToString();
                            if (string.IsNullOrEmpty(dllPath)) continue;

                            string expanded = Environment.ExpandEnvironmentVariables(dllPath);
                            if (!File.Exists(expanded) && !IsSystemPath(dllPath))
                            {
                                issues.Add(new RegistryIssue(
                                    Category: "Extensões",
                                    Description: $"Handler de ícone inválido: '{subKeyName}' → DLL não encontrada",
                                    RegistryPath: keyPath + "\\" + subKeyName,
                                    ValueName: "",
                                    CurrentValue: dllPath,
                                    IsSafe: false
                                ));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return issues;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static string ExtractExePath(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "";

            commandLine = commandLine.Trim();
            commandLine = Environment.ExpandEnvironmentVariables(commandLine);

            if (commandLine.StartsWith("rundll32", StringComparison.OrdinalIgnoreCase) ||
                commandLine.StartsWith("regsvr32", StringComparison.OrdinalIgnoreCase) ||
                commandLine.StartsWith("cmd", StringComparison.OrdinalIgnoreCase) ||
                commandLine.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
                commandLine.StartsWith("pwsh", StringComparison.OrdinalIgnoreCase) ||
                commandLine.StartsWith("explorer", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            // Caminho entre aspas: "C:\Program Files\App\app.exe" /args
            if (commandLine.StartsWith("\""))
            {
                int end = commandLine.IndexOf('"', 1);
                if (end > 1) return commandLine.Substring(1, end - 1);
            }

            int exeIdx = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx > 0)
            {
                return commandLine.Substring(0, exeIdx + 4).Trim();
            }

            // Caminho sem aspas: C:\Program Files\App\app.exe /args
            int spaceIdx = commandLine.IndexOf(' ');
            if (spaceIdx > 0) return commandLine.Substring(0, spaceIdx);

            return commandLine;
        }

        private static bool IsStartupRunKey(string registryPath)
        {
            return registryPath.EndsWith(@"\Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase) ||
                   registryPath.EndsWith(@"\Software\Microsoft\Windows\CurrentVersion\RunOnce", StringComparison.OrdinalIgnoreCase) ||
                   registryPath.EndsWith(@"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase) ||
                   registryPath.EndsWith(@"\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", StringComparison.OrdinalIgnoreCase);
        }

        private static void BackupIssue(RegistryIssue issue, string backupDir, int index)
        {
            string fileName = $"{index:000}-{SanitizeFileName(issue.Category)}-{SanitizeFileName(issue.Description)}.reg";
            if (fileName.Length > 140)
            {
                fileName = $"{index:000}-{SanitizeFileName(issue.Category)}.reg";
            }

            string backupPath = Path.Combine(backupDir, fileName);

            var startInfo = new ProcessStartInfo
            {
                FileName = "reg.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("export");
            startInfo.ArgumentList.Add(issue.RegistryPath);
            startInfo.ArgumentList.Add(backupPath);
            startInfo.ArgumentList.Add("/y");

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);

            if (process == null || process.ExitCode != 0)
            {
                throw new InvalidOperationException("Nao foi possivel exportar backup do registro.");
            }
        }

        private static void DeleteIssue(RegistryIssue issue)
        {
            var (baseKey, subPath) = ResolveRegistryPath(issue.RegistryPath);
            if (baseKey == null || string.IsNullOrWhiteSpace(subPath))
            {
                throw new InvalidOperationException("Hive de registro nao suportada.");
            }

            if (!string.IsNullOrWhiteSpace(issue.ValueName))
            {
                using var key = baseKey.OpenSubKey(subPath, writable: true);
                if (key == null) throw new InvalidOperationException("Chave nao encontrada.");
                key.DeleteValue(issue.ValueName, throwOnMissingValue: false);
                return;
            }

            int lastSlash = subPath.LastIndexOf('\\');
            if (lastSlash <= 0) throw new InvalidOperationException("Caminho de subchave invalido.");

            string parentPath = subPath[..lastSlash];
            string childName = subPath[(lastSlash + 1)..];

            using var parentKey = baseKey.OpenSubKey(parentPath, writable: true);
            if (parentKey == null) throw new InvalidOperationException("Chave pai nao encontrada.");
            parentKey.DeleteSubKeyTree(childName, throwOnMissingSubKey: false);
        }

        private static (RegistryKey? BaseKey, string SubPath) ResolveRegistryPath(string registryPath)
        {
            if (registryPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                return (Registry.CurrentUser, registryPath["HKEY_CURRENT_USER\\".Length..]);

            if (registryPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                return (Registry.LocalMachine, registryPath["HKEY_LOCAL_MACHINE\\".Length..]);

            return (null, "");
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return sanitized.Length == 0 ? "registro" : sanitized;
        }

        private static bool IsSystemPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string lower = path.ToLowerInvariant();
            return lower.Contains(@"windows\system32") ||
                   lower.Contains(@"windows\syswow64") ||
                   lower.Contains(@"windows\winsxs") ||
                   lower.Contains(@"program files\windows") ||
                   lower.Contains(@"program files (x86)\windows") ||
                   lower.StartsWith(@"c:\windows\") ||
                   lower.Contains("microsoft.net") ||
                   lower.Contains("dotnet");
        }
    }
}
