using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler; // Requer NuGet: TaskScheduler
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
// using IWshRuntimeLibrary; // Temporariamente comentado para permitir compilação

// Resolve ambiguidade entre System.IO.File e IWshRuntimeLibrary.File
using File = System.IO.File;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class StartupManager
    {
        private const string KitLugiaStartupKey = @"Software\KitLugia\StartupApps";
        #region Leitura e Análise

        public static List<StartupAppDetails> GetStartupAppsWithDetails(bool bypassElevationCheck = false)
        {

            // Típico: 10-50 apps de inicialização
            var apps = new Dictionary<string, StartupAppDetails>(50, StringComparer.OrdinalIgnoreCase);

            // Lista de caminhos elevados para verificar se um app do registro já tem uma tarefa admin correspondente

            var elevatedTaskPaths = bypassElevationCheck ? new HashSet<string>(20) : GetElevatedTaskExecutablePaths();

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
                        if (apps.ContainsKey(name)) continue;

                        string commandLine = GetCommandLineFromShortcut(file);
                        ExtractCommandParts(commandLine, out string? exePath, out _);

                        var value = approvedKey?.GetValue(Path.GetFileName(file)) as byte[];
                        bool isEnabled = value == null || value.Length < 1 || value[0] == 2 || value[0] == 0;

                        var status = (exePath != null && elevatedTaskPaths.Contains(exePath)) ? StartupStatus.Elevated : (isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled);

                        apps.Add(name, new StartupAppDetails(name, commandLine, folder, status));
                    }
                    catch { }
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
                        if (string.IsNullOrEmpty(valueName) || apps.ContainsKey(valueName)) continue;
                        var commandLine = key.GetValue(valueName)?.ToString() ?? "";

                        ExtractCommandParts(commandLine, out string? exePath, out _);

                        var value = approvedKey?.GetValue(valueName) as byte[];
                        bool isEnabled = value == null || value.Length < 1 || (value[0] % 2 == 0);

                        var status = (exePath != null && elevatedTaskPaths.Contains(exePath)) ? StartupStatus.Elevated : (isEnabled ? StartupStatus.Enabled : StartupStatus.Disabled);

                        apps.Add(valueName, new StartupAppDetails(valueName, commandLine, $"{locationPrefix}\\{path}", status));
                    }
                }
            };

            processFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), false);
            processFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true);
            string[] regPaths = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" };
            processRegistryKeys(Registry.CurrentUser, "HKCU", regPaths);
            processRegistryKeys(Registry.LocalMachine, "HKLM", regPaths);
            if (Environment.Is64BitOperatingSystem)
            {
                processRegistryKeys(Registry.LocalMachine, @"HKLM\WOW6432Node", new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" });
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
                        string cleanName = rawName.Replace("KitLUGIA_Elevated_", "").Replace("KitLUGIA_Delayed_", "");

                        string fullCommand = "";
                        if (task.Definition.Actions.FirstOrDefault() is ExecAction action)
                        {
                            fullCommand = $"\"{action.Path}\" {action.Arguments}".Trim();
                        }

                        bool isTaskEnabled = task.Enabled;
                        StartupStatus status;

                        if (!isTaskEnabled)
                            status = StartupStatus.Disabled;
                        else
                            status = rawName.Contains("Elevated") ? StartupStatus.Elevated : StartupStatus.Enabled;

                        if (apps.ContainsKey(cleanName))
                        {
                            var existing = apps[cleanName];
                            existing.Status = status;
                            existing.Location = "Agendador de Tarefas (KitLugia)";
                        }
                        else
                        {
                            apps.Add(cleanName, new StartupAppDetails(cleanName, fullCommand, "Agendador de Tarefas (KitLugia)", status));
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
                        var commandLine = key.GetValue(valueName)?.ToString() ?? "";
                        if (apps.ContainsKey(valueName))
                        {
                            var existing = apps[valueName];
                            existing.Status = StartupStatus.TurboBoot; // KitLugia runs elevated
                            existing.Location = "Turbo Boot (KitLugia)";
                        }
                        else
                        {
                            apps.Add(valueName, new StartupAppDetails(valueName, commandLine, "Turbo Boot (KitLugia)", StartupStatus.TurboBoot));
                        }
                    }
                }
            }
            catch { }

            return apps.Values.OrderBy(a => a.Name).ToList();
        }

        #endregion

        #region Gerenciamento de Estado (Habilitar/Desabilitar/Remover)

        public static (bool Success, string Message) SetStartupItemState(string appName, bool enable, bool silentMode = false)
        {
            var startupApp = GetStartupAppsWithDetails(true).FirstOrDefault(app => app.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (startupApp == null) return (false, "App não encontrado.");

            // CASO 1: Tarefa do Agendador (KitLugia)
            if (startupApp.Location.Contains("Agendador"))
            {
                try
                {
                    using (var ts = new TaskService())
                    {
                        // Busca flexível para encontrar qualquer variante do nome
                        var task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name.Contains(appName) && t.Name.StartsWith("KitLUGIA_"));
                        if (task != null)
                        {
                            task.Definition.Settings.Enabled = enable;
                            ts.RootFolder.RegisterTaskDefinition(task.Name, task.Definition, TaskCreation.Update, null, null, task.Definition.Principal.LogonType);

                            string actionMsg = enable ? "Habilitado" : "Desabilitado";
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

            // CASO 2: Registro ou Pasta de Inicialização
            try
            {
                string regPath;
                RegistryKey baseKey;
                string valueNameToChange = appName;

                if (startupApp.Location.StartsWith("HKCU"))
                {
                    baseKey = Registry.CurrentUser;
                    regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                }
                else if (startupApp.Location.StartsWith("HKLM"))
                {
                    baseKey = Registry.LocalMachine;
                    regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                }
                else if (startupApp.Location.Contains("Startup"))
                {
                    baseKey = Registry.CurrentUser;
                    regPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
                    valueNameToChange = appName + ".lnk";
                }
                else { return (false, "Localização não suportada."); }

                byte[] valueToSet = enable ? new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } : new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

                using (var key = baseKey.OpenSubKey(regPath, true) ?? baseKey.CreateSubKey(regPath))
                {
                    if (key.GetValue(valueNameToChange) != null)
                    {
                        key.SetValue(valueNameToChange, valueToSet, RegistryValueKind.Binary);
                    }
                    else
                    {
                        string fallbackName = GetFileNameFromCommandLine(startupApp.FullCommand);
                        key.SetValue(fallbackName, valueToSet, RegistryValueKind.Binary);
                    }
                }
                return (true, silentMode ? "" : $"'{appName}' {(enable ? "Habilitado" : "Desabilitado")}.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        public static (bool Success, string Message) RemoveStartupItem(string appName)
        {
            var startupApp = GetStartupAppsWithDetails(true).FirstOrDefault(app => app.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
            if (startupApp == null) return (false, "Aplicativo não encontrado na lista.");

            try
            {
                if (startupApp.Location.Contains("Agendador"))
                {
                    using (var ts = new TaskService())
                    {
                        var task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name.Contains(appName) && t.Name.StartsWith("KitLUGIA_"));
                        if (task != null)
                        {
                            ts.RootFolder.DeleteTask(task.Name);
                            return (true, $"Tarefa '{appName}' removida do agendador.");
                        }
                    }
                }
                else if (startupApp.Location.Contains("\\Startup") || startupApp.Location.Contains("\\Start Menu"))
                {
                    string lnkPath = Path.Combine(startupApp.Location, appName + ".lnk");
                    if (File.Exists(lnkPath))
                    {
                        File.Delete(lnkPath);
                        return (true, $"Atalho '{appName}' deletado permanentemente.");
                    }
                    var looseFile = Directory.GetFiles(startupApp.Location, $"{appName}.*").FirstOrDefault();
                    if (looseFile != null)
                    {
                        File.Delete(looseFile);
                        return (true, $"Arquivo '{Path.GetFileName(looseFile)}' deletado permanentemente.");
                    }
                }
                else if (startupApp.Location.StartsWith("HK"))
                {
                    RegistryKey baseKey = startupApp.Location.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKeyPath = startupApp.Location.Substring(startupApp.Location.IndexOf('\\') + 1);

                    using (var key = baseKey.OpenSubKey(subKeyPath, true))
                    {
                        if (key != null && key.GetValue(appName) != null)
                        {
                            key.DeleteValue(appName);
                            return (true, $"Entrada de registro '{appName}' removida.");
                        }
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
                    foreach (var task in ts.RootFolder.Tasks)
                    {
                        if (task.Name.StartsWith("KitLUGIA_", StringComparison.OrdinalIgnoreCase)) continue;
                        if (task.Name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                        if (task.Name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) continue;

                        bool hasLogonTrigger = task.Definition.Triggers.Any(t =>
                            t is LogonTrigger || t is BootTrigger);

                        if (!hasLogonTrigger) continue;
                        if (!task.Enabled) continue;

                        string fullCommand = "";
                        if (task.Definition.Actions.FirstOrDefault() is ExecAction action)
                        {
                            fullCommand = $"\"{action.Path}\" {action.Arguments}".Trim();
                        }

                        StartupManager.ExtractCommandParts(fullCommand, out string? exePath, out _);
                        if (string.IsNullOrEmpty(exePath)) continue;
                        if (exePath.Contains("system32", StringComparison.OrdinalIgnoreCase)) continue;

                        string name = task.Name;
                        bool isElevated = task.Definition.Principal.RunLevel == TaskRunLevel.Highest;
                        var status = isElevated ? StartupStatus.Elevated : StartupStatus.Enabled;
                        apps.Add(new StartupAppDetails(name, fullCommand, "Agendador de Tarefas", status));
                    }
                }
            }
            catch { }

            return apps.OrderBy(a => a.Name).ToList();
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

                try { using var rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); rk?.DeleteValue(appName, false); } catch { }
                try { using var rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true); rk?.DeleteValue(appName, false); } catch { }

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

        public static (bool Success, string Message) DelegateToKitLugia(string appName)
        {
            try
            {
                var apps = GetStartupAppsWithDetails(true);
                var app = apps.FirstOrDefault(a => a.Name.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (app == null) return (false, "App não encontrado.");

                // 1. Remove from standard startup softly
                RemoveStartupItem(appName);

                // 1.5. Remove from standard startup BRUTALLY (Ensures Task Manager reflects it)
                try { Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue(appName, false); } catch { }
                try { Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)?.DeleteValue(appName, false); } catch { }
                try { Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true)?.DeleteValue(appName, false); } catch { }
                try { Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true)?.DeleteValue(appName, false); } catch { }

                // 2. Add to KitLugia list
                using var key = Registry.CurrentUser.CreateSubKey(KitLugiaStartupKey);
                key.SetValue(appName, app.FullCommand);

                return (true, $"'{appName}' agora iniciará via Turbo Boot (KitLugia).");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static (bool Success, string Message) RemoveFromKitLugia(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey, true);
                if (key != null)
                {
                    key.DeleteValue(appName, false);
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

        public static void LaunchTurboApps()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KitLugiaStartupKey);
                if (key == null) return;

                foreach (var name in key.GetValueNames())
                {
                    string command = key.GetValue(name)?.ToString() ?? "";
                    if (string.IsNullOrEmpty(command)) continue;

                    // OTIMIZAÇÃO: Thread.Start garante concorrência absoluta e imediata.
                    // O Task.Run usa o ThreadPool que, em picos de estresse de CPU na inicialização,
                    // pode enfileirar tarefas (ex: Discord ficar na fila do Opera).
                    new System.Threading.Thread(() =>
                    {
                        try
                        {
                            ExtractCommandParts(command, out string? path, out string? args);
                            if (string.IsNullOrEmpty(path)) return;

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
                        catch { }
                    }){ IsBackground = true, Priority = System.Threading.ThreadPriority.AboveNormal }.Start();
                }
            }
            catch { }
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
                    string script = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{startupApp.Location}\\{appName}.lnk');$s.TargetPath='{newFullCommand}';$s.Save()";
                    SystemUtils.RunExternalProcess("powershell", $"-Command \"{script}\"", hidden: true);
                    return (true, $"Atalho '{appName}' atualizado com novos argumentos.");
                }
                else if (startupApp.Location.Contains("KitLugia") || startupApp.Location.Contains("Turbo Boot"))
                {
                    using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\StartupApps");
                    key.SetValue(appName, newFullCommand);
                    return (true, $"Argumentos atualizados para '{appName}' (Turbo Boot).");
                }

                return (false, "Tipo de inicialização não reconhecido.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao atualizar argumentos: {ex.Message}");
            }
        }

        #endregion

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
                return;
            }
        }

        int exeIndex = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            path = commandLine.Substring(0, exeIndex + 4).Trim();
            if (commandLine.Length > exeIndex + 4) args = commandLine.Substring(exeIndex + 4).Trim();
            return;
        }

        int firstSpace = commandLine.IndexOf(' ');
        if (firstSpace > 0 && !System.IO.File.Exists(commandLine))
        {
            path = commandLine.Substring(0, firstSpace);
            args = commandLine.Substring(firstSpace + 1).Trim();
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
            if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return $"\"{shortcutPath}\"";
            try
            {
                // Temporariamente desabilitado devido à dependência COM
                // var shell = new WshShell();
                // var shortcut = (IWshShortcut)shell.CreateShortcut(shortcutPath);
                // return $"\"{shortcut.TargetPath}\" {shortcut.Arguments}".Trim();
                return ""; // Retorna vazio temporariamente
            }
            catch { return ""; }
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
            catch { }
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
                catch { }
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
                    object loadFlag = key.GetValue("LoadAppInit_DLLs");

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
                catch { }
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
                            catch { }

                            items.Add(new StartupAppDetails($"BHO: {name}", clsid, bhoPath, StartupStatus.Enabled));
                        }
                        catch { }
                    }
                }
                catch { }
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

                object bootExec = key.GetValue("BootExecute");
                object setupExec = key.GetValue("SetupExecute");
                object exec = key.GetValue("Execute");
                object pnpExec = key.GetValue("PnPMajorDeviceInit");

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
            catch { }

            return items;
        }

        public static List<StartupAppDetails> GetAllAdvancedItems()
        {
            var all = new List<StartupAppDetails>();
            all.AddRange(GetWinlogonItems());
            all.AddRange(GetAppInitDlls());
            all.AddRange(GetBHOItems());
            all.AddRange(GetBootExecuteItems());
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
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null)
                    {
                        Logger.Log("❌ Não foi possível acessar o registro Run");
                        return;
                    }

                    var kitLugiaPath = key.GetValue("KitLugia") as string;
                    
                    if (string.IsNullOrEmpty(kitLugiaPath))
                    {
                        Logger.Log("❌ Nenhuma entrada no registro Run encontrada");
                        Logger.Log("🔧 Criando entrada no registro Run...");
                        key.SetValue("KitLugia", exePath + " --tray");
                        Logger.Log("✅ Entrada no registro Run criada com --tray");
                    }
                    else if (!File.Exists(kitLugiaPath))
                    {
                        Logger.Log($"⚠️ Entrada no registro Run aponta para arquivo inexistente: {kitLugiaPath}");
                        Logger.Log("🔧 Corrigindo entrada no registro...");
                        key.SetValue("KitLugia", exePath + " --tray");
                        Logger.Log("✅ Entrada no registro Run corrigida com --tray");
                    }
                    else if (kitLugiaPath != exePath)
                    {
                        Logger.Log($"⚠️ Entrada no registro Run aponta para versão antiga: {kitLugiaPath}");
                        Logger.Log("🔧 Atualizando entrada no registro...");
                        key.SetValue("KitLugia", exePath + " --tray");
                        Logger.Log("✅ Entrada no registro Run atualizada com --tray");
                    }
                    else
                    {
                        // Verificar se já tem --tray
                        if (!kitLugiaPath.Contains("--tray"))
                        {
                            Logger.Log("⚠️ Entrada no registro Run não tem --tray");
                            Logger.Log("🔧 Adicionando --tray para garantir Tray Icon...");
                            key.SetValue("KitLugia", kitLugiaPath + " --tray");
                            Logger.Log("✅ --tray adicionado à entrada do registro Run");
                        }
                        else
                        {
                            Logger.Log("✅ Entrada no registro Run está correta com --tray");
                        }
                    }
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
                        Logger.Log("❌ Nenhuma tarefa agendada encontrada");
                        Logger.Log("ℹ️ Criando tarefa agendada para inicialização com Windows...");
                        
                        // Criar tarefa agendada
                        var td = ts.NewTask();
                        td.RegistrationInfo.Description = "KitLugia Auto-Startup";
                        td.Settings.DisallowStartIfOnBatteries = false;
                        td.Settings.StopIfGoingOnBatteries = false;
                        td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                        td.Settings.StartWhenAvailable = true;
                        
                        var trigger = new LogonTrigger
                        {
                            Delay = TimeSpan.FromSeconds(5)
                        };
                        td.Triggers.Add(trigger);
                        td.Actions.Add(new ExecAction(exePath, "--tray", Path.GetDirectoryName(exePath) ?? ""));
                        
                        ts.RootFolder.RegisterTaskDefinition("KitLugia", td);
                        Logger.Log("✅ Tarefa agendada criada com sucesso");
                    }
                    else
                    {
                        var taskPath = task.Definition.Actions[0] as ExecAction;
                        if (taskPath?.Path != exePath)
                        {
                            Logger.Log($"⚠️ Tarefa agendada aponta para: {taskPath?.Path}");
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