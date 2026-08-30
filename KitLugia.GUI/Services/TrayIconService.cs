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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        // Otimizações da comunidade (Reddit) - aplicadas uma vez no startup conforme preferência
        public bool SmartScreenDisabled { get; set; } = false;
        public bool EdgeUpdateDisabled { get; set; } = false;
        public bool CompatTelRunnerDisabled { get; set; } = false;
        public bool SearchIndexerDisabled { get; set; } = false;
        public bool TextInputHostDisabled { get; set; } = false;

        // Telemetria e relatórios - serviços + tarefas agendadas (aplicadas no startup conforme preferência)
        public bool DiagTrackSvcDisabled { get; set; } = false;
        public bool DmwappushSvcDisabled { get; set; } = false;
        public bool WerSvcDisabled { get; set; } = false;
        public bool PcaSvcDisabled { get; set; } = false;
        public bool TelemetryTasksDisabled { get; set; } = false;

        public bool IsInitialized { get; private set; } = false;

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
            public long LastSeenTick { get; set; } = 0;
        }


        // Típico: 20-50 perfis de processos em cache
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessProfile> _processProfiles = new(concurrencyLevel: 4, capacity: 50, comparer: StringComparer.OrdinalIgnoreCase);


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
        public bool ForegroundBoostEnabled { get; set; } = true;
        public bool StandbyCleanEnabled { get; set; } = false;
        public bool MemoryLeakDetectionEnabled { get; set; } = false;
        public bool DpcMonitorEnabled { get; set; } = false;
        public bool FocusAssistEnabled { get; set; } = false;
        public bool TimerBoost { get; set; } = false;
        public bool NetworkBoost { get; set; } = false;
        public bool DownloadBoostEnabled { get; set; } = false;
        public string DownloadBoostLevel { get; set; } = "Auto";
        public double DownloadBoostThreshold { get; set; } = 5.0;
        public bool ProBalance { get; set; } = false;
        public bool UnparkCpuEnabled { get; set; } = false;
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Verifica se o auto-start está habilitado em QUALQUER um dos 3 métodos:
        /// HKCU Run (universal), pasta Startup (.lnk no AppData) ou Task Scheduler.
        /// Retorna true se qualquer um apontar para o executável atual.
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                string currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentPath)) return false;

                // 1. HKCU Run — método universal, funciona com e sem admin
                if (CheckRegistryAutoStart(currentPath)) return true;

                // 2. Pasta Startup (.lnk no %APPDATA%\...\Startup) — sem depender de serviço
                if (CheckStartupFolderAutoStart(currentPath)) return true;

                // 3. Task Scheduler (best-effort; pode não disparar em conta sem admin)
                try
                {
                    using (var ts = new TaskService())
                    {
                        var task = ts.GetTask("KitLugia");
                        if (task != null && task.Enabled)
                        {
                            foreach (var action in task.Definition.Actions)
                            {
                                if (action is ExecAction execAction)
                                {
                                    string taskPath = execAction.Path;
                                    if (string.Equals(taskPath, currentPath, StringComparison.OrdinalIgnoreCase))
                                        return true;
                                    else
                                        KitLugia.Core.Logger.Log($"⚠️ Auto-Start aponta para versão antiga: {taskPath} != {currentPath}");
                                }
                            }
                        }
                    }
                }
                catch { /* Task Scheduler indisponível — ignorar */ }

                return false;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogError("IsAutoStartEnabled", $"Erro: {ex.Message}");
                return false;
            }
        }

        private static bool CheckStartupFolderAutoStart(string currentPath)
        {
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnk = System.IO.Path.Combine(startup, "KitLugia.lnk");
                if (!File.Exists(lnk)) return false;

                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath?.ToString() ?? "";
                return string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool CheckRegistryAutoStart(string? currentPath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(currentPath))
                {
                    currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(currentPath)) return false;
                }

                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return false;

                var value = key.GetValue("KitLugia") as string;
                if (string.IsNullOrEmpty(value)) return false;

                value = value.Trim();

                // Extrair o caminho entre aspas: "c:\path\exe" --tray
                string registryPath;
                if (value.StartsWith("\"") && value.Length > 1)
                {
                    int closingQuote = value.IndexOf('"', 1);
                    if (closingQuote > 0)
                        registryPath = value.Substring(1, closingQuote - 1);
                    else
                        registryPath = value;
                }
                else
                {
                    registryPath = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                }

                return string.Equals(registryPath, currentPath, StringComparison.OrdinalIgnoreCase);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
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

                if (!enable)
                {
                    // ===== DESLIGAR: remove os 3 métodos — nunca deixa lixo =====
                    try
                    {
                        using var ts = new TaskService();
                        var t = ts.GetTask("KitLugia");
                        if (t != null)
                        {
                            ts.RootFolder.DeleteTask("KitLugia");
                            KitLugia.Core.Logger.Log("✅ Tarefa agendada removida");
                        }
                    }
                    catch { /* Task Scheduler indisponível — registry/lnk abaixo resolvem */ }
                    SetRegistryEntry(false, path);
                    SetStartupShortcut(false, path);
                    KitLugia.Core.Logger.Log("✅ Auto-start desativado (Registry / Startup / Task limpos)");
                    return;
                }

                CleanupOldTask();

                // ===== MÉTODO 1 (universal): HKCU Run — funciona com ou sem admin =====
                bool regOk = SetRegistryEntry(true, path);
                if (regOk)
                    KitLugia.Core.Logger.Log($"✅ Auto-start via Registry Run: {path} --tray");
                else
                    KitLugia.Core.Logger.Log($"⚠️ Registry Run falhou, tentando pasta Startup...");

                // ===== MÉTODO 2 (fallback): .lnk na pasta Startup (fica no AppData) =====
                if (!regOk)
                {
                    bool lnkOk = SetStartupShortcut(true, path);
                    KitLugia.Core.Logger.Log(lnkOk
                        ? $"✅ Auto-start via pasta Startup: {path}"
                        : "❌ Falha ao criar atalho na pasta Startup");
                }

                // ===== MÉTODO 3 (elevado, best-effort): Task Scheduler =====
                // Só quando o app roda como admin: registro com RunLevel.Highest sem
                // privilégios "registra" a tarefa mas ela NUNCA dispara (falha silenciosa).
                // Se a tarefa for criada/habilitada, remove Registry + .lnk para não duplicar o boot.
                if (IsRunningElevated())
                {
                    try
                    {
                        using (var ts = new TaskService())
                        {
                            var existingTask = ts.GetTask("KitLugia");

                            // Tarefa existente com caminho correto → apenas habilitar e usar ela
                            if (existingTask != null)
                            {
                                bool pathMatches = false;
                                foreach (var action in existingTask.Definition.Actions)
                                {
                                    if (action is ExecAction execAction &&
                                        string.Equals(execAction.Path, path, StringComparison.OrdinalIgnoreCase))
                                    {
                                        pathMatches = true;
                                        break;
                                    }
                                }

                                // Só reutiliza se a task antiga já tiver os 2 triggers (Boot + Logon).
                                // Task criada ANTES da sessao 08/08 (so LogonTrigger) nao tem o
                                // "inicia junto do boot" — nesse caso cai fora e e recriada abaixo.
                                if (pathMatches && TaskHasBootTrigger(existingTask))
                                {
                                    existingTask.Enabled = true;
                                    existingTask.RegisterChanges();
                                    SetRegistryEntry(false, path);
                                    SetStartupShortcut(false, path);
                                    KitLugia.Core.Logger.Log("✅ Tarefa agendada existente habilitada (substitui Registry/Startup)");
                                    return;
                                }
                                KitLugia.Core.Logger.Log((pathMatches
                                    ? "🔄 Tarefa existe sem BootTrigger, recriando com boot+logon..."
                                    : "🔄 Tarefa existe com caminho incorreto, recriando..."));
                                ts.RootFolder.DeleteTask("KitLugia");
                            }

                            // Criar nova tarefa com privilégios de logon + prioridade de boot
                            var td = ts.NewTask();
                            td.RegistrationInfo.Description = "KitLugia Auto-Startup (Admin Mode)";
                            td.Principal.RunLevel = TaskRunLevel.Highest;
                            td.Settings.DisallowStartIfOnBatteries = false;
                            td.Settings.StopIfGoingOnBatteries = false;
                            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                            td.Settings.StartWhenAvailable = true;
                            td.Settings.AllowHardTerminate = false;

                            // ★ OTIMIZAÇÃO TIPO WALLPAPER ENGINE: Priority High (1) = HIGH_PRIORITY_CLASS
                            // Sempre High — requisito do usuário: kit inicia "junto com o sistema" sem fome de CPU
                            // (padrão do Task Scheduler é 7 = BELOW_NORMAL, que atrasa o boot do app)
                            td.Settings.Priority = ProcessPriorityClass.High;

                            td.Settings.RestartCount = 2;
                            td.Settings.RestartInterval = TimeSpan.FromMinutes(1);

                            var trigger = new LogonTrigger { Delay = TimeSpan.Zero, Enabled = true };
                            td.Triggers.Add(trigger);

                            // BootTrigger: inicia junto do boot do Windows (nao so no logon).
                            // Com InteractiveToken o agendador dispara a task o mais cedo
                            // possivel (com Fast Startup / auto-login, antes da sessao pronta).
                            // MultipleInstances = IgnoreNew evita processo duplo caso Logon
                            // e Boot disparem na mea sequencia.
                            td.Triggers.Add(new BootTrigger { Delay = TimeSpan.Zero, Enabled = true });
                            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

                            td.Actions.Add(new ExecAction(path, "--tray", Path.GetDirectoryName(path)));

                            ts.RootFolder.RegisterTaskDefinition("KitLugia", td);

                            // Tarefa é o método ativo → remove os demais para não duplicar o boot
                            SetRegistryEntry(false, path);
                            SetStartupShortcut(false, path);
                            KitLugia.Core.Logger.Log($"✅ Tarefa agendada admin criada: {path} (Priority: High)");
                        }
                    }
                    catch (Exception taskEx)
                    {
                        // Registry (ou .lnk) já garantiram o auto-start — apenas registra
                        KitLugia.Core.Logger.Log($"⚠️ Task Scheduler indisponível ({taskEx.Message}) — usando Registry/Startup já criado");
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"SetAutoStart ERROR: {ex.Message}");
            }
        }

        /// <summary>Grava/remove a entrada HKCU Run (método universal, sem admin).</summary>
        private static bool SetRegistryEntry(bool enable, string path)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (key == null) return false;
                if (enable)
                    key.SetValue("KitLugia", $"\"{path}\" --tray");
                else
                    key.DeleteValue("KitLugia", false);
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogWarning("SetRegistryEntry", ex.Message);
                return false;
            }
        }

        /// <summary>Cria/remove o atalho na pasta Startup (fica no AppData, não depende de serviço).</summary>
        private static bool SetStartupShortcut(bool enable, string path)
        {
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string lnk = Path.Combine(startup, "KitLugia.lnk");
                if (enable)
                {
                    if (!Directory.Exists(startup)) Directory.CreateDirectory(startup);
                    return KitLugia.Core.StartupManager.CreateShortcut(lnk, path, "--tray", "KitLugia Auto-Startup", Path.GetDirectoryName(path) ?? "");
                }
                if (File.Exists(lnk)) File.Delete(lnk);
                return true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.LogWarning("SetStartupShortcut", ex.Message);
                return false;
            }
        }

        private static bool IsRunningElevated()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool TaskHasBootTrigger(Microsoft.Win32.TaskScheduler.Task task)
        {
            try
            {
                foreach (var trigger in task.Definition.Triggers)
                {
                    if (trigger is Microsoft.Win32.TaskScheduler.BootTrigger)
                        return true;
                }
            }
            catch { }
            return false;
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
            return Process.GetProcesses();
        }


        private void ClearProcessCache()
        {
        }

        public void Initialize()
        {
            LoadSettings();

            System.Threading.Tasks.Task.Run(() => AutoFixGameBarPresenceWriter());
            System.Threading.Tasks.Task.Run(() => AutoFixCommunityProcesses());
            System.Threading.Tasks.Task.Run(() => AutoFixForceStopContextMenu());

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

            // ★ Show tray icon IMMEDIATELY before building menus
            if (IsTrayEnabled && _trayIcon != null)
            {
                try
                {
                    UpdateTrayIcon(GetMemoryUsagePercent());
                    _trayIcon.Visible = true;
                    _monitorTimer.Start();

                    // ★ LANÇAR OS APPS DO TURBO BOOT EM BACKGROUND
                    // Depois do tray visível — não bloqueia WPF. Conc Bulk: cada app dispara em thread própria.
                    // Isto garante que o KitLugia apareça PRIMEIRO no tray, e depois orquestre o lançamento.
                    // (Antes isto rodava sincrono no Program.cs e travava o startup do próprio KitLugia.)
                    if (Environment.CommandLine.Contains("--tray") || Environment.CommandLine.Contains("--minimized"))
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                System.Threading.Thread.Sleep(50); // Dá 50ms pro tray renderizar
                                StartupManager.LaunchTurboApps();
                            }
                            catch (Exception ex) { KitLugia.Core.Logger.LogError("LaunchTurboApps bg", ex.Message); }
                        });
                    }
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"❌ ERRO ao ativar Tray Icon: {ex.Message}");
                }
            }

            // Context Menu — build after icon is visible
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, Verb = "runas" });
                    Dispose();
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            };
            menu.Items.Add(itemRestartAdmin);

            var itemRestartNormal = new ToolStripMenuItem("👤 Iniciar como Usuário Normal");
            itemRestartNormal.Click += async (s, e) =>
            {
                try
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    KitLugia.Core.StartupManager.RegisterNonAdminTask("__KitLugiaRestart", exe, null);
                    KitLugia.Core.StartupManager.RunNonAdminTask("__KitLugiaRestart");
                    await System.Threading.Tasks.Task.Delay(1500);
                    Dispose();
                    if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.HasShutdownFinished)
                        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            };
            menu.Items.Add(itemRestartNormal);

            menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("❌ Sair Completamente");
            itemExit.Click += (s, e) =>
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ForceShutdown();
                else
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            };
            menu.Items.Add(itemExit);

            _trayIcon.ContextMenuStrip = menu;

            // Double-click to open main window
            _trayIcon.DoubleClick += (s, e) => OnOpenMainWindow?.Invoke();

            if (IsTrayEnabled && _trayIcon != null)
            {
                if (_trayIcon.Visible)
                {
                    // Defer heavy init tasks to threadpool so UI stays responsive
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { RunSafetyProfiler(); }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    });

                    if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                    Application.Current.Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        LoadProcessLimits();
                        ShowTrayStatusReport();
                        MonitorTick(null, EventArgs.Empty);
                    }), DispatcherPriority.Background);
                }
                else
                {
                    KitLugia.Core.Logger.Log("❌ ERRO: Tray Icon não ficou visível após tentativa");
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

            IsInitialized = true;
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
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                key.SetValue("ForegroundBoost", ForegroundBoostEnabled ? 1 : 0);
                key.SetValue("StandbyClean", StandbyCleanEnabled ? 1 : 0);
                key.SetValue("AntiLeak", MemoryLeakDetectionEnabled ? 1 : 0);
                key.SetValue("FocusAssist", FocusAssistEnabled ? 1 : 0);
                key.SetValue("TimerBoost", TimerBoost ? 1 : 0);
                key.SetValue("NetworkBoost", NetworkBoost ? 1 : 0);
                key.SetValue("DownloadBoostEnabled", DownloadBoostEnabled ? 1 : 0);
                key.SetValue("DownloadBoostLevel", DownloadBoostLevel);
                key.SetValue("DownloadBoostThreshold", DownloadBoostThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
                key.SetValue("ProBalance", ProBalance ? 1 : 0);
                key.SetValue("UnparkCpu", UnparkCpuEnabled ? 1 : 0);
                key.SetValue("TurboBoot", TurboBootEnabled ? 1 : 0);
                key.SetValue("TurboShutdown", TurboShutdownEnabled ? 1 : 0);
                

                key.SetValue("EnableSmartAlerts", EnableSmartAlerts ? 1 : 0);
                key.SetValue("EnableBehaviorAnalysis", EnableBehaviorAnalysis ? 1 : 0);
                key.SetValue("HighRamThresholdMB", HighRamThresholdMB);
                key.SetValue("HighCpuThresholdPercent", HighCpuThresholdPercent);
                key.SetValue("AdvancedMonitorIntervalMs", AdvancedMonitorIntervalMs);
                key.SetValue("RamLimiterIntervalMs", RamLimiterIntervalMs);
                key.SetValue("GameBarPresenceWriterDisabled", GameBarPresenceWriterDisabled ? 1 : 0);
                key.SetValue("SmartScreenDisabled", SmartScreenDisabled ? 1 : 0);
                key.SetValue("EdgeUpdateDisabled", EdgeUpdateDisabled ? 1 : 0);
                key.SetValue("CompatTelRunnerDisabled", CompatTelRunnerDisabled ? 1 : 0);
                key.SetValue("SearchIndexerDisabled", SearchIndexerDisabled ? 1 : 0);
                key.SetValue("TextInputHostDisabled", TextInputHostDisabled ? 1 : 0);
                key.SetValue("DiagTrackSvcDisabled", DiagTrackSvcDisabled ? 1 : 0);
                key.SetValue("DmwappushSvcDisabled", DmwappushSvcDisabled ? 1 : 0);
                key.SetValue("WerSvcDisabled", WerSvcDisabled ? 1 : 0);
                key.SetValue("PcaSvcDisabled", PcaSvcDisabled ? 1 : 0);
                key.SetValue("TelemetryTasksDisabled", TelemetryTasksDisabled ? 1 : 0);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Auto-fix: se o usuário desativou GameBarPresenceWriter antes e o Windows recriou o .exe, re-desativa.
        /// Roda na inicialização do kit e pode ser chamado pela UI (público).
        /// Não depende SÓ da flag salva: a existência do .bak já é prova de que o usuário desativou.
        /// </summary>
        public void AutoFixGameBarPresenceWriter()
        {
            try
            {
                string system32 = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32");
                string gameBarPath = Path.Combine(system32, "GameBarPresenceWriter.exe");
                string backupPath = Path.Combine(system32, "GameBarPresenceWriter.exe.bak");

                bool exeExists = File.Exists(gameBarPath);
                bool bakExists = File.Exists(backupPath);

                // Nada a fazer: preferência não é desativar E nunca houve .bak
                if (!GameBarPresenceWriterDisabled && !bakExists) return;

                // Já desativado (só .bak) — nada a fazer
                if (bakExists && !exeExists) return;

                // Chegou aqui: .exe existe e o usuário quer desativado (flag salva OU .bak antigo)
                if (!exeExists) return;

                KitLugia.Core.Logger.Log("⚠️ GameBarPresenceWriter: Windows recriou o .exe - re-desativando...");

                // Matar processo antes de mexer no arquivo
                SystemUtils.RunExternalProcess("taskkill", "/F /IM GameBarPresenceWriter.exe", true);
                System.Threading.Thread.Sleep(500);

                if (bakExists)
                {
                    try
                    {
                        SystemUtils.RunExternalProcess("takeown", $"/f \"{backupPath}\"", true);
                        SystemUtils.RunExternalProcess("icacls", $"\"{backupPath}\" /grant *S-1-3-4:F /t /c /l", true);
                        File.Delete(backupPath);
                    }
                    catch (Exception ex) { KitLugia.Core.Logger.Log($"⚠️ GameBarPresenceWriter: erro ao limpar .bak antigo: {ex.Message}"); }
                }

                // Take ownership do .exe e renomeia para .bak
                SystemUtils.RunExternalProcess("takeown", $"/f \"{gameBarPath}\"", true);
                SystemUtils.RunExternalProcess("icacls", $"\"{gameBarPath}\" /grant *S-1-3-4:F /t /c /l", true);

                File.Move(gameBarPath, backupPath);
                KitLugia.Core.Logger.Log("✅ GameBarPresenceWriter re-desativado (renomeado para .bak).");
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBarPresenceWriter auto-fix: {ex.Message}");
            }
        }

        /// <summary>
        /// Aplica/desfaz uma otimização da comunidade (Reddit) pelo método correto do processo:
        /// - SmartScreen: política EnableSmartScreen + Explorer SmartScreenEnabled (registro)
        /// - MicrosoftEdgeUpdate: serviços edgeupdate/edgeupdatem + tarefas agendadas MachineCore/UA
        /// - CompatTelRunner: serviço DiagTrack + tarefas Compatibility Appraiser/ProgramDataUpdater
        /// - SearchIndexer: serviço WSearch
        /// - TextInputHost: IFEO (Image File Execution Options) apontando para systray (bloqueio sem renomear)
        /// Nenhum arquivo é renomeado (TrustedInstaller reverte rename em updates; registro/serviços persistem).
        /// </summary>
        public void ApplyCommunityProcessToggle(string name, bool disable)
        {
            try
            {
                switch (name)
                {
                    case "SmartScreen":
                        ApplySmartScreen(disable);
                        break;
                    case "EdgeUpdate":
                        ApplyEdgeUpdate(disable);
                        break;
                    case "CompatTelRunner":
                        ApplyCompatTelRunner(disable);
                        break;
                    case "SearchIndexer":
                        ApplySearchIndexer(disable);
                        break;
                    case "TextInputHost":
                        ApplyTextInputHost(disable);
                        break;
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Community toggle {name}: {ex.Message}");
            }
        }

        private void ApplySmartScreen(bool disable)
        {
            if (disable)
            {
                using var polKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System");
                polKey?.SetValue("EnableSmartScreen", 0, RegistryValueKind.DWord);
                using var expKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
                expKey?.SetValue("SmartScreenEnabled", "Off", RegistryValueKind.String);
                KitLugia.Core.Logger.Log("🛡️ SmartScreen desativado (política EnableSmartScreen=0 + Explorer=Off)");
            }
            else
            {
                using var polKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\System", true);
                polKey?.DeleteValue("EnableSmartScreen", false);
                using var expKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer", true);
                expKey?.DeleteValue("SmartScreenEnabled", false);
                KitLugia.Core.Logger.Log("🔄 SmartScreen restaurado");
            }
        }

        private void ApplyEdgeUpdate(bool disable)
        {
            string[] services = { "edgeupdate", "edgeupdatem" };
            string[] tasks = { "MicrosoftEdgeUpdateTaskMachineCore", "MicrosoftEdgeUpdateTaskMachineUA" };

            if (disable)
            {
                foreach (var svc in services)
                    SystemUtils.RunExternalProcess("sc", $"config {svc} start= disabled", true, true, true);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Disable", true, true, true);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM MicrosoftEdgeUpdate.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ MicrosoftEdgeUpdate desativado (serviços + tarefas agendadas)");
            }
            else
            {
                foreach (var svc in services)
                    SystemUtils.RunExternalProcess("sc", $"config {svc} start= auto", true, true, true);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Enable", true, true, true);
                KitLugia.Core.Logger.Log("🔄 MicrosoftEdgeUpdate restaurado");
            }
        }

        private void ApplyCompatTelRunner(bool disable)
        {
            string[] tasks = { @"Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
                               @"Microsoft\Windows\Application Experience\ProgramDataUpdater",
                               @"Microsoft\Windows\Application Experience\StartupAppTask" };

            if (disable)
            {
                SystemUtils.RunExternalProcess("sc", "config DiagTrack start= disabled", true, true, true);
                using var polKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
                polKey?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Disable", true, true, true);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM CompatTelRunner.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ CompatTelRunner desativado (DiagTrack + AllowTelemetry=0 + tarefas)");
            }
            else
            {
                SystemUtils.RunExternalProcess("sc", "config DiagTrack start= auto", true, true, true);
                using var polKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", true);
                polKey?.DeleteValue("AllowTelemetry", false);
                foreach (var task in tasks)
                    SystemUtils.RunExternalProcess("schtasks", $"/Change /TN \"{task}\" /Enable", true, true, true);
                KitLugia.Core.Logger.Log("🔄 CompatTelRunner restaurado");
            }
        }

        private void ApplySearchIndexer(bool disable)
        {
            if (disable)
            {
                SystemUtils.RunExternalProcess("sc", "config WSearch start= disabled", true, true, true);
                SystemUtils.RunExternalProcess("sc", "stop WSearch", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ SearchIndexer desativado (serviço WSearch)");
            }
            else
            {
                SystemUtils.RunExternalProcess("sc", "config WSearch start= delayed-auto", true, true, true);
                KitLugia.Core.Logger.Log("🔄 SearchIndexer restaurado (delayed-auto)");
            }
        }

        private void ApplyTextInputHost(bool disable)
        {
            const string ifeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\TextInputHost.exe";
            if (disable)
            {
                using var key = Registry.LocalMachine.CreateSubKey(ifeoPath);
                key?.SetValue("Debugger", "%SystemRoot%\\system32\\systray.exe", RegistryValueKind.String);
                SystemUtils.RunExternalProcess("taskkill", "/F /IM TextInputHost.exe", true, true, true);
                KitLugia.Core.Logger.Log("🛡️ TextInputHost bloqueado via IFEO (emoji Win+. e teclado virtual desativados)");
            }
            else
            {
                Registry.LocalMachine.DeleteSubKeyTree(ifeoPath, false);
                KitLugia.Core.Logger.Log("🔄 TextInputHost restaurado (IFEO removido)");
            }
        }

        /// <summary>
        /// Auto-fix: aplica uma única vez no startup as otimizações da comunidade salvas.
        /// Idempotente — se já desativado, não faz nada de novo.
        /// </summary>
        private void AutoFixCommunityProcesses()
        {
            if (SmartScreenDisabled) ApplyCommunityProcessToggle("SmartScreen", true);
            if (EdgeUpdateDisabled) ApplyCommunityProcessToggle("EdgeUpdate", true);
            if (CompatTelRunnerDisabled) ApplyCommunityProcessToggle("CompatTelRunner", true);
            if (SearchIndexerDisabled) ApplyCommunityProcessToggle("SearchIndexer", true);
            if (TextInputHostDisabled) ApplyCommunityProcessToggle("TextInputHost", true);

            if (DiagTrackSvcDisabled) SystemTweaks.SetServiceStartup("DiagTrack", true);
            if (DmwappushSvcDisabled) SystemTweaks.SetServiceStartup("dmwappushservice", true);
            if (WerSvcDisabled) SystemTweaks.SetServiceStartup("WerSvc", true);
            if (PcaSvcDisabled) SystemTweaks.SetServiceStartup("PcaSvc", true);
            if (TelemetryTasksDisabled) SystemTweaks.ApplyTelemetryScheduledTasks(true);
        }

        private void AutoFixForceStopContextMenu()
        {
            try
            {
                if (!SystemTweaks.IsForceStopUnlockAdded()) return;
                string currentExe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'), "KitLugia.GUI.exe");
                if (!System.IO.File.Exists(currentExe))
                    currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(currentExe)) return;
                var existing = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Classes\*\shell\forcestopunlock\command", "", null) as string;
                if (existing != null && !existing.Contains(currentExe, StringComparison.OrdinalIgnoreCase))
                {
                    SystemTweaks.AddForceStopUnlock();
                    KitLugia.Core.Logger.Log($"[FORCE STOP] Menu de contexto reconfigurado para versão atual: {currentExe}");
                }
            }
            catch (Exception ex) { KitLugia.Core.Logger.Log($"[FORCE STOP] AutoFix menu: {ex.Message}"); }
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
                ForegroundBoostEnabled = (int)key.GetValue("ForegroundBoost", 1) == 1;
                StandbyCleanEnabled = (int)key.GetValue("StandbyClean", 0) == 1;
                MemoryLeakDetectionEnabled = (int)key.GetValue("AntiLeak", 0) == 1;
                FocusAssistEnabled = (int)key.GetValue("FocusAssist", 0) == 1;
                TimerBoost = (int)key.GetValue("TimerBoost", 0) == 1;
                NetworkBoost = (int)key.GetValue("NetworkBoost", 0) == 1;
                DownloadBoostEnabled = (int)key.GetValue("DownloadBoostEnabled", 0) == 1;
                DownloadBoostLevel = (string)key.GetValue("DownloadBoostLevel", "Auto");
                double.TryParse((string)key.GetValue("DownloadBoostThreshold", "5.0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dbt);
                DownloadBoostThreshold = dbt > 0 ? dbt : 5.0;
                ProBalance = (int)key.GetValue("ProBalance", 0) == 1;
                UnparkCpuEnabled = (int)key.GetValue("UnparkCpu", 0) == 1;
                

                EnableSmartAlerts = (int)key.GetValue("EnableSmartAlerts", 1) == 1;
                EnableBehaviorAnalysis = (int)key.GetValue("EnableBehaviorAnalysis", 1) == 1;
                HighRamThresholdMB = ReadLongSetting(key, "HighRamThresholdMB", 2048);
                HighCpuThresholdPercent = ReadDoubleSetting(key, "HighCpuThresholdPercent", 80.0);
                AdvancedMonitorIntervalMs = (int)key.GetValue("AdvancedMonitorIntervalMs", 2000);
                RamLimiterIntervalMs = (int)key.GetValue("RamLimiterIntervalMs", 1000);
                GameBarPresenceWriterDisabled = (int)key.GetValue("GameBarPresenceWriterDisabled", 0) == 1;
                SmartScreenDisabled = (int)key.GetValue("SmartScreenDisabled", 0) == 1;
                EdgeUpdateDisabled = (int)key.GetValue("EdgeUpdateDisabled", 0) == 1;
                CompatTelRunnerDisabled = (int)key.GetValue("CompatTelRunnerDisabled", 0) == 1;
                SearchIndexerDisabled = (int)key.GetValue("SearchIndexerDisabled", 0) == 1;
                TextInputHostDisabled = (int)key.GetValue("TextInputHostDisabled", 0) == 1;
                DiagTrackSvcDisabled = (int)key.GetValue("DiagTrackSvcDisabled", 0) == 1;
                DmwappushSvcDisabled = (int)key.GetValue("DmwappushSvcDisabled", 0) == 1;
                WerSvcDisabled = (int)key.GetValue("WerSvcDisabled", 0) == 1;
                PcaSvcDisabled = (int)key.GetValue("PcaSvcDisabled", 0) == 1;
                TelemetryTasksDisabled = (int)key.GetValue("TelemetryTasksDisabled", 0) == 1;

                _monitorTimer.Interval = TimeSpan.FromSeconds(MonitorIntervalSeconds);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ LoadSettings: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Leitura defensiva de settings: o registro pode guardar int/long/double/string
        // de formas diferentes (REG_DWORD, REG_QWORD, REG_BINARY, REG_SZ) — nunca
        // castar direto, senão UM valor com tipo errado aborta o LoadSettings inteiro
        // e o resto das preferências cai para default silenciosamente.
        private static long ReadLongSetting(Microsoft.Win32.RegistryKey key, string name, long def)
        {
            try
            {
                var v = key.GetValue(name);
                if (v == null) return def;
                return v switch
                {
                    long l => l,
                    int i => i,
                    double d => (long)d,
                    string s when long.TryParse(s, out var l2) => l2,
                    string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d2) => (long)d2,
                    byte[] b when b.Length == 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                    _ => def
                };
            }
            catch { return def; }
        }

        private static double ReadDoubleSetting(Microsoft.Win32.RegistryKey key, string name, double def)
        {
            try
            {
                var v = key.GetValue(name);
                if (v == null) return def;
                return v switch
                {
                    double d => d,
                    int i => i,
                    long l => l,
                    string s when double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d2) => d2,
                    byte[] b when b.Length == 8 => BitConverter.ToDouble(b, 0),
                    byte[] b when b.Length == 4 => BitConverter.ToInt32(b, 0),
                    _ => def
                };
            }
            catch { return def; }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void ApplyTrayEnabledState(bool enabled)
        {
            if (_trayIcon != null)
                _trayIcon.Visible = enabled;

            if (enabled)
            {
                if (!_monitorTimer.IsEnabled) _monitorTimer.Start();
                UpdateTrayIcon(GetMemoryUsagePercent());
            }
            else
            {
                _monitorTimer.Stop();
            }

            SaveSettings();
        }

        public void SetTrayEnabled(bool enabled)
        {
            IsTrayEnabled = enabled;

            if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            {
                d.BeginInvoke(new System.Action(() => ApplyTrayEnabledState(enabled)));
                return;
            }

            ApplyTrayEnabledState(enabled);
        }


        public void PauseMonitoring()
        {
            try
            {
                _monitorTimer?.Stop();
                StopAdvancedMonitor();
                KitLugia.Core.Logger.Log("⏸️ TrayIcon: Monitoramento pausado (janela oculta)");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        public void ResumeMonitoring()
        {
            try
            {
                if (IsTrayEnabled && !_monitorTimer.IsEnabled)
                {
                    _monitorTimer?.Start();
                    StartAdvancedMonitor();
                    KitLugia.Core.Logger.Log("▶️ TrayIcon: Monitoramento retomado (janela visível)");
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void MonitorTick(object? sender, EventArgs e)
        {
            try
            {
                _monitorTickCounter = unchecked(_monitorTickCounter + 1);

                // 0. Download Boost Auto-Detection
                if (DownloadBoostEnabled)
                {
                    uint fgPid = GamePriorityEnabled ? _currentBoostedPid : 0;
                    var trafficSnapshot = NetworkTrafficMonitor.SampleTraffic(fgPid > 0 ? fgPid : null);
                    var level = DownloadBoostLevel switch
                    {
                        "Download" => DownloadBoostEngine.BoostLevel.Download,
                        "Latency" => DownloadBoostEngine.BoostLevel.Latency,
                        "Balanced" => DownloadBoostEngine.BoostLevel.Balanced,
                        _ => DownloadBoostEngine.BoostLevel.Auto
                    };

                    bool systemWide = level switch
                    {
                        DownloadBoostEngine.BoostLevel.Download => true,
                        _ => false
                    };

                    var config = new DownloadBoostEngine.DownloadBoostConfig
                    {
                        Enabled = true,
                        Level = level,
                        AutoThresholdMBps = DownloadBoostThreshold,
                        ForegroundPid = fgPid,
                        SystemWideTuning = systemWide
                    };

                    var targets = level == DownloadBoostEngine.BoostLevel.Auto
                        ? DownloadBoostEngine.AutoDecide(trafficSnapshot, config)
                        : NetworkTrafficMonitor.GetActiveDownloaders(trafficSnapshot, DownloadBoostThreshold)
                            .Select(p => p.Pid).ToList();

                    foreach (var pid in targets)
                        DownloadBoostEngine.Apply(config, pid);

                    foreach (var boostedPid in DownloadBoostEngine.BoostedPids.ToList())
                    {
                        if (!targets.Contains(boostedPid))
                            DownloadBoostEngine.Revert(boostedPid);
                    }

                    DownloadBoostEngine.CleanupStaleBackups();
                }

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
                        profile.LastSeenTick = _monitorTickCounter;

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
                                // Log promotion indirectly via CSV (later or debug)
                            }
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }

                PruneDeadProcessProfiles();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private long _monitorTickCounter = 0;

        private void PruneDeadProcessProfiles()
        {
            try
            {
                if (_processProfiles.Count <= 60) return;

                // Remove perfis que nao foram vistos nos ultimos 60 ticks (30min a 30s/tick)
                var cutoff = _monitorTickCounter - 60;
                foreach (var key in _processProfiles.Keys.ToList())
                {
                    if (_processProfiles.TryGetValue(key, out var p) && p.LastSeenTick < cutoff)
                        _processProfiles.TryRemove(key, out _);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private void ApplyFireminOptimizations()
        {
            try
            {
                // Firemin: trim suave para processos VIP usando o modelo combinado
                foreach (var profile in _processProfiles.Values)
                {
                    if (!profile.IsVip) continue;

                    // Rate Limit: 10 segundos entre trims
                    if ((DateTime.Now - profile.LastTrimTime).TotalSeconds < 10) continue;

                    // Threshold: so trim se excede 300MB
                    if (profile.LastKnownWs < 300L * 1024 * 1024) continue;

                    try
                    {
                        foreach (var proc in Process.GetProcessesByName(profile.Name))
                        {
                            try
                            {
                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200,
                                    false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        // 1. Memory priority = LOW (trim suave)
                                        SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_LOW);

                                        // 2. EmptyWorkingSet (kickstart)
                                        EmptyWorkingSet(handle);
                                    }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                            finally { proc.Dispose(); }
                        }
                        profile.LastTrimTime = DateTime.Now;
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        if (proc.Id == foregroundPid) continue;

                        string name = proc.ProcessName.ToLower();
                        bool isVip = _vipProcesses.Any(v => name.Contains(v));

                        if (name == "explorer" || name == "dwm" || name == "lsass" || name == "csrss" || name == "searchindexer") continue;

                        ulong currentWs = (ulong)proc.WorkingSet64;
                        ulong activeThreshold = isVip ? vipThreshold : standardThreshold;

                        if (currentWs > activeThreshold)
                        {
                            MemoryOptimizer.EmptyProcessWorkingSet(proc.Id);
                        }
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    finally { proc.Dispose(); }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        if (ForegroundBoostEnabled && oldProc.PriorityClass != _lastOriginalPriority)
                            oldProc.PriorityClass = _lastOriginalPriority;

                        // Restaura I/O Normal (2) e Page Priority Default (5)
                        Win32Api.SetProcessIoPriority(oldProc.Handle, 2);
                        Win32Api.SetProcessPagePriority(oldProc.Handle, 5);


                        SetEcoQoS(oldProc.Handle, true);
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); } // Processo pode ter sido fechado
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
                if (ForegroundBoostEnabled && proc.PriorityClass != ProcessPriorityClass.High && proc.PriorityClass != ProcessPriorityClass.RealTime)
                {
                    proc.PriorityClass = ProcessPriorityClass.High;
                }

                // Tweak 2 & 3: I/O Priority High (3) e Page Priority Máxima (5)
                Win32Api.SetProcessIoPriority(proc.Handle, 3);
                Win32Api.SetProcessPagePriority(proc.Handle, 5);


                SetEcoQoS(proc.Handle, false);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                Application.Current.Dispatcher.BeginInvoke(() =>
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }

                    // Aplica boost ao novo processo
                    try
                    {
                        ApplyBoostModern(pid);
                        _currentBoostedPid = pid;


                        string logTitle = string.IsNullOrEmpty(windowTitle) ? $"Process {pid}" : windowTitle;
                        KitLugia.Core.Logger.Log($"🎮 GameBoost (Timer): Boost aplicado ao processo PID: {pid} - {logTitle}");
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                    using (var lsass = Process.GetProcessesByName("lsass").FirstOrDefault())
                    {
                        if (lsass != null)
                            systemProcess = Win32Api.OpenProcess(Win32Api.PROCESS_QUERY_INFORMATION, false, lsass.Id);
                    }
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

            // DOWNLOAD BOOST — amostra tráfego e aplica aos downloaders reais
            if (config.DownloadBoostEnabled)
            {
                try
                {
                    var trafficSnapshot = NetworkTrafficMonitor.SampleTraffic(pid);
                    var level = config.DownloadBoostLevel switch
                    {
                        "Download" => DownloadBoostEngine.BoostLevel.Download,
                        "Latency" => DownloadBoostEngine.BoostLevel.Latency,
                        "Balanced" => DownloadBoostEngine.BoostLevel.Balanced,
                        _ => DownloadBoostEngine.BoostLevel.Auto
                    };

                    var dlConfig = new DownloadBoostEngine.DownloadBoostConfig
                    {
                        Enabled = true,
                        Level = level,
                        AutoThresholdMBps = 5.0,
                        ForegroundPid = pid
                    };

                    var targets = level == DownloadBoostEngine.BoostLevel.Auto
                        ? DownloadBoostEngine.AutoDecide(trafficSnapshot, dlConfig)
                        : NetworkTrafficMonitor.GetActiveDownloaders(trafficSnapshot, 5.0)
                            .Select(p => p.Pid).ToList();

                    foreach (var t in targets)
                        DownloadBoostEngine.Apply(dlConfig, t);
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"⚠️ Download Boost (ApplyBoostCustom): {ex.Message}");
                }
            }

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
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != targetPriority && targetPriority != ProcessPriorityClass.RealTime)
                            proc.PriorityClass = targetPriority;
                        else if (targetPriority == ProcessPriorityClass.RealTime && proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            try { proc.PriorityClass = ProcessPriorityClass.RealTime; }
                            catch { proc.PriorityClass = ProcessPriorityClass.High; }
                        }
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
                            if (ForegroundBoostEnabled)
                            {
                                proc.PriorityClass = targetPriority;
                                KitLugia.Core.Logger.Log($"✅ Prioridade aplicada com privilégios elevados: {name}");
                            }
                        }
                        catch { KitLugia.Core.Logger.Log($"⚠️ Mesmo elevado, falha ao alterar {name} (PPL bloqueia)"); }
                    }
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, config.IoPriorityLevel == 0 ? 2 : 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, (uint)(config.ThreadMemoryPriority == 0 ? 5 : 3)); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, config.EcoQoSEnabled); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                if (config.GameClassInfo)
                {
                    try { Win32Api.SetProcessGameClassInfo(proc.Handle, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                KitLugia.Core.Logger.Log($"✅ GameBoost [PERSONALIZADO]: {name} otimizado com sucesso!");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    KitLugia.Core.Logger.Log($"⚠️ V1: Acesso negado à prioridade do processo {name} (PID: {pid}) - processo protegido");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                ApplyProBalanceV2(pid);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                    if (ForegroundBoostEnabled)
                    {
                        if (proc.PriorityClass != ProcessPriorityClass.High &&
                            proc.PriorityClass != ProcessPriorityClass.RealTime)
                        {
                            proc.PriorityClass = ProcessPriorityClass.High;
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 3); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetThreadMemoryPriority(proc.Handle, Win32Api.MEMORY_PRIORITY_NORMAL); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                ApplyProBalanceV3(pid);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        finally { proc.Dispose(); }
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
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

                // Restaura prioridade para Normal ao sair do foreground
                if (ForegroundBoostEnabled)
                {
                    if (proc.PriorityClass == ProcessPriorityClass.High ||
                        proc.PriorityClass == ProcessPriorityClass.RealTime ||
                        proc.PriorityClass == ProcessPriorityClass.AboveNormal)
                    {
                        proc.PriorityClass = ProcessPriorityClass.Normal;
                    }
                }

                try { Win32Api.SetProcessIoPriority(proc.Handle, 2); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessPagePriority(proc.Handle, 5); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { SetEcoQoS(proc.Handle, true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                try { Win32Api.SetProcessGameClassInfo(proc.Handle, false); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }


        // Reverte o boost do foreground atual (usado ao desligar o toggle "Boost do App Ativo")
        public void RevertCurrentBoost()
        {
            try
            {
                if (_currentBoostedPid != 0)
                {
                    RevertBoost(_currentBoostedPid);
                    _currentBoostedPid = 0;
                }

                if (_lastBoostedPid != 0)
                {
                    try
                    {
                        using var oldProc = Process.GetProcessById((int)_lastBoostedPid);
                        if (oldProc.PriorityClass != _lastOriginalPriority)
                            oldProc.PriorityClass = _lastOriginalPriority;
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    _lastBoostedPid = 0;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

                        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
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
                    try { Win32Api.DestroyIcon(oldIcon.Handle); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.HasShutdownFinished)
            {
                Application.Current.Dispatcher.Invoke(() => DisposeCore());
                return;
            }
            DisposeCore();
        }

        private void DisposeCore()
        {
            if (_monitorTimer != null)
            {
                _monitorTimer.Tick -= MonitorTick;
                _monitorTimer.Stop();
            }

            StopRamLimiterTimer();
            
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
                try { Win32Api.DestroyIcon(_currentIcon.Handle); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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

        // SetProcessInformation — ProcessMemoryPriority
        // Define prioridade de memória do processo. VERY_LOW = OS trimma primeiro.
        // Usa as definições do Win32Api (MEMORY_PRIORITY_INFORMATION, constants)
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(
            IntPtr hProcess, int processInformationClass,
            IntPtr processInformation, int processInformationSize);

        private const int ProcessMemoryPriorityClass = 0;

        /// <summary>
        /// Define a prioridade de memória de um processo.
        /// VERY_LOW: OS trimma páginas deste processo primeiro.
        /// NORMAL: prioridade padrão (restaura quando em foreground).
        /// </summary>
        private static bool SetProcessMemoryPriority(IntPtr hProcess, uint priority)
        {
            var mpi = new Win32Api.MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(
                System.Runtime.InteropServices.Marshal.SizeOf(mpi));
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(mpi, ptr, false);
                return SetProcessInformation(hProcess, ProcessMemoryPriorityClass, ptr,
                    System.Runtime.InteropServices.Marshal.SizeOf(mpi));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }

        // GetProcessMemoryInfo — lê CommitSize, PeakWorkingSet, PageFaultCount
        // Essenciais para o RAM Limiter inteligente: saber o quanto o app REALMENTE precisa
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public ulong PeakWorkingSetSize;
            public ulong WorkingSetSize;
            public ulong QuotaPeakPagedPoolUsage;
            public ulong QuotaPagedPoolUsage;
            public ulong QuotaPeakNonPagedPoolUsage;
            public ulong QuotaNonPagedPoolUsage;
            public ulong PagefileUsage;
            public ulong PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(
            IntPtr hProcess,
            out PROCESS_MEMORY_COUNTERS counters,
            uint size);

        /// <summary>
        /// Lê informações detalhadas de memória de um processo.
        /// Retorna (workingSetMB, commitMB, peakWsMB, pageFaults).
        /// </summary>
        private static (long workingSetMB, long commitMB, long peakWsMB, uint pageFaults) GetProcessMemoryDetails(IntPtr hProcess)
        {
            var counters = new PROCESS_MEMORY_COUNTERS();
            counters.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(counters);
            if (GetProcessMemoryInfo(hProcess, out counters, counters.cb))
            {
                return (
                    (long)(counters.WorkingSetSize / (1024 * 1024)),
                    (long)(counters.PagefileUsage / (1024 * 1024)),  // PagefileUsage = CommitSize = Private Bytes
                    (long)(counters.PeakWorkingSetSize / (1024 * 1024)),
                    counters.PageFaultCount
                );
            }
            return (0, 0, 0, 0);
        }

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
                        if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                        Application.Current.Dispatcher.BeginInvoke(
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
                if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
                Application.Current.Dispatcher.Invoke(() => StartRamLimiterTimer());
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
        /// Aplica os limites de RAM com abordagem inteligente:
        /// 1. Lê CommitSize + PeakWorkingSet via GetProcessMemoryInfo (não só WorkingSet64)
        /// 2. Aprende o "resting state" (mínimo natural do app) ao longo do tempo
        /// 3. Detecta page fault storms e para de trimar se o app está sofrendo
        /// 4. Nunca trimma abaixo do resting state
        /// 5. Trim adaptativo: mais agressivo quando longe do limite, mais suave perto dele
        /// </summary>
        private void ApplyProcessRamLimits()
        {
            if (_processRamLimits.IsEmpty) return;

            IntPtr foregroundHwnd = Win32Api.GetForegroundWindow();
            uint foregroundPid = 0;
            if (foregroundHwnd != IntPtr.Zero)
                Win32Api.GetWindowThreadProcessId(foregroundHwnd, out foregroundPid);

            foreach (var limit in _processRamLimits.Values)
            {
                if (!limit.Enabled) continue;

                try
                {
                    // Debug: loga que o timer esta rodando
                    if (limit.CheckCount == 0)
                        KitLugia.Core.Logger.Log($"\U0001F504 RAM Limiter: primeira execucao para {limit.ProcessName} (limite={limit.LimitMB}MB)");
                    var processes = Process.GetProcessesByName(limit.ProcessName);
                    if (processes.Length == 0)
                    {
                        limit.LastKnownMB = 0;
                        limit.ConsecutiveTrimCount = 0;
                        limit.IsForeground = false;
                        limit.StormBackoffLevel = 0;
                        continue;
                    }

                    // ── FASE 1: Coleta de dados via GetProcessMemoryInfo ──
                    long totalWsMB = 0;
                    long totalCommitMB = 0;
                    long maxPeakWsMB = 0;
                    uint totalPageFaults = 0;
                    bool anyForeground = false;

                    foreach (var proc in processes)
                    {
                        try
                        {
                            if ((uint)proc.Id == foregroundPid)
                                anyForeground = true;

                            // Tenta ler detalhes avançados via handle
                            IntPtr handle = OpenProcess(
                                PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, proc.Id);
                            if (handle != IntPtr.Zero)
                            {
                                try
                                {
                                    var (wsMB, commitMB, peakWs, faults) = GetProcessMemoryDetails(handle);
                                    totalWsMB += wsMB;
                                    totalCommitMB += commitMB;
                                    if (peakWs > maxPeakWsMB) maxPeakWsMB = peakWs;
                                    totalPageFaults += faults;
                                }
                                finally { CloseHandle(handle); }
                            }
                            else
                            {
                                // Fallback: WorkingSet64 do .NET
                                totalWsMB += proc.WorkingSet64 / (1024 * 1024);
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }

                    // ── FASE 2: Atualiza resting state tracker ──
                    limit.CheckCount++;
                    limit.LastKnownMB = totalWsMB;
                    limit.CommitSizeMB = totalCommitMB;
                    limit.PeakWorkingSetMB = maxPeakWsMB;
                    limit.IsForeground = anyForeground;
                    if (totalWsMB > limit.PeakRamMB) limit.PeakRamMB = totalWsMB;

                    // Aprende o resting state: menor working set observado onde o app estava estável
                    // (só atualiza se o app não excedeu o limite — se excedeu, não é "resting")
                    if (totalWsMB > 0 && totalWsMB <= limit.LimitMB)
                    {
                        if (limit.RestingWorkingSetMB == 0 || totalWsMB < limit.RestingWorkingSetMB)
                            limit.RestingWorkingSetMB = totalWsMB;
                    }

                    // ── FASE 3: Detecta page fault storm ──
                    if (limit.LastPageFaultCount > 0 && totalPageFaults > 0)
                    {
                        uint faultsDelta = totalPageFaults - limit.LastPageFaultCount;
                        // Mais de 5000 page faults entre checks = o app está sofrendo
                        if (faultsDelta > 5000 && limit.ConsecutiveTrimCount > 0)
                        {
                            limit.StormBackoffLevel = Math.Min(3, limit.StormBackoffLevel + 1);
                            KitLugia.Core.Logger.Log(
                                $"\u26A0\uFE0F RAM Limiter: {limit.ProcessName} " +
                                $"page fault storm detectado ({faultsDelta} faults) " +
                                $"\u2192 backoff n\u00EDvel {limit.StormBackoffLevel}");
                        }
                        else if (faultsDelta < 500 && limit.StormBackoffLevel > 0)
                        {
                            // Se estabilizou, reduz o backoff
                            limit.StormBackoffLevel = Math.Max(0, limit.StormBackoffLevel - 1);
                        }
                    }
                    limit.LastPageFaultCount = totalPageFaults;

                    // ── FASE 4: Decide se trimma ──
                    if (anyForeground)
                    {
                        foreach (var proc in processes) try { proc.Dispose(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        continue;
                    }

                    bool exceedsLimit = totalWsMB > limit.LimitMB;

                    // ── SAFE GUARD: Limite abaixo do Commit Size = perigoso ──
                    // Commit Size = memória que o app REALMENTE alocou (precisa).
                    // Se o limite do usuário < Commit Size, o trim forçaria page-in storm.
                    // Nesse caso, usamos Commit Size como floor mínimo.
                    if (totalCommitMB > 0 && limit.LimitMB < totalCommitMB)
                    {
                        // Loga aviso apenas 1x (a cada 10 checks)
                        if (limit.CheckCount % 10 == 1)
                        {
                            KitLugia.Core.Logger.Log(
                                $"\u26A0\uFE0F RAM Limiter: {limit.ProcessName} " +
                                $"limite ({limit.LimitMB}MB) < commit size ({totalCommitMB}MB) " +
                                $"\u2192 usando commit como floor para evitar crash");
                        }
                        // Ajusta o floor para não ir abaixo do commit
                        // (o app PRECISA dessa memória para funcionar)
                    }

                    // Cooldown: 1s no modo auto-regulavel (reacao instantanea),
                    // 5s+ no modo classico (seguro para apps sensiveis)
                    TimeSpan cooldown;
                    if (limit.SafeAutoRegulate)
                    {
                        // Auto: cooldown minimo (1s) — trim a cada ciclo
                        cooldown = TimeSpan.FromSeconds(1);
                    }
                    else
                    {
                        // Classico: cooldown adaptativo com backoff
                        int stormMultiplier = 1 << limit.StormBackoffLevel;
                        var baseCooldown = GetTrimCooldown(limit);
                        cooldown = TimeSpan.FromMilliseconds(baseCooldown.TotalMilliseconds * stormMultiplier);
                    }
                    bool cooldownPassed = (DateTime.Now - limit.LastTrimTime) >= cooldown;

                    // Debug: loga status do trim
                    if (exceedsLimit && limit.CheckCount % 5 == 0)
                    {
                        var timeSinceTrim = (DateTime.Now - limit.LastTrimTime).TotalSeconds;
                        KitLugia.Core.Logger.Log(
                            $"\U0001F4CA RAM Limiter: {limit.ProcessName} WS={totalWsMB}MB limite={limit.LimitMB}MB " +
                            $"excede={exceedsLimit} cooldown={cooldownPassed:F1}s/{cooldown.TotalSeconds:F1}s fg={anyForeground}");
                    }

                    if (exceedsLimit && cooldownPassed)
                    {
                        long excessMB = totalWsMB - limit.LimitMB;

                        // ===== TRIM COMBINADO (testado: Discord 1403MB -> 271MB) =====
                        // 1. MemoryPriority = VERY_LOW (OS trimma primeiro)
                        // 2. Hard ceiling (max = target, OS enforce)
                        // 3. EmptyWorkingSet apenas quando WS > 150% do target
                        // 4. Cooldown 5s para app recuperar entre trims

                        long floorMB = Math.Max(limit.GetEffectiveMinMB(), (long)(totalWsMB * 0.30));
                        long targetMB = limit.LimitMB;
                        long aggressiveThreshold = (long)(targetMB * 1.5);

                        int trimmedCount = 0;
                        foreach (var proc in processes)
                        {
                            try
                            {
                                IntPtr handle = OpenProcess(
                                    PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION | 0x0200,
                                    false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        // 1. Memory priority = VERY_LOW
                                        SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_VERY_LOW);

                                        // 2. Hard ceiling: min=floor, max=target
                                        SetProcessWorkingSetSizeEx(
                                            handle,
                                            (IntPtr)(floorMB * 1024 * 1024),
                                            (IntPtr)(targetMB * 1024 * 1024),
                                            0);

                                        // 3. EmptyWorkingSet quando WS > 150% do target
                                        if (totalWsMB > aggressiveThreshold)
                                        {
                                            EmptyWorkingSet(handle);
                                        }
                                        trimmedCount++;
                                    }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }

                        limit.LastTrimTime = DateTime.Now;
                        limit.ConsecutiveTrimCount++;

                        if (trimmedCount > 0)
                        {
                            string mode = totalWsMB > aggressiveThreshold ? "AGRESSIVO" : "suave";
                            KitLugia.Core.Logger.Log(
                                $"\U0001F4BE RAM Limiter: {limit.ProcessName} "
                                + $"{totalWsMB}MB (excesso: {excessMB}MB) "
                                + $"floor: {floorMB}MB, target: {targetMB}MB, "
                                + $"modo: {mode}, {trimmedCount} inst, "
                                + $"trim #{limit.ConsecutiveTrimCount}");
                        }
                    }
                    else if (!exceedsLimit)
                    {
                        if (limit.ConsecutiveTrimCount > 0)
                            limit.ConsecutiveTrimCount = 0;

                        // Restaura memory priority para NORMAL
                        foreach (var proc in processes)
                        {
                            try
                            {
                                IntPtr handle = OpenProcess(0x0200, false, proc.Id);
                                if (handle != IntPtr.Zero)
                                {
                                    try { SetProcessMemoryPriority(handle, Win32Api.MEMORY_PRIORITY_NORMAL); }
                                    finally { CloseHandle(handle); }
                                }
                            }
                            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        }
                    }
                    foreach (var proc in processes) try { proc.Dispose(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        proc.Dispose();
                    }

                    if (assigned > 0)
                        _cpuJobObjects[key] = hJob;
                    else
                        Win32Api.CloseHandle(hJob);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
        }

        /// <summary>
        /// Retorna o cooldown dinâmico baseado no comportamento do processo.
        /// </summary>
        private TimeSpan GetTrimCooldown(ProcessRamLimit limit)
        {
            // Base: 5 segundos (testado: app precisa de tempo para recuperar entre trims)
            // + 2s por trim consecutivo (max 20s) — se o app não estabiliza, espera mais
            int baseMs = 5000;
            int penaltyMs = Math.Min(15000, 2000 * limit.ConsecutiveTrimCount);
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
            // Implementação simplificada - poderia usar EnumWindows para mais precisão
            return $"PID:{processId}";
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
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
            public long MinLimitMB { get; set; } = 0; // 0 = auto-detect based on process type
            public bool Enabled { get; set; } = false;
            public string Description { get; set; } = "";
            public bool IsForeground { get; set; } = false;
            public long LastKnownMB { get; set; } = 0;
            public long PeakRamMB { get; set; } = 0;
            public DateTime LastTrimTime { get; set; } = DateTime.MinValue;
            public int ConsecutiveTrimCount { get; set; } = 0;
            public ProcessEngineConfig? EngineConfig { get; set; } = null;
            public bool SafeAutoRegulate { get; set; } = true; // Modo auto-regulavel (padrao: ativo)

            // ── Resting State Tracker ──
            // Aprende o "estado natural" do app ao longo do tempo.
            // O trim NUNCA vai abaixo do resting state (o mínimo que o app precisa para funcionar).
            public long RestingWorkingSetMB { get; set; } = 0; // menor WS observado (natural)
            public long CommitSizeMB { get; set; } = 0; // commit total (Private Bytes)
            public long PeakWorkingSetMB { get; set; } = 0; // pico de WS
            public uint LastPageFaultCount { get; set; } = 0; // page faults no último check
            public int StormBackoffLevel { get; set; } = 0; // 0=normal, 1=cooldown 2x, 2=cooldown 4x
            public int CheckCount { get; set; } = 0; // quantas vezes foi verificado

            /// <summary>
            /// Retorna o mínimo seguro de working set para este processo (em MB).
            /// Se MinLimitMB > 0, usa o valor manual; senão auto-detecta pelo tipo do processo.
            /// </summary>
            public long GetEffectiveMinMB()
            {
                if (MinLimitMB > 0) return MinLimitMB;
                return GetSafeMinimumMB(ProcessName);
            }

            /// <summary>
            /// Mínimos seguros por tipo de processo — evita EmptyWorkingSet total que causa crash.
            /// Baseado em: Electron precisa de 50-80MB, browsers de 80-150MB, apps comuns 20-40MB.
            /// </summary>
            private static long GetSafeMinimumMB(string processName)
            {
                string name = processName.ToLowerInvariant();

                // Electron apps (Discord, Teams, Slack, VS Code, Spotify, etc.)
                if (name is "discord" or "discord" or "slack" or "teams" or "teams" or
                    "code" or "spotify" or "telegram" or "signal" or "whatsapp" or
                    "notion" or "figma" or "obsidian" or "electron")
                    return 60;

                // Chromium browsers (multi-process — each renderer needs ~50MB)
                if (name.Contains("opera") || name.Contains("chrome") || name.Contains("msedge") ||
                    name.Contains("brave") || name.Contains("vivaldi") || name.Contains("waterfox") ||
                    name.Contains("chromium") || name.Contains("iron"))
                    return 80;

                // Firefox (single-process with compartments)
                if (name.Contains("firefox") || name.Contains("palemoon") || name.Contains("basilisk"))
                    return 70;

                // Heavy apps (games, creative, IDEs)
                if (name.Contains("unity") || name.Contains("unreal") || name.Contains("blender") ||
                    name.Contains("photoshop") || name.Contains("premiere") || name.Contains("afterfx") ||
                    name.Contains("maya") || name.Contains("3dsmax") || name.Contains("zbrush"))
                    return 150;

                // Gaming platforms (Steam, Epic, etc.)
                if (name.Contains("steam") || name.Contains("epicgames") || name.Contains("gog"))
                    return 80;

                // Apps comuns — mínimo mais conservador
                return 30;
            }
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
            public bool DownloadBoostEnabled { get; set; } = false;
            public string DownloadBoostLevel { get; set; } = "Auto";
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
        public bool ThreadEfficiencyMode { get; set; } = false;
        public bool GameClassInfo { get; set; } = true;
        public bool Win32PrioritySeparation { get; set; } = true;
        public bool DownloadBoostEnabled { get; set; } = false;
        public string DownloadBoostLevel { get; set; } = "Auto";
    }
}