using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler; // Requer NuGet: TaskScheduler
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class StartupManager
    {
        private const string KitLugiaStartupKey = @"Software\KitLugia\StartupApps";

        private static List<StartupAppDetails>? _cachedApps;
        private static List<StartupAppDetails>? _cachedAppsFast;
        private static DateTime _cacheTimestamp;
        private static DateTime _cacheTimestampFast;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

        private static string MakeKey(string name, string? exePath)
        {
            string safePath = !string.IsNullOrEmpty(exePath) ? exePath : name;
            return $"{name}|{safePath}";
        }

        private static void InvalidateCache()
        {
            _cachedApps = null;
            _cachedAppsFast = null;
        }

        private static List<StartupAppDetails> GetCachedApps()
        {
            if (_cachedApps != null && (DateTime.UtcNow - _cacheTimestamp) < CacheLifetime)
                return _cachedApps;
            _cachedApps = BuildAppList(false);
            _cacheTimestamp = DateTime.UtcNow;
            return _cachedApps;
        }

        private static List<StartupAppDetails> GetCachedAppsFast()
        {
            if (_cachedAppsFast != null && (DateTime.UtcNow - _cacheTimestampFast) < CacheLifetime)
                return _cachedAppsFast;
            _cachedAppsFast = BuildAppList(true);
            _cacheTimestampFast = DateTime.UtcNow;
            return _cachedAppsFast;
        }

        public static List<StartupAppDetails> GetStartupAppsFast()
        {
            return GetCachedAppsFast();
        }

        private static StartupAppDetails? FindAppByName(string appName)
        {
            var all = GetCachedApps();

            // Remove sufixo [Desabilitado] para busca
            string cleanName = appName.Replace(" [Desabilitado]", "").Trim();

            var exact = all.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase)
                                             || a.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Fallback: busca por nome de arquivo (sem extensão) no comando
            var byFile = all.FirstOrDefault(a =>
            {
                if (string.IsNullOrEmpty(a.FullCommand)) return false;
                string fileName = Path.GetFileNameWithoutExtension(a.FullCommand.Trim('"'));
                return fileName.Equals(cleanName, StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals(appName, StringComparison.OrdinalIgnoreCase);
            });
            if (byFile != null) return byFile;

            // Fallback: busca parcial
            return all.FirstOrDefault(a =>
                a.Name.IndexOf(appName, StringComparison.OrdinalIgnoreCase) >= 0
                || a.Name.IndexOf(cleanName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        #region Leitura e Análise

        public static List<StartupAppDetails> GetStartupAppsWithDetails(bool bypassElevationCheck = false)
        {
            if (!bypassElevationCheck)
                return GetCachedApps();
            // For write operations, refresh cache
            InvalidateCache();
            return GetCachedApps();
        }

        private static List<StartupAppDetails> BuildAppList(bool fast = false)
        {

            // Chave composta: Name + ExePath para evitar colisões (ex: "Update" de apps diferentes)
            var apps = new Dictionary<string, StartupAppDetails>(80, StringComparer.OrdinalIgnoreCase);

            // Lista de caminhos elevados para verificar se um app do registro já tem uma tarefa admin correspondente
            var elevatedTaskPaths = GetElevatedTaskExecutablePaths();

            // --- 1. PROCESSAR PASTAS DE INICIALIZAÇÃO ---
            Action<string, bool> processFolder = (folder, isCommon) =>
            {
                if (!Directory.Exists(folder)) return;
                RegistryKey baseKey = isCommon ? Registry.LocalMachine : Registry.CurrentUser;
                using var approvedKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");

                foreach (var file in Directory.GetFiles(folder))
                {
                    try
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        string commandLine = GetCommandLineFromShortcut(file);
                        ExtractCommandParts(commandLine, out string? exePath, out _);
                        string key = MakeKey(name, exePath);
                        if (apps.ContainsKey(key)) continue;

                        // Tenta ler o status do StartupApproved com .lnk e .ink
                        string fileName = Path.GetFileName(file);
                        byte[]? value = approvedKey?.GetValue(fileName) as byte[];
                        if (value == null && fileName.EndsWith(".ink", StringComparison.OrdinalIgnoreCase))
                        {
                            string lnkVariant = Path.ChangeExtension(fileName, ".lnk");
                            value = approvedKey?.GetValue(lnkVariant) as byte[];
                        }
                        if (value == null && fileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                        {
                            string inkVariant = Path.ChangeExtension(fileName, ".ink");
                            value = approvedKey?.GetValue(inkVariant) as byte[];
                        }
                        bool isEnabled = value == null || value.Length < 1 || (value[0] % 2 == 0);

                        var status = (exePath != null && elevatedTaskPaths.Contains(exePath)) ? StartupStatus.Elevated : (isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled);

                        apps.Add(key, new StartupAppDetails(name, commandLine, folder, status));
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            };

            // --- 2. PROCESSAR REGISTRO (RUN / RUNONCE) ---
            Action<RegistryKey, string, string[]> processRegistryKeys = (baseKey, locationPrefix, paths) =>
            {
                foreach (var path in paths)
                {
                    using var key = baseKey.OpenSubKey(path);
                    if (key == null) continue;

                    string approvedKeyPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Path.GetFileName(path)}";
                    if (locationPrefix.Contains("WOW6432Node")) { approvedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"; }
                    using var approvedKey = baseKey.OpenSubKey(approvedKeyPath);

                    foreach (var valueName in key.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(valueName)) continue;
                        var commandLine = key.GetValue(valueName)?.ToString() ?? "";

                        ExtractCommandParts(commandLine, out string? exePath, out _);
                        string keyName = MakeKey(valueName, exePath);
                        if (apps.ContainsKey(keyName)) continue;

                        var value = approvedKey?.GetValue(valueName) as byte[];
                        bool isEnabled = value == null || value.Length < 1 || (value[0] % 2 == 0);

                        var status = (exePath != null && elevatedTaskPaths.Contains(exePath)) ? StartupStatus.Elevated : (isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled);

                        apps.Add(keyName, new StartupAppDetails(valueName, commandLine, $"{locationPrefix}\\{path}", status));
                    }
                }
            };

            processFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), false);
            processFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true);

            // --- 1-B. PASTA AutorunsDisabled (itens desabilitados que foram movidos) ---
            try
            {
                string disabledFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AutorunsDisabled");
                if (Directory.Exists(disabledFolder))
                {
                    foreach (var file in Directory.GetFiles(disabledFolder))
                    {
                        try
                        {
                            string name = Path.GetFileNameWithoutExtension(file);
                            string commandLine = GetCommandLineFromShortcut(file);
                            ExtractCommandParts(commandLine, out string? exePath, out _);
                            string key = MakeKey(name, exePath ?? commandLine);
                            if (apps.ContainsKey(key)) continue;
                            apps.Add(key, new StartupAppDetails($"{name} [Desabilitado]", commandLine, disabledFolder, StartupStatus.Disabled));
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            string[] regPaths = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServicesOnce"
            };
            string[] policyPaths = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run" };
            processRegistryKeys(Registry.CurrentUser, "HKCU", regPaths);
            processRegistryKeys(Registry.CurrentUser, "HKCU", policyPaths);
            processRegistryKeys(Registry.LocalMachine, "HKLM", regPaths);
            processRegistryKeys(Registry.LocalMachine, "HKLM", policyPaths);
            if (Environment.Is64BitOperatingSystem)
            {
                string[] wow64Paths = {
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunServices",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunServicesOnce"
                };
                processRegistryKeys(Registry.LocalMachine, @"HKLM\WOW6432Node", wow64Paths);
                // Winlogon Userinit (HKLM) - contém lista de executáveis separados por vírgula
                try
                {
                    using var userinitKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
                    if (userinitKey != null)
                    {
                        string userinit = userinitKey.GetValue("Userinit") as string ?? "";
                        if (!string.IsNullOrEmpty(userinit))
                        {
                            var parts = userinit.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            foreach (var part in parts)
                            {
                                if (!part.Contains("userinit.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    ExtractCommandParts(part, out string? upExePath, out _);
                                    string upKey = MakeKey($"Userinit: {Path.GetFileName(part)}", upExePath ?? part);
                                    if (!apps.ContainsKey(upKey))
                                        apps.Add(upKey, new StartupAppDetails($"Userinit: {Path.GetFileName(part)}", part, @"HKLM\SOFTWARE\...\Winlogon\Userinit", StartupStatus.Enabled));
                                }
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // --- 3. PROCESSAR TAREFAS DO AGENDADOR (KITLUGIA) ---
            try
            {
                using (var ts = new TaskService())
                {
                    var lugiaTasks = ts.RootFolder.Tasks.Where(t => t.Name.StartsWith("KitLUGIA_"));

                    foreach (var task in lugiaTasks)
                    {
                        string rawName = task.Name;
                        string cleanName = rawName
                            .Replace("KitLUGIA_Elevated_", "")
                            .Replace("KitLUGIA_Delayed_", "")
                            .Replace("KitLUGIA_NonAdmin_", "");

                        string fullCommand = "";
                        if (task.Definition.Actions.FirstOrDefault() is ExecAction action)
                        {
                            fullCommand = $"\"{action.Path}\" {action.Arguments}".Trim();
                        }

                        bool isTaskEnabled = task.Enabled;
                        StartupStatus status;

                        if (!isTaskEnabled)
                            status = StartupStatus.Disabled;
                        else if (rawName.Contains("Elevated"))
                            status = StartupStatus.Elevated;
                        else if (rawName.Contains("NonAdmin"))
                            status = StartupStatus.TurboBootNormal;
                        else
                            status = StartupStatus.Enabled;

                        // Find existing app by name match (key is compound so we scan values)
                        var existingEntry = apps.Values.FirstOrDefault(a =>
                            a.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
                        if (existingEntry != null)
                        {
                            existingEntry.Status = status;
                            existingEntry.Location = "Agendador de Tarefas (KitLugia)";
                        }
                        else
                        {
                            ExtractCommandParts(fullCommand, out string? exePath, out _);
                            string key = MakeKey(cleanName, exePath ?? fullCommand);
                            apps[key] = new StartupAppDetails(cleanName, fullCommand, "Agendador de Tarefas (KitLugia)", status);
                        }
                    }
                }
            }
            catch { /* Ignora erros de permissão */ }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey);
                if (key != null)
                {
                    foreach (var valueName in key.GetValueNames())
                    {
                        if (valueName.EndsWith("__Admin")) continue;
                        var commandLine = key.GetValue(valueName)?.ToString() ?? "";
                        bool isAdmin = key.GetValue(valueName + "__Admin")?.ToString() != "0";
                        var status = isAdmin ? StartupStatus.TurboBoot : StartupStatus.TurboBootNormal;

                        var existingEntry = apps.Values.FirstOrDefault(a =>
                            a.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase));
                        if (existingEntry != null)
                        {
                            existingEntry.Status = status;
                            existingEntry.BootTrayRunAsAdmin = isAdmin;
                            existingEntry.Location = "Turbo Boot (KitLugia)";
                        }
                        else
                        {
                            ExtractCommandParts(commandLine, out string? exePath, out _);
                            string lugiaKey = MakeKey(valueName, exePath ?? commandLine);
                            var app = new StartupAppDetails(valueName, commandLine, "Turbo Boot (KitLugia)", status);
                            app.BootTrayRunAsAdmin = isAdmin;
                            apps[lugiaKey] = app;
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // --- 4. WMI Win32_StartupCommand (captura apps de todas as origens, SEMPRE) ---
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\cimv2",
                    "SELECT Name, Command, Location, User FROM Win32_StartupCommand");
                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        string? name = obj["Name"]?.ToString();
                        string? command = obj["Command"]?.ToString();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(command)) continue;

                        // Resolve .lnk/.ink shortcuts to full path
                        string ext = Path.GetExtension(command);
                        if ((ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ink", StringComparison.OrdinalIgnoreCase)) && !command.Contains("\\"))
                        {
                            string sf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), command);
                            if (File.Exists(sf)) command = GetCommandLineFromShortcut(sf);
                        }

                        string? location = obj["Location"]?.ToString();
                        ExtractCommandParts(command, out string? exePath, out _);
                        if (!string.IsNullOrEmpty(exePath) && exePath.Contains("system32", StringComparison.OrdinalIgnoreCase)) continue;

                        string key = MakeKey(name, exePath ?? command);
                        if (!apps.ContainsKey(key))
                            apps[key] = new StartupAppDetails(name, command, $"Startup: {location ?? "WMI"}", StartupStatus.Enabled);
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // --- 5. HKCU\...\Windows\Load e Windows\Run (apps antigos) ---
            try
            {
                using var winKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
                if (winKey != null)
                {
                    foreach (var valName in new[] { "Load", "Run" })
                    {
                        string cmd = winKey.GetValue(valName) as string ?? "";
                        if (string.IsNullOrWhiteSpace(cmd)) continue;
                        ExtractCommandParts(cmd, out string? exePath, out _);
                        string k = MakeKey(Path.GetFileNameWithoutExtension(cmd), exePath ?? cmd);
                        if (!apps.ContainsKey(k))
                            apps[k] = new StartupAppDetails(Path.GetFileName(cmd), cmd, $"HKCU\\Windows\\{valName}", StartupStatus.Enabled);
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // --- 6. TAREFAS EXTERNAS DO AGENDADOR (não-KitLUGIA) ---
            if (!fast)
            {
                try
                {
                    var externalTasks = GetExternalTaskSchedulerApps();
                    foreach (var task in externalTasks)
                    {
                        ExtractCommandParts(task.FullCommand, out string? exePath, out _);
                        string key = MakeKey(task.Name, exePath ?? task.FullCommand);
                        if (!apps.ContainsKey(key))
                            apps[key] = task;
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // --- 7. UWP / Modern Store Apps (apenas Task Scheduler) ---
                try
                {
                    var uwpApps = GetUWPStartupApps();
                    foreach (var app in uwpApps)
                    {
                        ExtractCommandParts(app.FullCommand, out string? exePath, out _);
                        string key = MakeKey(app.Name, exePath ?? app.FullCommand);
                        if (!apps.ContainsKey(key))
                            apps[key] = app;
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // Remove WMI duplicates that have incomplete/broken paths
            var wmiDuplicates = apps.Values
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.Skip(1).Where(a => a.Location.StartsWith("Startup:") && (a.ExePath == null || a.ExePath == a.Name || a.ExePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !a.ExePath.Contains("\\"))))
                .ToList();
            foreach (var dup in wmiDuplicates)
            {
                string dupKey = MakeKey(dup.Name, dup.FullCommand);
                apps.Remove(dupKey);
            }

            // --- 6. RUNONCEEX (executado uma vez ao logon, depois removido) ---
            Action<RegistryKey, string> processRunOnceEx = (baseKey, prefix) =>
            {
                try
                {
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnceEx");
                    if (key == null) return;
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;
                        foreach (var valueName in subKey.GetValueNames())
                        {
                            if (!string.IsNullOrEmpty(valueName))
                            {
                                var cmd = subKey.GetValue(valueName)?.ToString() ?? "";
                                if (!string.IsNullOrEmpty(cmd))
                                {
                                    ExtractCommandParts(cmd, out string? exePath, out _);
                                    string k = MakeKey($"{subKeyName}_{valueName}", exePath ?? cmd);
                                    if (!apps.ContainsKey(k))
                                        apps[k] = new StartupAppDetails(valueName, cmd, $"{prefix}\\RunOnceEx\\{subKeyName}", StartupStatus.Enabled);
                                }
                            }
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            };
            processRunOnceEx(Registry.LocalMachine, "HKLM");
            processRunOnceEx(Registry.CurrentUser, "HKCU");

            // --- 7. ACTIVE SETUP (executado uma vez por usuário no logon) ---
            try
            {
                using var activeKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Active Setup\Installed Components");
                if (activeKey != null)
                {
                    foreach (var subName in activeKey.GetSubKeyNames())
                    {
                        try
                        {
                            if (subName.StartsWith("{", StringComparison.Ordinal)) continue; // skip CLSID GUIDs
                            using var compKey = activeKey.OpenSubKey(subName);
                            if (compKey == null) continue;
                            string stubPath = compKey.GetValue("StubPath") as string ?? "";
                            if (string.IsNullOrEmpty(stubPath)) continue;
                            ExtractCommandParts(stubPath, out string? exePath, out _);
                            string k = MakeKey($"ActiveSetup: {subName}", exePath ?? stubPath);
                            if (!apps.ContainsKey(k))
                            {
                                int version = compKey.GetValue("Version") as int? ?? 0;
                                var status = version > 0 ? StartupStatus.Enabled : StartupStatus.Disabled;
                                apps[k] = new StartupAppDetails($"ActiveSetup: {subName}", stubPath, @"HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components", status);
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return apps.Values.OrderBy(a => a.Name).ToList();
        }

        #endregion

        #region Gerenciamento de Estado (Habilitar/Desabilitar/Remover)

        public static (bool Success, string Message) SetStartupItemState(string appName, bool enable, bool silentMode = false)
        {
            // Limpa sufixo [Desabilitado] do nome
            appName = appName.Replace(" [Desabilitado]", "").Trim();
            var startupApp = FindAppByName(appName);
            if (startupApp == null) return (false, "App não encontrado.");

            // CASO 1: Tarefa do Agendador (KitLugia ou Externa)
            if (startupApp.Location.Contains("Agendador"))
            {
                try
                {
                    using (var ts = new TaskService())
                    {
                        var task = ts.RootFolder.Tasks
                            .FirstOrDefault(t => t.Name.Contains(appName) && t.Name.StartsWith("KitLUGIA_"));
                        if (task == null)
                        {
                            Microsoft.Win32.TaskScheduler.Task? FindTask(TaskFolder folder)
                            {
                                var found = folder.Tasks.FirstOrDefault(t =>
                                    t.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                                if (found != null) return found;
                                foreach (var sf in folder.SubFolders)
                                {
                                    try
                                    {
                                        var r = FindTask(sf);
                                        if (r != null) return r;
                                    }
                                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                                }
                                return null;
                            }
                            task = FindTask(ts.RootFolder);
                        }

                        if (task != null)
                        {
                            task.Definition.Settings.Enabled = enable;
                            var parentFolder = task.Folder ?? ts.RootFolder;
                            parentFolder.RegisterTaskDefinition(task.Name, task.Definition,
                                TaskCreation.Update, null, null, task.Definition.Principal.LogonType);

                            string actionMsg = enable ? "Habilitado" : "Desabilitado";
                            InvalidateCache();
                            return (true, silentMode ? "" : $"Item agendado '{appName}' foi {actionMsg}.");
                        }
                    }
                    return (false, "Tarefa agendada não encontrada.");
                }
                catch (Exception ex)
                {
                    return (false, $"Erro ao alterar tarefa: {ex.Message}");
                }
            }

            // CASO 1-B: Turbo Boot (KitLugia)
            if (startupApp.Location.Contains("Turbo Boot"))
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey, true);
                    if (key != null)
                    {
                        if (enable)
                        {
                            // Re-enable: restore command from backup if available
                            string? command = key.GetValue(appName + "__Backup")?.ToString();
                            if (!string.IsNullOrEmpty(command))
                                key.SetValue(appName, command);
                            else
                                return (false, "Comando original não encontrado em backup. Use 'Restaurar para Inicialização Padrão'.");
                            key.DeleteValue(appName + "__Backup", false);
                            InvalidateCache();
                            return (true, $"'{appName}' reativado no Turbo Boot.");
                        }
                        else
                        {
                            // Disable: save command to backup then remove from startup list
                            string? command = key.GetValue(appName)?.ToString();
                            if (command != null)
                                key.SetValue(appName + "__Backup", command);
                            key.DeleteValue(appName, false);
                            key.DeleteValue(appName + "__Admin", false);
                            UnregisterNonAdminTask(appName);
                            InvalidateCache();
                            return (true, $"'{appName}' desabilitado do Turbo Boot.");
                        }
                    }
                    return (false, "Chave KitLugia não encontrada.");
                }
                catch (Exception ex)
                {
                    return (false, $"Erro ao desabilitar Turbo Boot: {ex.Message}");
                }
            }

            // CASO 2: Registro ou Pasta de Inicialização
            // Corrigido: detecta pastas de startup mesmo de labels WMI
            string loc = startupApp.Location ?? "";
            string? resolvedFolderPath = null;
            string? resolvedRegHive = null;
            string? resolvedRegPath = null;
            string? resolvedValueName = null;

            // Detecta pasta de inicialização (exclui AutorunsDisabled)
            if ((loc.Contains("\\Startup") || loc.Contains("\\Start Menu")) && !loc.Contains("AutorunsDisabled"))
                resolvedFolderPath = loc;
            else if (loc.StartsWith("Startup: Startup", StringComparison.OrdinalIgnoreCase) || loc.Equals("Startup", StringComparison.OrdinalIgnoreCase))
                resolvedFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

            // Detecta registro (corrigido: WOW6432Node tem prefixo "HKLM\WOW6432Node\")
            if (loc.StartsWith("HKLM\\WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                resolvedRegHive = "HKLM";
                resolvedRegPath = loc.Substring("HKLM\\WOW6432Node\\".Length);
                resolvedValueName = appName;
            }
            else if (loc.StartsWith("HKCU") || loc.StartsWith("HKLM"))
            {
                resolvedRegHive = loc.StartsWith("HKLM") ? "HKLM" : "HKCU";
                int slashIdx = loc.IndexOf('\\');
                if (slashIdx > 0)
                    resolvedRegPath = loc.Substring(slashIdx + 1);
                resolvedValueName = appName;
            }

            // Detecta WMI que na verdade referencia pasta de startup
            if (loc.StartsWith("Startup:") && resolvedFolderPath == null)
            {
                string wmiLocation = loc.Replace("Startup:", "").Trim();
                if (wmiLocation.Equals("Startup", StringComparison.OrdinalIgnoreCase))
                    resolvedFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                else if (wmiLocation.StartsWith("HKU", StringComparison.OrdinalIgnoreCase) || wmiLocation.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
                {
                    var allApps = GetStartupAppsWithDetails(true);
                    var realEntry = allApps.FirstOrDefault(a =>
                        a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase) &&
                        !a.Location.StartsWith("Startup:"));
                    if (realEntry != null)
                    {
                        loc = realEntry.Location;
                        if (loc.Contains("\\Startup"))
                            resolvedFolderPath = loc;
                        else if (loc.StartsWith("HK"))
                        {
                            if (loc.StartsWith("HKLM\\WOW6432Node", StringComparison.OrdinalIgnoreCase))
                            {
                                resolvedRegHive = "HKLM";
                                resolvedRegPath = loc.Substring("HKLM\\WOW6432Node\\".Length);
                            }
                            else
                            {
                                resolvedRegHive = loc.StartsWith("HKLM") ? "HKLM" : "HKCU";
                                int slashIdx = loc.IndexOf('\\');
                                if (slashIdx > 0) resolvedRegPath = loc.Substring(slashIdx + 1);
                            }
                            resolvedValueName = appName;
                        }
                    }
                }
            }

            // --- EXECUTA A AÇÃO COM SUPORTE A MÚLTIPLOS MECANISMOS ---
            bool anyActionTaken = false;
            List<string> actionsTaken = new();

            // --- AÇÃO ESPECIAL: RESTAURAR DE AutorunsDisabled ---
            if (enable && loc.Contains("AutorunsDisabled"))
            {
                try
                {
                    string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    string disabledFolder = Path.Combine(startupFolder, "AutorunsDisabled");
                    if (Directory.Exists(disabledFolder))
                    {
                        foreach (var file in Directory.GetFiles(disabledFolder, appName + ".*"))
                        {
                            string dest = Path.Combine(startupFolder, Path.GetFileName(file));
                            if (!File.Exists(dest)) File.Move(file, dest);
                            else File.Delete(file);
                            anyActionTaken = true;
                            actionsTaken.Add($"Restaurado de AutorunsDisabled: {Path.GetFileName(file)}");
                        }
                        foreach (var file in Directory.GetFiles(disabledFolder))
                        {
                            if (Path.GetFileNameWithoutExtension(file).Equals(appName, StringComparison.OrdinalIgnoreCase))
                            {
                                string dest = Path.Combine(startupFolder, Path.GetFileName(file));
                                if (!File.Exists(dest)) File.Move(file, dest);
                                else File.Delete(file);
                                anyActionTaken = true;
                                actionsTaken.Add($"Restaurado de AutorunsDisabled: {Path.GetFileName(file)}");
                            }
                        }
                    }
                    // Atualiza localização para a pasta de startup correta
                    resolvedFolderPath = startupFolder;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // Ação A: Desabilitar via StartupApproved (RUN)
            if (resolvedRegHive != null)
            {
                try
                {
                    RegistryKey baseKey = resolvedRegHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    string approvedRegPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                    byte[] valueToSet = enable ? new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } : new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

                    using (var key = baseKey.OpenSubKey(approvedRegPath, true) ?? baseKey.CreateSubKey(approvedRegPath))
                    {
                        if (key.GetValue(resolvedValueName) != null)
                        {
                            key.SetValue(resolvedValueName, valueToSet, RegistryValueKind.Binary);
                            actionsTaken.Add($"StartupApproved[{resolvedValueName}]");
                        }
                        else
                        {
                            string fallbackName = GetFileNameFromCommandLine(startupApp.FullCommand);
                            key.SetValue(fallbackName, valueToSet, RegistryValueKind.Binary);
                            actionsTaken.Add($"StartupApproved[{fallbackName}]");
                            // Also try appName.lnk variant
                            if (!fallbackName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                try { key.SetValue(appName + ".lnk", valueToSet, RegistryValueKind.Binary); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                    }

                    // Also write to HKLM variant (in case the app has both)
                    if (resolvedRegHive == "HKCU")
                    {
                        try
                        {
                            using var lmKey = Registry.LocalMachine.OpenSubKey(approvedRegPath, true);
                            if (lmKey != null && lmKey.GetValue(resolvedValueName) != null)
                            {
                                lmKey.SetValue(resolvedValueName, valueToSet, RegistryValueKind.Binary);
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }

                    anyActionTaken = true;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Ação A2: Salvar valor Run e deletá-lo ao desabilitar (apps como Vencord/Vesktop ignoram StartupApproved)
                if (!enable && resolvedRegPath != null && !string.IsNullOrEmpty(resolvedValueName))
                {
                    try
                    {
                        RegistryKey baseKey = resolvedRegHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                        using var runKey = baseKey.OpenSubKey(resolvedRegPath, writable: true);
                        if (runKey != null)
                        {
                            string? originalValue = runKey.GetValue(resolvedValueName)?.ToString();
                            if (originalValue != null)
                            {
                                // Salva o valor original no backup antes de deletar
                                using var backupKey = Registry.CurrentUser.CreateSubKey(RemovedAppsBackupKey);
                                backupKey.SetValue(appName + "__RunCommand", originalValue);
                                backupKey.SetValue(appName + "__RunHive", resolvedRegHive);
                                backupKey.SetValue(appName + "__RunPath", resolvedRegPath);
                                backupKey.SetValue(appName + "__RunValueName", resolvedValueName);
                                backupKey.SetValue(appName + "__Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                                runKey.DeleteValue(resolvedValueName, throwOnMissingValue: false);
                                actionsTaken.Add($"Run value salvo em backup e deletado");
                            }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                else if (enable && resolvedRegPath != null && !string.IsNullOrEmpty(resolvedValueName))
                {
                    // Restaura o valor Run do backup ao reabilitar
                    try
                    {
                        using var backupKey = Registry.CurrentUser.OpenSubKey(RemovedAppsBackupKey);
                        if (backupKey != null)
                        {
                            string? savedCommand = backupKey.GetValue(appName + "__RunCommand")?.ToString();
                            string? savedHive = backupKey.GetValue(appName + "__RunHive")?.ToString();
                            string? savedPath = backupKey.GetValue(appName + "__RunPath")?.ToString();
                            string? savedValueName = backupKey.GetValue(appName + "__RunValueName")?.ToString();
                            if (!string.IsNullOrEmpty(savedCommand) && !string.IsNullOrEmpty(savedHive) && !string.IsNullOrEmpty(savedPath) && !string.IsNullOrEmpty(savedValueName))
                            {
                                RegistryKey restoreBase = savedHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                                using var restoreKey = restoreBase.OpenSubKey(savedPath, writable: true) ?? restoreBase.CreateSubKey(savedPath);
                                restoreKey.SetValue(savedValueName, savedCommand, RegistryValueKind.String);
                                actionsTaken.Add($"Run value restaurado do backup: {savedHive}\\{savedPath}\\{savedValueName}");

                                // Remove os valores de backup após restaurar
                                backupKey.DeleteValue(appName + "__RunCommand", false);
                                backupKey.DeleteValue(appName + "__RunHive", false);
                                backupKey.DeleteValue(appName + "__RunPath", false);
                                backupKey.DeleteValue(appName + "__RunValueName", false);
                            }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }

            // Ação B: Desabilitar via StartupApproved (StartupFolder)
            if (resolvedFolderPath != null)
            {
                try
                {
                    string approvedRegPath = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
                    byte[] valueToSet = enable ? new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } : new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

                    // Search for the correct entry name in StartupApproved
                    using (var approvedKey = Registry.CurrentUser.OpenSubKey(approvedRegPath, true) ?? Registry.CurrentUser.CreateSubKey(approvedRegPath))
                    {
                        bool found = false;
                        string fc = startupApp.FullCommand ?? "";
                        string[] lnkNames = {
                            appName + ".lnk",
                            appName + ".ink",
                            Path.GetFileName(fc.Trim('"')),
                            GetFileNameFromCommandLine(fc)
                        };

                        foreach (var lnkName in lnkNames)
                        {
                            if (!string.IsNullOrEmpty(lnkName) && approvedKey.GetValue(lnkName) != null)
                            {
                                approvedKey.SetValue(lnkName, valueToSet, RegistryValueKind.Binary);
                                actionsTaken.Add($"StartupApproved\\StartupFolder[{lnkName}]");
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            // Create the entry anyway with the .lnk extension
                            approvedKey.SetValue(appName + ".lnk", valueToSet, RegistryValueKind.Binary);
                            actionsTaken.Add($"StartupApproved\\StartupFolder[{appName}.lnk] (created)");
                        }
                    }

                    // If disabling, also move the .lnk to AutorunsDisabled as a safety measure
                    if (!enable)
                    {
                        try
                        {
                            string startupFolder = resolvedFolderPath;
                            if (!Directory.Exists(startupFolder)) startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

                            string disabledDir = Path.Combine(startupFolder, "AutorunsDisabled");
                            if (!Directory.Exists(disabledDir)) Directory.CreateDirectory(disabledDir);

                            foreach (var pattern in new[] { $"{appName}.lnk", $"{appName}.ink", $"{appName}.url" })
                            {
                                string srcPath = Path.Combine(startupFolder, pattern);
                                if (File.Exists(srcPath))
                                {
                                    string destPath = Path.Combine(disabledDir, pattern);
                                    if (!File.Exists(destPath)) File.Move(srcPath, destPath);
                                    else File.Delete(srcPath);
                                    actionsTaken.Add($"Move to AutorunsDisabled: {pattern}");
                                }
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }

                    anyActionTaken = true;
                }
                catch (Exception ex)
                {
                    return (false, $"Erro na pasta de inicialização: {ex.Message}");
                }
            }

            // Ação D: Para WMI-only items sem localização resolvida (fallback para Task Scheduler)
            if (!anyActionTaken)
            {
                try
                {
                    string cmdLine = startupApp.FullCommand ?? "";
                    ExtractCommandParts(cmdLine, out string? exePath, out string? exeArgs);
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        string taskName = $"KitLUGIA_Fallback_{SanitizeTaskName(appName)}";
                        if (!enable)
                        {
                            using (var ts = new TaskService())
                            {
                                var existing = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == taskName);
                                if (existing != null)
                                {
                                    ts.RootFolder.DeleteTask(taskName);
                                    actionsTaken.Add("Tarefa fallback removida");
                                }
                            }
                        }
                        else
                        {
                            using (var ts = new TaskService())
                            {
                                var td = ts.NewTask();
                                td.RegistrationInfo.Description = $"KitLugia fallback para {appName}";
                                td.Principal.LogonType = TaskLogonType.InteractiveToken;
                                td.Actions.Add(new ExecAction(exePath, exeArgs, null));
                                td.Triggers.Add(new LogonTrigger());
                                td.Settings.Enabled = true;
                                td.Settings.StartWhenAvailable = true;
                                td.Settings.AllowHardTerminate = true;
                                ts.RootFolder.RegisterTaskDefinition(taskName, td,
                                    TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);
                                actionsTaken.Add("Tarefa fallback criada");
                            }
                        }
                        anyActionTaken = true;
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // Ação E: Se mesmo assim não funcionou, varredura completa de backup
            if (!anyActionTaken && !enable)
            {
                try
                {
                    // Brute-force: scan all Run/RunOnce for any value containing this name
                    BruteForceDisableStartup(appName);
                    actionsTaken.Add("Varredura completa (brute-force)");
                    anyActionTaken = true;
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            if (!anyActionTaken)
                return (false, "Não foi possível encontrar o mecanismo de inicialização deste app.");

            InvalidateCache();
            string actionsSummary = string.Join("; ", actionsTaken);
            return (true, silentMode ? "" : $"'{appName}' {(enable ? "Habilitado" : "Desabilitado")}. [{actionsSummary}]");
        }

        private static void BruteForceDisableStartup(string appName)
        {
            // NOTE: Registry Run values are NOT deleted — StartupApproved handles the disabled state
            // so the app stays visible in the list

            // Scan Startup folder for any file matching the name
            string[] startupFolders = {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            };

            foreach (var folder in startupFolders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        if (fileName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                        {
                            string disabledDir = Path.Combine(folder, "AutorunsDisabled");
                            if (!Directory.Exists(disabledDir)) Directory.CreateDirectory(disabledDir);
                            string dest = Path.Combine(disabledDir, Path.GetFileName(file));
                            if (!File.Exists(dest)) File.Move(file, dest);
                            else File.Delete(file);
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            // Write StartupApproved\Run as disabled
            try
            {
                byte[] disabledValue = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                using var runApproved = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true)
                    ?? Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                runApproved?.SetValue(appName, disabledValue, RegistryValueKind.Binary);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Write StartupApproved\StartupFolder as disabled
            try
            {
                byte[] disabledValue = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                using var folderApproved = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", true)
                    ?? Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
                folderApproved?.SetValue(appName + ".lnk", disabledValue, RegistryValueKind.Binary);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) RemoveStartupItem(string appName)
        {
            appName = appName.Replace(" [Desabilitado]", "").Trim();
            var startupApp = FindAppByName(appName);
            if (startupApp == null) return (false, "Aplicativo não encontrado na lista.");

            // Backup before removal
            BackupStartupItem(startupApp);

            try
            {
                if (startupApp.Location.Contains("Agendador"))
                {
                    using (var ts = new TaskService())
                    {
                        // KitLugia tasks — match by prefix
                        var task = ts.RootFolder.Tasks.FirstOrDefault(t =>
                            t.Name.Contains(appName) && t.Name.StartsWith("KitLUGIA_"));
                        if (task != null)
                        {
                            ts.RootFolder.DeleteTask(task.Name);
                            InvalidateCache();
                            CleanStartupApproved(appName);
                            return (true, $"Tarefa '{appName}' removida do agendador.");
                        }

                        // External task — recursive search by exact name
                        Microsoft.Win32.TaskScheduler.Task? FindTask(TaskFolder folder)
                        {
                            var found = folder.Tasks.FirstOrDefault(t =>
                                t.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                            if (found != null) return found;
                            foreach (var sf in folder.SubFolders)
                            {
                                try
                                {
                                    var r = FindTask(sf);
                                    if (r != null) return r;
                                }
                                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                            return null;
                        }
                        var extTask = FindTask(ts.RootFolder);
                        if (extTask != null)
                        {
                            (extTask.Folder ?? ts.RootFolder).DeleteTask(extTask.Name);
                            InvalidateCache();
                            CleanStartupApproved(appName);
                            return (true, $"Tarefa '{appName}' removida do agendador.");
                        }

                        return (false, "Tarefa agendada não encontrada.");
                    }
                }
                else if (startupApp.Location.Contains("\\Startup") || startupApp.Location.Contains("\\Start Menu"))
                {
                    // Tenta múltiplas extensões de atalho
                    string[] extensions = { ".lnk", ".ink", ".url", ".pif", ".exe", ".bat", ".cmd", ".ps1" };
                    bool deleted = false;
                    foreach (var ext in extensions)
                    {
                        string fullPath = Path.Combine(startupApp.Location, appName + ext);
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            deleted = true;
                        }
                    }
                    if (deleted)
                    {
                        InvalidateCache();
                        CleanStartupApproved(appName);
                        return (true, $"Atalho/arquivo '{appName}' deletado permanentemente.");
                    }

                    // Fallback: busca por nome sem extensão
                    var looseFile = Directory.GetFiles(startupApp.Location, $"{appName}.*").FirstOrDefault();
                    if (looseFile != null)
                    {
                        File.Delete(looseFile);
                        InvalidateCache();
                        CleanStartupApproved(appName);
                        return (true, $"Arquivo '{Path.GetFileName(looseFile)}' deletado permanentemente.");
                    }
                }
                else if (startupApp.Location.StartsWith("HK"))
                {
                    RegistryKey baseKey = startupApp.Location.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKeyPath;
                    if (startupApp.Location.StartsWith("HKLM\\WOW6432Node", StringComparison.OrdinalIgnoreCase))
                        subKeyPath = startupApp.Location.Substring("HKLM\\WOW6432Node\\".Length);
                    else
                        subKeyPath = startupApp.Location.Substring(startupApp.Location.IndexOf('\\') + 1);

                    using (var key = baseKey.OpenSubKey(subKeyPath, true))
                    {
                        if (key != null && key.GetValue(appName) != null)
                        {
                            key.DeleteValue(appName);
                            InvalidateCache();
                            CleanStartupApproved(appName);
                            return (true, $"Entrada de registro '{appName}' removida.");
                        }
                    }
                }

                // CASO 3: UWP — desabilita via registro + remove tarefa fallback
                if (startupApp.Location.Contains("UWP"))
                {
                    string fullCmd = startupApp.FullCommand ?? "";
                    var match = System.Text.RegularExpressions.Regex.Match(fullCmd, @"^StartupTask:\s+(.+)$");
                    if (match.Success)
                    {
                        string id = match.Groups[1].Value;
                        string? pkgFamily = null;
                        string? taskId = null;
                        if (id.Contains("!"))
                        {
                            int idx = id.IndexOf('!');
                            pkgFamily = id.Substring(0, idx);
                            taskId = id.Substring(idx + 1);
                        }

                        // Desabilita via AppModel
                        if (pkgFamily != null && taskId != null)
                        {
                            string appModelPath = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{pkgFamily}\{taskId}";
                            using (var taskKey = Registry.CurrentUser.OpenSubKey(appModelPath, true))
                            {
                                if (taskKey?.GetValue("State") != null)
                                    taskKey.SetValue("State", 0, RegistryValueKind.DWord);
                            }
                        }

                        // Remove tarefa fallback KitLugia
                        try
                        {
                            using (var ts = new TaskService())
                            {
                                string taskName = $"KitLUGIA_UWP_{SanitizeTaskName(startupApp.Name)}";
                                var existing = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == taskName);
                                if (existing != null)
                                    ts.RootFolder.DeleteTask(taskName);
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        InvalidateCache();
                        CleanStartupApproved(appName);
                        return (true, $"App UWP '{appName}' desabilitado permanentemente.");
                    }
                }

                return (false, "Não foi possível localizar o item físico para remoção.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao remover item: {ex.Message}");
            }
        }

        #endregion

        #region Gerenciamento de Tarefas (Elevadas/Atrasadas)

        public static List<string> GetElevatedStartupTaskFullNames()
        {
            using (var ts = new TaskService())
            {
                return ts.RootFolder.Tasks
                    .Where(task => task.Name.StartsWith("KitLUGIA_"))
                    .Select(task => task.Name)
                    .ToList();
            }
        }

        public static List<StartupAppDetails> GetExternalTaskSchedulerApps()
        {
            var apps = new List<StartupAppDetails>();
            try
            {
                using (var ts = new TaskService())
                {
                    Action<TaskFolder> scanFolder = null!;
                    scanFolder = (folder) =>
                    {
                        foreach (var task in folder.Tasks)
                        {
                            if (task.Name.StartsWith("KitLUGIA_", StringComparison.OrdinalIgnoreCase)) continue;
                            if (task.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                            if (task.Name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) continue;

                            bool hasStartupTrigger = task.Definition.Triggers.Any(t =>
                                t is LogonTrigger || t is BootTrigger || t is SessionStateChangeTrigger);

                            if (!hasStartupTrigger) continue;

                            string fullCommand = "";
                            if (task.Definition.Actions.FirstOrDefault() is ExecAction action)
                            {
                                fullCommand = $"\"{action.Path}\" {action.Arguments}".Trim();
                            }

                            StartupManager.ExtractCommandParts(fullCommand, out string? exePath, out _);
                            if (string.IsNullOrEmpty(exePath)) continue;
                            if (exePath.Contains("system32", StringComparison.OrdinalIgnoreCase)) continue;

                            string name = task.Name;
                            string location = folder.Path == "\\" ? "Agendador de Tarefas" : $"Agendador de Tarefas ({folder.Path})";
                            bool isElevated = task.Definition.Principal.RunLevel == TaskRunLevel.Highest;
                            var status = task.Enabled ? (isElevated ? StartupStatus.Elevated : StartupStatus.Enabled) : StartupStatus.Disabled;
                            apps.Add(new StartupAppDetails(name, fullCommand, location, status));
                        }

                        foreach (var subFolder in folder.SubFolders)
                        {
                            try { scanFolder(subFolder); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    };

                    scanFolder(ts.RootFolder);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return apps.OrderBy(a => a.Name).ToList();
        }

        public static List<StartupAppDetails> GetUWPStartupApps()
        {
            var apps = new List<StartupAppDetails>();

            // --- Task Scheduler subfolders com AppUserModelId ---
            try
            {
                using (var ts = new TaskService())
                {
                    Action<TaskFolder> scanUwpFolder = null!;
                    scanUwpFolder = (folder) =>
                    {
                        foreach (var task in folder.Tasks)
                        {
                            try
                            {
                                bool hasUserTrigger = task.Definition.Triggers.Any(t =>
                                    t is LogonTrigger || t is SessionStateChangeTrigger);
                                if (!hasUserTrigger || !task.Enabled) continue;

                                string? appId = null;
                                string? exePath = null;
                                string args = "";

                                if (task.Definition.Actions.FirstOrDefault() is ExecAction execAction)
                                {
                                    exePath = execAction.Path;
                                    args = execAction.Arguments ?? "";
                                    var match = System.Text.RegularExpressions.Regex.Match(args,
                                        @"-AppId\s+([^\s]+)");
                                    if (match.Success)
                                        appId = match.Groups[1].Value;
                                }

                                if (string.IsNullOrEmpty(appId)) continue;

                                string fullCmd = !string.IsNullOrEmpty(exePath)
                                    ? $"\"{exePath}\" {args}".Trim()
                                    : args;
                                string loc = folder.Path == "\\" ? "UWP Store App" : $"UWP ({folder.Path})";

                                apps.Add(new StartupAppDetails(appId, fullCmd, loc, StartupStatus.Enabled));
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }

                        foreach (var sf in folder.SubFolders)
                        {
                            try { scanUwpFolder(sf); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    };

                    scanUwpFolder(ts.RootFolder);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // --- 3. UWP StartupTask API registries (o que o Task Manager mostra) ---
            // Windows 10/11 armazena o estado das tarefas de inicialização UWP em:
            // HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupTasks\{AUMID}
            AddUWPStartupTasksFromRegistry(Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupTasks",
                "UWP (Windows Settings)", apps);

            // --- 4. Machine-wide UWP StartupTasks ---
            AddUWPStartupTasksFromRegistry(Registry.LocalMachine,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupTasks",
                "UWP (Global)", apps);

            // --- 5. AppModel SystemAppData (fonte primária de estado para tarefas UWP) ---
            // Windows 10/11 também armazena o estado em:
            // HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{PackageFamilyName}\{TaskId}
            // State: 0=Disabled, 1=DisabledByUser, 2=Enabled, 3=DisabledByPolicy
            try
            {
                string appModelPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
                using var appModelKey = Registry.CurrentUser.OpenSubKey(appModelPath);
                if (appModelKey != null)
                {
                    foreach (var pkgFamilyName in appModelKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var pkgKey = appModelKey.OpenSubKey(pkgFamilyName);
                            if (pkgKey == null) continue;

                            foreach (var taskId in pkgKey.GetSubKeyNames())
                            {
                                try
                                {
                                    using var taskKey = pkgKey.OpenSubKey(taskId);
                                    if (taskKey == null) continue;

                                    var stateObj = taskKey.GetValue("State");
                                    if (stateObj == null) continue;

                                    int state = Convert.ToInt32(stateObj);

                                    // Filtra valores não-StarupTask (caso outros dados coincidam em SystemAppData)
                                    if (state < 0 || state > 3) continue;

                                    bool isEnabled = state == 2;
                                    string displayName = ResolvePackageFamilyName(pkgFamilyName);

                                    // Evita duplicatas se já veio do Explorer\StartupTasks
                                    string key = MakeKey(displayName, $"StartupTask: {pkgFamilyName}!{taskId}");
                                    if (apps.Any(a =>
                                        a.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase) &&
                                        a.FullCommand != null &&
                                        a.FullCommand.IndexOf(pkgFamilyName, StringComparison.OrdinalIgnoreCase) >= 0))
                                        continue;

                                    apps.Add(new StartupAppDetails(
                                        displayName,
                                        $"StartupTask: {pkgFamilyName}!{taskId}",
                                        "UWP (Settings)",
                                        isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled));
                                }
                                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return apps.OrderBy(a => a.Name).ToList();
        }

        private static string ResolveAumidToDisplayName(string aumid)
        {
            // Tenta resolver via Windows PackageManager
            try
            {
                int exclamationIdx = aumid.IndexOf('!');
                if (exclamationIdx < 0) return aumid;

                string familyName = aumid.Substring(0, exclamationIdx);

                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = pm.FindPackagesForUser(null, familyName);
                foreach (var pkg in packages)
                {
                    string? dn = pkg.DisplayName;
                    if (!string.IsNullOrEmpty(dn))
                        return dn;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Fallback: limpa o AUMID para algo legível
            try
            {
                // Remove sufixo _8wekyb3d8bbwe e parte após !
                string cleaned = aumid;
                int underscoreIdx = cleaned.LastIndexOf('_');
                if (underscoreIdx > 0 && cleaned.Length - underscoreIdx <= 20)
                    cleaned = cleaned.Substring(0, underscoreIdx);

                int exclamationIdx = cleaned.IndexOf('!');
                if (exclamationIdx > 0)
                    cleaned = cleaned.Substring(0, exclamationIdx);

                // Remove prefixo de publisher se parecer GUID
                cleaned = cleaned.Replace("_", " ").Replace(".", " ").Trim();
                // Remove partes numéricas hex curtas
                var parts = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && parts[0].Length == 8 && parts[0].All(c => Uri.IsHexDigit(c)))
                    cleaned = string.Join(" ", parts.Skip(1));

                return cleaned.Length > 3 ? cleaned : aumid;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return aumid; }
        }

        private static string ResolvePackageFamilyName(string familyName)
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = pm.FindPackagesForUser(null, familyName);
                foreach (var pkg in packages)
                {
                    string? dn = pkg.DisplayName;
                    if (!string.IsNullOrEmpty(dn))
                        return dn;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Fallback: limpa o family name
            try
            {
                string cleaned = familyName;
                int underscoreIdx = cleaned.LastIndexOf('_');
                if (underscoreIdx > 0 && cleaned.Length - underscoreIdx <= 20)
                    cleaned = cleaned.Substring(0, underscoreIdx);

                cleaned = cleaned.Replace("_", " ").Replace(".", " ").Trim();
                var parts = cleaned.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && parts[0].Length == 8 && parts[0].All(c => Uri.IsHexDigit(c)))
                    cleaned = string.Join(" ", parts.Skip(1));

                return cleaned.Length > 3 ? cleaned : familyName;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return familyName; }
        }

        // --- Resolução de apps empacotados (UWP/MSIX) para o "Abrir Local do Arquivo" ---
        // Cache por Package Family Name -> localizações de instalação unidas por '|' (a primeira não-neutral
        // fica à frente: a pasta _neutral_ só guarda recursos/manifest, o payload real fica na de arquitetura).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> PackagedAppInstallCache = new();
        private const int PackageQueryTimeoutMs = 20000;

        /// <summary>
        /// Todas as pastas de instalação (InstallLocation) de um pacote pelo Package Family Name.
        /// Ordenadas com a pasta de arquitetura (payload real) à frente da _neutral_. Vazia se não instalado.
        /// </summary>
        private static List<string> GetAllPackagedAppInstallLocations(string packageFamilyName)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(packageFamilyName)) return list;

            if (!PackagedAppInstallCache.TryGetValue(packageFamilyName, out string? joined))
            {
                joined = null;
                try
                {
                    string pkg = packageFamilyName.Replace("'", "''");
                    string cmd = $"$p = Get-AppxPackage -PackageTypeFilter All | Where-Object {{ $_.PackageFamilyName -eq '{pkg}' }}; if ($p) {{ $p.InstallLocation -join '|' }}";

                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{cmd}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var p = Process.Start(psi);
                    if (p == null) return list;
                    string? outText = p.StandardOutput.ReadToEnd();
                    string? errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(PackageQueryTimeoutMs)) { try { p.Kill(); } catch { } }
                    joined = (outText ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(joined)) joined = null;
                }
                catch { Logger.LogWarning("Unknown", "GetAllPackagedAppInstallLocations failed"); }

                PackagedAppInstallCache[packageFamilyName] = joined;
            }

            if (string.IsNullOrWhiteSpace(joined)) return list;
            foreach (string raw in joined.Split('|'))
            {
                string loc = raw.Trim();
                if (!string.IsNullOrEmpty(loc) && System.IO.Directory.Exists(loc)) list.Add(loc);
            }
            // Pastas de arquitetura (x64/arm64/x86) têm o payload real — vêm antes da _neutral_
            list.Sort((a, b) =>
                (a.Contains("_neutral_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
              - (b.Contains("_neutral_", StringComparison.OrdinalIgnoreCase) ? 1 : 0));
            return list;
        }

        /// <summary>
        /// Melhor pasta de instalação do pacote (a de arquitetura, que contém os arquivos reais).
        /// Retorna null se o pacote não estiver instalado.
        /// </summary>
        public static string? GetPackagedAppInstallLocation(string packageFamilyName)
        {
            var all = GetAllPackagedAppInstallLocations(packageFamilyName);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>
        /// Resolve o executável real de um app empacotado (UWP/MSIX/FullTrust) a partir do InstallLocation
        /// e do identificador da tarefa (ou Id da Application no manifest). Lê o AppxManifest.xml do pacote.
        /// Retorna o caminho completo do .exe, ou null se não for possível resolver.
        /// </summary>
        public static string? ResolvePackagedAppExecutable(string packageFamilyName, string taskOrAppId)
        {
            if (string.IsNullOrWhiteSpace(taskOrAppId)) return null;
            string targetId = taskOrAppId.Trim();

            // O payload pode estar em qualquer pasta do pacote (x64, neutral, arm64...) — varre todas
            foreach (string installLocation in GetAllPackagedAppInstallLocations(packageFamilyName))
            {
                string manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    var doc = XDocument.Load(manifestPath);
                    if (doc.Root == null) continue;

                    // 1) Acha a Application que contém um StartupTask com TaskId == targetId e pega seu Executable
                    foreach (var app in doc.Root.Descendants().Where(d => d.Name.LocalName == "Application"))
                    {
                        var task = app.Descendants().FirstOrDefault(d => d.Name.LocalName == "StartupTask");
                        if (task == null) continue;

                        var taskIdAttr = task.Attribute("TaskId");
                        if (taskIdAttr != null && string.Equals(taskIdAttr.Value.Trim(), targetId, StringComparison.OrdinalIgnoreCase))
                        {
                            var exe = BuildLocalExePath(app, installLocation);
                            if (exe != null) return exe;
                        }
                    }

                    // 2) Fallback: Application cujo Id == targetId
                    foreach (var app in doc.Root.Descendants().Where(d => d.Name.LocalName == "Application"))
                    {
                        var idAttr = app.Attribute("Id");
                        if (idAttr == null) continue;
                        if (!string.Equals(idAttr.Value.Trim(), targetId, StringComparison.OrdinalIgnoreCase)) continue;

                        var exe = BuildLocalExePath(app, installLocation);
                        if (exe != null) return exe;
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return null;
        }

        private static string? BuildLocalExePath(XElement app, string installLocation)
        {
            try
            {
                var execAttr = app.Attribute("Executable");
                if (execAttr == null || string.IsNullOrWhiteSpace(execAttr.Value)) return null;

                string rel = execAttr.Value.Replace('/', '\\').TrimStart('\\');
                if (string.IsNullOrEmpty(rel)) return null;

                string full = Path.GetFullPath(Path.Combine(installLocation, rel));
                if (File.Exists(full)) return full;
                return null;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        private static void AddUWPStartupTasksFromRegistry(RegistryKey hive, string subKeyPath, string location, List<StartupAppDetails> apps)
        {
            try
            {
                using var tasksKey = hive.OpenSubKey(subKeyPath);                if (tasksKey == null) return;

                foreach (var subKeyName in tasksKey.GetSubKeyNames())
                {
                    try
                    {
                        using var entryKey = tasksKey.OpenSubKey(subKeyName);
                        if (entryKey == null) continue;

                        var stateObj = entryKey.GetValue("State");
                        if (stateObj == null) continue;

                        int state = Convert.ToInt32(stateObj);
                        // State: 0=Disabled, 1=Enabled, 2=EnabledByPolicy (Explorer\StartupTasks)
                        bool isEnabled = state >= 1;

                        string aumid = subKeyName;
                        string displayName = ResolveAumidToDisplayName(aumid);

                        // Evita duplicatas por nome parecido
                        if (apps.Any(a =>
                            a.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase) &&
                            (a.Location ?? "").Contains("UWP", StringComparison.OrdinalIgnoreCase)))
                            continue;

                        apps.Add(new StartupAppDetails(
                            displayName,
                            $"StartupTask: {aumid}",
                            location,
                            isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled));
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private static (bool Success, string Message) CreateUWPFallbackTask(string appName, string aumid, bool enable, bool silentMode)
        {
            if (!enable)
            {
                // Remove a tarefa fallback se existir
                try
                {
                    using (var ts = new TaskService())
                    {
                        string taskName = $"KitLUGIA_UWP_{SanitizeTaskName(appName)}";
                        var existing = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == taskName);
                        if (existing != null)
                        {
                            ts.RootFolder.DeleteTask(taskName);
                            InvalidateCache();
                            return (true, silentMode ? "" : $"Tarefa fallback '{appName}' foi removida.");
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return (true, silentMode ? "" : $"App UWP '{appName}' desabilitado (sem tarefa fallback).");
            }

            try
            {
                using (var ts = new TaskService())
                {
                    string taskName = $"KitLUGIA_UWP_{SanitizeTaskName(appName)}";
                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = $"KitLugia fallback para {appName}";
                    td.Principal.LogonType = TaskLogonType.InteractiveToken;

                    // Launch via explorer shell:appsFolder
                    td.Actions.Add(new ExecAction("explorer.exe", $"shell:appsFolder\\{aumid}", null));

                    td.Triggers.Add(new LogonTrigger());
                    td.Settings.Enabled = true;
                    td.Settings.StartWhenAvailable = true;
                    td.Settings.AllowHardTerminate = true;

                    ts.RootFolder.RegisterTaskDefinition(taskName, td,
                        TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);

                    InvalidateCache();
                    return (true, silentMode ? "" : $"Tarefa fallback '{appName}' foi criada no agendador.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao criar tarefa fallback para UWP: {ex.Message}");
            }
        }

        private static string SanitizeTaskName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }
            // Trunca para evitar nomes muito longos no Task Scheduler
            return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
        }

        // --- AS 4 OPÇÕES DE CRIAÇÃO ---

        public static (bool Success, string Message) CreateDelayedStartupTask(string appName, string appPath, string? arguments)
        {
            return CreateTaskInternal(appName, appPath, arguments, elevated: false, forceLongDelay: false);
        }

        public static (bool Success, string Message) CreateElevatedStartupTask(string appName, string appPath, string? arguments)
        {
            return CreateTaskInternal(appName, appPath, arguments, elevated: true, forceLongDelay: false);
        }

        public static (bool Success, string Message) CreateElevatedDelayedStartupTask(string appName, string appPath, string? arguments)
        {
            return CreateTaskInternal(appName, appPath, arguments, elevated: true, forceLongDelay: true);
        }

        private static (bool Success, string Message) CreateTaskInternal(string appName, string appPath, string? arguments, bool elevated, bool forceLongDelay)
        {
            try
            {
                using (var ts = new TaskService())
                {
                    string prefix = elevated ? "KitLUGIA_Elevated_" : "KitLUGIA_Delayed_";
                    string taskName = $"{prefix}{appName}";

                    if (ts.FindTask(taskName) != null) ts.RootFolder.DeleteTask(taskName);

                    var td = ts.NewTask();
                    td.RegistrationInfo.Description = $"Startup task for {appName} by KitLUGIA (Elevated: {elevated}, Delayed: {forceLongDelay})";

                    td.Principal.RunLevel = elevated ? TaskRunLevel.Highest : TaskRunLevel.LUA;

                    var trigger = new LogonTrigger();

                    // Lógica de Tempo:
                    if (forceLongDelay)
                    {
                        trigger.Delay = TimeSpan.FromMinutes(2); // Força 2 min
                    }
                    else if (elevated)
                    {
                        trigger.Delay = TimeSpan.FromSeconds(5); // Padrão admin
                    }
                    else
                    {
                        trigger.Delay = TimeSpan.FromMinutes(2); // Padrão delayed
                    }

                    td.Triggers.Add(trigger);
                    td.Actions.Add(new ExecAction(appPath, arguments, Path.GetDirectoryName(appPath) ?? ""));

                    td.Settings.DisallowStartIfOnBatteries = false;
                    td.Settings.StopIfGoingOnBatteries = false;
                    td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

                    ts.RootFolder.RegisterTaskDefinition(taskName, td);
                }

                try { using var rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); rk?.DeleteValue(appName, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); rk?.DeleteValue(appName, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                string typeMsg = elevated ? "ADMIN" : "NORMAL";
                string delayMsg = forceLongDelay || (!elevated) ? "+ ATRASO" : "";
                return (true, $"Tarefa '{typeMsg} {delayMsg}' criada para {appName}.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao agendar tarefa: {ex.Message}");
            }
        }

        public static (bool Success, string Message) RemoveElevatedStartupTask(string fullTaskName)
        {
            try
            {
                string cleanName = fullTaskName.Replace("KitLUGIA_Elevated_", "").Replace("KitLUGIA_Delayed_", "");
                SetStartupItemState(cleanName, true, true);

                using (var ts = new TaskService())
                {
                    ts.RootFolder.DeleteTask(fullTaskName);
                    return (true, "Tarefa removida. Tentativa de restaurar inicialização padrão feita.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        #endregion

        #region KitLugia Parallel Startup (Turbo)

        public static bool GetBootTrayAdminFlag(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey);
                return key?.GetValue(appName + "__Admin")?.ToString() != "0";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return true; }
        }

        public static void SetBootTrayAdminFlag(string appName, bool runAsAdmin)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KitLugiaStartupKey);
                if (key != null)
                {
                    key.SetValue(appName + "__Admin", runAsAdmin ? "1" : "0");

                    if (!runAsAdmin)
                    {
                        string command = key.GetValue(appName)?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(command))
                    {
                        ExtractCommandParts(command, out string? path, out string? args);
                        if (!string.IsNullOrEmpty(path))
                            RegisterNonAdminTask(appName, path, args);
                    }
                }
                else
                {
                    UnregisterNonAdminTask(appName);
                }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) DelegateToKitLugia(string appName, bool runAsAdmin = true)
        {
            try
            {
                var apps = GetStartupAppsWithDetails(true);
                var app = apps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (app == null) return (false, "App não encontrado.");

                // 1. Remove from standard startup softly
                RemoveStartupItem(appName);

                // Mark as disabled in StartupApproved so it stays visible in the list
                try
                {
                    byte[] disabledValue = { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    using var approved = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
                    approved?.SetValue(appName, disabledValue, RegistryValueKind.Binary);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 2. Add to KitLugia list
                using var key = Registry.CurrentUser.CreateSubKey(KitLugiaStartupKey);
                if (key != null)
                {
                key.SetValue(appName, app.FullCommand);
                key.SetValue(appName + "__Admin", runAsAdmin ? "1" : "0");

                if (!runAsAdmin)
                {
                    ExtractCommandParts(app.FullCommand, out string? path, out string? args);
                    if (!string.IsNullOrEmpty(path))
                        RegisterNonAdminTask(appName, path, args);
                }
                }

                string suffix = runAsAdmin ? "" : " (sem Admin)";
                return (true, $"'{appName}' agora iniciará via Turbo Boot (KitLugia){suffix}.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static (bool Success, string Message) RemoveFromKitLugia(string appName)
        {
            try
            {
                UnregisterNonAdminTask(appName);

                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey, true);
                if (key != null)
                {
                    key.DeleteValue(appName, false);
                    key.DeleteValue(appName + "__Admin", false);
                    key.DeleteValue(appName + "__Backup", false);
                    return (true, $"'{appName}' removido do KitLugia com sucesso.");
                }
                return (false, "Chave de registro não encontrada.");
            }
            catch (Exception ex) { return (false, $"Erro ao remover: {ex.Message}"); }
        }

        public static (bool Success, string Message) RestoreToNormal(string appName)
        {
            try
            {
                var apps = GetStartupAppsWithDetails(true);
                var app = apps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (app == null) return (false, "App não encontrado.");

                string command = app.FullCommand;

                // 1. Remove from Turbo Boot
                RemoveFromKitLugia(appName);

                // 2. Remove from Task Scheduler (Elevated/Delayed)
                string taskNameElevated = "KitLUGIA_Elevated_" + appName.Replace(" ", "_");
                string taskNameDelayed = "KitLUGIA_Delayed_" + appName.Replace(" ", "_");
                using (var ts = new TaskService())
                {
                    if (ts.RootFolder.AllTasks.Any(t => t.Name == taskNameElevated)) ts.RootFolder.DeleteTask(taskNameElevated, false);
                    if (ts.RootFolder.AllTasks.Any(t => t.Name == taskNameDelayed)) ts.RootFolder.DeleteTask(taskNameDelayed, false);
                }

                // 3. Restore to standard registry
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue(appName, command);

                return (true, $"'{appName}' restaurado para inicialização padrão.");
            }
            catch (Exception ex) { return (false, $"Erro ao restaurar: {ex.Message}"); }
        }

        #region Dormant Task Scheduler (non-admin launch)

        private static string GetNonAdminTaskName(string appName)
        {
            return "KitLUGIA_NonAdmin_" + appName;
        }

        public static void RegisterNonAdminTask(string appName, string path, string? args)
        {
            try
            {
                string taskName = GetNonAdminTaskName(appName);
                using var ts = new TaskService();
                if (ts.GetTask(taskName) != null)
                    ts.RootFolder.DeleteTask(taskName, false);

                var td = ts.NewTask();
                td.RegistrationInfo.Description = $"Non-admin startup: {appName} (KitLUGIA)";
                td.Principal.LogonType = TaskLogonType.InteractiveToken;
                td.Principal.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                td.Settings.Hidden = true;
                td.Settings.WakeToRun = false;
                td.Actions.Add(new ExecAction(path, args ?? "", Path.GetDirectoryName(path) ?? ""));
                ts.RootFolder.RegisterTaskDefinition(taskName, td);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void UnregisterNonAdminTask(string appName)
        {
            try
            {
                string taskName = GetNonAdminTaskName(appName);
                using var ts = new TaskService();
                if (ts.GetTask(taskName) != null)
                    ts.RootFolder.DeleteTask(taskName, false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static void RunNonAdminTask(string appName)
        {
            try
            {
                string taskName = GetNonAdminTaskName(appName);
                using var ts = new TaskService();
                ts.GetTask(taskName)?.Run();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        #endregion

        public static void LaunchTurboAppsNonAdmin()
        {
            LaunchTurboApps(true);
        }

        public static void LaunchTurboApps(bool? forceNonAdmin = null)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey);
                if (key == null) return;

                foreach (var name in key.GetValueNames())
                {
                    if (name.EndsWith("__Admin")) continue;
                    string command = key.GetValue(name)?.ToString() ?? "";
                    if (string.IsNullOrEmpty(command)) continue;

                    bool runAsAdmin = forceNonAdmin ?? (key.GetValue(name + "__Admin")?.ToString() != "0");

                    // OTIMIZAÇÃO: Thread.Start garante concorrência absoluta e imediata.
                    // O Task.Run usa o ThreadPool que, em picos de estresse de CPU na inicialização,
                    // pode enfileirar tarefas (ex: Discord ficar na fila do Opera).
                    new System.Threading.Thread(() =>
                    {
                        try
                        {
                            ExtractCommandParts(command, out string? path, out string? args);
                            if (string.IsNullOrEmpty(path)) return;

                            if (runAsAdmin)
                            {
                                var startInfo = new ProcessStartInfo
                                {
                                    FileName = path,
                                    Arguments = args,
                                    UseShellExecute = true,
                                    WindowStyle = ProcessWindowStyle.Normal,
                                    WorkingDirectory = Path.GetDirectoryName(path) ?? ""
                                };
                                Process.Start(startInfo);
                            }
                            else
                            {
                                RunNonAdminTask(name);
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }){ IsBackground = true, Priority = System.Threading.ThreadPriority.AboveNormal }.Start();
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static (bool Success, string Message) UpdateStartupArgs(string appName, string newFullCommand)
        {
            try
            {
                var startupApp = GetStartupAppsWithDetails(true).FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (startupApp == null) return (false, "Aplicativo não encontrado.");

                ExtractCommandParts(newFullCommand, out string? exePath, out string? args);

                if (startupApp.Location.StartsWith("HK"))
                {
                    RegistryKey baseKey = startupApp.Location.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKeyPath = startupApp.Location.Substring(startupApp.Location.IndexOf('\\') + 1);
                    using var key = baseKey.OpenSubKey(subKeyPath, true);
                    if (key != null)
                    {
                        key.SetValue(appName, newFullCommand);
                        return (true, $"Argumentos atualizados para '{appName}'.");
                    }
                    return (false, "Não foi possível acessar o registro.");
                }
                else if (startupApp.Location.Contains("Agendador"))
                {
                    using (var ts = new TaskService())
                    {
                        var task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name.Contains(appName) && t.Name.StartsWith("KitLUGIA_"));
                        if (task != null)
                        {
                            task.Definition.Actions.Clear();
                            task.Definition.Actions.Add(new ExecAction(exePath, args, Path.GetDirectoryName(exePath) ?? ""));
                            task.RegisterChanges();
                            return (true, $"Argumentos atualizados para '{appName}'.");
                        }
                    }
                    return (false, "Tarefa agendada não encontrada.");
                }
                else if (startupApp.Location.Contains("\\Startup") || startupApp.Location.Contains("\\Start Menu"))
                {
                    string shortcutPath = System.IO.Path.Combine(startupApp.Location, appName + ".lnk");
                    bool ok = CreateShortcut(shortcutPath, exePath ?? newFullCommand, args ?? "", appName, System.IO.Path.GetDirectoryName(exePath ?? newFullCommand) ?? "");
                    return (ok, ok ? $"Atalho '{appName}' atualizado com novos argumentos." : $"Erro ao atualizar atalho '{appName}'.");
                }
                else if (startupApp.Location.Contains("KitLugia") || startupApp.Location.Contains("Turbo Boot"))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\StartupApps");
                    if (key != null)
                    {
                    key.SetValue(appName, newFullCommand);
                    return (true, $"Argumentos atualizados para '{appName}' (Turbo Boot).");
                    }
                }

                return (false, "Tipo de inicialização não reconhecido.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao atualizar argumentos: {ex.Message}");
            }
        }

        #endregion

        private const string RemovedAppsBackupKey = @"Software\KitLugia\RemovedApps";

        private static void BackupStartupItem(StartupAppDetails app)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RemovedAppsBackupKey);
                if (key != null)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    key.SetValue(app.Name + "__Command", app.FullCommand ?? "");
                    key.SetValue(app.Name + "__Location", app.Location ?? "");
                    key.SetValue(app.Name + "__Timestamp", timestamp);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        public static List<string> GetRemovedApps()
        {
            var list = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RemovedAppsBackupKey);
                if (key == null) return list;
                var names = new HashSet<string>();
                foreach (var val in key.GetValueNames())
                {
                    if (val.EndsWith("__Command") && !val.EndsWith("__RunCommand")) names.Add(val.Replace("__Command", ""));
                    else if (val.EndsWith("__Location") && !val.EndsWith("__RunLocation"))
                        names.Add(val.Replace("__Location", ""));
                }
                list = names.OrderBy(n => n).ToList();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return list;
        }

        public static string GetRemovedAppCommand(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RemovedAppsBackupKey);
                return key?.GetValue(appName + "__Command")?.ToString() ?? "";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return ""; }
        }

        public static string GetRemovedAppLocation(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RemovedAppsBackupKey);
                return key?.GetValue(appName + "__Location")?.ToString() ?? "";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return ""; }
        }

        public static (bool Success, string Message) RestoreRemovedItem(string appName)
        {
            try
            {
                string command = GetRemovedAppCommand(appName);
                string location = GetRemovedAppLocation(appName);
                if (string.IsNullOrEmpty(command)) return (false, "Nenhum backup encontrado para este app.");

                ExtractCommandParts(command, out string? exePath, out string? args);
                if (string.IsNullOrEmpty(exePath)) return (false, "Comando inválido no backup.");

                if (location.Contains("\\Startup") || location.Contains("\\Start Menu"))
                {
                    string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    string shortcutPath = Path.Combine(startupFolder, appName + ".lnk");
                    CreateShortcut(shortcutPath, exePath, args ?? "", appName, Path.GetDirectoryName(exePath) ?? "");
                }
                else if (location.StartsWith("HK"))
                {
                    RegistryKey baseKey = location.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKeyPath;
                    if (location.StartsWith("HKLM\\WOW6432Node", StringComparison.OrdinalIgnoreCase))
                        subKeyPath = location.Substring("HKLM\\WOW6432Node\\".Length);
                    else
                        subKeyPath = location.Substring(location.IndexOf('\\') + 1);
                    using var key = baseKey.OpenSubKey(subKeyPath, true);
                    if (key != null) key.SetValue(appName, command);
                }

                // Clean backup
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RemovedAppsBackupKey, true);
                    if (key != null)
                    {
                        key.DeleteValue(appName + "__Command", false);
                        key.DeleteValue(appName + "__Location", false);
                        key.DeleteValue(appName + "__Timestamp", false);
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                InvalidateCache();
                return (true, $"'{appName}' restaurado para inicialização.");
            }
            catch (Exception ex) { return (false, $"Erro ao restaurar: {ex.Message}"); }
        }

        public static bool CreateShortcut(string shortcutPath, string targetPath, string arguments, string description, string workingDirectory)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.Arguments = arguments ?? "";
                shortcut.Description = description ?? "";
                shortcut.WorkingDirectory = workingDirectory ?? "";
                shortcut.Save();
                return true;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        private static void CleanStartupApproved(string appName)
        {
            try
            {
                string[] approvedPaths = {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"
                };
                foreach (var path in approvedPaths)
                {
                    try { using var key = Registry.CurrentUser.OpenSubKey(path, true); key?.DeleteValue(appName, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    try { using var key = Registry.LocalMachine.OpenSubKey(path, true); key?.DeleteValue(appName, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        #region Helpers

    public static void ExtractCommandParts(string commandLine, out string? path, out string? args)
    {
        path = null; args = "";
        if (string.IsNullOrWhiteSpace(commandLine)) return;
        commandLine = Environment.ExpandEnvironmentVariables(commandLine.Trim());

        if (commandLine.StartsWith("\""))
        {
            int endQuote = commandLine.IndexOf('"', 1);
            if (endQuote > 0)
            {
                path = commandLine.Substring(1, endQuote - 1);
                if (endQuote < commandLine.Length - 1) args = commandLine.Substring(endQuote + 1).Trim();
                if (!string.IsNullOrEmpty(path) && !path.Contains(".") && args == "")
                {
                    path = commandLine;
                }
                return;
            }
        }

        int exeIndex = -1;
        string foundExt = "";
        string[] knownExtensions = { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".jar", ".lnk", ".url", ".ink", ".scr", ".pif", ".msi", ".wsf", ".py", ".pl", ".rb" };

        foreach (var ext in knownExtensions)
        {
            int idx = commandLine.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                int endOfExt = idx + ext.Length;
                if (endOfExt >= commandLine.Length || commandLine[endOfExt] == ' ' || commandLine[endOfExt] == '"' || commandLine[endOfExt] == '\t')
                {
                    if (exeIndex < 0 || endOfExt > exeIndex + foundExt.Length)
                    {
                        exeIndex = idx;
                        foundExt = ext;
                    }
                }
            }
        }

        if (exeIndex > 0)
        {
            path = commandLine.Substring(0, exeIndex + foundExt.Length).Trim();
            if (commandLine.Length > exeIndex + foundExt.Length)
            {
                string rest = commandLine.Substring(exeIndex + foundExt.Length).Trim();
                args = rest;
            }
            return;
        }

        // Fallback: verifica se é um caminho sem extensão (Windows auto-adiciona .exe)
        int firstSpace = commandLine.IndexOf(' ');
        if (firstSpace > 0)
        {
            string candidate = commandLine.Substring(0, firstSpace);
            if (candidate.Contains("\\") || candidate.Contains("/"))
            {
                path = candidate;
                args = commandLine.Substring(firstSpace + 1).Trim();
                return;
            }
            if (candidate.Contains("."))
            {
                path = candidate;
                args = commandLine.Substring(firstSpace + 1).Trim();
                return;
            }
        }
        else if (commandLine.Contains("\\") || commandLine.Contains("/"))
        {
            path = commandLine;
            return;
        }

        path = commandLine;
    }

        private static string GetFileNameFromCommandLine(string commandLine)
        {
            ExtractCommandParts(commandLine, out string? path, out _);
            return string.IsNullOrEmpty(path) ? commandLine : Path.GetFileName(path);
        }

        private static string GetCommandLineFromShortcut(string shortcutPath)
        {
            string ext = Path.GetExtension(shortcutPath);
            bool isShortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".ink", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".pif", StringComparison.OrdinalIgnoreCase);
            if (!isShortcut) return $"\"{shortcutPath}\"";

            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return $"\"{shortcutPath}\"";
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                string target = shortcut.TargetPath ?? "";
                string args = shortcut.Arguments ?? "";
                if (!string.IsNullOrEmpty(target))
                    return $"\"{target}\" {args}".Trim();
                return $"\"{shortcutPath}\"";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return $"\"{shortcutPath}\""; }
        }

        private static HashSet<string> GetElevatedTaskExecutablePaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var ts = new TaskService())
                {
                    foreach (var task in ts.RootFolder.Tasks)
                    {
                        if (task.Definition.Actions.FirstOrDefault() is ExecAction action)
                        {
                            bool isElevated = task.Name.StartsWith("KitLUGIA_Elevated_") ||
                                              task.Definition.Principal.RunLevel == TaskRunLevel.Highest;
                            if (isElevated)
                                paths.Add(action.Path);
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return paths;
        }

        #endregion

        #region Advanced Startup Locations (Winlogon, AppInit, BHO, BootExecute)

        public static List<StartupAppDetails> GetWinlogonItems()
        {
            var items = new List<StartupAppDetails>();
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Winlogon"
            };

            foreach (var regPath in keys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath);
                    if (key == null) continue;

                    string shell = key.GetValue("Shell") as string ?? "";
                    string userinit = key.GetValue("Userinit") as string ?? "";
                    string vmApplet = key.GetValue("AppSetup") as string ?? "";

                    if (!string.IsNullOrEmpty(shell) && !shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                        items.Add(new StartupAppDetails($"Winlogon Shell ({regPath})", shell, $"{regPath}\\Shell", StartupStatus.Enabled));

                    if (!string.IsNullOrEmpty(userinit))
                    {
                        var parts = userinit.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        foreach (var part in parts)
                        {
                            if (!part.Contains("userinit.exe", StringComparison.OrdinalIgnoreCase))
                                items.Add(new StartupAppDetails($"Userinit ({Path.GetFileName(part)})", part, $"{regPath}\\Userinit", StartupStatus.Enabled));
                        }
                    }

                    if (!string.IsNullOrEmpty(vmApplet))
                        items.Add(new StartupAppDetails($"AppSetup ({regPath})", vmApplet, $"{regPath}\\AppSetup", StartupStatus.Enabled));
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return items;
        }

        public static List<StartupAppDetails> GetAppInitDlls()
        {
            var items = new List<StartupAppDetails>();
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows"
            };

            foreach (var regPath in keys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath);
                    if (key == null) continue;

                    string dlls = key.GetValue("AppInit_DLLs") as string ?? "";
                    object? loadFlag = key.GetValue("LoadAppInit_DLLs");

                    bool isEnabled = loadFlag != null && loadFlag.ToString() == "1";

                    if (!string.IsNullOrEmpty(dlls))
                    {
                        var parts = dlls.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var dll in parts)
                        {
                            string name = $"AppInit_DLL: {Path.GetFileName(dll)}";
                            string status = isEnabled ? "Ativo" : "Inativo (LoadAppInit=0)";
                            items.Add(new StartupAppDetails(name, dll, $"{regPath}\\AppInit_DLLs",
                                isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled));
                        }
                    }

                    if (isEnabled && string.IsNullOrEmpty(dlls))
                    {
                        items.Add(new StartupAppDetails("AppInit_DLLs (habilitado, vazio)", "",
                            $"{regPath}\\AppInit_DLLs", StartupStatus.Enabled));
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return items;
        }

        public static List<StartupAppDetails> GetBHOItems()
        {
            var items = new List<StartupAppDetails>();
            string[] bhoPaths = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects",
                @"SOFTWARE\Microsoft\Internet Explorer\Extensions"
            };

            foreach (var bhoPath in bhoPaths)
            {
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(bhoPath);
                    if (baseKey == null) continue;

                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var subKey = baseKey.OpenSubKey(sub);
                            if (subKey == null) continue;

                            string name = subKey.GetValue("Name") as string
                                          ?? subKey.GetValue("ButtonText") as string
                                          ?? $"BHO {{{sub}}}";
                            string clsid = subKey.GetValue("CLSID") as string
                                           ?? subKey.GetValue("CLSID") as string ?? sub;

                            // Try to get the InProcServer32 from the CLSID
                            try
                            {
                                using var clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}\\InProcServer32");
                                if (clsidKey != null)
                                {
                                    string dllPath = clsidKey.GetValue(null) as string ?? "";
                                    items.Add(new StartupAppDetails($"BHO: {name}", dllPath, bhoPath, StartupStatus.Enabled));
                                    continue;
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                            items.Add(new StartupAppDetails($"BHO: {name}", clsid, bhoPath, StartupStatus.Enabled));
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return items;
        }

        public static List<StartupAppDetails> GetBootExecuteItems()
        {
            var items = new List<StartupAppDetails>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager");
                if (key == null) return items;

                object? bootExec = key.GetValue("BootExecute");
                object? setupExec = key.GetValue("SetupExecute");
                object? exec = key.GetValue("Execute");
                object? pnpExec = key.GetValue("PnPMajorDeviceInit");

                if (bootExec is string[] bootArr)
                {
                    foreach (var cmd in bootArr)
                    {
                        string trimmed = cmd.Trim().Trim('*');
                        if (!string.IsNullOrEmpty(trimmed))
                            items.Add(new StartupAppDetails("BootExecute", trimmed,
                                @"HKLM\SYSTEM\...\Session Manager\BootExecute", StartupStatus.Enabled));
                    }
                }
                else if (bootExec is string bootStr && !string.IsNullOrEmpty(bootStr))
                {
                    items.Add(new StartupAppDetails("BootExecute", bootStr,
                        @"HKLM\SYSTEM\...\Session Manager\BootExecute", StartupStatus.Enabled));
                }

                if (setupExec is string[] setupArr)
                {
                    foreach (var cmd in setupArr)
                    {
                        string trimmed = cmd.Trim().Trim('*');
                        if (!string.IsNullOrEmpty(trimmed))
                            items.Add(new StartupAppDetails("SetupExecute", trimmed,
                                @"HKLM\SYSTEM\...\Session Manager\SetupExecute", StartupStatus.Enabled));
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return items;
        }

        public static List<StartupAppDetails> GetKnownDllsItems()
        {
            var items = new List<StartupAppDetails>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs");
                if (key == null) return items;

                foreach (var name in key.GetValueNames())
                {
                    string dll = key.GetValue(name) as string ?? "";
                    if (!string.IsNullOrEmpty(dll))
                        items.Add(new StartupAppDetails($"KnownDLL: {name}", dll,
                            @"HKLM\SYSTEM\...\Session Manager\KnownDLLs", StartupStatus.Enabled));
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return items;
        }

        public static List<StartupAppDetails> GetShellServiceObjectDelayLoad()
        {
            var items = new List<StartupAppDetails>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\ShellServiceObjectDelayLoad");
                if (key == null) return items;

                foreach (var name in key.GetValueNames())
                {
                    string val = key.GetValue(name) as string ?? "";
                    if (!string.IsNullOrEmpty(val))
                    {
                        // Try to resolve CLSID to friendly name
                        string displayName = val;
                        try
                        {
                            using var clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{val}");
                            if (clsidKey != null)
                            {
                                string friendlyName = clsidKey.GetValue(null) as string ?? "";
                                if (!string.IsNullOrEmpty(friendlyName))
                                    displayName = $"{friendlyName} ({val})";
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        items.Add(new StartupAppDetails($"SSODL: {name}",
                            displayName,
                            @"HKLM\SYSTEM\...\Session Manager\ShellServiceObjectDelayLoad",
                            StartupStatus.Enabled));
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return items;
        }

        public static List<StartupAppDetails> GetShellExecuteHooks()
        {
            var items = new List<StartupAppDetails>();
            string[] paths = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ShellExecuteHooks"
            };

            foreach (var regPath in paths)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath);
                    if (key == null) continue;

                    foreach (var clsid in key.GetValueNames())
                    {
                        string val = key.GetValue(clsid) as string ?? "";
                        if (string.IsNullOrEmpty(val)) continue;

                        // Try to resolve CLSID to friendly name
                        string displayName = val;
                        try
                        {
                            using var clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}");
                            if (clsidKey != null)
                            {
                                string friendlyName = clsidKey.GetValue(null) as string ?? "";
                                if (!string.IsNullOrEmpty(friendlyName))
                                    displayName = $"{friendlyName} ({clsid})";
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        items.Add(new StartupAppDetails($"ShellExecHook: {displayName}", val,
                            regPath, StartupStatus.Enabled));
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return items;
        }

        public static List<StartupAppDetails> GetContextMenuHandlers()
        {
            var items = new List<StartupAppDetails>();
            string[] basePaths =
            {
                @"SOFTWARE\Classes\*\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Directory\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Directory\Background\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Drive\shellex\ContextMenuHandlers",
                @"SOFTWARE\Classes\Folder\shellex\ContextMenuHandlers",
                @"SOFTWARE\WOW6432Node\Classes\*\shellex\ContextMenuHandlers",
                @"SOFTWARE\WOW6432Node\Classes\Directory\shellex\ContextMenuHandlers",
                @"SOFTWARE\WOW6432Node\Classes\Directory\Background\shellex\ContextMenuHandlers",
                @"SOFTWARE\WOW6432Node\Classes\Drive\shellex\ContextMenuHandlers",
                @"SOFTWARE\WOW6432Node\Classes\Folder\shellex\ContextMenuHandlers"
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var regPath in basePaths)
            {
                try
                {
                    using var baseKey = Registry.LocalMachine.OpenSubKey(regPath);
                    if (baseKey == null) continue;

                    foreach (var sub in baseKey.GetSubKeyNames())
                    {
                        if (!seen.Add(sub)) continue;

                        try
                        {
                            using var subKey = baseKey.OpenSubKey(sub);
                            if (subKey == null) continue;

                            string clsid = subKey.GetValue(null) as string ?? sub;

                            // Try to resolve CLSID to friendly name
                            string displayName = clsid;
                            try
                            {
                                using var clsidKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{clsid}");
                                if (clsidKey != null)
                                {
                                    string friendlyName = clsidKey.GetValue(null) as string ?? "";
                                    if (!string.IsNullOrEmpty(friendlyName))
                                        displayName = $"{friendlyName} ({clsid})";
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                            items.Add(new StartupAppDetails($"ContextMenu: {sub}",
                                displayName,
                                regPath, StartupStatus.Enabled));
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }

            return items;
        }

        public static List<StartupAppDetails> GetAllAdvancedItems()
        {
            var all = new List<StartupAppDetails>();
            all.AddRange(GetWinlogonItems());
            all.AddRange(GetAppInitDlls());
            all.AddRange(GetBHOItems());
            all.AddRange(GetBootExecuteItems());
            all.AddRange(GetKnownDllsItems());
            all.AddRange(GetShellServiceObjectDelayLoad());
            all.AddRange(GetShellExecuteHooks());
            all.AddRange(GetContextMenuHandlers());
            return all;
        }

        #endregion

        #region Auto-Updater Integration

        public static void CheckAndFixStartupMethods()
        {
            try
            {
                Logger.Log("🔍 Verificando métodos de inicialização do KitLugia...");
                
                var currentExe = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location ?? AppContext.BaseDirectory.TrimEnd('\\') + "\\KitLugia.GUI.exe";
                
                // Executar em background para não travar a UI
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        // Pequena pausa para o app terminar de iniciar antes de fazer I/O pesado
                        System.Threading.Thread.Sleep(1500);

                        // 1. Verificar Registry Run (HKCU)
                        CheckRegistryRun(currentExe);
                        
                        // 2. Verificar Task Scheduler
                        CheckTaskScheduler(currentExe);
                        
                        // 3. Verificar Startup Folder
                        CheckStartupFolder(currentExe);

                        Logger.Log("✅ Verificação de inicialização concluída com sucesso");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"❌ Erro na verificação de inicialização: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao iniciar verificação de inicialização: {ex.Message}");
            }
        }
        
        private static void CheckRegistryRun(string exePath)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null)
                    {
                        Logger.Log("❌ Não foi possível acessar o registro Run");
                        return;
                    }

                    var rawValue = key.GetValue("KitLugia") as string;
                    string expectedValue = $"\"{exePath}\" --tray";

                    if (string.IsNullOrEmpty(rawValue))
                    {
                        Logger.Log("❌ Nenhuma entrada no registro Run encontrada");
                        return;
                    }

                    // Extrai o caminho do executável do valor (com ou sem aspas)
                    string existingPath = rawValue.Trim();
                    if (existingPath.StartsWith("\""))
                    {
                        int endQuote = existingPath.IndexOf('"', 1);
                        existingPath = endQuote > 0 ? existingPath.Substring(1, endQuote - 1) : exePath;
                    }
                    else
                    {
                        // Valor sem aspas — pega até o primeiro espaço
                        int firstSpace = existingPath.IndexOf(' ');
                        if (firstSpace > 0) existingPath = existingPath.Substring(0, firstSpace);
                    }

                    if (string.IsNullOrEmpty(existingPath))
                    {
                        Logger.Log("⚠️ Valor do registro Run inválido, ignorando...");
                        return;
                    }

                    if (!File.Exists(existingPath))
                    {
                        Logger.Log($"⚠️ Entrada no registro Run aponta para arquivo inexistente: {existingPath}");
                        return;
                    }

                    if (!string.Equals(existingPath, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"⚠️ Entrada no registro Run aponta para versão antiga: {existingPath}");
                        return;
                    }

                    if (rawValue != expectedValue)
                    {
                        Logger.Log("⚠️ Corrigindo entrada no registro Run (caminho sem aspas ou sem --tray)...");
                        key.SetValue("KitLugia", expectedValue);
                        Logger.Log("✅ Entrada no registro Run corrigida");
                        return;
                    }

                    Logger.Log("✅ Entrada no registro Run está correta");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao verificar registro Run: {ex.Message}");
            }
        }
        
        private static void CheckTaskScheduler(string exePath)
        {
            try
            {
                using (var ts = new TaskService())
                {
                    var task = ts.GetTask("KitLugia");
                    
                    if (task == null)
                    {
                        Logger.Log("ℹ️ Nenhuma tarefa agendada encontrada (o TrayIcon criará se necessário)");
                        return;
                    }

                    var execAction = task.Definition.Actions.FirstOrDefault() as ExecAction;
                    string? taskPath = execAction?.Path;

                    if (string.IsNullOrEmpty(taskPath) || !string.Equals(taskPath, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log($"⚠️ Tarefa agendada aponta para: {taskPath ?? "(vazio)"}");
                        Logger.Log("🔧 Atualizando tarefa agendada...");

                        task.Definition.Actions.Clear();
                        task.Definition.Actions.Add(new ExecAction(exePath, "--tray", Path.GetDirectoryName(exePath) ?? ""));
                        task.RegisterChanges();

                        Logger.Log("✅ Tarefa agendada atualizada");
                    }
                    else
                    {
                        Logger.Log("✅ Tarefa agendada está correta");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao verificar Task Scheduler: {ex.Message}");
            }
        }
        
        private static void CheckStartupFolder(string exePath)
        {
            try
            {
                var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var shortcutPath = Path.Combine(startupPath, "KitLugia.lnk");
                
                if (!File.Exists(shortcutPath))
                {
                    Logger.Log("❌ Nenhum atalho na pasta Startup encontrado");
                    Logger.Log("ℹ️ O KitLugia usa Registry Run e Task Scheduler para inicialização");
                }
                else
                {
                    Logger.Log("✅ Atalho na pasta Startup encontrado");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"❌ Erro ao verificar pasta Startup: {ex.Message}");
            }
        }

        #endregion
    }
}