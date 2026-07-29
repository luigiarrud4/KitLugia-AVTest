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

        // Serviços de terceiros conhecidos como não essenciais (seguros desativar na maioria dos cenários)
        private static readonly HashSet<string> _thirdPartySafeToDisable = new HashSet<string>(20, StringComparer.OrdinalIgnoreCase)
        {
            "PnkBstrA", "PnkBstrB", "AdobeUpdateService", "AdobeARMservice", "AGMService", "AGSService",
            "Creative Cloud", "CCLibrary", "CoreSync", "AdobeGCInvoker",
            "Steam Client Service", "OriginClientService", "GOGGalaxyService", "EpicOnlineServices",
            "BEService", "BEDaisy", "DiscordUpdater", "GoogleUpdate", "MozillaMaintenance",
            "Apple Mobile Device Service", "iPod Service", "iTunesHelper",
            "Everything", "Parsec", "ZeroTier", "ZeroTierOne",
            "Windhawk", "Sandboxie", "reWASD"
        };

        private static readonly HashSet<string> _knownMicrosoftServices = new HashSet<string>(60, StringComparer.OrdinalIgnoreCase)
        {
            // Lista base de serviços Microsoft para detecção quando PathName não está disponível
            "RpcSs", "DcomLaunch", "RpcEptMapper", "LSM", "gpsvc", "WinDefend", "Audiosrv", "Dhcp", "Dnscache",
            "EventLog", "lmhosts", "MpsSvc", "nsi", "Power", "ProfSvc", "SamSs", "Schedule", "SENS", "ShellHWDetection",
            "SystemEventsBroker", "Themes", "UserManager", "Winmgmt", "WpnService", "BFE", "CryptSvc", "PlugPlay",
            "DiagTrack", "dmwappushservice", "SysMain", "WSearch", "MapsBroker", "lfsvc", "Fax", "RetailDemo",
            "XblGameSave", "XboxNetApiSvc", "XboxGipSvc", "XblAuthManager", "WerSvc", "PcaSvc", "DPS", "WdiServiceHost",
            "PrintWorkflow", "Spooler", "W32Time", "RemoteRegistry", "WalletService", "NcdAutoSetup", "SharedAccess",
            "TouchKeyboard", "TabletInputService", "TrustedInstaller", "wuauserv", "UsoSvc", "DoSvc", "LicenseManager",
            "NgcSvc", "NgcCtnrSvc", "Browser", "SamSs", "seclogon", "WbioSrvc", "wisvc", "WlanSvc",
            "wlidsvc", "WpnService", "WpnpService", "DusmSvc", "AeLookupSvc", "ALG", "AppIDSvc",
            "Appinfo", "AppMgmt", "aspnet_state", "AxInstSV", "BITS", "BTAGService", "BthAvctpSvc",
            "BthHFSrv", "BthPan", "BthPort", "BthService", "BthVcp", "camsvc", "CDPSvc", "CertPropSvc",
            "ClipSVC", "CloudBackupRestoreSvc", "ConsentUxUserSvc", "CredentialEnrollmentManagerUserSvc",
            "CryptSvc", "DcomLaunch", "DeviceAssociationService", "DeviceInstall", "DevQueryBroker",
            "Dhcp", "DmEnrollmentSvc", "Dnscache", "DoSvc", "dot3svc", "DPS", "DsmSvc", "DsRoleSvc",
            "EdgeUpdate", "EQSvc", "EventLog", "EventSystem", "FDResPub", "FDDev", "FontCache",
            "FontCache3.0.0.0", "ftpvc", "gpsvc", "hidserv", "hkmsvc", "HomeGroupListener",
            "HomeGroupProvider", "HvHost", "icssvc", "IKEEXT", "iphlpsvc", "KeyIso", "KtmRm",
            "LanmanServer", "LanmanWorkstation", "LicenseManager", "lltdsvc", "lmhosts", "LSM",
            "LxpSvc", "MapsBroker", "McpManagementService", "MDMFusion", "MDMSS", "MessagingService",
            "MicrosoftEdgeElevationService", "MixedRealityOpenXRSvc", "MpsSvc", "MSiSCSI", "mxsvc",
            "NaturalAuthentication", "NcaSvc", "NcbService", "NcdAutoSetup", "Net Driver HPZ12",
            "Netlogon", "Netman", "netprofm", "NetSetupSvc", "NLgpSvc", "nsi", "p2pimsvc",
            "p2psvc", "PcaSvc", "PeerDistSvc", "PerfHost", "pla", "PlugPlay", "PNRPsvc",
            "PNRPAutoReg", "PolicyAgent", "Power", "ProfSvc", "PushToInstall", "RasAuto",
            "RasMan", "RemoteAccess", "RemoteRegistry", "RetailDemo", "RpcEptMapper", "RpcSs",
            "RSoPProv", "safebox", "SamSs", "SCardSvr", "ScDeviceEnum", "Schedule", "SCPolicySvc",
            "seclogon", "SENS", "Sense", "SessionEnv", "SgrmBroker", "SharedAccess", "SharedRealitySvc",
            "ShellHWDetection", "shpamsvc", "smphost", "SmsRouter", "SNMPTRAP", "spectrum",
            "Spooler", "sppsvc", "SSDPSRV", "ssh-agent", "StateRepository", "stisvc", "StorSvc",
            "svsvc", "swprv", "SynthVid", "SysMain", "SystemEventsBroker", "TabletInputService",
            "TapiSrv", "TermService", "Themes", "TieringEngineService", "TimeBroker", "TokenBroker",
            "TouchKeyboard", "TrkWks", "TrustedInstaller", "UI0Detect", "UmRdpService", "upnphost",
            "UserManager", "UsoSvc", "VaultSvc", "vdrvroot", "VerifierSvc", "VirtualRenderDeviceManager",
            "Vmms", "vmicguestinterface", "vmicheartbeat", "vmickvpexchange", "vmicrdv", "vmicshutdown",
            "vmictimesync", "vmicvmsession", "vmicvss", "VMTools", "VolumeShadowCopy", "VSS",
            "W32Time", "WalletService", "WAS", "wcncsvc", "WdiServiceHost", "WdiSystemHost",
            "WdnService", "WebClient", "Wecsvc", "WEPHOSTSVC", "wercplsupport", "WerSvc",
            "WFDSConSvc", "WiaRpc", "WinDefend", "WinHttpAutoProxySvc", "Winmgmt", "WinRM",
            "Winstall", "wlidsvc", "wlpasvc", "Wmi", "WMPNetworkSvc", "WMSVC", "workfolderssvc",
            "WpnService", "wscsvc", "WSearch", "wuauserv", "WwanSvc", "XblAuthManager",
            "XblGameSave", "XboxGipSvc", "XboxNetApiSvc"
        };

        private static string DetectManufacturer(string serviceName, string? pathName)
        {
            if (!string.IsNullOrEmpty(pathName))
            {
                string lowerPath = pathName.ToLowerInvariant();
                if (lowerPath.Contains(@"c:\windows\system32") || lowerPath.Contains(@"c:\windows\") ||
                    lowerPath.Contains(@"%systemroot%\system32") || lowerPath.Contains(@"%systemroot%"))
                    return "Microsoft";
                if (lowerPath.Contains(@"c:\program files") || lowerPath.Contains(@"c:\program files (x86)"))
                {
                    // Third-party or vendor-specific — check known Microsoft services
                    if (_knownMicrosoftServices.Contains(serviceName))
                        return "Microsoft";
                    // Check known third-party vendor paths
                    if (lowerPath.Contains(@"razer") || lowerPath.Contains(@"nvidia") || lowerPath.Contains(@"vmware") ||
                        lowerPath.Contains(@"adobe") || lowerPath.Contains(@"cloudflare") || lowerPath.Contains(@"wallpaper engine") ||
                        lowerPath.Contains(@"parsec") || lowerPath.Contains(@"everything") || lowerPath.Contains(@"zerotier") ||
                        lowerPath.Contains(@"rewasd") || lowerPath.Contains(@"windhawk") || lowerPath.Contains(@"sandboxie") ||
                        lowerPath.Contains(@"punkbuster") || lowerPath.Contains(@"steam") || lowerPath.Contains(@"discord") ||
                        lowerPath.Contains(@"google") || lowerPath.Contains(@"mozilla") || lowerPath.Contains(@"apple") ||
                        lowerPath.Contains(@"epic") || lowerPath.Contains(@"gog") || lowerPath.Contains(@"origin"))
                        return "Terceiros";
                    return "Terceiros"; // Default for Program Files
                }
            }

            // Fallback: check against known Microsoft service names
            if (_knownMicrosoftServices.Contains(serviceName))
                return "Microsoft";

            return "Desconhecido";
        }

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
                var query = "SELECT Name, DisplayName, Description, State, StartMode, PathName FROM Win32_Service";
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
                        string pathName = item["PathName"]?.ToString() ?? "";

                        ServiceSafetyLevel safety = ServiceSafetyLevel.Unknown;

                        if (_criticalServices.Contains(name)) safety = ServiceSafetyLevel.Dangerous;
                        else if (_safeToDisable.Contains(name)) safety = ServiceSafetyLevel.Safe;
                        else if (_thirdPartySafeToDisable.Contains(name)) safety = ServiceSafetyLevel.Safe;
                        else safety = ServiceSafetyLevel.Caution;

                        string manufacturer = DetectManufacturer(name, pathName);

                        string uiStatus = state == "Running" ? "Executando" : "Parado";
                        string uiStart = startMode == "Auto" ? "Automático" : (startMode == "Manual" ? "Manual" : "Desativado");

                        services.Add(new ServiceInfo(name, display, desc, uiStatus, uiStart, safety) { Manufacturer = manufacturer });
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
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

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
            else if (presetName == "GamerPlus")
            {
                targets.AddRange(_safeToDisable);
                targets.AddRange(_thirdPartySafeToDisable);
            }
            else if (presetName == "Restore") { mode = "auto"; targets.AddRange(_safeToDisable); }

            int totalTargets = targets.Count;
            int successCount = 0;
            foreach (var svc in targets)
            {
                string currentMode = mode;
                if (presetName == "Restore" && (svc == "XblGameSave" || svc == "Fax")) currentMode = "demand";
                if (ToggleServiceState(svc, currentMode).Success) successCount++;
            }
            return (true, $"{successCount}/{totalTargets} serviços processados.");
        }

        // =========================================================
        // 2. GERENCIAMENTO DE TAREFAS AGENDADAS (RESTAURADO DO CONSOLE)
        // =========================================================

        // Lista de tarefas monitoradas do Microsoft (telemetria, manutenção não essencial)
        private static readonly Dictionary<string, (string Description, string Category)> _trackedTasks = new()
        {
            // Telemetria e Diagnóstico
            { @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator", ("Coleta de Telemetria de Uso", "Microsoft") },
            { @"\Microsoft\Windows\Customer Experience Improvement Program\KernelCeipTask", ("Telemetria do Kernel", "Microsoft") },
            { @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip", ("Telemetria USB", "Microsoft") },
            { @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser", ("Análise de Compatibilidade (Telemetria)", "Microsoft") },
            { @"\Microsoft\Windows\Application Experience\ProgramDataUpdater", ("Atualizador de Dados de Apps", "Microsoft") },
            { @"\Microsoft\Windows\Autochk\Proxy", ("Proxy de Verificação de Disco (Telemetria)", "Microsoft") },
            { @"\Microsoft\Windows\Feedback\Siuf\DmClient", ("Feedback do Usuário (Siuf)", "Microsoft") },
            { @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector", ("Coleta de Diagnóstico de Disco", "Microsoft") },
            // Mapas, Xbox, Localização
            { @"\Microsoft\Windows\Maps\MapsUpdateTask", ("Atualização Automática de Mapas", "Microsoft") },
            { @"\Microsoft\Windows\Maps\MapsToastTask", ("Notificações de Mapas", "Microsoft") },
            { @"\Microsoft\XblGameSave\XblGameSaveTask", ("Sincronização Xbox Save (Background)", "Microsoft") },
            // Manutenção não essencial
            { @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticResolver", ("Resolução de Diagnóstico de Disco", "Microsoft") },
            { @"\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem", ("Análise de Eficiência Energética", "Microsoft") },
            { @"\Microsoft\Windows\Windows Error Reporting\QueueReporting", ("Relatório de Erros do Windows", "Microsoft") },
            { @"\Microsoft\Windows\CloudExperienceHost\CreateObjectTask", ("Experiência na Nuvem", "Microsoft") },
            { @"\Microsoft\Windows\Media Center\ActivateWindowsSearch", ("Ativação de busca do Media Center", "Microsoft") },
            { @"\Microsoft\Windows\Media Center\ConfigureInternetTimeService", ("Configuração de Internet do Media Center", "Microsoft") },
            { @"\Microsoft\Windows\Media Center\MediaCenterRecoveryTask", ("Recuperação do Media Center", "Microsoft") },
            { @"\Microsoft\Office\OfficeTelemetryAgentFallBack", ("Telemetria do Office (Fallback)", "Microsoft") },
            { @"\Microsoft\Office\OfficeTelemetryAgentLogOn", ("Telemetria do Office (LogOn)", "Microsoft") },
            { @"\Microsoft\Office\Office 15 Subscription Heartbeat", ("Heartbeat do Office 365", "Microsoft") },
            { @"\Microsoft\Windows\Application Experience\StartupAppTask", ("Rastreio de Apps de Inicialização", "Microsoft") },
            { @"\Microsoft\Windows\Location\Notifications", ("Notificações de Localização", "Microsoft") },
            { @"\Microsoft\Windows\Location\WindowsActionDialog", ("Diálogo de Ação de Localização", "Microsoft") },
            { @"\Microsoft\Windows\Speech\SpeechModelDownloadTask", ("Download de Modelos de Fala", "Microsoft") },
            { @"\Microsoft\Windows\PI\Sqm-Tasks", ("Coleta SQM (Telemetria)", "Microsoft") },
            { @"\Microsoft\OneDrive\OneDrive Standalone Update Task", ("OneDrive Standalone Update", "Microsoft") },
        };

        /// <summary>
        /// Verifica o status de todas as tarefas monitoradas (opcionalmente filtra por categoria).
        /// </summary>
        public static List<ScheduledTaskInfo> GetScheduledTasksStatus(string? categoryFilter = null)
        {
            var result = new List<ScheduledTaskInfo>();
            try
            {
                using (var ts = new TaskService())
                {
                    foreach (var kvp in _trackedTasks)
                    {
                        var taskPath = kvp.Key;
                        var (desc, category) = kvp.Value;

                        if (categoryFilter != null && !category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var taskName = System.IO.Path.GetFileName(taskPath);

                        // Suporte a wildcard '*' no path (ex: GoogleUpdateTaskUserS-1-5-21-*)
                        Microsoft.Win32.TaskScheduler.Task? task = null;
                        if (taskPath.EndsWith("*"))
                        {
                            string prefix = taskPath.TrimEnd('*');
                            task = ts.AllTasks.FirstOrDefault(t =>
                                t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            task = ts.GetTask(taskPath);
                        }

                        if (task != null)
                        {
                            result.Add(new ScheduledTaskInfo(taskPath, taskName, desc, task.Enabled, category));
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
        /// Aplica um preset nas tarefas agendadas (disable para uma categoria, ou enable para restore).
        /// </summary>
        public static (bool Success, string Message) ApplyTaskPreset(string presetName)
        {
            int count = 0;
            int total = 0;
            try
            {
                using (var ts = new TaskService())
                {
                    var targets = _trackedTasks.Where(kvp =>
                    {
                        if (presetName == "DisableMicrosoft") return true;
                        if (presetName == "DisableAll") return true;
                        if (presetName == "RestoreAll") return true;
                        return false;
                    });

                    foreach (var kvp in targets)
                    {
                        var taskPath = kvp.Key;
                        total++;

                        Microsoft.Win32.TaskScheduler.Task? task = null;
                        if (taskPath.EndsWith("*"))
                        {
                            string prefix = taskPath.TrimEnd('*');
                            task = ts.AllTasks.FirstOrDefault(t =>
                                t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        }
                        else
                        {
                            task = ts.GetTask(taskPath);
                        }

                        if (task != null)
                        {
                            bool newState = presetName == "RestoreAll";
                            if (task.Enabled != newState)
                            {
                                task.Enabled = newState;
                                count++;
                            }
                        }
                    }
                }

                string action = presetName == "RestoreAll" ? "restauradas" : "desativadas";
                return (true, $"{count}/{total} tarefas {action}.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro parcial: {ex.Message}");
            }
        }

        /// <summary>
        /// Desativa todas as tarefas monitoradas (Compatibilidade mantida).
        /// </summary>
        public static (bool Success, string Message) DisableTelemetryTasks() => ApplyTaskPreset("DisableAll");
    }
}