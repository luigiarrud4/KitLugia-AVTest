using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class NetworkTrafficMonitor
    {
        // === P/Invoke para GetExtendedTcpTable (enumera conexões TCP por PID) ===
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, uint ulAf, uint tableClass, uint reserved);

        private const uint AF_INET = 2;
        private const uint AF_INET6 = 23;
        private const uint TCP_TABLE_OWNER_PID_ALL = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint State;
            public uint LocalAddr;
            public int LocalPort;
            public uint RemoteAddr;
            public int RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPTABLE_OWNER_PID
        {
            public uint dwNumEntries;
            public MIB_TCPROW_OWNER_PID table;
        }

        // IPv6: layout é diferente — endereços de 16 bytes + scope id
        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddr;
            public uint LocalScopeId;
            public int LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddr;
            public uint RemoteScopeId;
            public int RemotePort;
            public uint State;
            public uint OwningPid;
        }

        // Performance counters por processo (instance -> counter)
        private static readonly ConcurrentDictionary<string, PerformanceCounter> _ioReadCounters = new();
        private static readonly ConcurrentDictionary<string, PerformanceCounter> _ioWriteCounters = new();
        private static readonly object _cacheLock = new();
        private static DateTime _lastSampleTime = DateTime.MinValue;
        private static Dictionary<uint, PerProcessIoCache> _lastIoSnapshot = new();

        private class PerProcessIoCache
        {
            public float ReadBytesPerSec { get; set; }
            public float WriteBytesPerSec { get; set; }
            public DateTime LastActivity { get; set; }
        }

        public class ProcessNetworkStats
        {
            public uint Pid { get; set; }
            public string ProcessName { get; set; } = "";
            public int ActiveConnections { get; set; }
            public double ReadSpeedMBps { get; set; }
            public double WriteSpeedMBps { get; set; }
            public double TotalSpeedMBps { get; set; }
            public DateTime LastActivity { get; set; } = DateTime.MinValue;
            public bool IsDownloading => ReadSpeedMBps > 1.0;
            public bool IsUploading => WriteSpeedMBps > 0.5;
            public bool IsActive => TotalSpeedMBps > 0.5;
        }

        public class TrafficSnapshot
        {
            public List<ProcessNetworkStats> Processes { get; set; } = new();
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public uint? ForegroundPid { get; set; }
        }

        /// <summary>
        /// Escaneia conexões TCP ativas (IPv4 + IPv6) e retorna contagem por PID.
        /// </summary>
        public static Dictionary<uint, int> GetActiveTcpConnectionsPerPid()
        {
            var result = new Dictionary<uint, int>();
            EnumerateTcpTable(AF_INET, result);
            EnumerateTcpTable(AF_INET6, result);
            return result;
        }

        private static void EnumerateTcpTable(uint addressFamily, Dictionary<uint, int> result)
        {
            try
            {
                int bufferSize = 0;
                uint ret = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, addressFamily, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0 && bufferSize <= 0) return;

                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    ret = GetExtendedTcpTable(buffer, ref bufferSize, false, addressFamily, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (ret == 0)
                    {
                        int entryCount = Marshal.ReadInt32(buffer);
                        bool isIPv6 = addressFamily == AF_INET6;
                        int entrySize = isIPv6 ? Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>() : Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        IntPtr current = buffer + 4;

                        for (int i = 0; i < entryCount; i++)
                        {
                            uint state, pid;
                            if (isIPv6)
                            {
                                var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(current);
                                state = row.State;
                                pid = row.OwningPid;
                            }
                            else
                            {
                                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(current);
                                state = row.State;
                                pid = row.OwningPid;
                            }

                            if (state == 5 || state == 8)
                            {
                                if (pid > 0)
                                    result[pid] = result.TryGetValue(pid, out int c) ? c + 1 : 1;
                            }
                            current += entrySize;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { }
        }

        /// <summary>
        /// Mede tráfego de IO por processo usando Performance Counters + conexões TCP.
        /// Chamar em intervalos regulares (1-2s) para precisão.
        /// </summary>
        public static TrafficSnapshot SampleTraffic(uint? foregroundPid = null)
        {
            var snapshot = new TrafficSnapshot
            {
                Timestamp = DateTime.Now,
                ForegroundPid = foregroundPid
            };

            try
            {
                var tcpMap = GetActiveTcpConnectionsPerPid();
                var now = DateTime.Now;

                // Limpa counters de processos que não existem mais
                CleanupStaleCounters(tcpMap);

                // Só mede processos com conexões TCP ativas
                foreach (var kvp in tcpMap)
                {
                    uint pid = kvp.Key;
                    int connections = kvp.Value;
                    if (connections == 0) continue;

                    string? procName = null;
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(procName)) continue;

                    // Lê performance counters de IO
                    float readPerSec = 0, writePerSec = 0;
                    try
                    {
                        var readCounter = _ioReadCounters.GetOrAdd(procName, name =>
                            new PerformanceCounter("Process", "IO Read Bytes/sec", name, true));
                        readPerSec = readCounter.NextValue();

                        var writeCounter = _ioWriteCounters.GetOrAdd(procName, name =>
                            new PerformanceCounter("Process", "IO Write Bytes/sec", name, true));
                        writePerSec = writeCounter.NextValue();
                    }
                    catch
                    {
                        // Pode falhar se processo encerrou entre a leitura
                        _ioReadCounters.TryRemove(procName, out _);
                        _ioWriteCounters.TryRemove(procName, out _);
                    }

                    bool hasActivity = readPerSec > 1024 || writePerSec > 1024;

                    var stats = new ProcessNetworkStats
                    {
                        Pid = pid,
                        ProcessName = procName,
                        ActiveConnections = connections,
                        ReadSpeedMBps = readPerSec / (1024.0 * 1024.0),
                        WriteSpeedMBps = writePerSec / (1024.0 * 1024.0),
                        TotalSpeedMBps = (readPerSec + writePerSec) / (1024.0 * 1024.0),
                        LastActivity = hasActivity ? now : DateTime.MinValue
                    };

                    snapshot.Processes.Add(stats);
                }

                // Atualiza cache
                lock (_cacheLock)
                {
                    var newCache = new Dictionary<uint, PerProcessIoCache>();
                    foreach (var s in snapshot.Processes)
                    {
                        newCache[s.Pid] = new PerProcessIoCache
                        {
                            ReadBytesPerSec = (float)(s.ReadSpeedMBps * 1024 * 1024),
                            WriteBytesPerSec = (float)(s.WriteSpeedMBps * 1024 * 1024),
                            LastActivity = s.LastActivity
                        };
                    }
                    _lastIoSnapshot = newCache;
                }

                _lastSampleTime = now;
            }
            catch { }

            return snapshot;
        }

        /// <summary>
        /// Remove PerformanceCounters de processos que não estão mais no TCP table.
        /// </summary>
        private static void CleanupStaleCounters(Dictionary<uint, int> currentTcpMap)
        {
            try
            {
                var activeNames = new HashSet<string>();
                foreach (var pid in currentTcpMap.Keys)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        activeNames.Add(proc.ProcessName);
                    }
                    catch { }
                }

                foreach (var key in _ioReadCounters.Keys.ToList())
                {
                    if (!activeNames.Contains(key))
                    {
                        if (_ioReadCounters.TryRemove(key, out var c)) c.Dispose();
                        if (_ioWriteCounters.TryRemove(key, out var c2)) c2.Dispose();
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Retorna processos baixando ativamente (download > threshold).
        /// </summary>
        public static List<ProcessNetworkStats> GetActiveDownloaders(TrafficSnapshot snapshot, double thresholdMBps = 1.0)
        {
            return snapshot.Processes
                .Where(p => p.ReadSpeedMBps >= thresholdMBps)
                .OrderByDescending(p => p.ReadSpeedMBps)
                .ToList();
        }

        /// <summary>
        /// Retorna processos com muitas conexões TCP.
        /// </summary>
        public static List<ProcessNetworkStats> GetHeavyNetworkUsers(TrafficSnapshot snapshot, int minConnections = 10)
        {
            return snapshot.Processes
                .Where(p => p.ActiveConnections >= minConnections)
                .OrderByDescending(p => p.ActiveConnections)
                .ToList();
        }
    }
}
