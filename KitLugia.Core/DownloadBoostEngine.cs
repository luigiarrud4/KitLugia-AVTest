using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class DownloadBoostEngine
    {
        public enum BoostLevel
        {
            Off = 0,
            Auto = 1,
            Download = 2,
            Latency = 3,
            Balanced = 4
        }

        public class DownloadBoostConfig
        {
            public bool Enabled { get; set; } = false;
            public BoostLevel Level { get; set; } = BoostLevel.Auto;
            public double AutoThresholdMBps { get; set; } = 5.0;
            public int AutoCooldownSec { get; set; } = 15;

            // Per-process optimizations (sempre seguras)
            public bool PerProcessPriority { get; set; } = true;
            public bool DisableEcoQoS { get; set; } = true;

            // System TCP — só aplicado quando o foreground é o downloader
            public bool SystemWideTuning { get; set; } = false;
            public bool TcpOptimization { get; set; } = true;
            public bool UseBBR2 { get; set; } = true;
            public bool RssEnable { get; set; } = true;
            public bool RscEnable { get; set; } = true;
            public bool NetworkThrottling { get; set; } = true;
            public bool NagleDisable { get; set; } = true;
            public bool DnsOptimize { get; set; } = true;
            public int MaxUserPort { get; set; } = 65534;
            public int TcpTimedWaitDelay { get; set; } = 30;

            // Contexto (preenchido pelo AutoDecide)
            public uint ForegroundPid { get; set; } = 0;
        }

        private static BoostLevel _currentLevel = BoostLevel.Off;
        private static DateTime _lastApplied = DateTime.MinValue;
        private static readonly HashSet<uint> _activeBoostedPids = new();
        private static DownloadBoostConfig? _activeConfig;
        private static readonly Dictionary<string, object?> _registryBackup = new();

        // Cache do estado anterior de cada PID (para reverter)
        private class PerProcessBackup
        {
            public ProcessPriorityClass? OriginalCpuPriority { get; set; }
            public int? OriginalIoPriority { get; set; }
            public int? OriginalPagePriority { get; set; }
            public bool EcoQoSWasEnabled { get; set; } = true;
        }
        private static readonly Dictionary<uint, PerProcessBackup> _processBackup = new();

        public static BoostLevel CurrentLevel => _currentLevel;
        public static bool IsActive => _currentLevel != BoostLevel.Off;
        public static IReadOnlySet<uint> BoostedPids => _activeBoostedPids;

        /// <summary>
        /// Aplica otimizações. Per-process sempre; system-wide só se config.SystemWideTuning=true.
        /// </summary>
        public static (bool Success, string Message) Apply(DownloadBoostConfig config, uint targetPid = 0)
        {
            _activeConfig = config;
            var steps = new List<string>();
            var errors = new List<string>();

            if (!config.Enabled || config.Level == BoostLevel.Off)
                return (false, "Download Boost desabilitado.");

            _currentLevel = config.Level;
            _lastApplied = DateTime.Now;

            if (targetPid > 0)
            {
                _activeBoostedPids.Add(targetPid);
                ApplyPerProcess(targetPid, config, steps, errors);
            }

            if (config.SystemWideTuning)
                ApplySystemTcp(config, steps, errors);
            else
                ApplyConservativeSystem(config, steps, errors);

            string tag = config.SystemWideTuning ? " [FULL]" : " [SAFE]";
            Logger.Log($"📥 Download Boost [{config.Level}{tag}]: " + string.Join(", ", steps) +
                (errors.Count > 0 ? $" | Falhas: {string.Join(", ", errors)}" : ""));

            return (errors.Count == 0, string.Join("\n", steps));
        }

        private static void ApplyPerProcess(uint pid, DownloadBoostConfig config, List<string> steps, List<string> errors)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.HasExited) return;

                var backup = new PerProcessBackup();

                // 1. CPU priority → AboveNormal (não High para não causar starvation)
                if (config.PerProcessPriority)
                {
                    try
                    {
                        backup.OriginalCpuPriority = proc.PriorityClass;
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                            steps.Add($"CPU: AboveNormal (PID {pid})");
                        }
                    }
                    catch { errors.Add("CPU priority"); }
                }

                // 2. I/O priority → High (3) — ajuda escrita em disco
                if (config.PerProcessPriority)
                {
                    try
                    {
                        IntPtr h = proc.Handle;
                        uint currentIo = 2; // Normal
                        backup.OriginalIoPriority = 2;
                        // Tenta ler o I/O priority atual
                        try { currentIo = (uint)GetProcessIoPriority(h); } catch { }
                        backup.OriginalIoPriority = (int)currentIo;
                        SetProcessIoPriority(h, 3u); // High
                        steps.Add($"I/O: High (PID {pid})");
                    }
                    catch { errors.Add("I/O priority"); }
                }

                // 3. Page priority → High (5) — mantém páginas do processo em RAM
                if (config.PerProcessPriority)
                {
                    try
                    {
                        IntPtr h = proc.Handle;
                        backup.OriginalPagePriority = 5; // default
                        try { backup.OriginalPagePriority = (int)GetProcessPagePriority(h); } catch { }
                        SetProcessPagePriority(h, 5);
                        steps.Add($"Page: High (PID {pid})");
                    }
                    catch { errors.Add("Page priority"); }
                }

                // 4. EcoQoS — desliga (impede Windows de throttlear o processo)
                if (config.DisableEcoQoS)
                {
                    try
                    {
                        var state = new PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = 1,
                            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                            StateMask = 0
                        };
                        int sz = Marshal.SizeOf(state);
                        IntPtr ptr = Marshal.AllocHGlobal(sz);
                        Marshal.StructureToPtr(state, ptr, false);
                        SetProcessInformation(proc.Handle, ProcessPowerThrottling, ptr, (uint)sz);
                        Marshal.FreeHGlobal(ptr);
                        backup.EcoQoSWasEnabled = false;
                        steps.Add($"EcoQoS: desligado (PID {pid})");
                    }
                    catch { errors.Add("EcoQoS"); }
                }

                _processBackup[pid] = backup;
            }
            catch
            {
                errors.Add($"PID {pid} não encontrado");
            }
        }

        /// <summary>
        /// TCP tuning agressivo para throughput máximo.
        /// </summary>
        private static void ApplySystemTcp(DownloadBoostConfig config, List<string> steps, List<string> errors)
        {
            // 1. Congestion provider
            if (config.TcpOptimization)
            {
                try
                {
                    if (config.UseBBR2 && Environment.OSVersion.Version.Build >= 26000)
                    {
                        SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=internet congestionprovider=bbr2", true);
                        SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=datacenter congestionprovider=bbr2", true);
                        steps.Add("TCP: BBR2 ativado");
                    }
                    else
                    {
                        SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=internet congestionprovider=ctcp", true);
                        steps.Add("TCP: CTCP ativado");
                    }
                }
                catch { errors.Add("congestion provider"); }
            }

            // 2. Auto-tuning = normal (max throughput)
            if (config.TcpOptimization)
            {
                try
                {
                    SystemUtils.RunExternalProcess("netsh", "int tcp set global autotuninglevel=normal", true);
                    steps.Add("TCP: auto-tuning=normal");
                }
                catch { errors.Add("autotuning"); }
            }

            // 3. RSS
            if (config.RssEnable)
            {
                try
                {
                    SystemUtils.RunExternalProcess("netsh", "int tcp set global rss=enabled", true);
                    using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                    key?.SetValue("EnableRSS", 1, RegistryValueKind.DWord);
                    steps.Add("RSS: habilitado");
                }
                catch { errors.Add("RSS"); }
            }

            // 4. RSC = enabled
            if (config.RscEnable)
            {
                try
                {
                    SystemUtils.RunExternalProcess("netsh", "int tcp set global rsc=enabled", true);
                    steps.Add("RSC: habilitado");
                }
                catch { errors.Add("RSC"); }
            }

            // 5. NetworkThrottling
            if (config.NetworkThrottling)
            {
                try
                {
                    using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                    if (key != null)
                    {
                        _registryBackup["NetworkThrottlingIndex"] = key.GetValue("NetworkThrottlingIndex");
                        _registryBackup["SystemResponsiveness"] = key.GetValue("SystemResponsiveness");
                        key.SetValue("NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                        key.SetValue("SystemResponsiveness", 10, RegistryValueKind.DWord);
                        steps.Add("NetworkThrottling: desabilitado");
                    }
                }
                catch { errors.Add("NetworkThrottling"); }
            }

            // 6. MaxUserPort + TcpTimedWaitDelay
            if (config.MaxUserPort > 0)
            {
                try
                {
                    using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var key = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                    if (key != null)
                    {
                        _registryBackup["MaxUserPort"] = key.GetValue("MaxUserPort");
                        _registryBackup["TcpTimedWaitDelay"] = key.GetValue("TcpTimedWaitDelay");
                        key.SetValue("MaxUserPort", config.MaxUserPort, RegistryValueKind.DWord);
                        key.SetValue("TcpTimedWaitDelay", config.TcpTimedWaitDelay, RegistryValueKind.DWord);
                        steps.Add($"MaxUserPort={config.MaxUserPort}, TcpTimedWaitDelay={config.TcpTimedWaitDelay}");
                    }
                }
                catch { errors.Add("MaxUserPort"); }
            }

            // 7. Nagle
            if (config.NagleDisable)
                ApplyNagleDisable(steps, errors);

            // 8. DNS
            if (config.DnsOptimize)
            {
                try
                {
                    SystemUtils.RunExternalProcess("ipconfig", "/flushdns", true);
                    steps.Add("DNS: cache limpo");
                }
                catch { errors.Add("DNS flush"); }
            }
        }

        /// <summary>
        /// TCP tuning conservador — só o que melhora latência sem prejudicar outros processos.
        /// </summary>
        private static void ApplyConservativeSystem(DownloadBoostConfig config, List<string> steps, List<string> errors)
        {
            // 1. Nagle disable (reduz latência de pacotes pequenos — seguro para TODOS)
            if (config.NagleDisable)
                ApplyNagleDisable(steps, errors);

            // 2. Auto-tuning = disabled (reduz bufferbloat — ajuda o foreground)
            if (config.TcpOptimization)
            {
                try
                {
                    string level = config.Level switch
                    {
                        BoostLevel.Latency => "disabled",
                        BoostLevel.Balanced => "normal",
                        _ => "disabled"
                    };
                    SystemUtils.RunExternalProcess("netsh", $"int tcp set global autotuninglevel={level}", true);
                    steps.Add($"TCP: auto-tuning={level} (modo conservador)");
                }
                catch { errors.Add("autotuning"); }
            }

            // 3. RSC = disabled (reduz latência — seguro)
            if (config.RscEnable)
            {
                try
                {
                    SystemUtils.RunExternalProcess("netsh", "int tcp set global rsc=disabled", true);
                    steps.Add("RSC: desabilitado (modo conservador)");
                }
                catch { errors.Add("RSC"); }
            }

            // Pula: BBR2, RSS, NetworkThrottling, MaxUserPort, DNS
        }

        private static void ApplyNagleDisable(List<string> steps, List<string> errors)
        {
            try
            {
                using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var tcpip = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                if (tcpip != null)
                {
                    _registryBackup["Tcp1323Opts"] = tcpip.GetValue("Tcp1323Opts");
                    tcpip.SetValue("Tcp1323Opts", 1, RegistryValueKind.DWord);
                }

                foreach (var ifKey in lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces")?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    try
                    {
                        using var iface = lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifKey}", true);
                        if (iface?.GetValue("IPAddress") != null || iface?.GetValue("DhcpIPAddress") != null)
                        {
                            iface.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                            iface.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                            iface.SetValue("TcpDelAckTicks", 0, RegistryValueKind.DWord);
                        }
                    }
                    catch { }
                }
                steps.Add("Nagle: desabilitado");
            }
            catch { errors.Add("Nagle"); }
        }

        /// <summary>
        /// Reverte tudo — per-process + system TCP.
        /// </summary>
        public static (bool Success, string Message) Revert(uint targetPid = 0)
        {
            var steps = new List<string>();

            // Reverte per-process
            if (targetPid > 0)
                RevertPerProcess(targetPid, steps);

            if (targetPid > 0)
                _activeBoostedPids.Remove(targetPid);

            // Se ainda há PIDs ativos, não reverte sistema
            if (_activeBoostedPids.Count > 0)
                return (true, "Ainda há processos com Download Boost ativo.");

            RevertSystemTcp(steps);

            _currentLevel = BoostLevel.Off;
            _activeConfig = null;

            Logger.Log("🔄 Download Boost: todas as otimizações revertidas. " + string.Join(", ", steps));
            return (true, string.Join("\n", steps));
        }

        private static void RevertPerProcess(uint pid, List<string> steps)
        {
            if (!_processBackup.TryGetValue(pid, out var backup))
                return;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (proc.HasExited) { _processBackup.Remove(pid); return; }

                if (backup.OriginalCpuPriority.HasValue)
                {
                    try { proc.PriorityClass = backup.OriginalCpuPriority.Value; } catch { }
                }

                if (backup.OriginalIoPriority.HasValue)
                {
                    try { SetProcessIoPriority(proc.Handle, (uint)backup.OriginalIoPriority.Value); } catch { }
                }

                if (backup.OriginalPagePriority.HasValue)
                {
                    try { SetProcessPagePriority(proc.Handle, (uint)backup.OriginalPagePriority.Value); } catch { }
                }

                if (!backup.EcoQoSWasEnabled)
                {
                    try
                    {
                        var state = new PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = 1,
                            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                        };
                        int sz = Marshal.SizeOf(state);
                        IntPtr ptr = Marshal.AllocHGlobal(sz);
                        Marshal.StructureToPtr(state, ptr, false);
                        SetProcessInformation(proc.Handle, ProcessPowerThrottling, ptr, (uint)sz);
                        Marshal.FreeHGlobal(ptr);
                    }
                    catch { }
                }

                steps.Add($"Per-process revertido (PID {pid})");
            }
            catch { }
            _processBackup.Remove(pid);
        }

        private static void RevertSystemTcp(List<string> steps)
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "int tcp set supplemental template=internet congestionprovider=default", true);
                SystemUtils.RunExternalProcess("netsh", "int tcp set global autotuninglevel=normal", true);
                SystemUtils.RunExternalProcess("netsh", "int tcp set global rss=enabled", true);
                SystemUtils.RunExternalProcess("netsh", "int tcp set global rsc=disabled", true);
                SystemUtils.RunExternalProcess("netsh", "int tcp set heuristics enabled", true);
                steps.Add("TCP: defaults restaurados");
            }
            catch { }

            try
            {
                using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var sysProfile = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", true);
                if (sysProfile != null)
                {
                    if (_registryBackup.TryGetValue("NetworkThrottlingIndex", out var val))
                        sysProfile.SetValue("NetworkThrottlingIndex", val ?? 10, RegistryValueKind.DWord);
                    else
                        sysProfile.SetValue("NetworkThrottlingIndex", 10, RegistryValueKind.DWord);

                    if (_registryBackup.TryGetValue("SystemResponsiveness", out var val2))
                        sysProfile.SetValue("SystemResponsiveness", val2 ?? 20, RegistryValueKind.DWord);
                    else
                        sysProfile.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
                }

                using var tcpip = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", true);
                if (tcpip != null)
                {
                    foreach (var key in new[] { "MaxUserPort", "TcpTimedWaitDelay", "Tcp1323Opts" })
                    {
                        if (_registryBackup.TryGetValue(key, out var val))
                            tcpip.SetValue(key, val ?? 0, RegistryValueKind.DWord);
                        else
                            tcpip.DeleteValue(key, false);
                    }
                }

                // Reverte Nagle per-interface
                foreach (var ifKey in lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces")?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    try
                    {
                        using var iface = lm.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{ifKey}", true);
                        if (iface?.GetValue("IPAddress") != null || iface?.GetValue("DhcpIPAddress") != null)
                        {
                            iface.DeleteValue("TcpAckFrequency", false);
                            iface.DeleteValue("TCPNoDelay", false);
                            iface.DeleteValue("TcpDelAckTicks", false);
                        }
                    }
                    catch { }
                }

                steps.Add("Registry: restaurado");
            }
            catch { }
            _registryBackup.Clear();
        }

        /// <summary>
        /// Decide quais PIDs receberão boost baseado no tráfego + foreground.
        /// </summary>
        public static List<uint> AutoDecide(NetworkTrafficMonitor.TrafficSnapshot snapshot, DownloadBoostConfig config)
        {
            var targets = new List<uint>();

            if (!config.Enabled || config.Level != BoostLevel.Auto)
                return targets;

            uint foregroundPid = config.ForegroundPid > 0 ? config.ForegroundPid :
                                 (snapshot.ForegroundPid ?? 0);

            var downloaders = NetworkTrafficMonitor.GetActiveDownloaders(snapshot, config.AutoThresholdMBps);

            foreach (var proc in downloaders)
            {
                bool isForeground = proc.Pid == foregroundPid;
                bool isHeavyNetwork = proc.ActiveConnections > 20;

                if (isForeground || isHeavyNetwork)
                {
                    targets.Add(proc.Pid);

                    // Só aplica system-wide TCP se o foreground for o próprio downloader
                    if (isForeground)
                        config.SystemWideTuning = true;

                    Logger.Log($"📥 Download Boost [AUTO]: {proc.ProcessName} (PID {proc.Pid}) — " +
                        $"{proc.ReadSpeedMBps:F1} MB/s down, {proc.ActiveConnections} conexões" +
                        (isForeground ? " [FULL]" : " [SAFE]"));
                }
            }

            return targets;
        }

        // === P/Invoke ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass, IntPtr processInformation, uint processInformationSize);

        public const int ProcessPowerThrottling = 2;
        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessIoPriority(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetProcessIoPriority(IntPtr hProcess, uint priority);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetProcessPagePriority(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetProcessPagePriority(IntPtr hProcess, uint priority);
    }
}
