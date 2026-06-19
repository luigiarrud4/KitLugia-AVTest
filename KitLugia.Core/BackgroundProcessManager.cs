using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler; // Requer NuGet: TaskScheduler
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class BackgroundProcessManager
    {
        // =========================================================
        // 1. GERENCIAMENTO DE SERVIÇOS (MANTIDO DA GOLD)
        // =========================================================


        // Típico: 20-30 serviços seguros para desativar
        private static readonly HashSet<string> _safeToDisable = new HashSet<string>(30, StringComparer.OrdinalIgnoreCase)
        {
            "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "MapsBroker", "lfsvc", "Fax", "RetailDemo",
            "XblGameSave", "XboxNetApiSvc", "XboxGipSvc", "XblAuthManager", "WerSvc", "PcaSvc", "DPS", "WdiServiceHost",
            "PrintWorkflow", "Spooler", "W32Time", "RemoteRegistry", "WalletService", "NcdAutoSetup", "SharedAccess",
            "TouchKeyboard", "TabletInputService"
        };


        // Típico: 25-35 serviços críticos
        private static readonly HashSet<string> _criticalServices = new HashSet<string>(35, StringComparer.OrdinalIgnoreCase)
        {
            "RpcSs", "DcomLaunch", "RpcEptMapper", "LSM", "gpsvc", "WinDefend", "Audiosrv", "Dhcp", "Dnscache",
            "EventLog", "lmhosts", "MpsSvc", "nsi", "Power", "ProfSvc", "SamSs", "Schedule", "SENS", "ShellHWDetection",
            "SystemEventsBroker", "Themes", "UserManager", "Winmgmt", "WpnService", "BFE", "CryptSvc", "PlugPlay"
        };

        public static List<ServiceInfo> GetAllServices()
        {

            // Típico: 150-300 serviços no Windows
            var services = new List<ServiceInfo>(300);
            try
            {
                var query = "SELECT Name, DisplayName, Description, State, StartMode FROM Win32_Service";
                using var searcher = new ManagementObjectSearcher(query);
                using var results = searcher.Get();

                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        string name = item["Name"]?.ToString() ?? "";
                        string display = item["DisplayName"]?.ToString() ?? "";
                        string desc = item["Description"]?.ToString() ?? "Sem descrição disponível.";
                        string state = item["State"]?.ToString() ?? "Unknown";
                        string startMode = item["StartMode"]?.ToString() ?? "Manual";

                        ServiceSafetyLevel safety = ServiceSafetyLevel.Unknown;

                        if (_criticalServices.Contains(name)) safety = ServiceSafetyLevel.Dangerous;
                        else if (_safeToDisable.Contains(name)) safety = ServiceSafetyLevel.Safe;
                        else safety = ServiceSafetyLevel.Caution;

                        string uiStatus = state == "Running" ? "Executando" : "Parado";
                        string uiStart = startMode == "Auto" ? "Automático" : (startMode == "Manual" ? "Manual" : "Desativado");

                        services.Add(new ServiceInfo(name, display, desc, uiStatus, uiStart, safety));
                    }
                }
            }
            catch (Exception ex) { Logger.LogError("GetAllServices", ex.Message); }

            return services.OrderBy(s => s.Safety).ThenBy(s => s.DisplayName).ToList();
        }

        public static (bool Success, string Message) ToggleServiceState(string serviceName, string newMode)
        {
            try
            {
                string cmd = $"config \"{serviceName}\" start= {newMode}";
                string result = SystemUtils.RunExternalProcess("sc.exe", cmd, true);

                if (result.Contains("sucesso", StringComparison.OrdinalIgnoreCase) || result.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    if (newMode == "disabled") SystemUtils.RunExternalProcess("sc.exe", $"stop \"{serviceName}\"", true);
                    if (newMode == "auto") SystemUtils.RunExternalProcess("sc.exe", $"start \"{serviceName}\"", true);

                    Logger.Log($"[SERVIÇO] '{serviceName}' definido como {newMode.ToUpper()}.");
                    return (true, $"Serviço configurado com sucesso.");
                }
                else
                {
                    // Fallback para Registro (Ignora bloqueios de permissão severos do sc.exe)
                    try
                    {
                        int startValue = newMode switch { "disabled" => 4, "auto" => 2, "demand" => 3, _ => 2 };
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
                        if (key != null)
                        {
                            key.SetValue("Start", startValue, Microsoft.Win32.RegistryValueKind.DWord);
                            if (newMode == "disabled") SystemUtils.RunExternalProcess("sc.exe", $"stop \"{serviceName}\"", true);
                            if (newMode == "auto") SystemUtils.RunExternalProcess("sc.exe", $"start \"{serviceName}\"", true);
                            
                            Logger.Log($"[SERVIÇO] '{serviceName}' definido como {newMode.ToUpper()} via Registro (Bypass forcado).");
                            return (true, "Forçado via Registro com sucesso.");
                        }
                    }
                    catch { }

                    return (false, $"Erro ao configurar: {result}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static (bool Success, string Message) ResetServiceToDefault(string serviceName)
        {
            string mode = "demand";
            if (_criticalServices.Contains(serviceName) || _safeToDisable.Contains(serviceName))
            {
                mode = "auto";
                if (serviceName == "XblGameSave" || serviceName == "Fax" || serviceName == "WerSvc") mode = "demand";
            }
            return ToggleServiceState(serviceName, mode);
        }

        public static (bool Success, string Message) ApplyServicePreset(string presetName)
        {
            Logger.Log($"Aplicando preset de serviços: {presetName}...");
            List<string> targets = new();
            string mode = "disabled";

            if (presetName == "Safe") targets.AddRange(new[] { "Fax", "RetailDemo", "Spooler", "PrintWorkflow" });
            else if (presetName == "Gamer") targets.AddRange(_safeToDisable);
            else if (presetName == "Restore") { mode = "auto"; targets.AddRange(_safeToDisable); }

            int successCount = 0;
            foreach (var svc in targets)
            {
                string currentMode = mode;
                if (presetName == "Restore" && (svc == "XblGameSave" || svc == "Fax")) currentMode = "demand";
                if (ToggleServiceState(svc, currentMode).Success) successCount++;
            }
            return (true, $"{successCount} serviços processados.");
        }

        // =========================================================
        // 2. GERENCIAMENTO DE TAREFAS AGENDADAS (RESTAURADO DO CONSOLE)
        // =========================================================

        // Lista de tarefas "bloatware" conhecidas do Windows
        private static readonly Dictionary<string, string> _trackedTasks = new()
        {
            { @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", "Coleta de Telemetria de Uso" },
            { @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask", "Telemetria do Kernel" },
            { @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", "Telemetria USB" },
            { @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", "Análise de Compatibilidade (Telemetria)" },
            { @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", "Atualizador de Dados de Apps" },
            { @"\Microsoft\Windows\Autochk\Proxy", "Proxy de Verificação de Disco (Telemetria)" },
            { @"\Microsoft\Windows\Feedback\Siuf\DmClient", "Feedback do Usuário (Siuf)" },
            { @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", "Coleta de Diagnóstico de Disco" },
            { @"\Microsoft\Windows\Maps\MapsUpdateTask", "Atualização Automática de Mapas" },
            { @"\Microsoft\Windows\Maps\MapsToastTask", "Notificações de Mapas" },
            { @"\Microsoft\XblGameSave\XblGameSaveTask", "Sincronização Xbox Save (Background)" }
        };

        /// <summary>
        /// Verifica o status de todas as tarefas monitoradas.
        /// </summary>
        public static List<ScheduledTaskInfo> GetScheduledTasksStatus()
        {
            var result = new List<ScheduledTaskInfo>();
            try
            {
                using (var ts = new TaskService())
                {
                    foreach (var kvp in _trackedTasks)
                    {
                        var taskPath = kvp.Key;
                        var desc = kvp.Value;
                        var taskName = System.IO.Path.GetFileName(taskPath);

                        var task = ts.GetTask(taskPath);
                        if (task != null)
                        {
                            result.Add(new ScheduledTaskInfo(taskPath, taskName, desc, task.Enabled));
                        }
                        else
                        {
                            // Se não achar, assumimos que já foi deletada ou não existe nesta versão do Windows
                            // Não adicionamos à lista para não poluir a UI com "Não Encontrado"
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("GetTasks", ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Habilita ou desabilita uma tarefa específica.
        /// </summary>
        public static (bool Success, string Message) ToggleTaskState(string taskPath, bool enable)
        {
            try
            {
                using (var ts = new TaskService())
                {
                    var task = ts.GetTask(taskPath);
                    if (task != null)
                    {
                        task.Enabled = enable;
                        string state = enable ? "ATIVADA" : "DESATIVADA";
                        Logger.Log($"[TAREFA] {state}: {task.Name}");
                        return (true, $"Tarefa {state} com sucesso.");
                    }
                    return (false, "Tarefa não encontrada no sistema.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao alterar tarefa: {ex.Message}");
            }
        }

        /// <summary>
        /// Desativa todas as tarefas de telemetria conhecidas de uma vez.
        /// </summary>
        public static (bool Success, string Message) DisableTelemetryTasks()
        {
            int count = 0;
            try
            {
                using (var ts = new TaskService())
                {
                    foreach (var path in _trackedTasks.Keys)
                    {
                        var task = ts.GetTask(path);
                        if (task != null && task.Enabled)
                        {
                            task.Enabled = false;
                            count++;
                        }
                    }
                }
                return (true, $"{count} tarefas de telemetria foram desativadas.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro parcial: {ex.Message}");
            }
        }
    }
}