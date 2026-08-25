using System;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public enum InsiderChannel
    {
        ReleasePreview = 5,
        Beta = 4,
        Dev = 3,
        Canary26H1 = 2,
        Canary = 1
    }

    public class InsiderStatus
    {
        public int BuildNumber { get; set; }
        public bool IsInsiderEnrolled { get; set; }
        public string? CurrentChannel { get; set; }
        public string? CurrentRing { get; set; }
        public string? CurrentBranch { get; set; }
        public bool FlightSigningEnabled { get; set; }
        public bool IsServer { get; set; }
        public bool IsCanary26H1Supported { get; set; }
        public bool IsFlightingServiceRunning { get; set; }
        public bool IsWUServiceRunning { get; set; }
        public bool IsElevated { get; set; }
    }

    /// <summary>
    /// Informacoes de disponibilidade de um canal Insider para o PC atual,
    /// replicando a logica do OfflineInsiderEnroll.cmd (variaveis _wis/_wif/_can2/_srv).
    /// </summary>
    public class InsiderChannelInfo
    {
        public InsiderChannel Channel { get; set; }
        public string DisplayName { get; set; } = "";
        public string TargetVersion { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Available { get; set; }
    }

    public static class OfflineInsiderManager
    {
        private const string SelfHostPath = @"SOFTWARE\Microsoft\WindowsSelfHost";
        private const string WUPath = @"SOFTWARE\Microsoft\WindowsUpdate";
        private const string PoliciesDC = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
        private const string PoliciesPB = @"SOFTWARE\Policies\Microsoft\Windows\PreviewBuilds";
        private const string PoliciesWU = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
        private const string SetupWU = @"SYSTEM\Setup\WindowsUpdate";
        private const string SetupMo = @"SYSTEM\Setup\MoSetup";
        private const string SetupLab = @"SYSTEM\Setup\LabConfig";
        private const string CurrentPoliciesDC = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

        public static InsiderStatus GetStatus()
        {
            var status = new InsiderStatus();
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuildNumber") is string buildStr && int.TryParse(buildStr, out var bldNum))
                status.BuildNumber = bldNum;

            using var applicability = Registry.LocalMachine.OpenSubKey($@"{SelfHostPath}\Applicability");
            status.IsInsiderEnrolled = applicability?.GetValue("IsBuildFlightingEnabled") is int enabled && enabled == 1;
            status.CurrentRing = applicability?.GetValue("Ring") as string;
            status.CurrentBranch = applicability?.GetValue("BranchName") as string;
            if (applicability?.GetValue("RingId") is int ringId)
            {
                status.CurrentChannel = ringId switch
                {
                    8 => "Release Preview",
                    9 => "Beta",
                    10 => "Dev",
                    11 => "External (Canary)",
                    30 => "Internal",
                    _ => $"Unknown ({ringId})"
                };
            }

            using var proc = Process.Start(new ProcessStartInfo("bcdedit", "/enum {current}")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                status.FlightSigningEnabled = System.Text.RegularExpressions.Regex.IsMatch(output,
                    @"^flightsigning\s+Yes$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                proc.WaitForExit();
            }

            status.IsServer = System.IO.File.Exists(
                $"{Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)}\\Servicing\\Packages\\Microsoft-Windows-Server*Edition~*.mum");

            int bld = status.BuildNumber;
            status.IsCanary26H1Supported = bld >= 19041 && bld < 27000;

            try
            {
                using var wisvc = new ServiceController("wisvc");
                status.IsFlightingServiceRunning = wisvc.Status == ServiceControllerStatus.Running;
            }
            catch { status.IsFlightingServiceRunning = false; }

            try
            {
                using var wuauserv = new ServiceController("wuauserv");
                status.IsWUServiceRunning = wuauserv.Status == ServiceControllerStatus.Running;
            }
            catch { status.IsWUServiceRunning = false; }

            return status;
        }

        private static bool IsElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private static void EnsureElevated()
        {
            if (!IsElevated())
                throw new UnauthorizedAccessException("Esta operacao requer privilegios de administrador.");
        }

        private static void SetRegDWord(RegistryKey baseKey, string path, string name, int value)
        {
            using var key = baseKey.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static void SetRegString(RegistryKey baseKey, string path, string name, string value)
        {
            using var key = baseKey.CreateSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree);
            key?.SetValue(name, value, RegistryValueKind.String);
        }

        private static void DeleteRegValue(RegistryKey baseKey, string path, string name)
        {
            using var key = baseKey.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        private static void DeleteRegValueCu(string path, string name)
        {
            using var cu = Registry.CurrentUser;
            using var key = cu.OpenSubKey(path, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        private static void DeleteRegTree(RegistryKey baseKey, string path)
        {
            try
            {
                if (baseKey.OpenSubKey(path) != null)
                    baseKey.DeleteSubKeyTree(path);
            }
            catch { }
        }

        /// <summary>
        /// Lista os canais com versao alvo calculada conforme o build do PC atual,
        /// replicando a logica do OfflineInsiderEnroll.cmd:
        ///   _wis (Beta) = 26220+; se build &gt;= 26300 = build+; se build == 28000 = 28020;
        ///                   se build &lt; 22000 = 22635; se build &lt; 19041 = 19045
        ///   _wif (Dev)  = 26300+; se build &gt;= 28000 = 28020+; se build &gt;= 29500 = build+
        ///   _can2 (Canary 26H1) disponivel apenas para builds 19041-26999
        ///   _srv: servidores so tem Canary e Release Preview
        /// </summary>
        public static List<InsiderChannelInfo> GetChannelInfos(int currentBuild, bool isServer)
        {
            string wis = "26220+";
            if (currentBuild >= 26300) wis = $"{currentBuild}+";
            if (currentBuild == 28000) wis = "28020";
            if (currentBuild < 22000) wis = "22635";
            if (currentBuild < 19041) wis = "19045";

            string wif = "26300+";
            if (currentBuild >= 28000) wif = "28020+";
            if (currentBuild >= 29500) wif = $"{currentBuild}+";

            bool can2 = currentBuild >= 19041 && currentBuild < 27000;

            var list = new List<InsiderChannelInfo>
            {
                new InsiderChannelInfo
                {
                    Channel = InsiderChannel.Canary,
                    DisplayName = "Canary (Experimental [Future])",
                    TargetVersion = "29500+",
                    Available = true,
                    Description = "Updates de plataforma e kernel mais recentes, podem impactar estabilidade. Recursos chegam depois em outros canais."
                },
                new InsiderChannelInfo
                {
                    Channel = InsiderChannel.Canary26H1,
                    DisplayName = "Canary 26H1 (Experimental)",
                    TargetVersion = "28020+",
                    Available = !isServer && can2,
                    Description = can2
                        ? "Disponivel para este build (19041-26999). Recebe builds experimentais 26H1."
                        : "Nao disponivel para este build. Requer build entre 19041 e 26999."
                },
                new InsiderChannelInfo
                {
                    Channel = InsiderChannel.Dev,
                    DisplayName = "Dev (Experimental)",
                    TargetVersion = wif,
                    Available = !isServer,
                    Description = "Recebe os proximos recursos do Windows em desenvolvimento ativo."
                },
                new InsiderChannelInfo
                {
                    Channel = InsiderChannel.Beta,
                    DisplayName = "Beta",
                    TargetVersion = wis,
                    Available = !isServer,
                    Description = "Preview de fixes e recursos quase prontos, antes do lancamento amplo."
                },
                new InsiderChannelInfo
                {
                    Channel = InsiderChannel.ReleasePreview,
                    DisplayName = "Release Preview",
                    TargetVersion = $"{currentBuild} / proximo RTM",
                    Available = true,
                    Description = "Preview de fixes e certos recursos, alem de acesso opcional a proxima versao do Windows antes do lancamento geral."
                }
            };
            return list;
        }

        public static void Enroll(InsiderChannel channel)
        {
            EnsureElevated();
            string ring, branch, uiChannel, uiBranch;
            int ringId = 11, uiVersion = 0;

            switch (channel)
            {
                case InsiderChannel.ReleasePreview:
                    ring = "External"; branch = "ReleasePreview";
                    uiChannel = "ReleasePreview"; uiBranch = branch; ringId = 8;
                    break;
                case InsiderChannel.Beta:
                    ring = "External"; branch = "Beta";
                    uiChannel = "Beta"; uiBranch = branch; ringId = 9;
                    break;
                case InsiderChannel.Dev:
                    ring = "External"; branch = "Dev";
                    uiChannel = "Dev"; uiBranch = branch; ringId = 10;
                    var bld = GetCurrentBuild();
                    if (bld < 27000) uiVersion = 26200;
                    break;
                case InsiderChannel.Canary26H1:
                    ring = "External"; branch = "CanaryChannel";
                    uiChannel = "Canary"; uiBranch = "Dev"; ringId = 11;
                    uiVersion = 28000;
                    break;
                case InsiderChannel.Canary:
                default:
                    ring = "External"; branch = "CanaryChannel";
                    uiChannel = "Canary"; uiBranch = branch; ringId = 11;
                    var build = GetCurrentBuild();
                    if (build < 29500) uiVersion = -1;
                    if (build >= 26100) uiBranch = "Dev";
                    break;
            }

            ResetInsiderConfig(resetAll: false);
            ApplyInsiderConfig(ring, branch, uiChannel, uiBranch, ringId, uiVersion);
            EnableFlightSigning();
        }

        public static void Unenroll(bool fullCleanup)
        {
            EnsureElevated();
            if (fullCleanup)
            {
                ResetInsiderConfig(resetAll: true);
                RunProcess("bcdedit", "/deletevalue {current} flightsigning");
                SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "UserDidOptOut", 1);
                SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "OptOutState", 25);
            }
            else
            {
                SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "TestFlags", 0x100);
                SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "UserDidOptOut", 0);
                SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "OptOutState", 0);
            }
        }

        public static void ResetConfig()
        {
            EnsureElevated();
            ResetInsiderConfig(resetAll: true);
        }

        public static void RefreshWUScan()
        {
            EnsureElevated();
            RunProcess("net", "stop wisvc /y");
            RunProcess("net", "stop usosvc /y");
            RunProcess("net", "stop wuauserv /y");

            try
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                var updateStore = System.IO.Path.Combine(programData, "USOPrivate", "UpdateStore");
                if (System.IO.Directory.Exists(updateStore))
                {
                    foreach (var f in System.IO.Directory.GetFiles(updateStore))
                        System.IO.File.Delete(f);
                }

                var usoLogs = System.IO.Path.Combine(programData, "USOShared", "Logs");
                if (System.IO.Directory.Exists(usoLogs))
                {
                    foreach (var f in System.IO.Directory.GetFiles(usoLogs))
                        System.IO.File.Delete(f);
                    foreach (var d in System.IO.Directory.GetDirectories(usoLogs))
                        System.IO.Directory.Delete(d, recursive: true);
                }
            }
            catch { }

            RunProcess("net", "start wuauserv /y");
            RunProcess("net", "start usosvc /y");
            RunProcess("net", "start wisvc /y");
            RunProcess("UsoClient", "RefreshSettings");
        }

        private static void ResetInsiderConfig(bool resetAll)
        {
            if (resetAll)
                DeleteRegTree(Registry.LocalMachine, $@"{SelfHostPath}\FIDs");

            if (resetAll)
                DeleteRegTree(Registry.LocalMachine, $@"{SelfHostPath}\OneSettings");

            DeleteRegValue(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "FlightSettingsMaxPauseDays");

            string[] selfHostTrees = { "Account", "Applicability", "Cache", "ClientState",
                "UI", "Restricted", "ToastNotification" };
            foreach (var tree in selfHostTrees)
                DeleteRegTree(Registry.LocalMachine, $@"{SelfHostPath}\{tree}");

            string[] slsPrograms = { "WUMUDCat", "RingExternal", "RingPreview", "RingInsiderSlow", "RingInsiderFast", "RingCanary" };
            foreach (var prog in slsPrograms)
                DeleteRegTree(Registry.LocalMachine,
                    $@"{WUPath}\CurrentVersion\WindowsUpdate\SLS\Programs\{prog}");

            DeleteRegValue(Registry.LocalMachine, CurrentPoliciesDC, "AllowTelemetry");
            DeleteRegValue(Registry.LocalMachine, PoliciesDC, "AllowTelemetry");
            DeleteRegValue(Registry.LocalMachine, PoliciesDC, "AllowTelemetry_PolicyManager");
            DeleteRegValue(Registry.LocalMachine, PoliciesDC, "DisableOneSettingsDownloads");
            DeleteRegValue(Registry.LocalMachine, PoliciesPB, "AllowBuildPreview");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "BranchReadinessLevel");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "ManagePreviewBuilds");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "ManagePreviewBuildsPolicyValue");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "TargetReleaseVersion");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "TargetReleaseVersionInfo");
            DeleteRegValue(Registry.LocalMachine, PoliciesWU, "ProductVersion");
            DeleteRegValue(Registry.LocalMachine, SetupWU, "AllowWindowsUpdate");
            DeleteRegValue(Registry.LocalMachine, SetupMo, "AllowUpgradesWithUnsupportedTPMOrCPU");
            DeleteRegValue(Registry.LocalMachine, SetupLab, "BypassCPUCheck");
            DeleteRegValue(Registry.LocalMachine, SetupLab, "BypassRAMCheck");
            DeleteRegValue(Registry.LocalMachine, SetupLab, "BypassSecureBootCheck");
            DeleteRegValue(Registry.LocalMachine, SetupLab, "BypassStorageCheck");
            DeleteRegValue(Registry.LocalMachine, SetupLab, "BypassTPMCheck");

            using var cu = Registry.CurrentUser;
            DeleteRegTree(cu, @"SOFTWARE\Microsoft\PCHC");
        }

        private static void ApplyInsiderConfig(string ring, string branch, string uiChannel, string uiBranch, int ringId, int uiVersion)
        {
            RunProcess("sc", "config DiagTrack start= auto");
            RunProcess("sc", "config wisvc start= demand");

            string[] flightTasks = {
                @"\Microsoft\Windows\Flighting\OneSettings\RefreshCache",
                @"\Microsoft\Windows\Flighting\FeatureConfig\GovernedFeatureUsageProcessing",
                @"\Microsoft\Windows\Flighting\FeatureConfig\ReconcileConfigs",
                @"\Microsoft\Windows\Flighting\FeatureConfig\ReconcileFeatures",
                @"\Microsoft\Windows\Flighting\FeatureConfig\SafeguardsReconciliation",
                @"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataReceiver",
                @"\Microsoft\Windows\Flighting\FeatureConfig\UsageDataFlushing"
            };
            foreach (var task in flightTasks)
                RunProcess("schtasks", $"/Change /ENABLE /TN \"{task}\"");

            SetRegDWord(Registry.LocalMachine, CurrentPoliciesDC, "AllowTelemetry", 3);
            SetRegDWord(Registry.LocalMachine,
                $@"{WUPath}\CurrentVersion\WindowsUpdate\Orchestrator", "EnableUUPScan", 1);
            SetRegDWord(Registry.LocalMachine,
                $@"{WUPath}\CurrentVersion\WindowsUpdate\SLS\Programs\Ring{ring}", "Enabled", 1);
            SetRegDWord(Registry.LocalMachine,
                $@"{WUPath}\CurrentVersion\WindowsUpdate\SLS\Programs\WUMUDCat", "WUMUDCATEnabled", 1);

            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "TestFlags", 0x130);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "EnablePreviewBuilds", 2);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "IsBuildFlightingEnabled", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "IsConfigSettingsFlightingEnabled", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "IsConfigExpFlightingEnabled", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "UseSettingsExperience", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "FlightUpgradeTarget", uiVersion);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "RingId", ringId);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "Ring", ring);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "ContentType", "Mainline");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "BranchName", branch);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "RingBackup", ring);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "RingBackupV2", ring);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "BranchBackup", branch);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Applicability", "ContentBackup", "Mainline");

            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIRing", ring);
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIContentType", "Mainline");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIBranch", uiBranch);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UITargetVersion", uiVersion);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "EulaAccepted", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "ReleasePreviewSelectable", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "AdvancedToggleState", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "OptOutState", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIDialogConsent", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIOptin", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Selection", "UIUsage", 0);

            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "PropertyIgnoreList",
                "AccountsBlob;CTACBlob;FlightIDBlob;ServiceDrivenActionResults;isVirtualMachine");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "RequestedCTACAppIds", "WU;FSS");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "BranchList",
                "{\"Branches\":[{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"Beta\",\"Alias\":null,\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"External\",\"Internal\"],\"RTMOnly\":false,\"ContentTypes\":[\"Mainline\"]},{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"CanaryChannel\",\"Alias\":null,\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"External\",\"Internal\"],\"RTMOnly\":false,\"ContentTypes\":[\"Mainline\"]},{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"Dev\",\"Alias\":null,\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"External\",\"Internal\"],\"RTMOnly\":false,\"ContentTypes\":[\"Mainline\"]},{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"Experimental\",\"Alias\":\"Dev\",\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"External\",\"Internal\"],\"RTMOnly\":false,\"ContentTypes\":[\"Mainline\"]},{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"ReleasePreview\",\"Alias\":null,\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"External\",\"Internal\"],\"RTMOnly\":false,\"ContentTypes\":[\"Mainline\"]},{\"Platform\":\"Windows.Desktop_0\",\"Name\":\"WindowsInnerRing\",\"Alias\":null,\"Description\":null,\"Migrate\":null,\"FlightingDisabled\":false,\"BranchRings\":[\"OSG\"],\"RTMOnly\":false,\"ContentTypes\":[\"Custom\"]}]}");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "RingList",
                "{\"Rings\":[{\"Order\":\"0000000003\",\"Name\":\"WIF\",\"Alias\":\"Fast\",\"Description\":\"WIF\",\"Id\":\"10\",\"OptInDescription\":null},{\"Order\":\"0000000005\",\"Name\":\"WIS\",\"Alias\":\"Slow\",\"Description\":\"WIS\",\"Id\":\"9\",\"OptInDescription\":null},{\"Order\":\"0000000015\",\"Name\":\"RP\",\"Alias\":\"Release Preview\",\"Description\":\"RP\",\"Id\":\"8\",\"OptInDescription\":null},{\"Order\":\"0000000016\",\"Name\":\"External\",\"Alias\":\"External\",\"Description\":\"External\",\"Id\":\"11\",\"OptInDescription\":null},{\"Order\":\"0000000017\",\"Name\":\"Internal\",\"Alias\":\"Internal\",\"Description\":\"Internal\",\"Id\":\"30\",\"OptInDescription\":null},{\"Order\":\"0000000018\",\"Name\":\"OSG\",\"Alias\":\"OSG\",\"Description\":\"OSG\",\"Id\":\"26\",\"OptInDescription\":null}]}");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "ConfigurationOptionList",
                "{\"ConfigurationOptionList\":[{\"Name\":\"Experimental\",\"Alias\":\"Experimental Channel\",\"Description\":\"Get early access to features under active development. Changes may evolve, be delayed or not ship.\",\"ContentType\":\"Mainline\",\"Branch\":\"Dev\",\"Ring\":\"External\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Experimental\",\"Warning\":\"\"},{\"Name\":\"CanaryChannel\",\"Alias\":\"Canary Channel\",\"Description\":\"Foundational platform and kernel updates which may impact stability. Features may arrive later from other releases.\",\"ContentType\":\"Mainline\",\"Branch\":\"CanaryChannel\",\"Ring\":\"External\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Canary\",\"Warning\":\"\"},{\"Name\":\"Dev\",\"Alias\":\"Dev Channel\",\"Description\":\"Get early access to upcoming Windows capabilities and OS improvements.\",\"ContentType\":\"Mainline\",\"Branch\":\"Dev\",\"Ring\":\"External\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Dev\",\"Warning\":\"\"},{\"Name\":\"Beta\",\"Alias\":\"Beta Channel\",\"Description\":\"Preview near-ready fixes and features before broad release.\",\"ContentType\":\"Mainline\",\"Branch\":\"Beta\",\"Ring\":\"External\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Beta\",\"Warning\":\"\"},{\"Name\":\"ReleasePreview\",\"Alias\":\"Release Preview\",\"Description\":\"Ideal if you want to preview fixes and certain key features, plus get optional access to the next version of Windows before it's generally available to the world. This channel is also recommended for commercial users.\",\"ContentType\":\"Mainline\",\"Branch\":\"ReleasePreview\",\"Ring\":\"External\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Release Preview\",\"Warning\":\"\"},{\"Name\":\"WindowsInnerRing\",\"Alias\":\"Windows Inner Ring\",\"Description\":\"Earliest platform builds. Not recommended for daily use.\",\"ContentType\":\"Mainline\",\"Branch\":\"WindowsInnerRing\",\"Ring\":\"OSG\",\"IsRecommended\":false,\"RecommendedOnly\":false,\"IsValid\":false,\"Title\":\"Windows Inner Ring\",\"Warning\":\"\"}]}");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "ContentList",
                "{\"ContentList\":[{\"Name\":\"Mainline\",\"Alias\":\"Channels\",\"Description\":\"Channels\",\"OptInDescription\":\"Select the channel you would like to receive updates from.\",\"ContentRings\":[\"External\"],\"RTMOnly\":false,\"ErrorMessage\":null,\"DefaultRing\":\"External\",\"CanSwitch\":false},{\"Name\":\"Custom\",\"Alias\":\"Custom\",\"Description\":\"Custom\",\"OptInDescription\":\"Custom Options.\",\"ContentRings\":[\"OSG\"],\"RTMOnly\":false,\"ErrorMessage\":null,\"DefaultRing\":\"OSG\",\"CanSwitch\":false}],\"DefaultSelectionName\":\"Mainline\"}");
            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\Cache", "CustomConfigurationOption",
                "{\"CustomConfigurationOption\":\"Your device is set to a custom configuration.\\nContent: FlightingContracts.DataContracts.Content\\nBranch: " + branch + "\\nRing: " + ring + "\"}");

            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Account", "SupportedTypes", 3);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\Account", "Status", 8);

            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "AllowFSSCommunications", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "UICapabilities", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "IgnoreConsolidation", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "MsaUserTicketHr", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "MsaDeviceTicketHr", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "ValidateOnlineHr", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "LastHR", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "ErrorState", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "PilotInfoRing", 3);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "RegistryAllowlistVersion", 4);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "FileAllowlistVersion", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "DefaultedToChannels", 1);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\ClientState", "UserDidOptOut", 0);

            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI", "UIControllableState", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIHiddenElements", 65535);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIDisabledElements", 65535);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIServiceDrivenElementVisibility", 0);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIErrorMessageVisibility", 192);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIHiddenElements_Rejuv", 65534);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\UI\Visibility", "UIDisabledElements_Rejuv", 65535);

            SetRegString(Registry.LocalMachine, $@"{SelfHostPath}\UI\Strings", "StickyMessage",
                "{\"Message\":\"Device Enrolled Using OfflineInsiderEnroll\",\"LinkTitle\":\"\",\"LinkUrl\":\"\",\"DynamicXaml\":\"<StackPanel xmlns=\\\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\\\"><TextBlock Style=\\\"{StaticResource BodyTextBlockStyle }\\\">This device has been enrolled to the Windows Insider program using OfflineInsiderEnroll. If you want to change settings of the enrollment or stop receiving Windows Insider builds, please use the script. <Hyperlink NavigateUri=\\\"https://github.com/abbodi1406/offlineinsiderenroll\\\" TextDecorations=\\\"None\\\">Learn more</Hyperlink></TextBlock></StackPanel>\",\"Severity\":0}");

            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\OneSettings", "FlightSettingsVersion", 2);
            SetRegDWord(Registry.LocalMachine, $@"{SelfHostPath}\OneSettings", "IsBuildUnsupported", 0);

            SetRegDWord(Registry.LocalMachine, SetupWU, "AllowWindowsUpdate", 1);
            SetRegDWord(Registry.LocalMachine, SetupMo, "AllowUpgradesWithUnsupportedTPMOrCPU", 1);
            SetRegDWord(Registry.LocalMachine, SetupLab, "BypassRAMCheck", 1);
            SetRegDWord(Registry.LocalMachine, SetupLab, "BypassSecureBootCheck", 1);
            SetRegDWord(Registry.LocalMachine, SetupLab, "BypassTPMCheck", 1);

            using var cu = Registry.CurrentUser;
            SetRegDWord(cu, @"SOFTWARE\Microsoft\PCHC", "UpgradeEligibility", 1);
        }

        private static void EnableFlightSigning()
        {
            RunProcess("bcdedit", "/set {current} flightsigning yes");
        }

        private static int GetCurrentBuild()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuildNumber") is string buildStr && int.TryParse(buildStr, out var bld))
                return bld;
            return 0;
        }

        private static void RunProcess(string file, string args)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(file, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                proc?.WaitForExit(30000);
            }
            catch { }
        }
    }
}
