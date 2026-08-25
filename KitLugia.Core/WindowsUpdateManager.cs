using System;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public class WindowsUpdateStatus
    {
        public int CurrentBuild { get; set; }
        public string? CurrentVersion { get; set; }
        public string? DisplayVersion { get; set; }
        public string? UBR { get; set; }
        public bool IsWUServiceRunning { get; set; }
        public bool IsUSOServiceRunning { get; set; }
        public bool IsFlightingServiceRunning { get; set; }
        public DateTime? LastCheckTime { get; set; }
        public bool IsPaused { get; set; }
        public int PauseDaysRemaining { get; set; }
        public string? TargetReleaseVersion { get; set; }
        public string? TargetReleaseVersionInfo { get; set; }
        public string? DeferralBranch { get; set; }
        public int DeferralDays { get; set; }
        public bool IsElevated { get; set; }
        public bool IsInsiderEnrolled { get; set; }
        public string? InsiderChannel { get; set; }
        public bool FlightSigningEnabled { get; set; }
    }

    public static class WindowsUpdateManager
    {
        public static WindowsUpdateStatus GetStatus()
        {
            var status = new WindowsUpdateStatus();
            using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (nt != null)
            {
                if (nt.GetValue("CurrentBuildNumber") is string b && int.TryParse(b, out var bld))
                    status.CurrentBuild = bld;
                status.CurrentVersion = nt.GetValue("CurrentVersion") as string;
                status.DisplayVersion = nt.GetValue("DisplayVersion") as string;
                status.UBR = nt.GetValue("UBR")?.ToString();
            }

            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            if (wu != null)
            {
                if (wu.GetValue("IsPaused") is int paused)
                    status.IsPaused = paused == 1;
                if (wu.GetValue("PauseMax") is int pauseMax && wu.GetValue("PauseEnd") is string pauseEndStr)
                {
                    if (DateTime.TryParse(pauseEndStr, out var pauseEnd))
                    {
                        var remaining = (pauseEnd - DateTime.UtcNow).Days;
                        status.PauseDaysRemaining = Math.Max(0, remaining);
                    }
                }
                if (wu.GetValue("LastChecked") is string lastChecked)
                    DateTime.TryParse(lastChecked, out _);
            }

            using var policies = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            if (policies != null)
            {
                status.TargetReleaseVersion = policies.GetValue("TargetReleaseVersion")?.ToString();
                status.TargetReleaseVersionInfo = policies.GetValue("TargetReleaseVersionInfo") as string;
                status.DeferralBranch = policies.GetValue("BranchReadinessLevel")?.ToString();
                if (policies.GetValue("DeferQualityUpdatesPeriodInDays") is int dq)
                    status.DeferralDays = dq;
            }

            using var shApplicability = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
            if (shApplicability != null)
            {
                status.IsInsiderEnrolled = shApplicability.GetValue("IsBuildFlightingEnabled") is int en && en == 1;
                status.InsiderChannel = shApplicability.GetValue("BranchName") as string;
            }

            try
            {
                using var proc = Process.Start(new ProcessStartInfo("bcdedit", "/enum {current}")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    status.FlightSigningEnabled = System.Text.RegularExpressions.Regex.IsMatch(output,
                        @"^flightsigning\s+Yes$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    proc.WaitForExit();
                }
            }
            catch { }

            try { using var wuauserv = new ServiceController("wuauserv"); status.IsWUServiceRunning = wuauserv.Status == ServiceControllerStatus.Running; }
            catch { }
            try { using var usosvc = new ServiceController("usosvc"); status.IsUSOServiceRunning = usosvc.Status == ServiceControllerStatus.Running; }
            catch { }
            try { using var wisvc = new ServiceController("wisvc"); status.IsFlightingServiceRunning = wisvc.Status == ServiceControllerStatus.Running; }
            catch { }

            return status;
        }

        public static void PauseUpdates(int days)
        {
            EnsureElevated();
            RunProcess("net", "stop wuauserv /y");
            RunProcess("net", "stop usosvc /y");

            using var ux = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            ux?.SetValue("IsPaused", 1, RegistryValueKind.DWord);
            ux?.SetValue("PauseMax", days, RegistryValueKind.DWord);
            ux?.SetValue("PauseEnd", DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            ux?.SetValue("PauseFeatureUpdatesStartTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            ux?.SetValue("PauseFeatureUpdatesEndTime", DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            ux?.SetValue("PauseQualityUpdatesStartTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            ux?.SetValue("PauseQualityUpdatesEndTime", DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);

            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.SetValue("PauseFeatureUpdates", 1, RegistryValueKind.DWord);
            policies?.SetValue("PauseFeatureUpdatesStartTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            policies?.SetValue("PauseFeatureUpdatesEndTime", DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            policies?.SetValue("PauseQualityUpdates", 1, RegistryValueKind.DWord);
            policies?.SetValue("PauseQualityUpdatesStartTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);
            policies?.SetValue("PauseQualityUpdatesEndTime", DateTime.UtcNow.AddDays(days).ToString("yyyy-MM-ddTHH:mm:ssZ"), RegistryValueKind.String);

            RunProcess("net", "start wuauserv /y");
            RunProcess("net", "start usosvc /y");
        }

        public static void ResumeUpdates()
        {
            EnsureElevated();
            RunProcess("net", "stop wuauserv /y");
            RunProcess("net", "stop usosvc /y");

            using var ux = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            ux?.DeleteValue("IsPaused", throwOnMissingValue: false);
            ux?.DeleteValue("PauseMax", throwOnMissingValue: false);
            ux?.DeleteValue("PauseEnd", throwOnMissingValue: false);
            ux?.DeleteValue("PauseFeatureUpdatesStartTime", throwOnMissingValue: false);
            ux?.DeleteValue("PauseFeatureUpdatesEndTime", throwOnMissingValue: false);
            ux?.DeleteValue("PauseQualityUpdatesStartTime", throwOnMissingValue: false);
            ux?.DeleteValue("PauseQualityUpdatesEndTime", throwOnMissingValue: false);

            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.DeleteValue("PauseFeatureUpdates", throwOnMissingValue: false);
            policies?.DeleteValue("PauseFeatureUpdatesStartTime", throwOnMissingValue: false);
            policies?.DeleteValue("PauseFeatureUpdatesEndTime", throwOnMissingValue: false);
            policies?.DeleteValue("PauseQualityUpdates", throwOnMissingValue: false);
            policies?.DeleteValue("PauseQualityUpdatesStartTime", throwOnMissingValue: false);
            policies?.DeleteValue("PauseQualityUpdatesEndTime", throwOnMissingValue: false);

            RunProcess("net", "start wuauserv /y");
            RunProcess("net", "start usosvc /y");
        }

        public static void SetTargetVersion(string version, string? releaseId = null)
        {
            EnsureElevated();
            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.SetValue("TargetReleaseVersion", 1, RegistryValueKind.DWord);
            policies?.SetValue("TargetReleaseVersionInfo", version, RegistryValueKind.String);
            if (!string.IsNullOrEmpty(releaseId))
                policies?.SetValue("ProductVersion", releaseId, RegistryValueKind.String);
            else
                policies?.DeleteValue("ProductVersion", throwOnMissingValue: false);
        }

        public static void ClearTargetVersion()
        {
            EnsureElevated();
            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.DeleteValue("TargetReleaseVersion", throwOnMissingValue: false);
            policies?.DeleteValue("TargetReleaseVersionInfo", throwOnMissingValue: false);
            policies?.DeleteValue("ProductVersion", throwOnMissingValue: false);
        }

        public static void RefreshScan()
        {
            EnsureElevated();
            RunProcess("UsoClient", "StartScan");
            RunProcess("UsoClient", "RefreshSettings");
        }

        public static void ScanUpdates()
        {
            EnsureElevated();
            RunProcess("UsoClient", "StartScan");
        }

        public static void InteractiveScan()
        {
            EnsureElevated();
            RunProcess("UsoClient", "StartInteractiveScan");
        }

        public static void DownloadUpdates()
        {
            EnsureElevated();
            RunProcess("UsoClient", "StartDownload");
        }

        public static void InstallUpdates()
        {
            EnsureElevated();
            RunProcess("UsoClient", "StartInstall");
        }

        public static void ScanInstallWait()
        {
            EnsureElevated();
            RunProcess("UsoClient", "ScanInstallWait");
        }

        public static void SetDeferralDays(int featureUpdatesDays, int qualityUpdatesDays)
        {
            EnsureElevated();
            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.SetValue("DeferFeatureUpdates", 1, RegistryValueKind.DWord);
            policies?.SetValue("DeferFeatureUpdatesPeriodInDays", featureUpdatesDays, RegistryValueKind.DWord);
            policies?.SetValue("DeferQualityUpdates", 1, RegistryValueKind.DWord);
            policies?.SetValue("DeferQualityUpdatesPeriodInDays", qualityUpdatesDays, RegistryValueKind.DWord);
        }

        public static void ClearDeferralPolicies()
        {
            EnsureElevated();
            using var policies = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
            policies?.DeleteValue("DeferFeatureUpdates", throwOnMissingValue: false);
            policies?.DeleteValue("DeferFeatureUpdatesPeriodInDays", throwOnMissingValue: false);
            policies?.DeleteValue("DeferQualityUpdates", throwOnMissingValue: false);
            policies?.DeleteValue("DeferQualityUpdatesPeriodInDays", throwOnMissingValue: false);
        }

        private static void EnsureElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Esta operacao requer privilegios de administrador.");
        }

        public static void RunProcessStatic(string file, string args) => RunProcess(file, args);

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

        public static async System.Threading.Tasks.Task RunProcessAsync(string file, string args, int timeoutMs = 30000)
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo(file, args)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                    proc?.WaitForExit(timeoutMs);
                }
                catch { }
            });
        }
    }
}
