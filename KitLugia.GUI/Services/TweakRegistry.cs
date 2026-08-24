using System;
using System.Collections.Generic;
using System.Threading;
using KitLugia.Core;
using KitLugia.GUI.Pages;
using Microsoft.Win32;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Definition of a single tweak that can be maintained across reboots.
    /// </summary>
    public sealed class TweakDef
    {
        /// <summary>Returns true if the tweak is currently applied (active).</summary>
        public Func<bool> IsApplied { get; init; } = () => false;

        /// <summary>Re-applies the tweak. Idempotent — safe to call even if already applied.</summary>
        public Action Apply { get; init; } = () => { };

        /// <summary>Human-readable description for logging.</summary>
        public string Description { get; init; } = "";
    }

    /// <summary>
    /// Central registry of all TweaksPage tweaks. To add a new tweak to the
    /// "maintain after reboot" system, simply add an entry to <see cref="Tweaks"/>.
    /// No changes needed in AutoFixTweaks or the persistence helpers.
    /// </summary>
    public static class TweakRegistry
    {
        private const string RegistryPath = @"Software\KitLugia\Tweaks";

        public static readonly Dictionary<string, TweakDef> Tweaks = new(StringComparer.OrdinalIgnoreCase)
        {
            // === Performance ===
            ["GameMode"] = new() { IsApplied = SystemTweaks.IsGamingOptimized, Apply = SystemTweaks.ApplyGamingOptimizations, Description = "Modo Jogo" },
            ["MPO"] = new() { IsApplied = SystemTweaks.IsMpoDisabled, Apply = () => SystemTweaks.ToggleMpo(), Description = "MPO (Multi-Plane Overlay)" },
            ["VBS"] = new() { IsApplied = () => !SystemTweaks.IsVbsEnabled(), Apply = () => { if (SystemTweaks.IsVbsEnabled()) SystemTweaks.ToggleVbs(); }, Description = "VBS (Virtualization-Based Security)" },
            ["Bing"] = new() { IsApplied = SystemTweaks.IsBingDisabled, Apply = SystemTweaks.ApplyBingTweak, Description = "Bing Search" },
            ["MemoryUsage"] = new() { IsApplied = SystemTweaks.IsMemoryUsageEnabled, Apply = () => SystemTweaks.ToggleMemoryUsage(), Description = "Memory Usage Display" },
            ["TimerResolution"] = new() { IsApplied = SystemTweaks.IsTimerResolutionOptimized, Apply = () => SystemTweaks.ToggleTimerResolution(), Description = "Timer Resolution" },
            ["FastShutdown"] = new() { IsApplied = SystemTweaks.IsFastShutdownEnabled, Apply = SystemTweaks.ToggleFastShutdown, Description = "Fast Shutdown / Turbo Boot" },

            // === Latency Slides ===
            ["SlideInput"] = new() { IsApplied = SystemTweaks.IsInputLatencyOptimized, Apply = () => SystemTweaks.OptimizeInputLatency(), Description = "Input Latency (máximo)" },
            ["SlideUsb"] = new() { IsApplied = SystemTweaks.IsUsbPowerSavingDisabled, Apply = () => SystemTweaks.DisableUsbPowerSaving(), Description = "USB Power Saving" },
            ["SlideGaming"] = new() { IsApplied = SystemTweaks.IsGamingLatencyOptimized, Apply = () => SystemTweaks.OptimizeGamingLatency(), Description = "Gaming Latency (extremo)" },
            ["PciePower"] = new() { IsApplied = SystemTweaks.IsPcieLinkStatePowerManagementDisabled, Apply = () => SystemTweaks.DisablePcieLinkStatePowerManagement(), Description = "PCIe Link State Power" },
            ["Timeout"] = new() { IsApplied = SystemTweaks.IsHardDiskDisplayTimeoutDisabled, Apply = () => SystemTweaks.DisableHardDiskDisplayTimeout(), Description = "Disk/Display Timeout" },

            // === Telemetry & Background ===
            ["BackgroundApps"] = new() { IsApplied = SystemTweaks.IsBackgroundAppsDisabled, Apply = SystemTweaks.DisableBackgroundApps, Description = "Background Apps" },
            ["NDU"] = new() { IsApplied = SystemTweaks.IsNDUDisabled, Apply = SystemTweaks.DisableNDU, Description = "NDU (Network Data Usage)" },
            ["ServiceStartup"] = new() { IsApplied = SystemTweaks.IsServiceStartupOptimized, Apply = SystemTweaks.OptimizeServiceStartup, Description = "Service Startup (Manual)" },
            ["NoAutoReboot"] = new() { IsApplied = SystemTweaks.IsNoAutoRebootEnabled, Apply = SystemTweaks.EnableNoAutoReboot, Description = "No Auto Reboot" },
            ["DiagnosticServices"] = new() { IsApplied = SystemTweaks.IsDiagnosticServicesDisabled, Apply = () => SystemTweaks.DisableDiagnosticServices(), Description = "Diagnostic Services" },
            ["PowerThrottling"] = new() { IsApplied = SystemTweaks.IsPowerThrottlingDisabled, Apply = () => SystemTweaks.DisablePowerThrottling(), Description = "Power Throttling" },
            ["GdiScaling"] = new() { IsApplied = SystemTweaks.IsGdiScalingDisabled, Apply = () => SystemTweaks.DisableGdiScaling(), Description = "GDI Scaling" },

            // === Telemetry Services (also maintained by AutoFixCommunityProcesses, but IsApplied check prevents duplicate work) ===
            ["DiagTrack"] = new() { IsApplied = () => SystemTweaks.IsServiceDisabled("DiagTrack"), Apply = () => SystemTweaks.SetServiceStartup("DiagTrack", true), Description = "Service: DiagTrack" },
            ["Dmwappush"] = new() { IsApplied = () => SystemTweaks.IsServiceDisabled("dmwappushservice"), Apply = () => SystemTweaks.SetServiceStartup("dmwappushservice", true), Description = "Service: dmwappushservice" },
            ["WerSvc"] = new() { IsApplied = () => SystemTweaks.IsServiceDisabled("WerSvc"), Apply = () => SystemTweaks.SetServiceStartup("WerSvc", true), Description = "Service: WerSvc" },
            ["PcaSvc"] = new() { IsApplied = () => SystemTweaks.IsServiceDisabled("PcaSvc"), Apply = () => SystemTweaks.SetServiceStartup("PcaSvc", true), Description = "Service: PcaSvc" },
            ["TelemetryTasks"] = new() { IsApplied = SystemTweaks.AreTelemetryTasksDisabled, Apply = () => SystemTweaks.ApplyTelemetryScheduledTasks(true), Description = "Telemetry Scheduled Tasks" },

            // === SmartScreen ===
            ["SmartScreenSystem"] = new() { IsApplied = TweaksPage.IsSmartScreenSystemDisabled, Apply = ApplySmartScreenSystem, Description = "SmartScreen (System)" },
            ["SmartScreenExplorer"] = new() { IsApplied = TweaksPage.IsSmartScreenExplorerDisabled, Apply = ApplySmartScreenExplorer, Description = "SmartScreen (Explorer)" },

            // === Hardware & Rede ===
            ["L2Cache"] = new() { IsApplied = SystemTweaks.IsSecondLevelDataCacheSet, Apply = SystemTweaks.ApplyAutoCacheTweak, Description = "L2/L3 Cache" },
            ["RmCacheLoc"] = new() { IsApplied = SystemTweaks.IsRmCacheLocSet, Apply = () => SystemTweaks.ToggleRmCacheLocTweak(), Description = "RmCacheLoc (NVIDIA)" },
            ["Nagle"] = new() { IsApplied = SystemTweaks.IsNagleAlgorithmDisabled, Apply = () => SystemTweaks.DisableNagleAlgorithm(), Description = "Nagle Algorithm" },
            ["CoreParking"] = new() { IsApplied = SystemTweaks.IsCoreParkingDisabled, Apply = () => SystemTweaks.DisableCoreParking(), Description = "Core Parking" },

            // === GPU ===
            ["FrameQueue"] = new() { IsApplied = SystemTweaks.IsFrameQueueModeSet, Apply = SystemTweaks.ApplyLowLatencyFrameQueue, Description = "Frame Queue (Low Latency)" },
            ["Preemption"] = new() { IsApplied = SystemTweaks.IsPreemptionEnabled, Apply = SystemTweaks.EnableGpuPreemption, Description = "GPU Preemption" },
            ["GpuIdle"] = new() { IsApplied = SystemTweaks.IsGpuIdleDisabled, Apply = SystemTweaks.DisableGpuIdleSchedule, Description = "GPU Idle" },
            ["PowerLatency"] = new() { IsApplied = SystemTweaks.IsGpuPowerLatencySet, Apply = SystemTweaks.SetGpuPowerLatency, Description = "GPU Power Latency" },
            ["Hags"] = new() { IsApplied = SystemTweaks.IsHagsEnabled, Apply = SystemTweaks.EnableHags, Description = "HAGS (Hardware Accelerated GPU Scheduling)" },
            ["Tdr"] = new() { IsApplied = SystemTweaks.IsTdrDelayIncreased, Apply = SystemTweaks.IncreaseTdrDelay, Description = "TDR Delay (10s)" },
            ["IoLock"] = new() { IsApplied = SystemTweaks.IsIoPageLockLimitSet, Apply = SystemTweaks.SetIoPageLockLimit, Description = "IoPageLockLimit (8192 KB)" },
            ["InputQueue"] = new() { IsApplied = SystemTweaks.IsInputQueueSizeIncreased, Apply = SystemTweaks.IncreaseInputQueueSize, Description = "Input Queue Size (200)" },

            // === Startup ===
            ["StartupDelay"] = new() { IsApplied = SystemTweaks.IsStartupDelayOptimized, Apply = SystemTweaks.OptimizeStartupDelay, Description = "Startup Delay" },
            ["FastStartup"] = new() { IsApplied = SystemTweaks.IsFastStartupDisabled, Apply = SystemTweaks.DisableFastStartup, Description = "Fast Startup" },
            ["BootMenu"] = new() { IsApplied = SystemTweaks.IsBootMenuTimeoutZero, Apply = SystemTweaks.SetBootMenuTimeoutZero, Description = "Boot Menu Timeout" },
            ["NumLock"] = new() { IsApplied = SystemTweaks.IsNumLockOnStartupEnabled, Apply = SystemTweaks.EnableNumLockOnStartup, Description = "NumLock On Startup" },

            // === Shutdown ===
            ["WaitToKillService"] = new() { IsApplied = SystemTweaks.IsWaitToKillServiceOptimized, Apply = SystemTweaks.OptimizeWaitToKillService, Description = "WaitToKillService (2s)" },
            ["QuickAppKill"] = new() { IsApplied = SystemTweaks.IsQuickAppKillApplied, Apply = SystemTweaks.ApplyQuickAppKill, Description = "Quick App Kill" },
            ["ClearPageFile"] = new() { IsApplied = SystemTweaks.IsClearPageFileDisabled, Apply = SystemTweaks.DisableClearPageFile, Description = "Clear PageFile" },
            ["VerboseStatus"] = new() { IsApplied = SystemTweaks.IsVerboseStatusEnabled, Apply = SystemTweaks.EnableVerboseStatus, Description = "Verbose Status" },

            // === Interface & Explorer ===
            ["MenuDelay"] = new() { IsApplied = SystemTweaks.IsMenuShowDelayOptimized, Apply = SystemTweaks.OptimizeMenuShowDelay, Description = "Menu Show Delay (0ms)" },
            ["VisualFX"] = new() { IsApplied = SystemTweaks.IsVisualFXOptimized, Apply = SystemTweaks.OptimizeVisualFX, Description = "Visual FX (Maximum)" },
            ["IconCache"] = new() { IsApplied = SystemTweaks.IsIconCacheIncreased, Apply = SystemTweaks.IncreaseIconCache, Description = "Icon Cache (8192 KB)" },
            ["PCA"] = new() { IsApplied = SystemTweaks.IsPCADisabled, Apply = SystemTweaks.DisablePCA, Description = "PCA (Program Compatibility)" },
        };

        // ── Persistence helpers ──────────────────────────────────────────

        public static bool IsMaintained()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key != null && (int)(key.GetValue("TweaksMaintained", 0) ?? 0) == 1;
            }
            catch { return false; }
        }

        public static void SetMaintained(bool value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key?.SetValue("TweaksMaintained", value ? 1 : 0);
            }
            catch { }
        }

        public static void SaveTweakState(string name, bool active)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key?.SetValue(name, active ? 1 : 0);
            }
            catch { }
        }

        public static void SaveAllTweakStates()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                if (key == null) return;
                foreach (var (name, def) in Tweaks)
                {
                    bool active = false;
                    try { active = def.IsApplied(); } catch { }
                    key.SetValue(name, active ? 1 : 0);
                }
            }
            catch { }
        }

        // ── Startup fix ──────────────────────────────────────────────────

        /// <summary>
        /// Called from TrayIconService.Initialize() via Task.Run (non-blocking).
        /// Reads saved tweak states and re-applies any that were reverted by Windows Update.
        /// </summary>
        public static void AutoFixTweaks()
        {
            if (!IsMaintained()) return;

            try
            {
                var saved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return;
                    foreach (var name in Tweaks.Keys)
                    {
                        var val = key.GetValue(name);
                        if (val is int i)
                            saved[name] = i == 1;
                    }
                }

                int reapplied = 0, alreadyOk = 0, failed = 0;
                foreach (var (name, def) in Tweaks)
                {
                    if (!saved.TryGetValue(name, out bool shouldBeActive)) continue;
                    if (!shouldBeActive) continue; // tweak was OFF when saved — don't re-apply

                    bool currentlyActive;
                    try { currentlyActive = def.IsApplied(); } catch { continue; }

                    if (currentlyActive) { alreadyOk++; continue; }

                    try
                    {
                        def.Apply();
                        reapplied++;
                        Logger.Log($"[Tweaks] Reaplicado: {def.Description}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.Log($"[Tweaks] Falha ao reaplicar {def.Description}: {ex.Message}");
                    }
                }

                Logger.Log($"[Tweaks] Verificacao concluida: {reapplied} reaplicados, {alreadyOk} ja ok, {failed} falhas");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Tweaks] Erro no AutoFixTweaks: {ex.Message}");
            }
        }

        // ── SmartScreen apply helpers (inlined from TweaksPage Click handlers) ──

        private static void ApplySmartScreenSystem()
        {
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                "EnableSmartScreen", 0, RegistryValueKind.DWord);
            using (var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
            using (var appKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost"))
                appKey?.SetValue("EnableWebContentEvaluation", 0, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\PhishingFilter",
                "EnabledV9", 0, RegistryValueKind.DWord);
            using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
                attKey?.SetValue("SaveZoneInformation", 2, RegistryValueKind.DWord);
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Spynet",
                "SpynetReporting", 0, RegistryValueKind.DWord);
        }

        private static void ApplySmartScreenExplorer()
        {
            using (var expKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\Explorer"))
                expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
            using (var attKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments"))
            {
                attKey?.SetValue("SaveZoneInformation", 2, RegistryValueKind.DWord);
                attKey?.SetValue("ScanWithAntiVirus", 3, RegistryValueKind.DWord);
            }
            Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Policies\Attachments",
                "ScanWithAntiVirus", 3, RegistryValueKind.DWord);
        }
    }
}
