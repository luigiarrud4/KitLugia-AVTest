$src = @"
using System;
using System.Runtime.InteropServices;

public static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    // PERFORMANCE_INFORMATION (psapi)
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public int cb;
        public IntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalAvailable, PhysicalTotal,
                      SystemCache, KernelTotal, KernelPaged, KernelNonPaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION p, int cb);

    // ISLC: SYSTEM_MEMORY_LIST_INFORMATION (NtQuerySystemInformation class 80)
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_MEMORY_LIST_INFORMATION
    {
        public UIntPtr ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount;
        public UIntPtr P0, P1, P2, P3, P4, P5, P6, P7;
        public UIntPtr R0, R1, R2, R3, R4, R5, R6, R7;
        public UIntPtr ModifiedPageCountPageFile;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, IntPtr buf, uint len, out uint ret);

    public static string Run()
    {
        var m = new MEMORYSTATUSEX(); m.dwLength = (uint)Marshal.SizeOf(m);
        GlobalMemoryStatusEx(ref m);
        double totalMB = m.ullTotalPhys / 1048576.0;
        double availMB = m.ullAvailPhys / 1048576.0;

        var pi = new PERFORMANCE_INFORMATION(); pi.cb = Marshal.SizeOf(pi);
        long systemCacheMB = -1;
        if (GetPerformanceInfo(ref pi, pi.cb))
            systemCacheMB = pi.SystemCache.ToInt64() * pi.PageSize.ToInt64() / 1048576L;

        int size = Marshal.SizeOf(typeof(SYSTEM_MEMORY_LIST_INFORMATION));
        IntPtr p = Marshal.AllocHGlobal(size);
        long standbyMB = -1, freePagesMB = -1, repurposedMB = -1;
        try
        {
            uint ret;
            int st = NtQuerySystemInformation(80, p, (uint)size, out ret);
            if (st == 0)
            {
                var i = (SYSTEM_MEMORY_LIST_INFORMATION)Marshal.PtrToStructure(p, typeof(SYSTEM_MEMORY_LIST_INFORMATION));
                ulong sum = (ulong)i.P0 + (ulong)i.P1 + (ulong)i.P2 + (ulong)i.P3
                          + (ulong)i.P4 + (ulong)i.P5 + (ulong)i.P6 + (ulong)i.P7;
                ulong rep = (ulong)i.R0 + (ulong)i.R1 + (ulong)i.R2 + (ulong)i.R3
                          + (ulong)i.R4 + (ulong)i.R5 + (ulong)i.R6 + (ulong)i.R7;
                standbyMB = (long)(sum * 4096 >> 20);
                freePagesMB = (long)(((ulong)i.ZeroPageCount + (ulong)i.FreePageCount) * 4096 >> 20);
                repurposedMB = (long)(rep * 4096 >> 20);
            }
            else return "FAIL NtQuerySystemInformation status=" + st;
        }
        finally { Marshal.FreeHGlobal(p); }

        return string.Format(
            "Total={0:F0} MB | Available={1:F0} MB | SystemCache(psapi)={2} MB | Standby(NtQuery 80)={3} MB | FreePages={4} MB | Repurposed={5} MB | diff(SystemCache-Standby)={6} MB",
            totalMB, availMB, systemCacheMB, standbyMB, freePagesMB, repurposedMB,
            (systemCacheMB - standbyMB));
    }
}
"@

Add-Type -TypeDefinition $src -Language CSharp
1..5 | ForEach-Object {
    Write-Output ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), [Probe]::Run())
    Start-Sleep -Seconds 1
}
