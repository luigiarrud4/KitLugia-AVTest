using System;
using System.Drawing;
using System.Diagnostics;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using KitLugia.Core;
using Microsoft.Win32.TaskScheduler;
using System.IO;
using System.Text;
using Application = System.Windows.Application;
using Timer = System.Windows.Threading.DispatcherTimer;

namespace KitLugia.GUI.Services
{
    // Helper: logs first failure per operation, then stays silent until success resumes
    public static class ConditionalLog
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _lastFailed = new();

        public static void Try(string key, System.Action action)
        {
            try { action(); _lastFailed[key] = false; }
            catch (Exception ex) { LogOnce(key, ex); }
        }

        public static void LogOnce(string key, Exception ex)
        {
            if (!_lastFailed.TryGetValue(key, out bool lastFailed) || !lastFailed)
            {
                _lastFailed[key] = true;
                KitLugia.Core.Logger.Log($"⚠️ {key}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        public static void Reset(string key) => _lastFailed.TryRemove(key, out _);
    }

    public static class Win32Api
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        public const uint GW_OWNER = 4;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        // --- Hybrid CPU Detection (P-cores + E-cores) ---
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(int RelationshipType, IntPtr Buffer, ref uint ReturnedLength);

        public const int RelationProcessorCore = 0;

        public static bool IsCpuHybrid()
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return false;
                uint size = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
                if (size == 0) return false;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size))
                        return false;
                    IntPtr ptr = buffer;
                    uint remaining = size;
                    while (remaining > 0)
                    {
                        int relationship = Marshal.ReadInt32(ptr);
                        uint entrySize = (uint)Marshal.ReadInt32(ptr, 4);
                        if (relationship == RelationProcessorCore)
                        {
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                            if (efficiencyClass > 0) return true;
                        }
                        remaining -= entrySize;
                        ptr += (int)entrySize;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { }
            return false;
        }

        // === Extensões Multi-Layer Accelerator (ntdll) ===
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetInformationProcess(IntPtr processHandle, int processInformationClass, ref int processInformation, int processInformationLength);

        private const int ProcessIoPriority = 33;
        private const int ProcessPagePriority = 39;

        public static void SetProcessIoPriority(IntPtr handle, int priorityHint)
        {
            ConditionalLog.Try("SetProcessIoPriority", () =>
            { int pInfo = priorityHint; NtSetInformationProcess(handle, ProcessIoPriority, ref pInfo, sizeof(int)); });
        }

        public static void SetProcessPagePriority(IntPtr handle, int pagePriority)
        {
            ConditionalLog.Try("SetProcessPagePriority", () =>
            { int pInfo = pagePriority; NtSetInformationProcess(handle, ProcessPagePriority, ref pInfo, sizeof(int)); });
        }

        // === SetWinEventHook (GameBoost instantâneo) ===
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const uint WINEVENT_SKIPOWNTHREAD = 0x0004;

        // === EcoQoS (Windows 11 Power Throttling) ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

        public const int ProcessPowerThrottling = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_POWER_THROTTLING_STATE { public uint Version; public uint ControlMask; public uint StateMask; }

        public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        // === Thread Memory Priority ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetThreadInformation(IntPtr hThread, int ThreadInformationClass, IntPtr ThreadInformation, uint ThreadInformationSize);

        public const int ThreadMemoryPriority = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_PRIORITY_INFORMATION { public uint MemoryPriority; }

        public const uint MEMORY_PRIORITY_VERY_LOW = 1;
        public const uint MEMORY_PRIORITY_LOW = 2;
        public const uint MEMORY_PRIORITY_MEDIUM = 3;
        public const uint MEMORY_PRIORITY_BELOW_NORMAL = 4;
        public const uint MEMORY_PRIORITY_NORMAL = 5;

        public static void SetThreadMemoryPriority(IntPtr threadHandle, uint priority)
        {
            ConditionalLog.Try("SetThreadMemoryPriority", () =>
            {
                var memPrio = new MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(memPrio));
                Marshal.StructureToPtr(memPrio, ptr, false);
                SetThreadInformation(threadHandle, ThreadMemoryPriority, ptr, (uint)Marshal.SizeOf(memPrio));
                Marshal.FreeHGlobal(ptr);
            });
        }

        // === Thread Efficiency Mode (P-Cores Only, Win11 24H2+) ===
        public const int ThreadEfficiencyMode = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct THREAD_EFFICIENCY_MODE { public byte UseEfficiencyClass; }

        public static void SetThreadEfficiencyMode(IntPtr threadHandle, bool useEfficiencyClass)
        {
            if (Environment.OSVersion.Version.Build < 26100) return;
            ConditionalLog.Try("SetThreadEfficiencyMode", () =>
            {
                var mode = new THREAD_EFFICIENCY_MODE { UseEfficiencyClass = useEfficiencyClass ? (byte)1 : (byte)0 };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(mode));
                try { Marshal.StructureToPtr(mode, ptr, false); SetThreadInformation(threadHandle, ThreadEfficiencyMode, ptr, (uint)Marshal.SizeOf(mode)); }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        // === Game Mode (SetProcessGameClassInfo) ===
        public const int ProcessGameClassInfo = 13;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_GAME_CLASS_INFO { public uint GameMode; public uint GameModeFlags; }

        public static void SetProcessGameClassInfo(IntPtr processHandle, bool enableGameMode)
        {
            ConditionalLog.Try("SetProcessGameClassInfo", () =>
            {
                var info = new PROCESS_GAME_CLASS_INFO { GameMode = enableGameMode ? 1u : 0u, GameModeFlags = 0 };
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
                try { Marshal.StructureToPtr(info, ptr, false); SetProcessInformation(processHandle, ProcessGameClassInfo, ptr, (uint)Marshal.SizeOf(info)); }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        // === Thread/Process helpers ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        public const uint THREAD_SET_INFORMATION = 0x0020;
        public const uint THREAD_QUERY_INFORMATION = 0x0040;

        // === Win32PrioritySeparation Registry ===
        public static void SetWin32PrioritySeparation(bool enableForegroundBoost)
        {
            ConditionalLog.Try("SetWin32PrioritySeparation", () =>
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl", true);
                if (key == null) return;
                if (enableForegroundBoost)
                    key.SetValue("Win32PrioritySeparation", 38, Microsoft.Win32.RegistryValueKind.DWord);
                else
                    key.SetValue("Win32PrioritySeparation", 2, Microsoft.Win32.RegistryValueKind.DWord);
            });
        }

        // === Timer Resolution (NtSetTimerResolution) ===
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetTimerResolution(int DesiredResolution, bool SetResolution, out int CurrentResolution);

        private static int _originalTimerResolution = 0;
        private static bool _timerResolutionChanged = false;

        public static void BoostTimerResolution()
        {
            if (_timerResolutionChanged) return;
            ConditionalLog.Try("BoostTimerResolution", () =>
            {
                NtSetTimerResolution(0, false, out _originalTimerResolution);
                // 1ms (10000) em vez de 0.5ms (5000) — 0.5ms causa estouro/popping
                // em dispositivos de áudio virtual (Voicemeeter, VB-Cable etc.) porque
                // aumenta latência DPC/ISR e causa underruns no buffer de software.
                // 1ms é seguro para áudio e ainda melhora performance em jogos.
                int desired = 10000;
                int result = NtSetTimerResolution(desired, true, out int current);
                if (result == 0)
                {
                    _timerResolutionChanged = true;
                    Logger.Log($"⏱️ Timer Resolution: {_originalTimerResolution / 10000.0:F2}ms → {current / 10000.0:F2}ms (boosted)");
                }
            });
        }

        public static void RestoreTimerResolution()
        {
            if (!_timerResolutionChanged) return;
            ConditionalLog.Try("RestoreTimerResolution", () =>
            {
                NtSetTimerResolution(_originalTimerResolution, true, out int current);
                _timerResolutionChanged = false;
                Logger.Log($"⏱️ Timer Resolution restaurado: {current / 10000.0:F2}ms");
            });
        }

        // === Privilege Elevation ===
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetThreadToken(IntPtr ThreadHandle, IntPtr TokenHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public long Luid; public uint Attributes; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_IMPERSONATE = 0x0004;
        public const uint TOKEN_QUERY = 0x0008;
        public const int SecurityImpersonation = 2;
        public const int TokenImpersonation = 2;

        // === Toolhelp32 Thread Enumeration ===
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32First(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Thread32Next(IntPtr hSnapshot, ref THREADENTRY32 lpte);

        public const uint TH32CS_SNAPTHREAD = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        public struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public uint tpBasePri;
            public uint tpDeltaPri;
            public uint dwFlags;
        }

        public static List<uint> GetThreadIds(uint targetPid)
        {
            var threadIds = new List<uint>();
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1)) return threadIds;
            try
            {
                var entry = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                if (Thread32First(snapshot, ref entry))
                {
                    do { if (entry.th32OwnerProcessID == targetPid) threadIds.Add(entry.th32ThreadID); }
                    while (Thread32Next(snapshot, ref entry));
                }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetThreadIds", ex); }
            finally { CloseHandle(snapshot); }
            return threadIds;
        }

        public static void SetThreadEfficiencyForAllThreads(uint pid, bool useEfficiencyClass)
        {
            if (Environment.OSVersion.Version.Build < 26100) return;
            var threadIds = GetThreadIds(pid);
            foreach (uint tid in threadIds)
            {
                IntPtr hThread = IntPtr.Zero;
                try
                {
                    hThread = OpenThread(THREAD_SET_INFORMATION | THREAD_QUERY_INFORMATION, false, tid);
                    if (hThread != IntPtr.Zero) SetThreadEfficiencyMode(hThread, useEfficiencyClass);
                }
                catch (Exception ex) { ConditionalLog.LogOnce("SetThreadEfficiencyForAllThreads", ex); }
                finally { if (hThread != IntPtr.Zero) CloseHandle(hThread); }
            }
        }

        // === Thread analysis for P-Cores Only detection ===
        public const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetThreadIdealProcessorEx(IntPtr hThread, out PROCESSOR_NUMBER lpIdealProcessor);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_NUMBER { public ushort Group; public byte Number; public byte Reserved; }

        public static HashSet<int> GetECoreProcessorNumbers()
        {
            var result = new HashSet<int>();
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return result;
                uint size = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref size);
                if (size == 0) return result;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref size)) return result;
                    IntPtr ptr = buffer;
                    uint remaining = size;
                    while (remaining > 0)
                    {
                        int relationship = Marshal.ReadInt32(ptr);
                        uint entrySize = (uint)Marshal.ReadInt32(ptr, 4);
                        if (relationship == RelationProcessorCore)
                        {
                            byte efficiencyClass = Marshal.ReadByte(ptr, 9);
                            if (efficiencyClass > 0)
                            {
                                ulong mask = (ulong)Marshal.ReadIntPtr(ptr + 32);
                                for (int i = 0; i < 64; i++) { if ((mask & (1UL << i)) != 0) result.Add(i); }
                            }
                        }
                        remaining -= entrySize;
                        ptr += (int)entrySize;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetECoreProcessorNumbers", ex); }
            return result;
        }

        public static int GetThreadCountOnECores(uint processId)
        {
            int count = 0;
            try
            {
                var eCores = GetECoreProcessorNumbers();
                if (eCores.Count == 0) return 0;
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
                if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1)) return 0;
                try
                {
                    var te = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
                    if (!Thread32First(snapshot, ref te)) return 0;
                    do
                    {
                        if (te.th32OwnerProcessID == processId)
                        {
                            IntPtr hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, te.th32ThreadID);
                            if (hThread == IntPtr.Zero) continue;
                            try { if (GetThreadIdealProcessorEx(hThread, out var procNum) && eCores.Contains(procNum.Number)) count++; }
                            finally { CloseHandle(hThread); }
                        }
                    }
                    while (Thread32Next(snapshot, ref te));
                }
                finally { CloseHandle(snapshot); }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetThreadCountOnECores", ex); }
            return count;
        }

        // === Job Object CPU Rate Control (hard cap per-process) ===
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        public const int JobObjectCpuRateControlInformation = 15;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
        public const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            public uint ControlFlags;
            public uint CpuRate;
        }
    }

    public class TrayIconService : IDisposable
    {
        private NotifyIcon? _trayIcon;
        private DispatcherTimer _monitorTimer;
        private Icon? _currentIcon;
        

        public bool GameBarPresenceWriterDisabled { get; set; } = false;

        // RAM Limiter - Variáveis e configurações
        private DispatcherTimer? _ramLimiterTimer;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessRamLimit> _processRamLimits = new();
        private int _ramLimiterIntervalMs = 1000; // Intervalo em milissegundos
        private readonly Dictionary<string, IntPtr> _cpuJobObjects = new();
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessInfo> _processCache = new();
        private DispatcherTimer? _advancedMonitorTimer;
        private int _advancedMonitorIntervalMs = 2000; // 2 segundos para monitor avançado
        private long _totalSystemRamMB = 0;
        private long _availableRamMB = 0;
        private double _currentCpuUsage = 0;
        private int _activeProcessCount = 0;
        private DateTime _lastSystemStatsUpdate = DateTime.MinValue;
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessAlert> _processAlerts = new();
        private bool _enableSmartAlerts = true;
        private long _highRamThresholdMB = 2048; // 2GB para alerta
        private double _highCpuThresholdPercent = 80.0; // 80% CPU para alerta
        

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessBehavior> _processBehaviors = new();
        private bool _enableBehaviorAnalysis = true;
        

        private int _consecutiveErrors = 0;
        private DateTime _lastErrorTime = DateTime.MinValue;
        private readonly TimeSpan _errorCooldown = TimeSpan.FromMinutes(5);
        private readonly int _maxConsecutiveErrors = 3;
        private bool _isInSafeMode = false;
        private readonly object _robustnessLock = new object();
        

        private static readonly string _backupPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "process_ram_limits_backup.json");
        private DateTime _lastBackupTime = DateTime.MinValue;
        private readonly TimeSpan _backupInterval = TimeSpan.FromHours(1);
        public int RamLimiterIntervalMs
        {
            get => _ramLimiterIntervalMs;
            set
            {
                _ramLimiterIntervalMs = Math.Max(500, value); // Mínimo 500ms
                if (_ramLimiterTimer != null)
                {
                    _ramLimiterTimer.Interval = TimeSpan.FromMilliseconds(_ramLimiterIntervalMs);
                }
            }
        }
        

        public int AdvancedMonitorIntervalMs
        {
            get => _advancedMonitorIntervalMs;
            set
            {
                _advancedMonitorIntervalMs = Math.Max(1000, value); // Mínimo 1s
                if (_advancedMonitorTimer != null)
                {
                    _advancedMonitorTimer.Interval = TimeSpan.FromMilliseconds(_advancedMonitorIntervalMs);
                }
            }
        }
        
        public bool EnableSmartAlerts
        {
            get => _enableSmartAlerts;
            set => _enableSmartAlerts = value;
        }
        
        public bool EnableBehaviorAnalysis
        {
            get => _enableBehaviorAnalysis;
            set => _enableBehaviorAnalysis = value;
        }
        
        public long HighRamThresholdMB
        {
            get => _highRamThresholdMB;
            set => _highRamThresholdMB = Math.Max(512, value); // Mínimo 512MB
        }
        
        public double HighCpuThresholdPercent
        {
            get => _highCpuThresholdPercent;
            set => _highCpuThresholdPercent = Math.Max(10.0, Math.Min(100.0, value)); // 10-100%
        }
        

        public long TotalSystemRamMB => _totalSystemRamMB;
        public long AvailableRamMB => _availableRamMB;
        public double CurrentCpuUsage => _currentCpuUsage;
        public int ActiveProcessCount => _activeProcessCount;
        public double RamUsagePercent => _totalSystemRamMB > 0 ? ((_totalSystemRamMB - _availableRamMB) * 100.0 / _totalSystemRamMB) : 0;
        private class ProcessProfile
        {
            public string Name { get; set; } = "";
            public int TotalCyclesVisible { get; set; } = 0;
            public int CyclesForeground { get; set; } = 0;
            public bool IsVip { get; set; } = false;
            public DateTime LastTrimTime { get; set; } = DateTime.MinValue;
            public long LastKnownWs { get; set; } = 0;
        }


        // Típico: 20-50 perfis de processos em cache
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessProfile> _processProfiles = new(concurrencyLevel: 4, capacity: 50, comparer: StringComparer.OrdinalIgnoreCase);


        private Process[]? _cachedProcesses;
        private DateTime _lastProcessCacheTime = DateTime.MinValue;
        private readonly TimeSpan _processCacheLifetime = TimeSpan.FromSeconds(5); // Cache por 5 segundos
        private readonly object _processCacheLock = new();


        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime Timestamp, TimeSpan CpuTime)> _cpuTimeCache = new();

        // Settings
        public bool AutoCleanEnabled { get; set; } = false;
        public int AutoCleanThresholdPercent { get; set; } = 80;
        private int _monitorIntervalSeconds = 60;
        public int MonitorIntervalSeconds
        {
            get => _monitorIntervalSeconds;
            set
            {

                _monitorIntervalSeconds = Math.Max(5, value);
                if (_monitorTimer != null)
                {
                    _monitorTimer.Interval = TimeSpan.FromSeconds(_monitorIntervalSeconds);
                }
            }
        }
        public MemoryOptimizer.CleaningMode SelectedCleaningMode { get; set; } = MemoryOptimizer.CleaningMode.Normal;

        // Background Features
        public bool GamePriorityEnabled { get; set; } = false;
        public bool StandbyCleanEnabled { get; set; } = false;
        public bool MemoryLeakDetectionEnabled { get; set; } = false;
        public bool DpcMonitorEnabled { get; set; } = false;
        public bool FocusAssistEnabled { get; set; } = false;
        public bool TimerBoost { get; set; } = false;
        public bool NetworkBoost { get; set; } = false;
        public bool ProBalance { get; set; } = false;
        public bool TurboBootEnabled
        {
            get => SystemTweaks.IsTurboBootEnabled();
            set => SystemTweaks.ToggleTurboBoot(value);
        }
        public bool TurboShutdownEnabled
        {
            get => SystemTweaks.IsFastShutdownEnabled();
            set => SystemTweaks.ToggleFastShutdown();
        }

        // Tray active state
        public bool IsTrayEnabled { get; set; } = false;
        
        // Close to Tray (minimizar ao invés de fechar)
        public bool CloseToTray { get; set; } = true;

        public static bool IsTrayEnabledStatic()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                return (int)(key?.GetValue("IsTrayEnabled", 0) ?? 0) == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// Verifica se o auto-start está habilitado e se o caminho da tarefa corresponde à versão atual
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentPath)) return false;

                using (var ts = new TaskService())
                {
                    var task = ts.GetTask("KitLugia");
                    if (task == null) return false;

                    // Verificar se a tarefa está habilitada
                    if (!task.Enabled) return false;

                    // Verificar se o caminho do executável corresponde à versão atual
                    foreach (var action in task.Definition.Actions)
                    {
                        if (action is ExecAction execAction)
                        {
                            string taskPath = execAction.Path;
                            if (string.Equals(taskPath, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                // KitLugia.Core.Logger.Log($"✅ Auto-Start habilitado com caminho correto: {currentPath}");
                                return true;
                            }
                            else
                            {
                                KitLugia.Core.Logger.Log($"⚠️ Auto-Start aponta para versão antiga: {taskPath} != {currentPath}");
                                return false;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("IsAutoStartEnabled", $"Erro: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Remove tarefa antiga se o caminho não corresponder à versão atual
        /// </summary>
        private static void CleanupOldTask()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentPath)) return;

                using (var ts = new TaskService())
                {
                    var task = ts.GetTask("KitLugia");
                    if (task == null) return;

                    // Verificar se o caminho corresponde
                    bool pathMatches = false;
                    foreach (var action in task.Definition.Actions)
                    {
                        if (action is ExecAction execAction)
                        {
                            if (string.Equals(execAction.Path, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                pathMatches = true;
                                break;
                            }
                        }
                    }

                    if (!pathMatches)
                    {
                        KitLugia.Core.Logger.Log("🧹 Removendo tarefa antiga com caminho incorreto...");
                        ts.RootFolder.DeleteTask("KitLugia");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CleanupOldTask", $"Erro: {ex.Message}");
            }
        }

        public static void SetAutoStart(bool enable)
        {
            try
            {
                string path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(path)) return;


                CleanupOldTask();


                try
                {
                    using (var ts = new TaskService())
                    {
                        if (enable)
                        {
                            // Remover entrada antiga do Registry se existir
                            try
                            {
                                using var regKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                                if (regKey?.GetValue("KitLugia") != null)
                                {
                                    KitLugia.Core.Logger.Log("Removendo entrada antiga do Registry...");
                                    regKey.DeleteValue("KitLugia", false);
                                }
                            }
                            catch { }

                            // Verificar se tarefa já existe com caminho correto
                            var existingTask = ts.GetTask("KitLugia");
                            if (existingTask != null)
                            {
                                // Verificar se o caminho já está correto
                                bool pathMatches = false;
                                foreach (var action in existingTask.Definition.Actions)
                                {
                                    if (action is ExecAction execAction)
                                    {
                                        if (string.Equals(execAction.Path, path, StringComparison.OrdinalIgnoreCase))
                                        {
                                            pathMatches = true;
                                            break;
                                        }
                                    }
                                }

                                if (pathMatches)
                                {
                                    KitLugia.Core.Logger.Log("✅ Tarefa já existe com caminho correto, apenas habilitando...");
                                    existingTask.Enabled = true;
                                    existingTask.RegisterChanges();
                                    KitLugia.Core.Logger.Log("✅ Tarefa agendada habilitada: " + path);
                                    return;
                                }
                                else
                                {
                                    KitLugia.Core.Logger.Log("🔄 Tarefa existe com caminho incorreto, recriando...");
                                    ts.RootFolder.DeleteTask("KitLugia");
                                }
                            }

                            // Criar nova tarefa com privilégios admin
                            var td = ts.NewTask();
                            td.RegistrationInfo.Description = "KitLugia Auto-Startup (Admin Mode)";
                            td.Principal.RunLevel = TaskRunLevel.Highest;
                            td.Settings.DisallowStartIfOnBatteries = false;
                            td.Settings.StopIfGoingOnBatteries = false;
                            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                            td.Settings.StartWhenAvailable = true;
                            td.Settings.AllowHardTerminate = false;

                            // Trigger: Logon imediato para inicialização rápida
                            var trigger = new LogonTrigger
                            {
                                Delay = TimeSpan.Zero,
                                Enabled = true
                            };
                            td.Triggers.Add(trigger);

                            // Action: Executar com --tray
                            td.Actions.Add(new ExecAction(path, "--tray", Path.GetDirectoryName(path)));

                            // Registrar tarefa
                            ts.RootFolder.RegisterTaskDefinition("KitLugia", td);
                            KitLugia.Core.Logger.Log("✅ Tarefa agendada com privilégios admin criada: " + path);
                        }
                        else
                        {
                            // Remover tarefa
                            var task = ts.GetTask("KitLugia");
                            if (task != null)
                            {
                                ts.RootFolder.DeleteTask("KitLugia");
                                KitLugia.Core.Logger.Log("✅ Tarefa agendada removida");
                            }
                        }
                    }
                }
                catch (Exception taskEx)
                {
                    KitLugia.Core.Logger.Log($"ERRO no Task Scheduler: {taskEx.Message}");

                    // Fallback para Registry (sem privilégios admin)
                    KitLugia.Core.Logger.Log("Usando fallback Registry Run (sem privilégios admin)...");
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                    if (key != null)
                    {
                        if (enable)
                        {
                            key.SetValue("KitLugia", $"\"{path}\" --tray");
                        }
                        else
                        {
                            key.DeleteValue("KitLugia", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"SetAutoStart ERROR: {ex.Message}");
            }
        }

        // Adaptive Data
        private readonly string[] _vipProcesses = { "opera", "discord", "taskmgr", "devenv", "kitlugia", "steam", "riotclient" };
        private long _stutterBackoffCycles = 0;
        private long _lastCleanDurationMs = 0;

        // LocalApplicationData não depende de Roaming e é mais seguro
        private string _logPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "ram_stats.csv");

        private bool _seDebugEnabled;

        private void EnsureSeDebugPrivilege()
        {
            if (_seDebugEnabled) return;
            _seDebugEnabled = true;
            EnableSeDebugPrivilege();
        }

        public event System.Action? OnOpenMainWindow;
        public event System.Action? OnOpenSettings;

        public TrayIconService()
        {

            _instance = this;

            _monitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(MonitorIntervalSeconds)
            };
            _monitorTimer.Tick += MonitorTick;
        }


        private void EnableSeDebugPrivilege()
        {
            try
            {
                IntPtr hToken;
                if (!Win32Api.OpenProcessToken(Process.GetCurrentProcess().Handle, Win32Api.TOKEN_ADJUST_PRIVILEGES | Win32Api.TOKEN_QUERY, out hToken))
                {
                    KitLugia.Core.Logger.Log("⚠️ Falha ao abrir token do processo");
                    return;
                }

                try
                {
                    long luid;
                    if (!Win32Api.LookupPrivilegeValue(string.Empty, "SeDebugPrivilege", out luid))
                    {
                        KitLugia.Core.Logger.Log("⚠️ Falha ao obter LUID do SeDebugPrivilege");
                        return;
                    }

                    var tp = new Win32Api.TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = Win32Api.SE_PRIVILEGE_ENABLED
                    };

                    if (!Win32Api.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                    {
                        KitLugia.Core.Logger.Log("⚠️ Falha ao ajustar privilégio SeDebugPrivilege");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("✅ SeDebugPrivilege habilitado com sucesso");
                    }
                }
                finally
                {
                    Win32Api.CloseHandle(hToken);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao habilitar SeDebugPrivilege: {ex.Message}");
            }
        }


        private Process[] GetCachedProcesses()
        {
            lock (_processCacheLock)
            {
                if (_cachedProcesses != null && DateTime.Now - _lastProcessCacheTime < _processCacheLifetime)
                {
                    return _cachedProcesses;
                }

                // Descartar cache antigo se existir
                if (_cachedProcesses != null)
                {
                    foreach (var proc in _cachedProcesses)
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }

                _cachedProcesses = Process.GetProcesses();
                _lastProcessCacheTime = DateTime.Now;
                return _cachedProcesses;
            }
        }


        private void ClearProcessCache()
        {
            lock (_processCacheLock)
            {
                if (_cachedProcesses != null)
                {
                    foreach (var proc in _cachedProcesses)
                    {
                        try { proc.Dispose(); } catch { }
                    }
                    _cachedProcesses = null;
                }
            }
        }

        public void Initialize()
        {
            LoadSettings();

            System.Threading.Tasks.Task.Run(() => AutoFixGameBarPresenceWriter());


            try
            {
                _trayIcon = new NotifyIcon
                {
                    Text = "KitLugia RAM Monitor",
                    Visible = false
                };
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"ERRO ao criar NotifyIcon: {ex.Message}");
                return;
            }

            // Generate the initial icon
            UpdateTrayIcon(0);

            // Context Menu
            var menu = new ContextMenuStrip();

            var itemClean = new ToolStripMenuItem("🧹 Limpar RAM Agora");
            itemClean.Click += (s, e) => CleanRamNow();
            menu.Items.Add(itemClean);

            menu.Items.Add(new ToolStripSeparator());

            var itemAutoClean = new ToolStripMenuItem($"⚡ Auto-Limpeza ({AutoCleanThresholdPercent}%)");
            itemAutoClean.Checked = AutoCleanEnabled;
            itemAutoClean.Click += (s, e) =>
            {
                AutoCleanEnabled = !AutoCleanEnabled;
                itemAutoClean.Checked = AutoCleanEnabled;
                SaveSettings();
            };
            menu.Items.Add(itemAutoClean);

            menu.Items.Add(new ToolStripSeparator());


            var itemGameBoost = new ToolStripMenuItem("🚀 GameBoost Pro");
            itemGameBoost.Checked = GamePriorityEnabled;
            itemGameBoost.Click += (s, e) =>
            {
                GamePriorityEnabled = !GamePriorityEnabled;
                itemGameBoost.Checked = GamePriorityEnabled;
                if (GamePriorityEnabled) EnsureSeDebugPrivilege();
                SaveSettings();
                KitLugia.Core.Logger.Log($"🚀 GameBoost {(GamePriorityEnabled ? "ativado" : "desativado")} via Tray Icon");
            };
            menu.Items.Add(itemGameBoost);

            menu.Items.Add(new ToolStripSeparator());

            // Boot Tray
            int bootAppCount = 0;
            try
            {
                using var bootKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\StartupApps");
                if (bootKey != null) bootAppCount = bootKey.ValueCount;
            }
            catch { }
            string bootCountStr = bootAppCount > 0 ? $"{bootAppCount} apps" : "vazio";
            var itemBootTrayAdmin = new ToolStripMenuItem($"🛡️ Boot Tray: Iniciar (Admin)");
            itemBootTrayAdmin.ToolTipText = $"Inicia os apps do Boot Tray com privilégios de Administrador ({bootCountStr})";
            itemBootTrayAdmin.Click += (s, e) =>
            {
                try { StartupManager.LaunchTurboApps(); }
                catch (Exception ex) { KitLugia.Core.Logger.LogError("BootTrayAdmin", ex.Message); }
            };
            menu.Items.Add(itemBootTrayAdmin);

            var itemBootTrayNormal = new ToolStripMenuItem($"👤 Boot Tray: Iniciar (Normal - Sem Admin)");
            itemBootTrayNormal.ToolTipText = $"Inicia os apps do Boot Tray como usuário normal via tarefa agendada ({bootCountStr})";
            itemBootTrayNormal.Click += (s, e) =>
            {
                try { StartupManager.LaunchTurboAppsNonAdmin(); }
                catch (Exception ex) { KitLugia.Core.Logger.LogError("BootTrayNormal", ex.Message); }
            };
            menu.Items.Add(itemBootTrayNormal);

            var itemBootTrayManager = new ToolStripMenuItem($"📋 Gerenciar Boot Tray ({bootCountStr})");
            itemBootTrayManager.Click += (s, e) => OnOpenSettings?.Invoke();
            menu.Items.Add(itemBootTrayManager);

            menu.Items.Add(new ToolStripSeparator());

            var itemSettings = new ToolStripMenuItem("⚙ Configurações");
            itemSettings.Click += (s, e) => OnOpenSettings?.Invoke();
            menu.Items.Add(itemSettings);

            var itemOpen = new ToolStripMenuItem("🚀 Abrir KitLugia");
            itemOpen.Font = new Font(itemOpen.Font, FontStyle.Bold);
            itemOpen.Click += (s, e) => OnOpenMainWindow?.Invoke();
            menu.Items.Add(itemOpen);

            menu.Items.Add(new ToolStripSeparator());

            var itemRestartAdmin = new ToolStripMenuItem("🛡️ Iniciar como Admin");
            itemRestartAdmin.Click += (s, e) =>
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { }
            };
            menu.Items.Add(itemRestartAdmin);

            var itemRestartNormal = new ToolStripMenuItem("👤 Iniciar como Usuário Normal");
            itemRestartNormal.Click += (s, e) =>
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule.FileName;
                    KitLugia.Core.StartupManager.RegisterNonAdminTask("__KitLugiaRestart", exe, null);
                    KitLugia.Core.StartupManager.RunNonAdminTask("__KitLugiaRestart");
                    System.Threading.Thread.Sleep(1500);
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { }
            };
            menu.Items.Add(itemRestartNormal);

            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("❌ Sair Completamente");
            itemExit.Click += (s, e) =>
            {
                Dispose();
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            };
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;

            // Double-click to open main window
            _trayIcon.DoubleClick += (s, e) => OnOpenMainWindow?.Invoke();


            if (IsTrayEnabled && _trayIcon != null)
            {
                try
                {
                    // Força atualização do ícone antes de tornar visível
                    UpdateTrayIcon(GetMemoryUsagePercent());

                    // Torna visível e verifica
                    _trayIcon.Visible = true;


                    if (_trayIcon.Visible)
                    {
                        // Start monitoring (first tick fires at interval)
                        _monitorTimer.Start();

                        // Defer heavy init to after icon is visible
                        Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
                        {
                            LoadProcessLimits();
                            ShowTrayStatusReport();
                            RunSafetyProfiler();
                            MonitorTick(null, EventArgs.Empty);
                        }), DispatcherPriority.Background);
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("❌ ERRO: Tray Icon não ficou visível após tentativa");
                    }
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"❌ ERRO ao ativar Tray Icon: {ex.Message}");
                }
            }
            else
            {
                KitLugia.Core.Logger.Log($"Tray Icon desativado ou nulo. Enabled: {IsTrayEnabled}, Icon: {_trayIcon != null}");
            }

            // Register for Shutdown events
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => ShutdownTurboCharge();


            if (GamePriorityEnabled)
            {
                InitializeGameBoost();
            }
        }

        public void ShutdownTurboCharge()
        {
            try
            {
                // Active Shutdown Charge (WM_CLOSE broadcast)
                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        if (proc.MainWindowHandle != IntPtr.Zero)
                        {
                            Win32Api.SendMessage(proc.MainWindowHandle, Win32Api.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\TraySettings");
                key.SetValue("IsTrayEnabled", IsTrayEnabled ? 1 : 0);
                key.SetValue("CloseToTray", CloseToTray ? 1 : 0);
                key.SetValue("AutoCleanEnabled", AutoCleanEnabled ? 1 : 0);
                key.SetValue("Threshold", AutoCleanThresholdPercent);
                key.SetValue("Interval", MonitorIntervalSeconds);
                key.SetValue("CleaningMode", (int)SelectedCleaningMode);
                key.SetValue("GamePriority", GamePriorityEnabled ? 1 : 0);
                key.SetValue("StandbyClean", StandbyCleanEnabled ? 1 : 0);
                key.SetValue("AntiLeak", MemoryLeakDetectionEnabled ? 1 : 0);
                key.SetValue("FocusAssist", FocusAssistEnabled ? 1 : 0);
                key.SetValue("TimerBoost", TimerBoost ? 1 : 0);
                key.SetValue("NetworkBoost", NetworkBoost ? 1 : 0);
                key.SetValue("ProBalance", ProBalance ? 1 : 0);
                key.SetValue("TurboBoot", TurboBootEnabled ? 1 : 0);
                key.SetValue("TurboShutdown", TurboShutdownEnabled ? 1 : 0);
                

                key.SetValue("EnableSmartAlerts", EnableSmartAlerts ? 1 : 0);
                key.SetValue("EnableBehaviorAnalysis", EnableBehaviorAnalysis ? 1 : 0);
                key.SetValue("HighRamThresholdMB", HighRamThresholdMB);
                key.SetValue("HighCpuThresholdPercent", HighCpuThresholdPercent);
                key.SetValue("AdvancedMonitorIntervalMs", AdvancedMonitorIntervalMs);
                key.SetValue("RamLimiterIntervalMs", RamLimiterIntervalMs);
                key.SetValue("GameBarPresenceWriterDisabled", GameBarPresenceWriterDisabled ? 1 : 0);
            }
            catch { }
        }

        /// <summary>
        /// Auto-fix: se o usuário desativou GameBarPresenceWriter antes e o Windows recriou o .exe, re-desativa
        /// </summary>
        private void AutoFixGameBarPresenceWriter()
        {
            if (!GameBarPresenceWriterDisabled) return;

            try
            {
                string system32 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32");
                string gameBarPath = Path.Combine(system32, "GameBarPresenceWriter.exe");
                string backupPath = Path.Combine(system32, "GameBarPresenceWriter.exe.bak");

                // Se o .exe existe E o .bak também, Windows recriou - precisa re-desativar
                if (File.Exists(gameBarPath) && File.Exists(backupPath))
                {
                    KitLugia.Core.Logger.Log("⚠️ GameBarPresenceWriter: Windows recriou o .exe - re-desativando...");

                    // Take ownership
                    SystemUtils.RunExternalProcess("takeown", $"/f \"{gameBarPath}\"", true);
                    SystemUtils.RunExternalProcess("icacls", $"\"{gameBarPath}\" /grant *S-1-3-4:F /t /c /l", true);

                    // Matar processo
                    SystemUtils.RunExternalProcess("taskkill", "/F /IM GameBarPresenceWriter.exe", true);
                    System.Threading.Thread.Sleep(500);

                    // Excluir .bak antigo
                    File.Delete(backupPath);

                    // Renomear .exe → .bak
                    File.Move(gameBarPath, backupPath);
                    KitLugia.Core.Logger.Log("✅ GameBarPresenceWriter re-desativado automaticamente na inicialização.");
                }
                else if (File.Exists(gameBarPath) && !File.Exists(backupPath))
                {
                    // Nunca foi desativado antes, mas o usuário quer desativar
                    KitLugia.Core.Logger.Log("ℹ️ GameBarPresenceWriter ativo - desativando conforme preferência salva...");
                    SystemUtils.RunExternalProcess("takeown", $"/f \"{gameBarPath}\"", true);
                    SystemUtils.RunExternalProcess("icacls", $"\"{gameBarPath}\" /grant *S-1-3-4:F /t /c /l", true);
                    SystemUtils.RunExternalProcess("taskkill", "/F /IM GameBarPresenceWriter.exe", true);
                    System.Threading.Thread.Sleep(500);
                    File.Move(gameBarPath, backupPath);
                    KitLugia.Core.Logger.Log("✅ GameBarPresenceWriter desativado conforme preferência.");
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBarPresenceWriter auto-fix: {ex.Message}");
            }
        }

        public void LoadSettings()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\TraySettings");
                if (key == null)
                {
                    IsTrayEnabled = false; // Default on first run
                    return;
                }

                IsTrayEnabled = (int)key.GetValue("IsTrayEnabled", 0) == 1;
                CloseToTray = (int)key.GetValue("CloseToTray", 1) == 1;
                AutoCleanEnabled = (int)key.GetValue("AutoCleanEnabled", 0) == 1;
                AutoCleanThresholdPercent = (int)key.GetValue("Threshold", 80);
                MonitorIntervalSeconds = (int)key.GetValue("Interval", 30);
                SelectedCleaningMode = (MemoryOptimizer.CleaningMode)(int)key.GetValue("CleaningMode", (int)MemoryOptimizer.CleaningMode.Normal);
                GamePriorityEnabled = (int)key.GetValue("GamePriority", 0) == 1;
                StandbyCleanEnabled = (int)key.GetValue("StandbyClean", 0) == 1;
                MemoryLeakDetectionEnabled = (int)key.GetValue("AntiLeak", 0) == 1;
                FocusAssistEnabled = (int)key.GetValue("FocusAssist", 0) == 1;
                TimerBoost = (int)key.GetValue("TimerBoost", 0) == 1;
                NetworkBoost = (int)key.GetValue("NetworkBoost", 0) == 1;
                ProBalance = (int)key.GetValue("ProBalance", 0) == 1;
                

                EnableSmartAlerts = (int)key.GetValue("EnableSmartAlerts", 1) == 1;
                EnableBehaviorAnalysis = (int)key.GetValue("EnableBehaviorAnalysis", 1) == 1;
                HighRamThresholdMB = (long)key.GetValue("HighRamThresholdMB", 2048);
                HighCpuThresholdPercent = (double)key.GetValue("HighCpuThresholdPercent", 80.0);
                AdvancedMonitorIntervalMs = (int)key.GetValue("AdvancedMonitorIntervalMs", 2000);
                RamLimiterIntervalMs = (int)key.GetValue("RamLimiterIntervalMs", 1000);
                GameBarPresenceWriterDisabled = (int)key.GetValue("GameBarPresenceWriterDisabled", 0) == 1;

                _monitorTimer.Interval = TimeSpan.FromSeconds(MonitorIntervalSeconds);
            }
            catch { }
        }

        private void RunSafetyProfiler()
        {
            try
            {
                // Baseline: How long does a 'Leve' clean take on this system?
                Stopwatch sw = Stopwatch.StartNew();
                MemoryOptimizer.Optimize(MemoryOptimizer.CleaningMode.Leve);
                sw.Stop();

                _lastCleanDurationMs = sw.ElapsedMilliseconds;
                // If it takes > 300ms just for a Leve clean, this system is slow/busy
                if (_lastCleanDurationMs > 300)
                {
                    _stutterBackoffCycles = 1; // Start with caution
                }
            }
            catch { }
        }

        public void SetTrayEnabled(bool enabled)
        {
            IsTrayEnabled = enabled;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = enabled;
            }

            if (enabled)
            {
                if (!_monitorTimer.IsEnabled) _monitorTimer.Start();
            }
            else
            {
                _monitorTimer.Stop();
            }
        }


        public void PauseMonitoring()
        {
            try
            {
                _monitorTimer?.Stop();
                KitLugia.Core.Logger.Log("⏸️ TrayIcon: Monitoramento pausado (janela perdeu foco)");
            }
            catch { }
        }


        public void ResumeMonitoring()
        {
            try
            {
                if (IsTrayEnabled && !_monitorTimer.IsEnabled)
                {
                    _monitorTimer?.Start();
                    KitLugia.Core.Logger.Log("▶️ TrayIcon: Monitoramento retomado (janela ganhou foco)");
                }
            }
            catch { }
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            try
            {
                // 1. Refresh System Stats
                var stats = MemoryOptimizer.GetMemoryStats();
                int usedPercent = stats.Percent;
                UpdateTrayIcon(usedPercent);

                if (_trayIcon != null)
                    _trayIcon.Text = $"KitLugia - RAM: {usedPercent}% em uso";

                // 2. Auto-clean logic (Manual/Threshold)
                if (AutoCleanEnabled && usedPercent >= AutoCleanThresholdPercent)
                {
                    CleanRamNow();
                }

                // 3. Game Priority Boost
                if (GamePriorityEnabled)
                {
                    OptimizeForegroundProcess();
                }

                // 4. Standby List Cleaning (be gentle)
                if (StandbyCleanEnabled)
                {
                    CheckAndCleanStandby(usedPercent);
                }

                // 5. Memory Leak Mitigation (Anti-Leak) - Targeted and Smart
                if (MemoryLeakDetectionEnabled)
                {
                    DetectAndTrimLeaks(usedPercent, stats);
                }

                // 6. Focus Assist (Quiet Hours)
                if (FocusAssistEnabled)
                {
                    ManageFocusAssist();
                }

                // 7. Dynamic Intelligence (V2) - Tracker & Firemin-Optimized Trim
                UpdateProcessProfiles(stats);
                ApplyFireminOptimizations();

                // 8. Auto-Log Stats
                LogStats(stats);
            }
            catch
            {
                // Silently ignore monitoring errors
            }
        }

        private void UpdateProcessProfiles(MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
                uint foregroundPid = 0;
                if (foregroundHwnd != IntPtr.Zero) Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName.ToLower();
                        if (name == "explorer" || name == "dwm" || name == "lsass" || name == "csrss") continue;

                        // Only track user-facing apps for VIP promotion
                        if (proc.MainWindowHandle == IntPtr.Zero || !IsTaskbarWindow(proc.MainWindowHandle)) continue;

                        var profile = _processProfiles.GetOrAdd(name, _ => new ProcessProfile { Name = name });

                        profile.TotalCyclesVisible++;
                        if (proc.Id == foregroundPid) profile.CyclesForeground++;
                        profile.LastKnownWs = proc.WorkingSet64;

                        // Promotion logic:
                        // 1. Known browsers/apps
                        if (!profile.IsVip)
                        {
                            bool isKnownVip = _vipProcesses.Any(v => name.Contains(v)) || name.Contains("chrome") || name.Contains("msedge") || name.Contains("brave") || name.Contains("vivaldi");
                            // 2. Used heavily (long cycles visible)
                            bool isHeavyUse = profile.TotalCyclesVisible > 5;

                            if (isKnownVip || isHeavyUse)
                            {
                                profile.IsVip = true;
                                // Log promotion event indirectly via CSV later or debug
                            }
                        }
                    }
                    catch { }

                }
            }
            catch { }
        }

        private void ApplyFireminOptimizations()
        {
            try
            {
                // Firemin logic: targeted frequent but ultra-gentle trim for VIPs
                foreach (var profile in _processProfiles.Values)
                {
                    if (!profile.IsVip) continue;

                    // Rate Limit: 10 seconds between trims for the same process
                    if ((DateTime.Now - profile.LastTrimTime).TotalSeconds < 10) continue;

                    // Threshold: only trim if it exceeds 300MB
                    if (profile.LastKnownWs < 300L * 1024 * 1024) continue;

                    try
                    {
                        // Find all instances of this VIP process
                        foreach (var proc in Process.GetProcessesByName(profile.Name))
                        {
                            try
                            {
                                MemoryOptimizer.EmptyProcessWorkingSet(proc.Id);
                            }
                            catch { }
                            finally { proc.Dispose(); }
                        }
                        profile.LastTrimTime = DateTime.Now;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void LogStats(MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(_logPath)!;
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                bool exists = System.IO.File.Exists(_logPath);
                using var sw = new System.IO.StreamWriter(_logPath, true);
                if (!exists) sw.WriteLine("Timestamp,UsedPercent,UsedGB,FreeGB,LastDurationMs,StutterCycles");

                sw.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{stats.Percent},{stats.UsedGB:F2},{stats.FreeGB:F2},{_lastCleanDurationMs},{_stutterBackoffCycles}");
            }
            catch { }
        }

        private bool _lastFocusState = false;
        private void ManageFocusAssist()
        {
            try
            {
                // Check if a game/foreground app is likely active
                IntPtr hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);


                if (pid == 0) return;

                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName.ToLower();

                // If it's not a system/shell process, assume we want focus
                bool shouldFocus = (name != "explorer" && name != "dwm" && name != "shellexperiencehost" && name != "searchhost");

                if (shouldFocus != _lastFocusState)
                {
                    SetWindowsFocusAssist(shouldFocus);
                    _lastFocusState = shouldFocus;
                }
            }
            catch (System.ComponentModel.Win32Exception) { /* Processo encerrou - ignorar */ }
            catch (ArgumentException) { /* PID inválido - ignorar */ }
            catch { }
        }

        private void SetWindowsFocusAssist(bool enable)
        {
            try
            {
                // Registry key for Focus Assist (Quiet Hours) - simplified approach
                // 0 = Off, 1 = Priority Only, 2 = Alarms Only
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings", true);
                if (key != null)
                {
                    key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", enable ? 0 : 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private void DetectAndTrimLeaks(int systemUsagePercent, MemoryOptimizer.MemoryInfo stats)
        {
            try
            {
                // Only act if system RAM usage is starting to get high
                if (systemUsagePercent < 65) return;

                // Threshold: 15% of total RAM or at least 2GB
                ulong standardThreshold = (ulong)(stats.TotalBytes * 0.15);
                if (standardThreshold < 2000UL * 1024 * 1024) standardThreshold = 2000UL * 1024 * 1024;

                // VIP Threshold: 25% of total RAM (much more tolerant)
                ulong vipThreshold = (ulong)(stats.TotalBytes * 0.25);

                IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
                uint foregroundPid = 0;
                if (foregroundHwnd != IntPtr.Zero) Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

                foreach (var proc in GetCachedProcesses())
                {
                    try
                    {
                        // Don't touch the user's active game/app
                        if (proc.Id == foregroundPid) continue;

                        string name = proc.ProcessName.ToLower();
                        bool isVip = _vipProcesses.Any(v => name.Contains(v));

                        // Essential system processes to ignore
                        if (name == "explorer" || name == "dwm" || name == "lsass" || name == "csrss" || name == "searchindexer") continue;

                        ulong currentWs = (ulong)proc.WorkingSet64;
                        ulong activeThreshold = isVip ? vipThreshold : standardThreshold;

                        if (currentWs > activeThreshold)
                        {
                            // Target ONLY this leaky process
                            // If it's a VIP, we ONLY do a Leve trim to prevent lag
                            MemoryOptimizer.EmptyProcessWorkingSet(proc.Id);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
            catch { }
        }

        private uint _lastBoostedPid = 0;
        private ProcessPriorityClass _lastOriginalPriority = ProcessPriorityClass.Normal;


        public enum GameBoostEngine
        {
            V1_Balanced = 1,      // Equilibrado - não trava, velocidade consistente (PADRÃO)
            V2_StableFPS = 2,     // FPS estável - pode travar um pouco
            V3_Extreme = 3,       // Extremo - rede estável/mais rápida, pode travar mais
            V4_ExtremePro = 4     // Máximo desempenho - RealTime + Critical I/O + P-Cores
        }

        private static GameBoostEngine _currentEngine = GameBoostEngine.V1_Balanced;

        public static GameBoostEngine CurrentEngine
        {
            get => _currentEngine;
            set
            {
                _currentEngine = value;
            }
        }

        public static string GetEngineDescription(GameBoostEngine engine) => engine switch
        {
            GameBoostEngine.V1_Balanced => "V1 - Equilibrado (Padrão)",
            GameBoostEngine.V2_StableFPS => "V2 - FPS Estável",
            GameBoostEngine.V3_Extreme => "V3 - Extremo (Rede+)",
            GameBoostEngine.V4_ExtremePro => "V4 - Extreme Pro (RealTime + Critical I/O)",
            _ => "Desconhecido"
        };

        public static void SetEngine(GameBoostEngine engine) => CurrentEngine = engine;
        public static void SetEngine(int engineNumber) => CurrentEngine = (GameBoostEngine)engineNumber;


        public static CustomEngineConfig? _customEngineConfig = null;
        public static bool IsCustomEngineActive => _customEngineConfig != null;

        private static GameBoostEngine _previousEngine = GameBoostEngine.V1_Balanced;

        public static void SetCustomEngine(CustomEngineConfig config)
        {
            _previousEngine = _currentEngine;
            _customEngineConfig = config;
            _currentEngine = GameBoostEngine.V1_Balanced; // Reset para não conflitar
            KitLugia.Core.Logger.Log($"🎮 GameBoost: Motor personalizado ativado - {config.CpuPriority} | ProBalance: {(config.ProBalance ? "ON" : "OFF")}");
        }

        public static void ClearCustomEngine()
        {
            _customEngineConfig = null;
            _currentEngine = _previousEngine;
            KitLugia.Core.Logger.Log($"🎮 GameBoost: Motor personalizado desativado - Restaurado para V{(int)_previousEngine}");
        }


        public static void ForceReapplyBoost(uint pid)
        {
            // NUNCA fazer boost em si mesmo
            if (pid == 0 || pid == Environment.ProcessId) return;

            try
            {
                var service = _instance;
                if (service == null) return;

                // Reverte o boost anterior para garantir estado limpo
                service.RevertBoost(pid);

                // Aplica o boost com o novo motor
                service.ApplyBoostModern(pid);

                // Se for V2, V3 ou motor personalizado com ProBalance, aplica o ProBalance também
                if (_currentEngine != GameBoostEngine.V1_Balanced || 
                    (_customEngineConfig != null && _customEngineConfig.ProBalance))
                {
                    service.ApplyProBalance(pid);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Erro em ForceReapplyBoost: {ex.Message}");
            }
        }

        // Referência estática para acesso ao método privado
        private static TrayIconService? _instance;


        public uint CurrentForegroundPid => _currentBoostedPid;


        public IntPtr CurrentForegroundHwnd => _lastForegroundHwnd;


        private uint _currentBoostedPid = 0;
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private DateTime _lastBoostTime = DateTime.MinValue;
        private readonly TimeSpan _boostCooldown = TimeSpan.FromMilliseconds(50);
        private Win32Api.WinEventDelegate? _winEventDelegate;
        private IntPtr _winEventHook = IntPtr.Zero;
        private bool _useWinEventHook = false;


        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>
        /// Obtém o título da janela com precisão usando UIAutomation para identificar abas específicas
        /// </summary>
        private string GetWindowTitle(IntPtr hwnd)
        {
            try
            {
                int length = GetWindowTextLength(hwnd);
                if (length > 0)
                {
                    var sb = new StringBuilder(length + 1);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    return sb.ToString();
                }
            }
            catch (Exception ex) { ConditionalLog.LogOnce("GetWindowTitle", ex); }
            return "";
        }

        // Sistema de IA/Heurística para detecção inteligente
        private readonly HashSet<string> _heavyAppIndicators = new(StringComparer.OrdinalIgnoreCase)
        {
            // Engines de jogo
            "unreal", "unity", "cryengine", "source", "idtech", "frostbite", "rage", " Creation ",
            // Termos de janela de jogos
            "game", "match", "lobby", "ranked", "competitive", "multiplayer", "online",
            // Classes de janela comuns
            "UnrealWindow", "UnityWndClass", "CryENGINE", "SDL_app", "GLFW",
            // Processos que indicam jogo rodando
            "steam", "epicgameslauncher", "riotgames", "battlenet", "eaapp", "ubisoftconnect"
        };


        private static readonly HashSet<string> _protectedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Windows Core
            "explorer", "dwm", "shellexperiencehost", "searchindexer", "taskmgr",
            "csrss", "lsass", "svchost", "services", "winlogon", "smss", "crss",
            "wininit", "memory compression", "registry", "system",
            // Áudio (crítico para não travar som)
            "audiodg", "audioendpointbuilder", "audiosrv", "audioengine",
            // GPU/Drivers (crítico para display)
            "nvcontainer", "nvservices", "nvdisplay.container", "amdremont",
            "amdrsserv", "intelgraphics", "igfxem", "igfxhk", "igfxtray",
            // Rede (crítico para conectividade)
            "wpnService", "wpnUserService",
            // Input (crítico para mouse/teclado)
            "ctfmon", "tabtip", "textinputhost"
        };


        private static readonly HashSet<string> _userExceptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "discord", "discordptb", "discordcanary",  // Discord
            "opera", "operagx", "operagxc",             // Opera GX
            "spotify",                                   // Spotify
            "chrome", "msedge", "firefox",             // Browsers
            "steam", "steamwebhelper",                   // Steam
            "epicgameslauncher",                         // Epic
            "battlenet", "battle.net"                  // Battle.net
        };

        private void OptimizeForegroundProcess()
        {
            try
            {
                IntPtr hwnd = Win32Api.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                Win32Api.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                // Se o foco não mudou, não faz nada
                if (pid == _lastBoostedPid) return;

                // 1. Reverter o aplicativo anterior
                if (_lastBoostedPid != 0)
                {
                    try
                    {
                        using var oldProc = Process.GetProcessById((int)_lastBoostedPid);
                        // Restaura CPUPriority
                        if (oldProc.PriorityClass != _lastOriginalPriority)
                            oldProc.PriorityClass = _lastOriginalPriority;

                        // Restaura I/O Normal (2) e Page Priority Default (5)
                        Win32Api.SetProcessIoPriority(oldProc.Handle, 2);
                        Win32Api.SetProcessPagePriority(oldProc.Handle, 5);


                        SetEcoQoS(oldProc.Handle, true);
                    }
                    catch { } // Processo pode ter sido fechado
                }

                _lastBoostedPid = pid;

                if (pid == 0) return;

                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName.ToLower();

                if (pid == Environment.ProcessId || _protectedProcesses.Contains(name) || _userExceptions.Contains(name))
                {
                    _lastOriginalPriority = ProcessPriorityClass.Normal;
                    return;
                }

                _lastOriginalPriority = proc.PriorityClass;

                // Tweak 1: CPU Priority para High ou AboveNormal
                if (proc.PriorityClass != ProcessPriorityClass.High && proc.PriorityClass != ProcessPriorityClass.RealTime)
                {
                    proc.PriorityClass = ProcessPriorityClass.High;
                }

                // Tweak 2 & 3: I/O Priority High (3) e Page Priority Máxima (5)
                Win32Api.SetProcessIoPriority(proc.Handle, 3);
                Win32Api.SetProcessPagePriority(proc.Handle, 5);


                SetEcoQoS(proc.Handle, false);
            }
            catch { }
        }

        // Timer para verificação rápida do foreground (alternativa estável ao hook)
        private DispatcherTimer? _foregroundCheckTimer;


        // Tenta registrar SetWinEventHook — retorna true se bem-sucedido
        private bool TryRegisterWinEventHook()
        {
            try
            {
                _winEventDelegate = OnForegroundChanged;
                _winEventHook = Win32Api.SetWinEventHook(
                    Win32Api.EVENT_SYSTEM_FOREGROUND, Win32Api.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventDelegate, 0, 0,
                    Win32Api.WINEVENT_OUTOFCONTEXT);
                if (_winEventHook != IntPtr.Zero)
                {
                    _useWinEventHook = true;
                    KitLugia.Core.Logger.Log("🎮 GameBoost: SetWinEventHook registrado com sucesso");
                    return true;
                }
                KitLugia.Core.Logger.Log("⚠️ GameBoost: SetWinEventHook retornou handle nulo, usando polling");
            }
            catch (Exception ex)
            {
                ConditionalLog.LogOnce("WinEventHookRegister", ex);
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Falha no SetWinEventHook ({ex.GetType().Name}), usando polling fallback");
            }
            return false;
        }

        public void InitializeGameBoost()
        {
            if (!GamePriorityEnabled) return;

            EnsureSeDebugPrivilege();

            try
            {
                _instance = this;

                // Tenta hook primeiro; se falhar, fallback para polling
                if (!TryRegisterWinEventHook())
                {
                    _foregroundCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    _foregroundCheckTimer.Tick += (s, e) => CheckForegroundWindow();
                    _foregroundCheckTimer.Start();
                    KitLugia.Core.Logger.Log("🎮 GameBoost ativado (Polling 250ms)");
                }

                // Timer dedicado do ProBalance (sempre polling, independente)
                _proBalanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _proBalanceTimer.Tick += ProBalanceTimerTick;
                _proBalanceTimer.Start();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Falha no GameBoost: {ex.Message}");
            }
        }

        // Callback do WinEventHook — RODA EM THREAD DO WINDOWS (qualquer exceção não tratada = crash)
        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                if (hwnd == IntPtr.Zero || hwnd == _lastForegroundHwnd) return;
                _lastForegroundHwnd = hwnd;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    try { CheckForegroundWindow(); }
                    catch (Exception ex) { ConditionalLog.LogOnce("WinEventHookDispatch", ex); }
                });
            }
            catch { /* engole qualquer erro na thread do hook para não crashar */ }
        }
        

        private void CheckForegroundWindow()
        {
            try
            {
                IntPtr currentHwnd = Win32Api.GetForegroundWindow();
                if (currentHwnd == IntPtr.Zero) return;

                // Debounce - verifica ANTES de processar
                if ((DateTime.Now - _lastBoostTime) < _boostCooldown) return;
                _lastBoostTime = DateTime.Now;

                // Obtém PID do foreground
                Win32Api.GetWindowThreadProcessId(currentHwnd, out uint pid);
                if (pid == 0) return;
                if (pid == _currentBoostedPid) return; // Mesmo processo


                string windowTitle = GetWindowTitle(currentHwnd);

                // Verifica se deve aplicar boost
                bool shouldBoost = ShouldBoostProcess(pid, currentHwnd);

                if (shouldBoost)
                {
                    // Reverte boost anterior
                    if (_currentBoostedPid != 0 && _currentBoostedPid != pid)
                    {
                        try
                        {
                            RevertBoost(_currentBoostedPid);
                        }
                        catch { }
                    }

                    // Aplica boost ao novo processo
                    try
                    {
                        ApplyBoostModern(pid);
                        _currentBoostedPid = pid;


                        string logTitle = string.IsNullOrEmpty(windowTitle) ? $"Process {pid}" : windowTitle;
                        KitLugia.Core.Logger.Log($"🎮 GameBoost (Timer): Boost aplicado ao processo PID: {pid} - {logTitle}");
                    }
                    catch { }
                }
                else
                {
                    // Atualiza _currentBoostedPid mesmo sem boost para evitar loop
                    if (_currentBoostedPid != 0 && _currentBoostedPid != pid)
                    {
                        RevertBoost(_currentBoostedPid);
                    }
                    _currentBoostedPid = pid;
                }
            }
            catch { /* Ignora erros silenciosamente */ }
        }


        private bool ShouldBoostProcess(uint pid, IntPtr hwnd)
        {
            if (pid == 0) return false;

            // NUNCA fazer boost em si mesmo
            if (pid == Environment.ProcessId) return false;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName.ToLower();

                if (_protectedProcesses.Contains(procName) || _userExceptions.Contains(procName))
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }


        private bool IsFullScreen(IntPtr hwnd)
        {
            try
            {
                Win32Api.GetWindowRect(hwnd, out Win32Api.RECT rect);
                int screenWidth = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
                int screenHeight = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;

                // Se a janela ocupa quase toda a tela
                int windowWidth = rect.Right - rect.Left;
                int windowHeight = rect.Bottom - rect.Top;

                return windowWidth >= screenWidth - 10 && windowHeight >= screenHeight - 10;
            }
            catch { return false; }
        }


        private void ApplyBoostModern(uint pid)
        {
            // NUNCA fazer boost em si mesmo
            if (pid == 0 || pid == Environment.ProcessId) return;

            try
            {

                if (_customEngineConfig != null)
                {
                    ApplyBoostCustom(pid, _customEngineConfig);
                    return;
                }

                // Chama o motor selecionado pelo usuário
                switch (_currentEngine)
                {
                    case GameBoostEngine.V1_Balanced:
                        ApplyBoostV1(pid);
                        break;
                    case GameBoostEngine.V2_StableFPS:
                        ApplyBoostV2(pid);
                        break;
                    case GameBoostEngine.V3_Extreme:
                        ApplyBoostV3(pid);
                        break;
                    case GameBoostEngine.V4_ExtremePro:
                        ApplyBoostV4(pid);
                        break;
                    default:
                        ApplyBoostV1(pid); // Padrão: equilibrado
                        break;
                }
            }
            catch { }
        }


        private bool ElevateToSystem()
        {
            try
            {
                // Obtém token do processo System (PID 4 - kernel/ntoskrnl)
                IntPtr systemProcess = Win32Api.OpenProcess(Win32Api.PROCESS_QUERY_INFORMATION, false, 4);
                if (systemProcess == IntPtr.Zero)
                {
                    // Fallback: tenta lsass.exe (Local Security Authority)
                    var lsass = Process.GetProcessesByName("lsass").FirstOrDefault();
                    if (lsass != null)
                        systemProcess = Win32Api.OpenProcess(Win32Api.PROCESS_QUERY_INFORMATION, false, lsass.Id);
                }

                if (systemProcess == IntPtr.Zero) return false;

                try
                {
                    // Abre token do processo System
                    if (!Win32Api.OpenProcessToken(systemProcess, 
                        Win32Api.TOKEN_DUPLICATE | Win32Api.TOKEN_IMPERSONATE | Win32Api.TOKEN_QUERY, 
                        out IntPtr systemToken))
                        return false;

                    try
                    {
                        // Duplica token para impersonação
                        if (!Win32Api.DuplicateTokenEx(systemToken, 
                            0x1F0FFF, // MAXIMUM_ALLOWED
                            IntPtr.Zero, 
                            Win32Api.SecurityImpersonation, 
                            Win32Api.TokenImpersonation, 
                            out IntPtr impersonationToken))
                            return false;

                        try
                        {
                            // Aplica token à thread atual
                            if (Win32Api.SetThreadToken(Win32Api.GetCurrentThread(), impersonationToken))
                            {
                                KitLugia.Core.Logger.Log("🔐 Privilégios elevados para System - Acesso a processos protegidos habilitado");
                                return true;
                            }
                        }
                        finally
                        {
                            Win32Api.CloseHandle(impersonationToken);
                        }
                    }
                    finally
                    {
                        Win32Api.CloseHandle(systemToken);
                    }
                }
                finally
                {
                    Win32Api.CloseHandle(systemProcess);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Falha ao elevar privilégios: {ex.Message}");
            }
            return false;
        }

        private void ApplyBoostCustom(uint pid, CustomEngineConfig config)
        {
            if (pid == 0) return;

            // GLOBAL TIMER BOOST (não precisa de handle)
            if (config.TimerBoost)
                Win32Api.BoostTimerResolution();

            // GLOBAL NETWORK BOOST (não precisa de handle)
            if (config.NetworkBoost)
                ApplyNetworkBoostV3();

            // GLOBAL Win32PrioritySeparation (não precisa de handle)
            if (config.Win32PrioritySeparation)
                Win32Api.SetWin32PrioritySeparation(true);

            // GLOBAL ThreadEfficiencyMode via pid (não precisa de handle do processo)
            if (config.ThreadEfficiencyMode == false)
            {
                CheckPcoreBenefit(pid);
                Win32Api.SetThreadEfficiencyForAllThreads(pid, false);
            }

            // PROCESS-LEVEL: Prioridade, I/O, Page, GameClassInfo, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                KitLugia.Core.Logger.Log($"⚡ GameBoost [PERSONALIZADO]: {name} (PID: {pid}) aplicando configurações...");

                var targetPriority = config.CpuPriority.ToLower() switch
                {
                    "normal" => ProcessPriorityClass.Normal,
                    "high" => ProcessPriorityClass.High,
                    "realtime" => config.ProBalance ? ProcessPriorityClass.High : ProcessPriorityClass.RealTime,
                    _ => ProcessPriorityClass.High
                };

                bool elevated = false;
                try
                {
                    if (proc.PriorityClass != targetPriority && targetPriority != ProcessPriorityClass.RealTime)
                        proc.PriorityClass = targetPriority;
                    else if (targetPriority == ProcessPriorityClass.RealTime && proc.PriorityClass != ProcessPriorityClass.RealTime)
                    {
                        try { proc.PriorityClass = ProcessPriorityClass.RealTime; }
                        catch { proc.PriorityClass = ProcessPriorityClass.High; }
                    }
                }
                catch (System.ComponentModel.Win32Exception) when (!elevated)
                {
                    KitLugia.Core.Logger.Log($"🔒 Acesso negado ao processo {name} - tentando elevar privilégios...");
                    if (ElevateToSystem())
                    {
                        elevated = true;
                        try
                        {
                            proc.PriorityClass = targetPriority;
                            KitLugia.Core.Logger.Log($"✅ Prioridade aplicada com privilégios elevados: {name}");
                        }
                        catch { KitLugia.Core.Logger.Log($"⚠️ Mesmo elevado, falha ao alterar {name} (PPL bloqueia)"); }
                    }
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, config.IoPriorityLevel == 0 ? 2 : 3); } catch { }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, (uint)(config.ThreadMemoryPriority == 0 ? 5 : 3)); } catch { }
                try { SetEcoQoS(proc.Handle, config.EcoQoSEnabled); } catch { }

                if (config.GameClassInfo)
                {
                    try { Win32Api.SetProcessGameClassInfo(proc.Handle, true); } catch { }
                }

                KitLugia.Core.Logger.Log($"✅ GameBoost [PERSONALIZADO]: {name} otimizado com sucesso!");
            }
            catch { }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void CheckPcoreBenefit(uint pid)
        {
            int onECores = Win32Api.GetThreadCountOnECores(pid);
            System.Diagnostics.Debug.WriteLine($"[GameBoost] P-Cores Only PID {pid}: {onECores} thread(s) em E-cores - {(onECores > 0 ? "ben\u00E9fico" : "n\u00E3o ben\u00E9fico")}");
        }


        private void ApplyBoostV1(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Win32PrioritySeparation (não precisa de handle do processo)
            Win32Api.SetWin32PrioritySeparation(true);

            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);

            // PROCESS-LEVEL: Prioridade, I/O, Page, Memory
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                try
                {
                    if (proc.PriorityClass != ProcessPriorityClass.High &&
                        proc.PriorityClass != ProcessPriorityClass.RealTime)
                    {
                        proc.PriorityClass = ProcessPriorityClass.High;
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    KitLugia.Core.Logger.Log($"⚠️ V1: Acesso negado à prioridade do processo {name} (PID: {pid}) - processo protegido");
                }
                catch { }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { }
            }
            catch { }
        }


        private void ApplyBoostV2(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Scheduler + P-Cores (não precisa de handle do processo)
            Win32Api.SetWin32PrioritySeparation(true);
            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);

            // PROCESS-LEVEL: GameClassInfo, Prioridade, I/O, Page, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                Win32Api.SetProcessGameClassInfo(proc.Handle, true);

                try
                {
                    if (proc.PriorityClass != ProcessPriorityClass.High &&
                        proc.PriorityClass != ProcessPriorityClass.RealTime)
                    {
                        proc.PriorityClass = ProcessPriorityClass.High;
                    }
                }
                catch { }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { }
                try { SetEcoQoS(proc.Handle, false); } catch { }

                ApplyProBalanceV2(pid);
            }
            catch { }
        }


        private void ApplyBoostV3(uint pid)
        {
            if (pid == 0) return;

            // GLOBAL: Scheduler + P-Cores + Timer + Network (não precisa de handle)
            Win32Api.SetWin32PrioritySeparation(true);
            CheckPcoreBenefit(pid);
            Win32Api.SetThreadEfficiencyForAllThreads(pid, false);
            Win32Api.BoostTimerResolution();
            ApplyNetworkBoostV3();

            KitLugia.Core.Logger.Log($"⚡ GameBoost V3 [Extremo]: PID {pid} com boosts globais ativos");

            // PROCESS-LEVEL: GameClassInfo, Prioridade, I/O, Page, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                Win32Api.SetProcessGameClassInfo(proc.Handle, true);

                try
                {
                    if (proc.PriorityClass != ProcessPriorityClass.High &&
                        proc.PriorityClass != ProcessPriorityClass.RealTime)
                    {
                        proc.PriorityClass = ProcessPriorityClass.High;
                    }
                }
                catch { }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { }
                try { SetEcoQoS(proc.Handle, false); } catch { }

                ApplyProBalanceV3(pid);
            }
            catch { }
        }


        private void ApplyBoostV4(uint pid)
        {
            var v4Config = new CustomEngineConfig
            {
                CpuPriority = "realtime",
                IoPriorityLevel = 1,
                PagePriorityLevel = 2,
                TimerBoost = true,
                EcoQoSEnabled = true,
                ProBalance = false,
                NetworkBoost = true,
                ThreadMemoryPriority = 0,
                ThreadEfficiencyMode = false,
                GameClassInfo = true,
                Win32PrioritySeparation = true
            };
            ApplyBoostCustom(pid, v4Config);
        }


        private void ApplyNetworkBoostV3()
        {
            try
            {
                // CORREÇÃO: Usar valores MENOS agressivos para não causar latência em jogos multiplayer
                // NetworkThrottlingIndex = 0 desabilita o throttling (padrão é 10, 0xFFFFFFFF desabilita)
                // SystemResponsiveness = 10 (padrão: 20, menor = mais prioridade para multimídia)
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                if (key != null)
                {
                    key.SetValue("NetworkThrottlingIndex", 10, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("SystemResponsiveness", 10, Microsoft.Win32.RegistryValueKind.DWord);
                }

                KitLugia.Core.Logger.Log("🌐 GameBoost V3: Network prioritizado (throttling reduzido)");
            }
            catch { }
        }


        private readonly HashSet<uint> _throttledProcesses = new();
        private readonly object _throttleLock = new();
        private DispatcherTimer? _proBalanceTimer; // Timer dedicado para ProBalance (executa independente)
        private const int ProBalanceSamplesRequired = 3; // 3 amostras consecutivas (3s timer = ~9s) antes de throttle
        private const int ProBalanceCooldownSec = 30;    // 30s de cooldown antes de re-throttle do mesmo processo
        private readonly Dictionary<uint, int> _proBalanceConsecutive = new();    // PID → contagem de amostras acima do threshold
        private readonly Dictionary<uint, DateTime> _proBalanceCooldowns = new(); // PID → fim do cooldown

        // Dispatcher para o ProBalance correto baseado no motor
        private void ApplyProBalance(uint foregroundPid)
        {

            if (!ProBalance)
            {
                return;
            }
            

            if (_customEngineConfig != null && _customEngineConfig.ProBalance)
            {
                ApplyProBalanceCustom(foregroundPid, _customEngineConfig.ProBalanceCpuThreshold);
                return;
            }

            switch (_currentEngine)
            {
                case GameBoostEngine.V1_Balanced:
                    // V1 ORIGINAL: Não aplica ProBalance (comportamento puro)
                    break;
                case GameBoostEngine.V2_StableFPS:
                    ApplyProBalanceV2(foregroundPid);
                    break;
                case GameBoostEngine.V3_Extreme:
                    ApplyProBalanceV3(foregroundPid);
                    break;
                default:
                    // Padrão também não aplica (V1)
                    break;
            }
        }


        private void ApplyProBalanceV2(uint foregroundPid)
        {
            ApplyProBalanceCore(foregroundPid, 8.0, "V2");
        }


        private void ApplyProBalanceV3(uint foregroundPid)
        {
            ApplyProBalanceCore(foregroundPid, 3.0, "V3");
        }


        private void ApplyProBalanceCustom(uint foregroundPid, int thresholdPercent)
        {
            ApplyProBalanceCore(foregroundPid, thresholdPercent, "Custom");
        }

        // Core do ProBalance com threshold configurável e amostras consecutivas
        private void ApplyProBalanceCore(uint foregroundPid, double cpuThreshold, string version)
        {
            ConditionalLog.Try("ApplyProBalanceCore", () =>
            {
                lock (_throttleLock)
                {
                    var now = DateTime.UtcNow;

                    // Restaura processos que saíram do foreground ou saíram de cooldown
                    var toRestore = _throttledProcesses.Where(p => p != foregroundPid).ToList();
                    foreach (var pid in toRestore)
                    {
                        try
                        {
                            using var proc = Process.GetProcessById((int)pid);
                            string name = proc.ProcessName.ToLower();

                            if (_protectedProcesses.Contains(name) || _userExceptions.Contains(name))
                            {
                                _throttledProcesses.Remove(pid);
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                                continue;
                            }

                            if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                            {
                                proc.PriorityClass = ProcessPriorityClass.Normal;
                                ConditionalLog.Try("ProBalanceRestore",
                                    () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL));
                                KitLugia.Core.Logger.Log($"🔼 ProBalance {version}: {name} (PID: {pid}) restaurado para Normal");
                            }
                            _throttledProcesses.Remove(pid);
                            _proBalanceConsecutive.Remove(pid);
                            _proBalanceCooldowns.Remove(pid);
                        }
                        catch (Exception ex)
                        {
                            _throttledProcesses.Remove(pid);
                            _proBalanceConsecutive.Remove(pid);
                            _proBalanceCooldowns.Remove(pid);
                            ConditionalLog.LogOnce("ProBalanceRestoreFail", ex);
                        }
                    }

                    // Throttle com amostras consecutivas + cooldown
                    var currentProcess = Process.GetCurrentProcess();
                    foreach (var proc in GetCachedProcesses())
                    {
                        try
                        {
                            uint pid = (uint)proc.Id;
                            if (pid == foregroundPid || pid == (uint)currentProcess.Id || _throttledProcesses.Contains(pid))
                                continue;

                            string name = proc.ProcessName.ToLower();
                            if (_protectedProcesses.Contains(name) || _userExceptions.Contains(name))
                            {
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                                continue;
                            }

                            // Verifica cooldown
                            if (_proBalanceCooldowns.TryGetValue(pid, out var cooldownEnd) && now < cooldownEnd)
                                continue;

                            double cpuUsage = GetProcessCpuUsage(proc);

                            if (cpuUsage > cpuThreshold)
                            {
                                // Incrementa contagem de amostras consecutivas
                                if (!_proBalanceConsecutive.TryGetValue(pid, out int count))
                                    count = 0;
                                count++;
                                _proBalanceConsecutive[pid] = count;

                                // Só throttle após N amostras consecutivas
                                if (count >= ProBalanceSamplesRequired && proc.PriorityClass >= ProcessPriorityClass.Normal)
                                {
                                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                                    _throttledProcesses.Add(pid);
                                    _proBalanceConsecutive.Remove(pid);
                                    _proBalanceCooldowns[pid] = now.AddSeconds(ProBalanceCooldownSec);

                                    ConditionalLog.Try("ProBalanceMemoryPrio",
                                        () => Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_VERY_LOW));

                                    KitLugia.Core.Logger.Log($"🔻 ProBalance {version}: {name} (PID: {pid}) throttled após {count} amostras (CPU: {cpuUsage:F1}%)");
                                }
                            }
                            else
                            {
                                // Abaixo do threshold: reseta contagem
                                _proBalanceConsecutive.Remove(pid);
                                _proBalanceCooldowns.Remove(pid);
                            }
                        }
                        catch (Exception ex) { ConditionalLog.LogOnce("ProBalanceThrottle", ex); }
                    }
                }
            });
        }


        public void RestoreAllThrottledProcesses()
        {
            lock (_throttleLock)
            {
                var toRestore = _throttledProcesses.ToList();
                foreach (var pid in toRestore)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        string name = proc.ProcessName.ToLower();

                        // Restaura para Normal
                        if (proc.PriorityClass == ProcessPriorityClass.BelowNormal)
                        {
                            proc.PriorityClass = ProcessPriorityClass.Normal;

                            // Restaura thread memory priority
                            try
                            {
                                Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL);
                            }
                            catch { }

                            KitLugia.Core.Logger.Log($"🔼 ProBalance Global: {name} (PID: {pid}) restaurado para Normal");
                        }
                        _throttledProcesses.Remove(pid);
                    }
                    catch
                    {
                        _throttledProcesses.Remove(pid);
                    }
                }
                
                KitLugia.Core.Logger.Log($"⚖️ ProBalance: {_throttledProcesses.Count} processos restaurados, {_throttledProcesses.Count} ainda throttled");
            }
        }

        // REMOVIDO: ApplyProBalanceOld (não usado, substituído por ApplyProBalanceCore)

        // Helper: estima uso de CPU de um processo (CORRIGIDO: usa delta entre amostras)
        private double GetProcessCpuUsage(Process proc)
        {
            try
            {
                int pid = proc.Id;
                var now = DateTime.Now;
                var currentCpu = proc.TotalProcessorTime;

                if (_cpuTimeCache.TryGetValue(pid, out var prev))
                {
                    var elapsed = (now - prev.Timestamp).TotalSeconds;
                    var cpuDelta = (currentCpu - prev.CpuTime).TotalSeconds;

                    _cpuTimeCache[pid] = (now, currentCpu);

                    if (elapsed > 0 && cpuDelta >= 0)
                    {
                        return (cpuDelta / (Environment.ProcessorCount * elapsed)) * 100;
                    }
                }
                else
                {
                    _cpuTimeCache[pid] = (now, currentCpu);
                }
            }
            catch { }
            return 0;
        }


        public static void AddUserException(string processName)
        {
            if (!string.IsNullOrEmpty(processName))
            {
                _userExceptions.Add(processName.ToLower());
                KitLugia.Core.Logger.Log($"✅ ProBalance: {processName} adicionado às exceções");
            }
        }

        public static void RemoveUserException(string processName)
        {
            if (!string.IsNullOrEmpty(processName))
            {
                _userExceptions.Remove(processName.ToLower());
                KitLugia.Core.Logger.Log($"❌ ProBalance: {processName} removido das exceções");
            }
        }

        public static HashSet<string> GetUserExceptions() => new(_userExceptions);


        private void RevertBoost(uint pid)
        {
            if (pid == 0)
            {
                Logger.Log("⚠️ RevertBoost: PID inválido (0)");
                return;
            }

            // GLOBAL: ThreadEfficiencyMode via pid (não precisa de handle)
            Win32Api.SetThreadEfficiencyForAllThreads(pid, true);
            Win32Api.RestoreTimerResolution();

            // PROCESS-LEVEL: Prioridade, I/O, Page, GameClassInfo, EcoQoS
            try
            {
                using var proc = Process.GetProcessById((int)pid);

                if (proc.PriorityClass == ProcessPriorityClass.High)
                {
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 2); } catch { }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { }
                try { SetEcoQoS(proc.Handle, true); } catch { }
                try { Win32Api.SetProcessGameClassInfo(proc.Handle, false); } catch { }
            }
            catch { }
        }


        private void SetEcoQoS(IntPtr processHandle, bool enableEcoMode)
        {
            try
            {
                // Só aplica no Windows 11 (build >= 22000)
                if (Environment.OSVersion.Version.Build < 22000) return;

                var state = new Win32Api.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = Win32Api.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = enableEcoMode ? Win32Api.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                };

                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(state));
                Marshal.StructureToPtr(state, ptr, false);

                Win32Api.SetProcessInformation(processHandle, Win32Api.ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf(state));

                Marshal.FreeHGlobal(ptr);
            }
            catch { }
        }


        private void ProBalanceTimerTick(object? sender, EventArgs e)
        {
            try
            {
                if (ProBalance && GamePriorityEnabled && _currentBoostedPid != 0)
                {
                    ApplyProBalance(_currentBoostedPid);
                }
            }
            catch { }
        }


        public void ShutdownGameBoost()
        {
            try
            {

                if (_useWinEventHook && _winEventHook != IntPtr.Zero)
                {
                    Win32Api.UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                    _useWinEventHook = false;
                    _winEventDelegate = null;
                }


                if (_proBalanceTimer != null)
                {
                    _proBalanceTimer.Tick -= ProBalanceTimerTick;
                    _proBalanceTimer.Stop();
                    _proBalanceTimer = null;
                }


                _foregroundCheckTimer?.Stop();
                _foregroundCheckTimer = null;

                // Reverte boost atual
                if (_currentBoostedPid != 0)
                {
                    RevertBoost(_currentBoostedPid);
                    _currentBoostedPid = 0;
                }


                Win32Api.SetWin32PrioritySeparation(false);
                Win32Api.RestoreTimerResolution();

                // Limpa referências
                _lastForegroundHwnd = IntPtr.Zero;

                KitLugia.Core.Logger.Log("🎮 GameBoost desativado (SetWinEventHook Kernel Hook)");
            }
            catch { }
        }

        private void CheckAndCleanStandby(int systemUsagePercent)
        {
            try
            {
                // On high RAM systems (32GB+), standby list is actually good for performance
                // We only clear it if RAM is truly crowded (> 80%)
                if (systemUsagePercent > 80)
                {
                    // Use Normal instead of Alta to avoid heavy purging of useful cache
                    MemoryOptimizer.Optimize(MemoryOptimizer.CleaningMode.Normal);
                }
            }
            catch { }
        }

        private bool IsTaskbarWindow(IntPtr hwnd)
        {
            if (!Win32Api.IsWindowVisible(hwnd)) return false;

            IntPtr owner = Win32Api.GetWindow(hwnd, Win32Api.GW_OWNER);
            int exStyle = Win32Api.GetWindowLong(hwnd, Win32Api.GWL_EXSTYLE);

            // A window is on the taskbar if:
            // 1. It is visible (checked above)
            // 2. It has no owner AND is not a tool window
            // 3. OR it has the explicit WS_EX_APPWINDOW style
            bool isToolWindow = (exStyle & Win32Api.WS_EX_TOOLWINDOW) != 0;
            bool isAppWindow = (exStyle & Win32Api.WS_EX_APPWINDOW) != 0;

            if (owner == IntPtr.Zero && !isToolWindow) return true;
            if (isAppWindow) return true;

            return false;
        }

        private void CleanRamNow()
        {
            try
            {
                // Handle stutter backoff
                if (_stutterBackoffCycles > 0)
                {
                    _stutterBackoffCycles--;
                    return;
                }


                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        int before = GetMemoryUsagePercent();
                        var result = MemoryOptimizer.Optimize(SelectedCleaningMode);
                        int after = GetMemoryUsagePercent();
                        sw.Stop();

                        _lastCleanDurationMs = sw.ElapsedMilliseconds;

                        // If cleaning takes too long (> 800ms on a 32GB system), it might cause stutter
                        // Adaptive learning: wait more cycles before next auto-clean
                        if (_lastCleanDurationMs > 800)
                        {
                            _stutterBackoffCycles = 3; // Skip next 3 cycles (~1.5 min)
                        }

                        int freed = before - after;
                        string msg = freed > 0
                            ? $"RAM liberada! {before}% → {after}% ({freed}% liberado) [{_lastCleanDurationMs}ms]"
                            : $"Limpeza concluída. RAM: {after}%";

                        // Atualizar UI na thread principal
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            UpdateTrayIcon(after);

                            _trayIcon?.ShowBalloonTip(
                                3000,
                                "KitLugia RAM Booster",
                                msg,
                                ToolTipIcon.Info
                            );
                        });
                    }
                    catch
                    {
                        // Silently ignore clean errors
                    }
                });
            }
            catch
            {
                // Silently ignore clean errors
            }
        }

        private int GetMemoryUsagePercent()
        {
            return MemoryOptimizer.GetMemoryStats().Percent;
        }

        private void UpdateTrayIcon(int percent)
        {
            try
            {
                if (_trayIcon == null) return;

                // Determine color based on usage
                Color bgColor;
                if (percent >= 90) bgColor = Color.FromArgb(220, 53, 69);      // Red
                else if (percent >= 70) bgColor = Color.FromArgb(255, 193, 7);  // Yellow
                else bgColor = Color.FromArgb(40, 167, 69);                     // Green

                // Create a 16x16 icon with the percentage text
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(bgColor);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                    string text = percent.ToString();
                    float fontSize = text.Length > 2 ? 6.5f : 8f;
                    using var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
                    using var brush = new SolidBrush(Color.White);

                    var size = g.MeasureString(text, font);
                    float x = (16 - size.Width) / 2;
                    float y = (16 - size.Height) / 2;
                    g.DrawString(text, font, brush, x, y);
                }

                var newIcon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
                var oldIcon = _currentIcon;
                _trayIcon.Icon = newIcon;
                _currentIcon = newIcon;

                // Cleanup old icon
                if (oldIcon != null)
                {
                    try { Win32Api.DestroyIcon(oldIcon.Handle); } catch { }
                }

                bmp.Dispose();
            }
            catch
            {
                // Fallback: use app icon if available
            }
        }

        public void ShowMinimizedNotification()
        {
            _trayIcon?.ShowBalloonTip(
                2000,
                "KitLugia",
                "Monitorando RAM em segundo plano. Clique duas vezes para abrir.",
                ToolTipIcon.Info
            );
        }


        public bool IsTrayIconHealthy()
        {
            try
            {
                if (_trayIcon == null)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon é null");
                    return false;
                }

                if (!_trayIcon.Visible)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon não está visível");
                    return false;
                }

                if (string.IsNullOrEmpty(_trayIcon.Text))
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon Text está vazio");
                    return false;
                }

                if (_trayIcon.ContextMenuStrip == null)
                {
                    KitLugia.Core.Logger.Log("❌ Tray Icon ContextMenu é null");
                    return false;
                }

                // Testa se consegue atualizar o ícone
                UpdateTrayIcon(GetMemoryUsagePercent());

                KitLugia.Core.Logger.Log("✅ Tray Icon está saudável");
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro na verificação de saúde do Tray Icon: {ex.Message}");
                return false;
            }
        }


        public bool RecoverTrayIcon()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔄 Tentando recuperar Tray Icon...");

                // Dispose do antigo
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }

                // Recria completamente
                _trayIcon = new NotifyIcon
                {
                    Text = "KitLugia RAM Monitor",
                    Visible = false
                };

                // Recria menu
                var menu = new ContextMenuStrip();
                var itemClean = new ToolStripMenuItem("🧹 Limpar RAM Agora");
                itemClean.Click += (s, e) => CleanRamNow();
                menu.Items.Add(itemClean);

                var itemOpen = new ToolStripMenuItem("🚀 Abrir KitLugia");
                itemOpen.Click += (s, e) => OnOpenMainWindow?.Invoke();
                menu.Items.Add(itemOpen);

                var itemExit = new ToolStripMenuItem("❌ Sair");
                itemExit.Click += (s, e) =>
                {
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                };
                menu.Items.Add(itemExit);

                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (s, e) => OnOpenMainWindow?.Invoke();

                // Ativa
                UpdateTrayIcon(GetMemoryUsagePercent());
                _trayIcon.Visible = true;

                bool success = _trayIcon.Visible;
                KitLugia.Core.Logger.Log(success ? "✅ Tray Icon recuperado com sucesso" : "❌ Falha na recuperação do Tray Icon");

                return success;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro na recuperação do Tray Icon: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {

            if (_monitorTimer != null)
            {
                _monitorTimer.Tick -= MonitorTick;
                _monitorTimer.Stop();
            }

            // Para o timer dedicado do RAM Limiter
            StopRamLimiterTimer();
            
            // Limpa job objects do limitador de CPU
            foreach (var job in _cpuJobObjects.Values)
                Win32Api.CloseHandle(job);
            _cpuJobObjects.Clear();

            StopAdvancedMonitor();


            ClearProcessCache();
            

            _processCache.Clear();
            _processAlerts.Clear();
            _processBehaviors.Clear();
            _cpuTimeCache.Clear();


            ShutdownGameBoost();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_currentIcon != null)
            {
                try { Win32Api.DestroyIcon(_currentIcon.Handle); } catch { }
                _currentIcon = null;
            }
        }

        // =========================================================
        // PER-PROCESS RAM LIMITER (inspirado no Firemin)
        // Permite definir limites de RAM por processo e aplicar
        // EmptyWorkingSet quando o processo excede o limite.
        // =========================================================

        // P/Invoke: EmptyWorkingSet da psapi.dll — API que o Firemin usa.
        // Mais eficaz que SetProcessWorkingSetSize(-1,-1) porque força
        // a remoção imediata das páginas do working set para o standby list.
        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        // P/Invoke: OpenProcess com PROCESS_ALL_ACCESS para obter handle com permissão
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        // SetProcessWorkingSetSizeEx — versão estendida que aceita flags
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(
            IntPtr hProcess,
            IntPtr dwMinimumWorkingSetSize,
            IntPtr dwMaximumWorkingSetSize,
            uint Flags);

        // Flag: desabilita o hard limit no máximo (soft limit — o Windows pode exceder se necessário)
        private const uint QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x00000008;

        // Caminho do arquivo de configuração de limites
        private static readonly string _processLimitsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "process_ram_limits.json");

        /// <summary>
        /// Salva os limites de RAM em JSON com sistema robusto de backup e validação.
        /// </summary>
        public void SaveProcessLimits()
        {
            lock (_robustnessLock)
            {
                try
                {

                    if (!ValidateProcessLimitsIntegrity())
                    {
                        KitLugia.Core.Logger.Log("⚠️ Dados corrompidos detectados - usando backup");
                        RestoreFromBackup();
                        return;
                    }

                    var dir = System.IO.Path.GetDirectoryName(_processLimitsPath)!;
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                    var list = _processRamLimits.Values.ToList();
                    

                    KitLugia.Core.Logger.Log($"💾 Salvando {_processRamLimits.Count} limite(s) de RAM:");
                    foreach (var limit in list)
                    {
                        KitLugia.Core.Logger.Log($"   - {limit.ProcessName}: {limit.LimitMB}MB (Enabled: {limit.Enabled})");
                    }
                    

                    string json = System.Text.Json.JsonSerializer.Serialize(list,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    

                    if (string.IsNullOrEmpty(json) || json.Length < 10)
                    {
                        throw new InvalidOperationException("JSON gerado está vazio ou inválido");
                    }
                    

                    CreateBackupIfNeeded();
                    

                    string tempPath = _processLimitsPath + ".tmp";
                    System.IO.File.WriteAllText(tempPath, json);
                    

                    if (!System.IO.File.Exists(tempPath) || new System.IO.FileInfo(tempPath).Length == 0)
                    {
                        throw new InvalidOperationException("Arquivo temporário não foi criado corretamente");
                    }
                    

                    if (System.IO.File.Exists(_processLimitsPath))
                    {
                        System.IO.File.Replace(tempPath, _processLimitsPath, null);
                    }
                    else
                    {
                        System.IO.File.Move(tempPath, _processLimitsPath);
                    }
                    
                    KitLugia.Core.Logger.Log($"✅ JSON salvo em: {_processLimitsPath}");
                    

                    _consecutiveErrors = 0;
                    _lastErrorTime = DateTime.MinValue;
                    _isInSafeMode = false;
                }
                catch (Exception ex)
                {
                    HandleRobustnessError("SaveProcessLimits", ex);
                }
            }
        }

        /// <summary>
        /// Carrega os limites de RAM do JSON com sistema robusto de fallback e validação.
        /// </summary>
        public void LoadProcessLimits()
        {
            lock (_robustnessLock)
            {
                try
                {

                    if (!LoadFromMainFile())
                    {

                        KitLugia.Core.Logger.Log("🔄 Falha ao carregar arquivo principal, tentando backup...");
                        if (!LoadFromBackup())
                        {

                            KitLugia.Core.Logger.Log("⚠️ Falha ao carregar backup, criando configurações padrão");
                            CreateDefaultConfiguration();
                        }
                    }
                    

                    if (!ValidateProcessLimitsIntegrity())
                    {
                        KitLugia.Core.Logger.Log("⚠️ Dados carregados estão corrompidos, tentando recovery...");
                        AttemptDataRecovery();
                    }
                    
                    KitLugia.Core.Logger.Log($"💾 {_processRamLimits.Count} limite(s) de RAM carregado(s) com sucesso.");

                    // Inicia o timer dedicado se houver limites configurados
                    if (!_processRamLimits.IsEmpty)
                    {
                        // Usa BeginInvoke para garantir que o Dispatcher está pronto
                        Application.Current?.Dispatcher.BeginInvoke(
                            new System.Action(StartRamLimiterTimer),
                            System.Windows.Threading.DispatcherPriority.Background);
                        KitLugia.Core.Logger.Log("🔄 Timer do RAM Limiter iniciado automaticamente");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log("⏸️ Nenhum limite de RAM configurado - timer não iniciado");
                    }
                    

                    _consecutiveErrors = 0;
                    _lastErrorTime = DateTime.MinValue;
                    _isInSafeMode = false;
                }
                catch (Exception ex)
                {
                    HandleRobustnessError("LoadProcessLimits", ex);
                }
            }
        }

        /// <summary>
        /// Inicia o timer dedicado do RAM Limiter.
        /// </summary>
        private void StartRamLimiterTimer()
        {
            if (_ramLimiterTimer != null) return; // Já iniciado

            _ramLimiterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_ramLimiterIntervalMs)
            };
            _ramLimiterTimer.Tick += (s, e) => { ApplyProcessRamLimits(); ApplyProcessCpuLimits(); };
            _ramLimiterTimer.Start();
            KitLugia.Core.Logger.Log($"💾 RAM Limiter timer iniciado ({_ramLimiterIntervalMs}ms)");
        }

        /// <summary>
        /// Para o timer dedicado do RAM Limiter.
        /// </summary>
        private void StopRamLimiterTimer()
        {
            _ramLimiterTimer?.Stop();
            _ramLimiterTimer = null;
        }

        /// <summary>
        /// Retorna todos os limites configurados.
        /// </summary>
        public IReadOnlyCollection<ProcessRamLimit> GetProcessRamLimits()
            => _processRamLimits.Values.ToList().AsReadOnly();

        /// <summary>
        /// Adiciona ou atualiza um limite de RAM para um processo.
        /// </summary>
        public void SetProcessRamLimit(string processName, long limitMB, bool enabled = true)
        {
            if (string.IsNullOrWhiteSpace(processName)) return;
            string key = processName.ToLowerInvariant().Replace(".exe", "");

            _processRamLimits.AddOrUpdate(key,
                _ => new ProcessRamLimit { ProcessName = key, LimitMB = limitMB, Enabled = enabled },
                (_, existing) => { existing.LimitMB = limitMB; existing.Enabled = enabled; return existing; });

            SaveProcessLimits();

            // Inicia o timer dedicado se ainda não estiver rodando
            if (!_processRamLimits.IsEmpty)
            {
                Application.Current?.Dispatcher.Invoke(() => StartRamLimiterTimer());
            }

            KitLugia.Core.Logger.Log($"💾 Limite de RAM definido: {key} → {limitMB} MB ({(enabled ? "ativo" : "inativo")})");
        }

        public ProcessEngineConfig? GetProcessEngineConfig(string processName)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            if (_processRamLimits.TryGetValue(key, out var limit))
                return limit.EngineConfig;
            return null;
        }

        public void SetProcessEngineConfig(string processName, ProcessEngineConfig config)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            if (_processRamLimits.TryGetValue(key, out var limit))
            {
                limit.EngineConfig = config;
                SaveProcessLimits();
            }
        }

        /// <summary>
        /// Remove o limite de RAM de um processo.
        /// </summary>
        public void RemoveProcessRamLimit(string processName)
        {
            string key = processName.ToLowerInvariant().Replace(".exe", "");
            _processRamLimits.TryRemove(key, out _);
            SaveProcessLimits();
        }

        /// <summary>
        /// Aplica os limites de RAM configurados com trim gradual inteligente.
        /// </summary>
        private void ApplyProcessRamLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            // Detecta qual processo está em foreground agora
            IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
            uint foregroundPid = 0;
            if (foregroundHwnd != IntPtr.Zero)
                Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

            foreach (var limit in _processRamLimits.Values)
            {
                if (!limit.Enabled) continue;

                try
                {
                    var processes = Process.GetProcessesByName(limit.ProcessName);
                    if (processes.Length == 0)
                    {
                        limit.LastKnownMB = 0;
                        limit.ConsecutiveTrimCount = 0;
                        limit.IsForeground = false;
                        continue;
                    }

                    // Soma RAM de todas as instâncias e detecta se alguma está em foreground
                    long totalRamMB = 0;
                    bool anyForeground = false;

                    foreach (var proc in processes)
                    {
                        try
                        {
                            totalRamMB += proc.WorkingSet64 / (1024 * 1024);
                            if ((uint)proc.Id == foregroundPid)
                                anyForeground = true;
                        }
                        catch { }
                    }

                    // Atualiza estado
                    limit.LastKnownMB = totalRamMB;
                    limit.IsForeground = anyForeground;
                    if (totalRamMB > limit.PeakRamMB) limit.PeakRamMB = totalRamMB;

                    // Não trima se o processo está em foreground — evita stutters visíveis
                    if (anyForeground)
                    {
                        foreach (var proc in processes) try { proc.Dispose(); } catch { }
                        continue;
                    }

                    // Verifica se excede o limite e se passou o cooldown
                    bool exceedsLimit = totalRamMB > limit.LimitMB;
                    bool cooldownPassed = (DateTime.Now - limit.LastTrimTime) >= GetTrimCooldown(limit);

                    if (exceedsLimit && cooldownPassed)
                    {
                        long excessMB = totalRamMB - limit.LimitMB;

                        // Trim gradual: reduz 30% do excesso por ciclo
                        long targetMB = totalRamMB - (long)(excessMB * 0.30);
                        targetMB = Math.Max(targetMB, limit.LimitMB);

                        // Aplica SetProcessWorkingSetSize com teto sugerido em CADA instância
                        long targetBytes = targetMB * 1024 * 1024;
                        int trimmedCount = 0;

                        foreach (var proc in processes)
                        {
                            try
                            {
                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION,
                                    false, proc.Id);

                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        // SetProcessWorkingSetSize com teto = targetBytes
                                        bool ok = SetProcessWorkingSetSizeEx(
                                            handle,
                                            (IntPtr)(-1),
                                            (IntPtr)targetBytes,
                                            QUOTA_LIMITS_HARDWS_MAX_DISABLE);

                                        if (!ok)
                                        {
                                            // Fallback: EmptyWorkingSet se SetProcessWorkingSetSizeEx falhar
                                            EmptyWorkingSet(handle);
                                        }
                                        trimmedCount++;
                                    }
                                    finally { CloseHandle(handle); }
                                }
                                else
                                {
                                    // Fallback sem handle privilegiado
                                    KitLugia.Core.MemoryOptimizer.EmptyProcessWorkingSet(proc.Id);
                                    trimmedCount++;
                                }
                            }
                            catch { }
                        }

                        limit.LastTrimTime = DateTime.Now;
                        limit.ConsecutiveTrimCount++;


                        // if (trimmedCount > 0)
                        // {
                        //     KitLugia.Core.Logger.Log(
                        //         $"💾 RAM Limiter: {limit.ProcessName} " +
                        //         $"{totalRamMB}MB → alvo {targetMB}MB " +
                        //         $"(limite {limit.LimitMB}MB, {trimmedCount} instância(s), " +
                        //         $"trim #{limit.ConsecutiveTrimCount})");
                        // }
                    }
                    else if (!exceedsLimit)
                    {
                        // Processo voltou ao normal — reseta contador
                        if (limit.ConsecutiveTrimCount > 0)
                            limit.ConsecutiveTrimCount = 0;
                    }

                    foreach (var proc in processes) try { proc.Dispose(); } catch { }
                }
                catch { }
            }
        }

        /// <summary>
        /// Aplica o limitador de CPU por Job Object (hard cap) para processos configurados.
        /// </summary>
        private void ApplyProcessCpuLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            foreach (var limit in _processRamLimits.Values)
            {
                if (!limit.Enabled) continue;
                var cfg = limit.EngineConfig;
                if (cfg == null || !cfg.CpuLimitEnabled) continue;

                string key = limit.ProcessName.ToLowerInvariant().Replace(".exe", "");
                int percent = Math.Clamp(cfg.CpuLimitPercent, 1, 99);

                try
                {
                    var processes = Process.GetProcessesByName(limit.ProcessName);
                    if (processes.Length == 0)
                    {
                        foreach (var p in processes) p.Dispose();
                        continue;
                    }

                    if (_cpuJobObjects.TryGetValue(key, out var existingJob) && existingJob != IntPtr.Zero)
                    {
                        Win32Api.CloseHandle(existingJob);
                    }

                    IntPtr hJob = Win32Api.CreateJobObject(IntPtr.Zero, $"KitLugia_CPULimit_{key}");
                    if (hJob == IntPtr.Zero)
                    {
                        foreach (var p in processes) p.Dispose();
                        continue;
                    }

                    var cpuInfo = new Win32Api.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                    {
                        ControlFlags = Win32Api.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | Win32Api.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                        CpuRate = (uint)(percent * 100)
                    };

                    int size = System.Runtime.InteropServices.Marshal.SizeOf(cpuInfo);
                    IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
                    bool setOk = false;
                    try
                    {
                        System.Runtime.InteropServices.Marshal.StructureToPtr(cpuInfo, ptr, false);
                        setOk = Win32Api.SetInformationJobObject(hJob, Win32Api.JobObjectCpuRateControlInformation, ptr, (uint)size);
                    }
                    finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }

                    if (!setOk)
                    {
                        Win32Api.CloseHandle(hJob);
                        foreach (var p in processes) p.Dispose();
                        continue;
                    }

                    int assigned = 0;
                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (Win32Api.AssignProcessToJobObject(hJob, proc.Handle))
                                assigned++;
                        }
                        catch { }
                        proc.Dispose();
                    }

                    if (assigned > 0)
                        _cpuJobObjects[key] = hJob;
                    else
                        Win32Api.CloseHandle(hJob);
                }
                catch { }
            }
        }

        /// <summary>
        /// Retorna o cooldown dinâmico baseado no comportamento do processo.
        /// </summary>
        private TimeSpan GetTrimCooldown(ProcessRamLimit limit)
        {
            // Base: 1 segundo + 500ms por trim consecutivo (máximo 10s)
            int baseMs = 1000;
            int penaltyMs = Math.Min(9000, _ramLimiterIntervalMs * limit.ConsecutiveTrimCount);
            return TimeSpan.FromMilliseconds(baseMs + penaltyMs);
        }

        /// <summary>
        /// Retorna o uso atual de RAM de todos os processos monitorados.
        /// </summary>
        public List<(string Name, long CurrentMB, long LimitMB, bool Exceeded)> GetProcessRamStatus()
        {
            var result = new List<(string, long, long, bool)>();
            foreach (var limit in _processRamLimits.Values)
            {
                result.Add((limit.ProcessName, limit.LastKnownMB, limit.LimitMB,
                    limit.LastKnownMB > limit.LimitMB));
            }
            return result;
        }
        

        
        /// <summary>
        /// Inicia o monitor avançado de processos com análise comportamental
        /// </summary>
        public void StartAdvancedMonitor()
        {
            if (_advancedMonitorTimer != null) return; // Já iniciado
            
            _advancedMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_advancedMonitorIntervalMs)
            };
            _advancedMonitorTimer.Tick += AdvancedMonitor_Tick;
            _advancedMonitorTimer.Start();
            
            KitLugia.Core.Logger.Log($"🔥 Monitor Avançado iniciado ({_advancedMonitorIntervalMs}ms)");
        }
        
        /// <summary>
        /// Para o monitor avançado
        /// </summary>
        public void StopAdvancedMonitor()
        {
            _advancedMonitorTimer?.Stop();
            _advancedMonitorTimer = null;
            KitLugia.Core.Logger.Log("🔥 Monitor Avançado parado");
        }
        
        /// <summary>
        /// Evento principal do monitor avançado
        /// </summary>
        private void AdvancedMonitor_Tick(object? sender, EventArgs e)
        {
            try
            {
                UpdateSystemStats();
                UpdateProcessCache();
                AnalyzeProcessBehaviors();
                CheckSmartAlerts();
                UpdateTrayIconAdvanced();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AdvancedMonitor_Tick", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza estatísticas do sistema (CPU, RAM total)
        /// </summary>
        private void UpdateSystemStats()
        {
            if (DateTime.Now - _lastSystemStatsUpdate < TimeSpan.FromSeconds(1))
                return; // Limitar atualizações a 1 por segundo
                
            try
            {
                // CORREÇÃO: Usar GlobalMemoryStatusEx para RAM total REAL do sistema
                var memStatus = new Win32Api.MEMORYSTATUSEX();
                Win32Api.GlobalMemoryStatusEx(memStatus);
                _totalSystemRamMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                _availableRamMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                
                // CPU usage (Performance Counter)
                using var cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                _currentCpuUsage = cpuCounter.NextValue();
                
                _lastSystemStatsUpdate = DateTime.Now;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateSystemStats", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza cache de processos com informações detalhadas
        /// </summary>
        private void UpdateProcessCache()
        {
            try
            {
                var processes = Process.GetProcesses();
                _activeProcessCount = 0;
                
                foreach (var proc in processes)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(proc.ProcessName)) continue;
                        
                        var processInfo = new ProcessInfo
                        {
                            ProcessId = proc.Id,
                            ProcessName = proc.ProcessName.ToLowerInvariant(),
                            WorkingSetMB = proc.WorkingSet64 / (1024 * 1024),
                            VirtualMemoryMB = proc.VirtualMemorySize64 / (1024 * 1024),
                            StartTime = proc.StartTime,
                            IsResponding = proc.Responding,
                            MainWindowTitle = GetMainWindowTitle(proc.Id),
                            CpuUsage = GetProcessCpuUsage(proc),
                            ThreadCount = proc.Threads.Count,
                            HandleCount = proc.HandleCount
                        };
                        
                        _processCache.AddOrUpdate(processInfo.ProcessName, processInfo, (_, _) => processInfo);
                        _activeProcessCount++;
                    }
                    catch
                    {
                        // Ignora processos que não podem ser acessados
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateProcessCache", ex.Message);
            }
        }
        
        /// <summary>
        /// Analisa comportamento dos processos ao longo do tempo
        /// </summary>
        private void AnalyzeProcessBehaviors()
        {
            if (!_enableBehaviorAnalysis) return;
            
            try
            {
                foreach (var kvp in _processCache)
                {
                    var processName = kvp.Key;
                    var currentInfo = kvp.Value;
                    
                    var behavior = _processBehaviors.GetOrAdd(processName, _ => new ProcessBehavior
                    {
                        ProcessName = processName,
                        FirstSeen = DateTime.Now,
                        LastSeen = DateTime.Now,
                        PeakRamMB = currentInfo.WorkingSetMB,
                        PeakCpuUsage = currentInfo.CpuUsage,
                        SampleCount = 1,
                        AverageRamMB = currentInfo.WorkingSetMB,
                        AverageCpuUsage = currentInfo.CpuUsage
                    });
                    
                    // Atualiza estatísticas
                    behavior.LastSeen = DateTime.Now;
                    behavior.SampleCount++;
                    
                    if (currentInfo.WorkingSetMB > behavior.PeakRamMB)
                        behavior.PeakRamMB = currentInfo.WorkingSetMB;
                        
                    if (currentInfo.CpuUsage > behavior.PeakCpuUsage)
                        behavior.PeakCpuUsage = currentInfo.CpuUsage;
                        
                    // Média móvel (simplificada)
                    behavior.AverageRamMB = (long)((behavior.AverageRamMB * 0.9) + (currentInfo.WorkingSetMB * 0.1));
                    behavior.AverageCpuUsage = (behavior.AverageCpuUsage * 0.9) + (currentInfo.CpuUsage * 0.1);
                    
                    // Detecta anomalias
                    var ramAnomaly = currentInfo.WorkingSetMB > behavior.AverageRamMB * 2.0;
                    var cpuAnomaly = currentInfo.CpuUsage > behavior.AverageCpuUsage * 2.0;
                    
                    if (ramAnomaly || cpuAnomaly)
                    {
                        behavior.AnomalyCount++;
                        KitLugia.Core.Logger.Log($"⚠️ Anomalia detectada: {processName} RAM:{currentInfo.WorkingSetMB}MB CPU:{currentInfo.CpuUsage:F1}%");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AnalyzeProcessBehaviors", ex.Message);
            }
        }
        
        /// <summary>
        /// Verifica alertas inteligentes baseados em limiares
        /// </summary>
        private void CheckSmartAlerts()
        {
            if (!_enableSmartAlerts) return;
            
            try
            {
                foreach (var kvp in _processCache)
                {
                    var processName = kvp.Key;
                    var info = kvp.Value;
                    
                    var alert = _processAlerts.GetOrAdd(processName, _ => new ProcessAlert
                    {
                        ProcessName = processName,
                        LastAlertTime = DateTime.MinValue
                    });
                    
                    bool shouldAlert = false;
                    string alertType = "";
                    
                    // Alerta de RAM
                    if (info.WorkingSetMB > _highRamThresholdMB)
                    {
                        shouldAlert = true;
                        alertType = $"RAM Alta: {info.WorkingSetMB}MB > {_highRamThresholdMB}MB";
                    }
                    
                    // Alerta de CPU
                    if (info.CpuUsage > _highCpuThresholdPercent)
                    {
                        shouldAlert = true;
                        alertType += (string.IsNullOrEmpty(alertType) ? "" : ", ") + $"CPU Alta: {info.CpuUsage:F1}% > {_highCpuThresholdPercent}%";
                    }
                    
                    // Processo não responsivo
                    if (!info.IsResponding)
                    {
                        shouldAlert = true;
                        alertType += (string.IsNullOrEmpty(alertType) ? "" : ", ") + "Não Responsivo";
                    }
                    
                    // Envia alerta se passou o cooldown (5 minutos)
                    if (shouldAlert && DateTime.Now - alert.LastAlertTime > TimeSpan.FromMinutes(5))
                    {
                        alert.LastAlertTime = DateTime.Now;
                        alert.AlertCount++;
                        
                        KitLugia.Core.Logger.Log($"🚨 Alerta: {processName} - {alertType}");
                        
                        // Mostra notificação no tray (se habilitado)
                        if (_trayIcon != null && _trayIcon.Visible)
                        {
                            _trayIcon.ShowBalloonTip(3000, "KitLugia Monitor", 
                                $"{processName}: {alertType}", ToolTipIcon.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CheckSmartAlerts", ex.Message);
            }
        }
        
        /// <summary>
        /// Atualiza ícone do tray com informações avançadas
        /// </summary>
        private void UpdateTrayIconAdvanced()
        {
            try
            {
                if (_trayIcon == null) return;
                
                var ramPercent = RamUsagePercent;
                var cpuPercent = _currentCpuUsage;
                
                // Ícone dinâmico baseado no uso
                var iconText = $"RAM:{ramPercent:F0}% CPU:{cpuPercent:F0}%";
                _trayIcon.Text = iconText;
                
                // Atualiza tooltip detalhado
                var tooltip = $"KitLugia Monitor Avançado\n" +
                           $"RAM: {(_totalSystemRamMB - _availableRamMB)}MB / {_totalSystemRamMB}MB ({ramPercent:F1}%)\n" +
                           $"CPU: {cpuPercent:F1}%\n" +
                           $"Processos Ativos: {_activeProcessCount}\n" +
                           $"Limites RAM: {_processRamLimits.Count} configurados";
                           
                _trayIcon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "..." : tooltip;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("UpdateTrayIconAdvanced", ex.Message);
            }
        }
        
        /// <summary>
        /// Obtém o título da janela principal de um processo
        /// </summary>
        private string GetMainWindowTitle(int processId)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    // Implementação simplificada - poderia usar EnumWindows para mais precisão
                    return $"PID:{processId}";
                }
                return $"PID:{processId}";
            }
            catch
            {
                return $"PID:{processId}";
            }
        }
        
        

        
        /// <summary>
        /// Valida integridade dos dados de limites de processo
        /// </summary>
        private bool ValidateProcessLimitsIntegrity()
        {
            try
            {
                if (_processRamLimits == null) return false;
                
                foreach (var kvp in _processRamLimits)
                {
                    var limit = kvp.Value;
                    if (string.IsNullOrEmpty(limit.ProcessName)) return false;
                    if (limit.LimitMB <= 0 || limit.LimitMB > 1024 * 1024) return false; // Máximo 1TB
                    if (limit.PeakRamMB < 0) return false;
                    if (limit.LastTrimTime > DateTime.Now) return false;
                    if (limit.ConsecutiveTrimCount < 0) return false;
                }
                
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Carrega do arquivo principal
        /// </summary>
        private bool LoadFromMainFile()
        {
            try
            {
                if (!System.IO.File.Exists(_processLimitsPath))
                {
                    KitLugia.Core.Logger.Log($"📂 Arquivo principal não encontrado: {_processLimitsPath}");
                    return false;
                }

                return LoadFromFile(_processLimitsPath, "principal");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("LoadFromMainFile", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Carrega do arquivo de backup
        /// </summary>
        private bool LoadFromBackup()
        {
            try
            {
                if (!System.IO.File.Exists(_backupPath))
                {
                    KitLugia.Core.Logger.Log($"📂 Arquivo de backup não encontrado: {_backupPath}");
                    return false;
                }

                return LoadFromFile(_backupPath, "backup");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("LoadFromBackup", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Carrega de um arquivo específico
        /// </summary>
        private bool LoadFromFile(string filePath, string sourceName)
        {
            try
            {
                string json = System.IO.File.ReadAllText(filePath);
                

                if (string.IsNullOrWhiteSpace(json) || json.Length < 10)
                {
                    KitLugia.Core.Logger.Log($"⚠️ JSON {sourceName} está vazio ou muito pequeno");
                    return false;
                }
                
                var list = System.Text.Json.JsonSerializer.Deserialize<List<ProcessRamLimit>>(json);
                if (list == null)
                {
                    KitLugia.Core.Logger.Log($"⚠️ JSON {sourceName} não pôde ser desserializado");
                    return false;
                }

                _processRamLimits.Clear();
                int loadedCount = 0;
                foreach (var limit in list)
                {
                    if (!string.IsNullOrEmpty(limit.ProcessName) && limit.LimitMB > 0)
                    {
                        _processRamLimits[limit.ProcessName] = limit;
                        loadedCount++;
                        KitLugia.Core.Logger.Log($"   ✅ {limit.ProcessName}: {limit.LimitMB}MB (Enabled: {limit.Enabled})");
                    }
                    else
                    {
                        KitLugia.Core.Logger.Log($"   ⚠️ Ignorando limite inválido: {limit.ProcessName}");
                    }
                }

                KitLugia.Core.Logger.Log($"💾 {loadedCount}/{list.Count} limite(s) carregado(s) do {sourceName}.");
                return loadedCount > 0;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError($"LoadFrom{sourceName}", ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// Cria configurações padrão
        /// </summary>
        private void CreateDefaultConfiguration()
        {
            try
            {
                _processRamLimits.Clear();
                

                var defaultLimits = new List<(string name, long limitMB)>
                {
                    ("chrome", 2048),
                    ("firefox", 1536),
                    ("code", 1024),
                    ("explorer", 512),
                    ("msedge", 2048)
                };
                
                foreach (var (name, limit) in defaultLimits)
                {
                    _processRamLimits[name] = new ProcessRamLimit
                    {
                        ProcessName = name,
                        LimitMB = limit,
                        Enabled = false, // Desabilitado por padrão
                        Description = $"Limite padrão para {name}",
                        LastKnownMB = 0,
                        PeakRamMB = 0,
                        LastTrimTime = DateTime.MinValue,
                        ConsecutiveTrimCount = 0
                    };
                }
                
                KitLugia.Core.Logger.Log("🔧 Configurações padrão criadas com sucesso");
                SaveProcessLimits(); // Salva as configurações padrão
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CreateDefaultConfiguration", ex.Message);
            }
        }
        
        /// <summary>
        /// Cria backup se necessário
        /// </summary>
        private void CreateBackupIfNeeded()
        {
            try
            {
                if (DateTime.Now - _lastBackupTime < _backupInterval) return;
                
                if (!System.IO.File.Exists(_processLimitsPath)) return;
                
                var backupDir = System.IO.Path.GetDirectoryName(_backupPath)!;
                if (!System.IO.Directory.Exists(backupDir)) System.IO.Directory.CreateDirectory(backupDir);
                
                System.IO.File.Copy(_processLimitsPath, _backupPath, true);
                _lastBackupTime = DateTime.Now;
                
                KitLugia.Core.Logger.Log($"💾 Backup criado: {_backupPath}");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("CreateBackupIfNeeded", ex.Message);
            }
        }
        
        /// <summary>
        /// Restaura do backup
        /// </summary>
        private void RestoreFromBackup()
        {
            try
            {
                if (!System.IO.File.Exists(_backupPath))
                {
                    KitLugia.Core.Logger.Log("⚠️ Backup não encontrado para restauração");
                    return;
                }
                
                System.IO.File.Copy(_backupPath, _processLimitsPath, true);
                KitLugia.Core.Logger.Log("🔄 Configurações restauradas do backup");
                
                // Tenta carregar novamente
                LoadFromMainFile();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("RestoreFromBackup", ex.Message);
            }
        }
        
        /// <summary>
        /// Tenta recuperar dados corrompidos
        /// </summary>
        private void AttemptDataRecovery()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔧 Iniciando recuperação de dados...");
                
                // Remove entradas inválidas
                var invalidKeys = _processRamLimits.Where(kvp => 
                    string.IsNullOrEmpty(kvp.Value.ProcessName) || 
                    kvp.Value.LimitMB <= 0).Select(kvp => kvp.Key).ToList();
                    
                foreach (var key in invalidKeys)
                {
                    _processRamLimits.TryRemove(key, out _);
                    KitLugia.Core.Logger.Log($"🗑️ Removida entrada inválida: {key}");
                }
                
                // Corrige valores inválidos
                foreach (var kvp in _processRamLimits)
                {
                    var limit = kvp.Value;
                    
                    if (limit.LimitMB > 1024 * 1024) // Máximo 1TB
                    {
                        limit.LimitMB = 2048; // 2GB padrão
                        KitLugia.Core.Logger.Log($"🔧 Corrigido limite exagerado: {limit.ProcessName}");
                    }
                    
                    if (limit.PeakRamMB < 0) limit.PeakRamMB = 0;
                    if (limit.LastTrimTime > DateTime.Now) limit.LastTrimTime = DateTime.MinValue;
                    if (limit.ConsecutiveTrimCount < 0) limit.ConsecutiveTrimCount = 0;
                }
                
                KitLugia.Core.Logger.Log("✅ Recuperação de dados concluída");
                SaveProcessLimits();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("AttemptDataRecovery", ex.Message);
                CreateDefaultConfiguration(); // Último recurso
            }
        }
        
        /// <summary>
        /// Manipula erros de forma robusta
        /// </summary>
        private void HandleRobustnessError(string operation, Exception ex)
        {
            _consecutiveErrors++;
            _lastErrorTime = DateTime.Now;
            
            KitLugia.Core.Logger.LogError($"{operation} (Erro #{_consecutiveErrors})", ex.Message);
            

            if (_consecutiveErrors >= _maxConsecutiveErrors)
            {
                _isInSafeMode = true;
                KitLugia.Core.Logger.Log($"🚨 Sistema entrando em modo seguro após {_maxConsecutiveErrors} erros consecutivos");
                
                // Desativa funcionalidades críticas para evitar mais erros
                StopRamLimiterTimer();
                StopAdvancedMonitor();
                
                // Tenta restaurar do backup
                if (operation.Contains("Load"))
                {
                    RestoreFromBackup();
                }
            }
            

            if (DateTime.Now - _lastErrorTime < _errorCooldown)
            {
                KitLugia.Core.Logger.Log("⏱️ Sistema em cooldown devido a erros recentes");
                return;
            }
        }
        
        /// <summary>
        /// Verifica saúde do sistema
        /// </summary>
        public bool IsSystemHealthy()
        {
            return !_isInSafeMode && 
                   _consecutiveErrors < _maxConsecutiveErrors &&
                   (DateTime.Now - _lastErrorTime) > _errorCooldown;
        }
        
        /// <summary>
        /// Força saída do modo seguro
        /// </summary>
        public void ExitSafeMode()
        {
            lock (_robustnessLock)
            {
                _isInSafeMode = false;
                _consecutiveErrors = 0;
                _lastErrorTime = DateTime.MinValue;
                
                KitLugia.Core.Logger.Log("✅ Sistema saiu do modo seguro");
                
                // Reinicia serviços se necessário
                if (!_processRamLimits.IsEmpty)
                {
                    StartRamLimiterTimer();
                }
                
                if (_enableSmartAlerts || _enableBehaviorAnalysis)
                {
                    StartAdvancedMonitor();
                }
            }
        }
        
        /// <summary>
        /// Mostra relatório completo de status do Tray e RAM Limiter
        /// </summary>
        private void ShowTrayStatusReport()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔔 === STATUS DO TRAY ICON SERVICE ===");
                KitLugia.Core.Logger.Log($"📊 Tray Habilitado: {(IsTrayEnabled ? "✅ SIM" : "❌ NÃO")}");
                KitLugia.Core.Logger.Log($"🔄 Close to Tray: {(CloseToTray ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"🧹 Auto Clean: {(AutoCleanEnabled ? "✅ ATIVO" : "❌ INATIVO")} (Limite: {AutoCleanThresholdPercent}%)");
                KitLugia.Core.Logger.Log($"🎮 GameBoost: {(GamePriorityEnabled ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"📈 Monitor Avançado: {(EnableSmartAlerts || EnableBehaviorAnalysis ? "✅ ATIVO" : "❌ INATIVO")}");
                KitLugia.Core.Logger.Log($"⚡ Smart Alerts: {(EnableSmartAlerts ? "✅ ATIVO" : "❌ INATIVO")} (RAM: {HighRamThresholdMB}MB, CPU: {HighCpuThresholdPercent}%)");
                KitLugia.Core.Logger.Log($"🔍 Behavior Analysis: {(EnableBehaviorAnalysis ? "✅ ATIVO" : "❌ INATIVO")}");
                
                // Status do RAM Limiter
                var activeLimits = _processRamLimits.Where(kvp => kvp.Value.Enabled).ToList();
                if (activeLimits.Any())
                {
                    KitLugia.Core.Logger.Log($"💾 RAM Limiter: ✅ ATIVO ({activeLimits.Count} processo(s) monitorado(s))");
                    foreach (var (name, limit) in activeLimits)
                    {
                        KitLugia.Core.Logger.Log($"   🎯 {name}: {limit.LimitMB}MB {(limit.Enabled ? "✅" : "❌")}");
                    }
                }
                else
                {
                    KitLugia.Core.Logger.Log("💾 RAM Limiter: ❌ INATIVO (nenhum processo selecionado)");
                }
                
                // Status de saúde do sistema
                if (IsSystemHealthy())
                {
                    KitLugia.Core.Logger.Log("🏥 Sistema: ✅ SAUDÁVEL");
                }
                else
                {
                    KitLugia.Core.Logger.Log("🏥 Sistema: ⚠️ MODO SEGURO");
                }
                
                KitLugia.Core.Logger.Log("🔔 ======================================");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("ShowTrayStatusReport", ex.Message);
            }
        }
        

        
        public class ProcessInfo
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; } = "";
            public long WorkingSetMB { get; set; }
            public long VirtualMemoryMB { get; set; }
            public DateTime StartTime { get; set; }
            public bool IsResponding { get; set; }
            public string MainWindowTitle { get; set; } = "";
            public double CpuUsage { get; set; }
            public int ThreadCount { get; set; }
            public int HandleCount { get; set; }
        }
        
        public class ProcessAlert
        {
            public string ProcessName { get; set; } = "";
            public DateTime LastAlertTime { get; set; }
            public int AlertCount { get; set; }
            public string LastAlertType { get; set; } = "";
        }
        
        public class ProcessBehavior
        {
            public string ProcessName { get; set; } = "";
            public DateTime FirstSeen { get; set; }
            public DateTime LastSeen { get; set; }
            public long PeakRamMB { get; set; }
            public double PeakCpuUsage { get; set; }
            public long AverageRamMB { get; set; }
            public double AverageCpuUsage { get; set; }
            public int SampleCount { get; set; }
            public int AnomalyCount { get; set; }
            public TimeSpan TotalRuntime => LastSeen - FirstSeen;
        }
        

        public class ProcessRamLimit
        {
            public string ProcessName { get; set; } = "";
            public long LimitMB { get; set; } = 1024;
            public bool Enabled { get; set; } = false;
            public string Description { get; set; } = "";
            public bool IsForeground { get; set; } = false;
            public long LastKnownMB { get; set; } = 0;
            public long PeakRamMB { get; set; } = 0;
            public DateTime LastTrimTime { get; set; } = DateTime.MinValue;
            public int ConsecutiveTrimCount { get; set; } = 0;
            public ProcessEngineConfig? EngineConfig { get; set; } = null;
        }

        public class ProcessEngineConfig
        {
            public string CpuPriority { get; set; } = "High";
            public int IoPriorityLevel { get; set; } = 1;
            public int PagePriorityLevel { get; set; } = 1;
            public bool TimerBoost { get; set; } = false;
            public bool EcoQoSEnabled { get; set; } = false;
            public bool ProBalance { get; set; } = false;
            public int ProBalanceCpuThreshold { get; set; } = 5;
            public bool CpuLimitEnabled { get; set; } = false;
            public int CpuLimitPercent { get; set; } = 50;
            public bool NetworkBoost { get; set; } = false;
            public int ThreadMemoryPriority { get; set; } = 0;
            public bool ThreadEfficiencyMode { get; set; } = false;
            public bool GameClassInfo { get; set; } = true;
            public bool Win32PrioritySeparation { get; set; } = true;
        }
    }


    public class CustomEngineConfig
    {
        public string CpuPriority { get; set; } = "High";
        public int IoPriorityLevel { get; set; } = 1;
        public int PagePriorityLevel { get; set; } = 1;
        public bool TimerBoost { get; set; } = false;
        public bool EcoQoSEnabled { get; set; } = false;
        public bool ProBalance { get; set; } = false;
        public int ProBalanceCpuThreshold { get; set; } = 5;
        public bool CpuLimitEnabled { get; set; } = false;
        public int CpuLimitPercent { get; set; } = 50;
        public bool NetworkBoost { get; set; } = false;
        public int ThreadMemoryPriority { get; set; } = 0;
        public bool ThreadEfficiencyMode { get; set; } = false; // false=P-cores, true=E-cores
        public bool GameClassInfo { get; set; } = true; // Sinaliza ao Windows como jogo
        public bool Win32PrioritySeparation { get; set; } = true; // Registry scheduler boost
    }
}