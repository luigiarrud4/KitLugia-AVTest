using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Fast per-process disk I/O monitoring via kernel32 GetProcessIoCounters (native P/Invoke).
    /// Avoids PerformanceCounter overhead that freezes WPF UI.
    /// FIX: remove Process.GetProcessById double OpenProcess leak
    /// </summary>
    public static class ProcessIoHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
        }

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private struct IoSnapshot
        {
            public DateTime Timestamp;
            public ulong ReadBytes;
            public ulong WriteBytes;
            public ulong ReadOps;
            public ulong WriteOps;
        }

        private static readonly ConcurrentDictionary<int, IoSnapshot> _lastSnapshots = new();

        public struct ProcessIoRate
        {
            public int Pid;
            public double ReadBytesPerSec;
            public double WriteBytesPerSec;
            public double ReadOpsPerSec;
            public double WriteOpsPerSec;
            public ulong TotalReadBytes;
            public ulong TotalWriteBytes;
            public ulong TotalReadOps;
            public ulong TotalWriteOps;
        }

        public static ProcessIoRate SampleProcessIo(int pid)
        {
            var result = new ProcessIoRate { Pid = pid };
            if (pid <= 4) return result;
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return result;
                if (GetProcessIoCounters(hProcess, out var counters))
                {
                    var now = DateTime.UtcNow;
                    var current = new IoSnapshot
                    {
                        Timestamp = now,
                        ReadBytes = counters.ReadTransferCount,
                        WriteBytes = counters.WriteTransferCount,
                        ReadOps = counters.ReadOperationCount,
                        WriteOps = counters.WriteOperationCount
                    };
                    result.TotalReadBytes = counters.ReadTransferCount;
                    result.TotalWriteBytes = counters.WriteTransferCount;
                    result.TotalReadOps = counters.ReadOperationCount;
                    result.TotalWriteOps = counters.WriteOperationCount;
                    if (_lastSnapshots.TryGetValue(pid, out var prev))
                    {
                        double elapsed = (current.Timestamp - prev.Timestamp).TotalSeconds;
                        if (elapsed > 0.05)
                        {
                            // bytes/sec
                            if (current.ReadBytes >= prev.ReadBytes)
                                result.ReadBytesPerSec = (current.ReadBytes - prev.ReadBytes) / elapsed;
                            if (current.WriteBytes >= prev.WriteBytes)
                                result.WriteBytesPerSec = (current.WriteBytes - prev.WriteBytes) / elapsed;
                            // IOPS
                            if (current.ReadOps >= prev.ReadOps)
                                result.ReadOpsPerSec = (current.ReadOps - prev.ReadOps) / elapsed;
                            if (current.WriteOps >= prev.WriteOps)
                                result.WriteOpsPerSec = (current.WriteOps - prev.WriteOps) / elapsed;
                            // clamp negatives already handled by >= check
                        }
                    }
                    _lastSnapshots[pid] = current;
                }
            }
            catch { }
            finally
            {
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
            return result;
        }

        /// <summary>
        /// Batch sampling — reuses the same logic but avoids per-call overhead when caller has a pid list.
        /// </summary>
        public static System.Collections.Generic.Dictionary<int, ProcessIoRate> SampleBatch(System.Collections.Generic.IEnumerable<int> pids)
        {
            var dict = new System.Collections.Generic.Dictionary<int, ProcessIoRate>();
            if (pids == null) return dict;
            foreach (var pid in pids)
            {
                dict[pid] = SampleProcessIo(pid);
            }
            return dict;
        }

        public static void CleanupStaleSnapshots(System.Collections.Generic.HashSet<int> alivePids)
        {
            foreach (var key in _lastSnapshots.Keys)
                if (!alivePids.Contains(key)) _lastSnapshots.TryRemove(key, out _);
        }

        public static void ResetAll() => _lastSnapshots.Clear();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
