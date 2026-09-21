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

                        // Detecta delayed-auto via registro (WMI StartMode não distingue)
                        bool isDelayed = false;
                        try
                        {
                            using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                            isDelayed = regKey?.GetValue("DelayedAutostart") is int d && d == 1;
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        string uiStart = startMode switch
                        {
                            "Auto" => isDelayed ? "Automático (Atrasado)" : "Automático",
                            "Manual" => "Manual",
                            "Disabled" => "Desativado",
                            "Boot" => "Boot",
                            "System" => "System",
                            _ => startMode
                        };

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
                // -------- MÉTODO 1: sc.exe config (padrão da Microsoft) --------
                string cmd = $"config \"{serviceName}\" start= {newMode}";
                string result = SystemUtils.RunExternalProcess("sc.exe", cmd, true);

                if (result.Contains("sucesso", StringComparison.OrdinalIgnoreCase) || result.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
                {
                    if (newMode == "disabled") SystemUtils.RunExternalProcess("sc.exe", $"stop \"{serviceName}\"", true);
                    if (newMode == "auto" || newMode == "delayed-auto") SystemUtils.RunExternalProcess("sc.exe", $"start \"{serviceName}\"", true);

                    Logger.Log($"[SERVIÇO] '{serviceName}' definido como {newMode.ToUpper()}.");
                    return (true, $"Serviço configurado com sucesso.");
                }

                // -------- MÉTODO 2: Registro direto (bypassa bloqueio de permissão do sc.exe) --------
                try
                {
                    int startValue = newMode switch { "disabled" => 4, "auto" => 2, "delayed-auto" => 2, "demand" => 3, _ => 2 };
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
                    if (key != null)
                    {
                        key.SetValue("Start", startValue, Microsoft.Win32.RegistryValueKind.DWord);
                        if (newMode == "delayed-auto")
                            key.SetValue("DelayedAutostart", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        else
                        {
                            try { key.DeleteValue("DelayedAutostart", throwOnMissingValue: false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                        if (newMode == "disabled") SystemUtils.RunExternalProcess("sc.exe", $"stop \"{serviceName}\"", true);
                        if (newMode == "auto" || newMode == "delayed-auto") SystemUtils.RunExternalProcess("sc.exe", $"start \"{serviceName}\"", true);

                        Logger.Log($"[SERVIÇO] '{serviceName}' definido como {newMode.ToUpper()} via Registro (Bypass forcado).");
                        return (true, "Forçado via Registro com sucesso.");
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // -------- MÉTODO 3: PowerShell Set-Service/CIM (último recurso; funciona
                // quando o SCM trava o serviço protegido mas o CIM aceita) --------
                try
                {
                    string psMode = newMode switch { "disabled" => "Disabled", "auto" => "Automatic", "delayed-auto" => "Automatic", _ => "Manual" };
                    string psScript = newMode == "delayed-auto"
                        ? $"try {{ Set-Service -Name '{serviceName}' -StartupType {psMode} -ErrorAction Stop; $s=Get-CimInstance Win32_Service -Filter \"Name='{serviceName}'\"; if ($s) {{ Set-CimInstance -InputObject $s -Property @{{DelayedAutoStart=$true}} -ErrorAction Stop }}; exit 0 }} catch {{ exit 1 }}"
                        : $"try {{ Set-Service -Name '{serviceName}' -StartupType {psMode} -ErrorAction Stop; exit 0 }} catch {{ exit 1 }}";

                    var (exitCode, _) = SystemUtils.RunExternalProcessWithCode("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"", hidden: true);
                    if (exitCode == 0)
                    {
                        if (newMode == "disabled") SystemUtils.RunExternalProcess("sc.exe", $"stop \"{serviceName}\"", true);
                        if (newMode == "auto" || newMode == "delayed-auto") SystemUtils.RunExternalProcess("sc.exe", $"start \"{serviceName}\"", true);

                        Logger.Log($"[SERVIÇO] '{serviceName}' definido como {newMode.ToUpper()} via PowerShell (3º método).");
                        return (true, "Configurado via PowerShell (CIM) com sucesso.");
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                return (false, $"Erro ao configurar '{serviceName}': todos os 3 métodos falharam (sc.exe: {result.Trim()}).");
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

        /// <summary>
        /// Inicia (liga) um serviço com cascata de métodos:
        /// ServiceController -> sc.exe start -> net start -> PowerShell Start-Service.
        /// Serviços desativados são reconfigurados para Manual antes do start.
        /// </summary>
        public static (bool Success, string Message) StartServiceNow(string serviceName)
        {
            // Método 0: se o serviço está DESATIVADO, reconfigura para Manual primeiro
            // (serviço desativado nunca inicia — erro 1058).
            try
            {
                string mode = ServiceHelper.GetServiceStartMode(serviceName) ?? "";
                if (mode == "Disabled")
                {
                    ToggleServiceState(serviceName, "demand");
                    Logger.Log($"[SERVIÇO] '{serviceName}' estava DESATIVADO — reconfigurado para Manual antes do start.");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Método 1: ServiceController (API nativa .NET)
            var r1 = ServiceHelper.TryStartServiceWithMessage(serviceName);
            if (r1.Success) return r1;

            // Método 2: sc.exe start (resolve quando o SCM trava a API .NET)
            var (code2, out2) = SystemUtils.RunExternalProcessWithCode("sc.exe", $"start \"{serviceName}\"", true);
            string outTrim = out2.Trim();
            if (code2 == 0 || outTrim.Contains("FAILED", StringComparison.OrdinalIgnoreCase) == false &&
                (outTrim.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) ||
                 outTrim.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Log($"[SERVIÇO] Start '{serviceName}' OK via sc.exe (exit={code2}).");
                return (true, $"'{serviceName}' iniciado via sc.exe.");
            }

            // Método 3: net start (caminho legado que às vezes passa onde sc trava)
            var (code3, out3) = SystemUtils.RunExternalProcessWithCode("net.exe", $"start \"{serviceName}\"", true);
            if (code3 == 0 || out3.Contains("já foi iniciado", StringComparison.OrdinalIgnoreCase) || out3.Contains("already been started", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"[SERVIÇO] Start '{serviceName}' OK via net start (exit={code3}).");
                return (true, $"'{serviceName}' iniciado via net start.");
            }

            // Método 4: PowerShell Start-Service (último recurso)
            var (code4, _) = SystemUtils.RunExternalProcessWithCode("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ Start-Service -Name '{serviceName}' -ErrorAction Stop; exit 0 }} catch {{ exit 1 }}\"", true);
            if (code4 == 0)
            {
                Logger.Log($"[SERVIÇO] Start '{serviceName}' OK via PowerShell (4º método).");
                return (true, $"'{serviceName}' iniciado via PowerShell.");
            }

            Logger.LogError("StartServiceNow", $"'{serviceName}': todos os métodos falharam. Último erro: {outTrim}");
            return (false, $"Não foi possível iniciar '{serviceName}' (4 métodos tentados).\nÚltima resposta: {outTrim}");
        }

        /// <summary>
        /// Para (desliga) um serviço com cascata de métodos:
        /// ServiceController -> taskkill nos processos hospedeiros -> sc.exe stop -> net stop -> PowerShell Stop-Service.
        /// Força encerrando os processos svchost que hospedam o serviço (kill suave do PID).
        /// </summary>
        public static (bool Success, string Message) StopServiceNow(string serviceName)
        {
            // Método 1: ServiceController (graceful — permite cleanup do serviço)
            var r1 = ServiceHelper.TryStopServiceWithMessage(serviceName);
            if (r1.Success) return r1;

            // Método 2: sc.exe stop
            var (code2, out2) = SystemUtils.RunExternalProcessWithCode("sc.exe", $"stop \"{serviceName}\"", true);
            string outTrim = out2.Trim();
            if (code2 == 0 || outTrim.Contains("1051", StringComparison.OrdinalIgnoreCase)) // 1051 = já parado
            {
                Logger.Log($"[SERVIÇO] Stop '{serviceName}' OK via sc.exe (exit={code2}).");
                return (true, $"'{serviceName}' parado via sc.exe.");
            }

            // Método 3: matar os processos que hospedam o serviço (svchost -k ...
            // e processos com SDDL do serviço). Força quando o serviço ignora o stop.
            try
            {
                int killed = KillServiceHostProcesses(serviceName);
                if (killed > 0)
                {
                    Logger.Log($"[SERVIÇO] Stop '{serviceName}': {killed} processo(s) hospedeiro(s) finalizado(s) (taskkill).");
                    return (true, $"'{serviceName}' parado forçadamente ({killed} processo(s) finalizado(s)).");
                }
            }
            catch (Exception ex) { Logger.LogWarning("StopServiceNow.KillHost", ex.Message); }

            // Método 4: net stop
            var (code4, out4) = SystemUtils.RunExternalProcessWithCode("net.exe", $"stop \"{serviceName}\"", true);
            if (code4 == 0 || out4.Contains("não foi iniciado", StringComparison.OrdinalIgnoreCase) || out4.Contains("not started", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"[SERVIÇO] Stop '{serviceName}' OK via net stop (exit={code4}).");
                return (true, $"'{serviceName}' parado via net stop.");
            }

            // Método 5: PowerShell Stop-Service -Force
            var (code5, _) = SystemUtils.RunExternalProcessWithCode("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ Stop-Service -Name '{serviceName}' -Force -ErrorAction Stop; exit 0 }} catch {{ exit 1 }}\"", true);
            if (code5 == 0)
            {
                Logger.Log($"[SERVIÇO] Stop '{serviceName}' OK via PowerShell -Force (5º método).");
                return (true, $"'{serviceName}' parado via PowerShell.");
            }

            Logger.LogError("StopServiceNow", $"'{serviceName}': todos os métodos falharam. Último erro: {outTrim}");
            return (false, $"Não foi possível parar '{serviceName}' (5 métodos tentados).\nÚltima resposta: {outTrim}");
        }

        /// <summary>
        /// Finaliza os processos que hospedam um serviço (svchost -k grupo, ou processo
        /// próprio via taskkill /PID). Retorna quantos processos foram finalizados.
        /// </summary>
        private static int KillServiceHostProcesses(string serviceName)
        {
            int killed = 0;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, Name, ProcessId FROM Win32_Service WHERE Name='{serviceName.Replace("'", "''")}'");
                foreach (var obj in searcher.Get().Cast<ManagementObject>().ToList())
                {
                    try
                    {
                        int pid = Convert.ToInt32(obj["ProcessId"]);
                        string procName = obj["Name"]?.ToString() ?? "";
                        if (pid > 0)
                        {
                            // taskkill sem /F primeiro (graceful); se falhar, força
                            var (code, _) = SystemUtils.RunExternalProcessWithCode("taskkill", $"/PID {pid}", true);
                            if (code != 0)
                                SystemUtils.RunExternalProcessWithCode("taskkill", $"/F /PID {pid}", true);
                            killed++;
                            Logger.Log($"[SERVIÇO] taskkill PID {pid} ({procName}) do serviço '{serviceName}'.");
                        }
                    }
                    catch { continue; }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return killed;
        }

        /// <summary>Indica se o serviço está na lista de críticos do sistema (stop = risco de instabilidade).</summary>
        public static bool IsCriticalService(string serviceName) => _criticalServices.Contains(serviceName);

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
        // PERFIS COM METADADOS (explicação por serviço p/ a janela grande)
        // =========================================================

        /// <summary>Item de perfil de serviços com explicação didática por serviço.</summary>
        public class ServicePresetItem
        {
            public string ServiceName { get; set; } = "";
            public string DisplayName { get; set; } = "";
            /// <summary>Por que desativar este serviço faz bem (ou que risco tem).</summary>
            public string Reason { get; set; } = "";
            /// <summary>Impacto se o usuário realmente usa a função (aviso do 'i').</summary>
            public string Warning { get; set; } = "";
            /// <summary>Safe = sem risco aparente; Caution = requer atenção; Dangerous = crítico.</summary>
            public ServiceSafetyLevel Safety { get; set; } = ServiceSafetyLevel.Caution;
            public string Manufacturer { get; set; } = "";
            /// <summary>Está marcado para desativar? (checkbox da janela)</summary>
            public bool IsDisabled { get; set; }
        }

        // Explicações por serviço (chave = nome técnico; fallback genérico para o resto)
        private static readonly Dictionary<string, (string Reason, string Warning)> _serviceReasons = new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- Telemetria / diagnóstico (Gamer) ----
            { "DiagTrack", ("Telemetria completa do Windows (Connected User Experiences). Envia dados de uso para a Microsoft 24/7.", "Nenhum impacto perceptível para o usuário final.") },
            { "dmwappushservice", ("Roteia mensagens WAP push e coleta telemetria de dispositivos.", "Nenhum impacto para desktops.") },
            { "SysMain", ("Superfetch: pré-carrega apps na RAM com base em uso. Em máquinas com SSD e >=8GB, desperdiça RAM e causa I/O em background.", "Se você usa HDD (não SSD), manter ativado ajuda a abrir apps.") },
            { "WSearch", ("Indexação de arquivos (busca do Explorer/Menu Iniciar). Indexar consome CPU/HD constantemente em background.", "A busca de arquivos fica lenta/partial. A busca de APPS no menu Iniciar continua funcionando.") },
            { "MapsBroker", ("Baixa e atualiza mapas do Windows (baixados pelo app Mapas).", "App Mapas fica sem atualização. Ninguém usa.") },
            { "lfsvc", ("Serviço de geolocalização do Windows.", "Apps que usam localização param de funcionar (raro em desktop).") },
            { "WerSvc", ("Envia relatórios de erro de aplicativos para a Microsoft.", "Nenhum impacto — erros deixam de ser reportados.") },
            { "PcaSvc", ("Assistente de Compatibilidade de Programas: coleta dados de apps que falharam.", "Nenhum impacto para o usuário.") },
            { "DPS", ("Serviço de Políticas de Diagnóstico: resolve problemas reportados pelo troubleshooter.", "Troubleshooters (solucionadores de problemas) param de funcionar.") },
            { "WdiServiceHost", ("Hospeda diagnósticos funcionais (Service Diagnostic etc.).", "Troubleshooters específicos param.") },
            { "WdiSystemHost", ("Hospeda diagnósticos de sistema (Performance etc.).", "Troubleshooters de performance param.") },
            // ---- Xbox (Gamer) ----
            { "XblGameSave", ("Sincroniza saves de jogos Xbox Live com a nuvem.", "Se você joga pela Xbox/Microsoft Store, saves param de sincronizar.") },
            { "XboxNetApiSvc", ("API de rede do Xbox Live (multiplayer da Microsoft Store).", "Jogos UWP/Xbox da Store perdem multiplayer.") },
            { "XboxGipSvc", ("Gerencia acessórios do Xbox (controles via protocolo Xbox).", "Controles Xbox (via dongle) podem perder funcionalidades.") },
            { "XblAuthManager", ("Autenticação Xbox Live.", "Jogos UWP da Store não logam no Xbox Live.") },
            // ---- Fax / impressão (Safe) ----
            { "Fax", ("Serviço de fax do Windows. Ninguém mais usa fax.", "Se você usa fax via modem (raro), ele para.") },
            { "RetailDemo", ("Modo demonstração para aparelhos em loja (Brightness/vitrine).", "Nenhum — é para lojistas.") },
            { "Spooler", ("Fila de impressão. Carrega e mantém processos de impressão em RAM.", "IMPRIMIR para de funcionar. Só desative se não tem impressora.") },
            { "PrintWorkflow", ("Suporte a fluxo de impressão universal do Windows.", "Apps UWP de impressão param.") },
            // ---- Outros (Gamer) ----
            { "W32Time", ("Sincronização de horário via NTP (só sincroniza periodicamente).", "O relógio pode atrasar; o Windows ajusta ao logar de novo.") },
            { "RemoteRegistry", ("Permite que usuários REMOTOS editem seu registro (servidor).", "Nenhum para desktop doméstico — é um vetor de ataque inútil e perigoso.") },
            { "WalletService", ("Carteira digital do Windows (descontinuada).", "Nenhum — a feature foi abandonada.") },
            { "NcdAutoSetup", ("Configuração automática de dispositivos de rede (NCD).", "Compartilhamento de rede discovery pode atrasar.") },
            { "SharedAccess", ("Internet Connection Sharing (hotspot mobile do Windows).", "Se você usa hotspot do PC, ele para.") },
            { "TouchKeyboard", ("Teclado touch para telas de toque.", "Em desktop sem touchscreen: nenhum.") },
            { "TabletInputService", ("Serviço de caneta/escrita à mão para tablets.", "Sem tablet/caneta: nenhum. Com caneta: para de funcionar.") },
            // ---- Terceiros (Gamer+) ----
            { "PnkBstrA", ("PunkBuster A: anticheat legado de jogos antigos (BF4, CoD MW2).", "Jogos com PunkBuster não iniciam multiplayer.") },
            { "PnkBstrB", ("PunkBuster B: par do A, mesmo papel.", "Mesmo acima.") },
            { "AdobeUpdateService", ("Atualizador automático do Adobe (verifica updates em background).", "Você precisará atualizar o Adobe manualmente.") },
            { "AdobeARMservice", ("Adobe Reader Update Manager.", "Reader não auto-atualiza.") },
            { "AGMService", ("Adobe Genuine Monitoring: verifica pirataria em background.", "Nenhum para o usuário (só a Adobe perde telemetria).") },
            { "AGSService", ("Adobe Genuine Software Integrity.", "Mesmo acima.") },
            { "Steam Client Service", ("Serviço de suporte do Steam (patching, comprovantes).", "Steam recria o serviço ao abrir; games instalam normalmente.") },
            { "DiscordUpdater", ("Atualizador do Discord em background.", "Discord não auto-atualiza ao abrir.") },
            { "GoogleUpdate", ("Atualizador do Chrome/Google em background (gasta banda e RAM).", "Chrome não auto-atualiza — atualize manualmente.") },
            { "MozillaMaintenance", ("Manutenção/atualização silenciosa do Firefox.", "Firefox não auto-atualiza.") },
            { "Apple Mobile Device Service", ("Suporte a iPhone/iPad via cabo.", "Sem iPhone: nenhum.") },
            { "iPod Service", ("Suporte a iPod clássico.", "Nenhum hoje em dia.") },
            { "iTunesHelper", ("Detecção de iDevice conectado.", "Sem iDevice: nenhum.") },
            { "Everything", ("Serviço de indexação NTFS do Everything (busca instantânea).", "A busca do Everything fica sem atualização em tempo real.") },
            { "Parsec", ("Streaming remoto de jogos (Parsec).", "Parsec não funciona como host.") },
            { "ZeroTier", ("VPN mesh ZeroTier.", "Redes ZeroTier param de conectar.") },
            { "ZeroTierOne", ("VPN mesh ZeroTier (variante).", "Mesmo acima.") },
            { "Windhawk", ("Mod loader de apps (customização do Windows).", "Mods do Windhawk param.") },
            { "Sandboxie", ("Sandboxie (isolamento de apps).", "Sandboxes param.") },
            { "reWASD", ("Remapeamento de controles (reWASD).", "Remaps param.") },
            { "BEService", ("BattlEye anticheat.", "Jogos com BattlEye (DayZ, R6) NÃO iniciam.") },
            { "BEDaisy", ("BattlEye driver anticheat.", "Mesmo acima.") },
            { "EpicOnlineServices", ("Serviços online da Epic (EOS SDK).", "Jogos com EOS podem não logar.") },
            { "OriginClientService", ("EA Origin/EA App.", "EA App não funciona.") },
            { "GOGGalaxyService", ("GOG Galaxy.", "Galaxy não funciona.") },
            { "Creative Cloud", ("Adobe Creative Cloud.", "Apps Adobe CC podem não logar.") },
            { "CCLibrary", ("Biblioteca CC da Adobe.", "Painel CC perde funcionalidades.") },
            { "CoreSync", ("Sync de arquivos Adobe CC.", "Sync para.") },
            { "AdobeGCInvoker", ("Adobe Genuinemonitor.", "Nenhum.") }
        };

        private static string GetServiceReason(string name, out string warning)
        {
            if (_serviceReasons.TryGetValue(name, out var rw)) { warning = rw.Warning; return rw.Reason; }
            warning = "Funções específicas deste serviço param até que seja reativado.";
            return $"Serviço identificado como não essencial (lista curada KitLugia). Desativar libera RAM e CPU usados em background.";
        }

        /// <summary>
        /// Monta os itens de um perfil (para a janela "Perfil de Otimização"):
        /// lista o que será desativado, com explicação e warning por serviço.
        /// presetName: Safe | Gamer | GamerPlus | Restore
        /// </summary>
        public static List<ServicePresetItem> GetServicePresetItems(string presetName)
        {
            var result = new List<ServicePresetItem>();
            try
            {
                // Busca nomes amigáveis + fabricante do WMI numa unica passada
                var wmiInfo = new Dictionary<string, (string Display, string Manufacturer)>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName FROM Win32_Service");
                    foreach (var obj in searcher.Get().Cast<ManagementObject>().ToList())
                    {
                        string n = obj["Name"]?.ToString() ?? "";
                        wmiInfo[n] = (obj["DisplayName"]?.ToString() ?? n, DetectManufacturer(n, obj["PathName"]?.ToString() ?? ""));
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                List<string> targets;
                string mode;
                switch (presetName)
                {
                    case "Safe": targets = new List<string> { "Fax", "RetailDemo", "Spooler", "PrintWorkflow" }; mode = "disabled"; break;
                    case "Gamer": targets = _safeToDisable.ToList(); mode = "disabled"; break;
                    case "GamerPlus": targets = _safeToDisable.Concat(_thirdPartySafeToDisable).ToList(); mode = "disabled"; break;
                    case "Restore": targets = _safeToDisable.ToList(); mode = "auto"; break;
                    default: return result;
                }

                foreach (var name in targets.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string reason = GetServiceReason(name, out string warning);
                    string display = wmiInfo.TryGetValue(name, out var wi) && !string.IsNullOrWhiteSpace(wi.Display) ? wi.Display : name;
                    string manufacturer = wmiInfo.TryGetValue(name, out var wi2) ? wi2.Manufacturer : "Desconhecido";

                    ServiceSafetyLevel safety = _criticalServices.Contains(name) ? ServiceSafetyLevel.Dangerous
                        : _serviceReasons.ContainsKey(name) ? ServiceSafetyLevel.Safe
                        : ServiceSafetyLevel.Caution;

                    result.Add(new ServicePresetItem
                    {
                        ServiceName = name,
                        DisplayName = display,
                        Reason = reason,
                        Warning = warning,
                        Safety = safety,
                        Manufacturer = manufacturer,
                        // No Restore, o default é DESMARCADO (não restaurar); nos outros, marcado
                        IsDisabled = presetName != "Restore"
                    });
                }

                // Ordena: Safe primeiro, Caution, Dangerous por último (estilo PrivacyPage)
                result = result.OrderBy(i => (int)i.Safety).ThenBy(i => i.DisplayName).ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError("GetServicePresetItems", ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Aplica uma lista customizada da janela de perfil: itens marcados vão para 'disabled'
        /// (ou 'auto' quando restore=true), desmarcados vão para o padrão do Windows.
        /// Retorna (sucessos, total, mensagens de falha).
        /// </summary>
        public static (int Success, int Total, List<string> Failures) ApplyCustomServicePreset(List<ServicePresetItem> items, bool restore)
        {
            int success = 0;
            var failures = new List<string>();
            int total = items?.Count ?? 0;
            foreach (var item in items ?? new List<ServicePresetItem>())
            {
                // Marcado = aplica a ação do perfil; desmarcado = volta ao padrão
                string mode = item.IsDisabled ? (restore ? "auto" : "disabled") : GetWindowsDefaultStartMode(item.ServiceName);
                var r = ToggleServiceState(item.ServiceName, mode);
                if (r.Success) success++;
                else failures.Add($"{item.ServiceName}: {r.Message}");
            }
            return (success, total, failures);
        }

        /// <summary>Modo de início padrão de fábrica do Windows (aproximação BlackViper).</summary>
        public static string GetWindowsDefaultStartMode(string serviceName)
        {
            return serviceName switch
            {
                "Fax" or "RetailDemo" or "RemoteRegistry" or "WalletService" => "disabled",
                "XblGameSave" or "WerSvc" or "MapsBroker" or "lfsvc" => "demand",
                _ => "auto"
            };
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