using System;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace KitLugia.Core
{
    public static class MemoryOptimizer
    {
        // P/Invoke Constants & Enums
        private const int SystemFileCacheInformation = 21;
        private const int SystemMemoryListInformation = 80;
        
        // Commands for SystemMemoryListInformation
        private const int MemoryCaptureAccessedBits = 0;
        private const int MemoryCaptureAndResetAccessedBits = 1;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;
        private const int MemoryCommandMax = 6;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SYSTEM_FILECACHE_INFORMATION
        {
            public long CurrentSize;
            public long PeakSize;
            public int PageFaultCount;
            public long MinimumWorkingSet;
            public long MaximumWorkingSet;
            public long CurrentSizeIncludingTransitionInPages;
            public long PeakSizeIncludingTransitionInPages;
            public int TransitionRePurposedCount;
            public int Flags;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TOKEN_PRIVILEGES
        {
            public int PrivilegeCount;
            public long Luid;
            public int Attributes;
        }

        private const int SE_PRIVILEGE_ENABLED = 0x00000002;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;

        public enum CleaningMode
        {
            Leve = 0,     // Barely noticeable
            Normal = 1,   // Standard (default)
            Alta = 2,     // Slight freeze
            Bruta = 3     // System freeze - maximum clean
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct MemoryInfo
        {
            public int Percent;
            public double TotalGB;
            public double UsedGB;
            public double FreeGB;
            public ulong TotalBytes;
            public ulong AvailBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
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
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static MemoryInfo GetMemoryStats()
        {
            var m = new MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(m);
            GlobalMemoryStatusEx(ref m);

            double total = m.ullTotalPhys;
            double avail = m.ullAvailPhys;
            double used = total - avail;
            
            // ðŸ”¥ CORREÃ‡ÃƒO: Usar dwMemoryLoad da API do Windows em vez de calcular manualmente
            // dwMemoryLoad é o mesmo valor que o Windows Task Manager mostra
            // Isso garante consistência entre TrayIcon e Task Manager
            int percent = (int)m.dwMemoryLoad;

            return new MemoryInfo
            {
                Percent = percent,
                TotalBytes = m.ullTotalPhys,
                AvailBytes = m.ullAvailPhys,
                TotalGB = m.ullTotalPhys / (1024.0 * 1024.0 * 1024.0),
                FreeGB = m.ullAvailPhys / (1024.0 * 1024.0 * 1024.0),
                UsedGB = used / (1024.0 * 1024.0 * 1024.0)
            };
        }

        public static void EmptyProcessWorkingSet(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        public static (bool Success, string Message) Optimize() => Optimize(CleaningMode.Normal);

        public static (bool Success, string Message) Optimize(CleaningMode mode)
        {
            try
            {
                EnablePrivilege("SeProfileSingleProcessPrivilege");
                EnablePrivilege("SeIncreaseQuotaPrivilege");

                // â”€â”€ LEVE: Just empty working sets (nem sente) â”€â”€
                ExecuteMemoryCommand(MemoryEmptyWorkingSets);

                if (mode >= CleaningMode.Normal)
                {
                    // â”€â”€ NORMAL: + Flush modified list + System file cache â”€â”€
                    var cacheInfo = new SYSTEM_FILECACHE_INFORMATION
                    {
                        MinimumWorkingSet = -1,
                        MaximumWorkingSet = -1
                    };

                    int size = Marshal.SizeOf(cacheInfo);
                    IntPtr pCacheInfo = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(cacheInfo, pCacheInfo, true);
                    try { NtSetSystemInformation(SystemFileCacheInformation, pCacheInfo, size); }
                    finally { Marshal.FreeHGlobal(pCacheInfo); }

                    ExecuteMemoryCommand(MemoryFlushModifiedList);
                }

                if (mode >= CleaningMode.Alta)
                {
                    // â”€â”€ ALTA: + Purge standby list (leve travada) â”€â”€
                    ExecuteMemoryCommand(MemoryPurgeStandbyList);
                }

                if (mode >= CleaningMode.Bruta)
                {
                    // â”€â”€ BRUTA: + Low priority standby + shrink all processes â”€â”€
                    ExecuteMemoryCommand(MemoryPurgeLowPriorityStandbyList);

                    // Shrink working sets of all accessible processes
                    try
                    {
                        foreach (var proc in Process.GetProcesses())
                        {
                            try
                            {
                                SetProcessWorkingSetSize(proc.Handle, (IntPtr)(-1), (IntPtr)(-1));
                            }
                            catch { }
                            finally { proc.Dispose(); }
                        }
                    }
                    catch { }
                }

                string[] modeNames = { "Leve", "Normal", "Alta", "Bruta" };
                return (true, $"Memória otimizada ({modeNames[(int)mode]})");
            }
            catch (Exception ex)
            {
                return (false, $"Erro na otimização: {ex.Message}");
            }
        }

        private static void EnablePrivilege(string privilegeName)
        {
            IntPtr hToken;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken))
                return;

            try
            {
                long luid;
                if (!LookupPrivilegeValue(string.Empty, privilegeName, out luid))
                    return;

                var tp = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                // Simple handle close not strictly needed for pseudo handle but good practice if real handle
            }
        }

        private static void ExecuteMemoryCommand(int command)
        {
            GCHandle handle = GCHandle.Alloc(command, GCHandleType.Pinned);
            try
            {
                NtSetSystemInformation(SystemMemoryListInformation, handle.AddrOfPinnedObject(), Marshal.SizeOf(typeof(int)));
            }
            catch
            {
                // Ignore individual failures, try to proceed
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
