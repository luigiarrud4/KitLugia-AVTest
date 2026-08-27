using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KitLugia.Core.TaskManager
{
    /// <summary>
    /// Leitura NATIVA de hardware — sem depender de WMI/WMIPrvSE.
    /// Prioridade: Nativo (Registry + P/Invoke kernel32 + SMBIOS + DXGI) → CIM (Microsoft.Management.Infrastructure / WsMan) → WMI legado (System.Management / DCOM) fallback.
    /// Cada método nunca lança — retorna fallback ou valor neutro se falhar (WinPE, serviço travado, permissão).
    /// </summary>
    public static class NativeHardware
    {
        // ══════════════════════════════════════════════
        //  P/Invoke
        // ══════════════════════════════════════════════
        [DllImport("kernel32.dll")] private static extern bool IsProcessorFeaturePresent(uint feature);
        private const uint PF_VIRT_FIRMWARE_ENABLED = 21;
        private const uint PF_HYPERVISOR_PRESENT = 22;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP relationship, IntPtr buffer, ref uint returnedLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnedLength);

        private enum LOGICAL_PROCESSOR_RELATIONSHIP : uint { RelationProcessorCore = 0, RelationNumaNode = 1, RelationCache = 2, RelationProcessorPackage = 3, RelationGroup = 4, RelationCacheEx = 2 }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint FirmwareTableProviderSignature, uint FirmwareTableID, IntPtr pFirmwareTableBuffer, uint BufferSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDeviceW(string lpDeviceName, System.Text.StringBuilder lpTargetPath, uint ucchMax);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void GetPhysicallyInstalledSystemMemory(out long totalKb);

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        private const uint RSMB = 0x52534D42; // 'RSMB'

        // ══════════════════════════════════════════════
        //  CIM HELPER — sucessor oficial do WMI (WsMan, 30% mais rápido)
        // ══════════════════════════════════════════════
        public static class Cim
        {
            /// <summary>Query via CIM (Microsoft.Management.Infrastructure) com fallback WMI legado.</summary>
            public static List<Dictionary<string, object?>> Query(string wql, string ns = @"root\cimv2")
            {
                // 1) Tenta CIM moderno (WsMan, sem DCOM)
                var cimResult = TryCimQuery(wql, ns);
                if (cimResult != null) return cimResult;
                // 2) Fallback WMI legado (DCOM)
                return TryWmiQuery(wql, ns);
            }

            private static List<Dictionary<string, object?>>? TryCimQuery(string wql, string ns)
            {
                try
                {
                    // Carrega MI via reflection-safe + direct reference (se pacote instalado)
                    // Direct reference: Microsoft.Management.Infrastructure
                    var session = Microsoft.Management.Infrastructure.CimSession.Create(null);
                    using (session)
                    {
                        var instances = session.QueryInstances(ns, "WQL", wql);
                        var list = new List<Dictionary<string, object?>>();
                        foreach (var inst in instances)
                        {
                            var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in inst.CimInstanceProperties)
                                d[prop.Name] = prop.Value;
                            list.Add(d);
                        }
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    // MI não disponível, serviço WinRM desabilitado, ou WinPE sem MI — silencioso
                    try { Debug.WriteLine($"[CIM] fallback WMI: {ex.Message}"); } catch { }
                    return null;
                }
            }

            private static List<Dictionary<string, object?>> TryWmiQuery(string wql, string ns)
            {
                var list = new List<Dictionary<string, object?>>();
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher(ns, wql);
                    foreach (System.Management.ManagementObject o in searcher.Get())
                    {
                        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (System.Management.PropertyData p in o.Properties)
                            d[p.Name] = p.Value;
                        list.Add(d);
                    }
                }
                catch { }
                return list;
            }

            public static string GetFirstString(string wql, string prop, string ns = @"root\cimv2")
            {
                try
                {
                    var rows = Query(wql, ns);
                    if (rows.Count > 0 && rows[0].TryGetValue(prop, out var v) && v != null)
                        return v.ToString() ?? "";
                }
                catch { }
                return "";
            }
        }

        // ══════════════════════════════════════════════
        //  CPU — Nativo Registry + GetLogicalProcessorInformationEx
        // ══════════════════════════════════════════════
        public static string GetCpuNameNative()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = k?.GetValue("ProcessorNameString")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            catch { }
            // Fallback CIM → WMI
            try
            {
                var rows = Cim.Query("SELECT Name FROM Win32_Processor");
                var n = rows.FirstOrDefault()?["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(n)) return n!.Trim();
            }
            catch { }
            return $"CPU ({Environment.ProcessorCount} núcleos)";
        }

        public static string GetCpuClockNative()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var mhz = k?.GetValue("~MHz");
                if (mhz != null)
                {
                    double ghz = Convert.ToDouble(mhz) / 1000.0;
                    if (ghz > 0.1) return $"{ghz:F2} GHz";
                }
            }
            catch { }
            try
            {
                var rows = Cim.Query("SELECT MaxClockSpeed FROM Win32_Processor");
                if (rows.Count > 0 && rows[0].TryGetValue("MaxClockSpeed", out var v) && v != null)
                {
                    double mhz = Convert.ToDouble(v);
                    if (mhz > 0) return $"{mhz / 1000.0:F2} GHz";
                }
            }
            catch { }
            return "?";
        }

        public static string GetCpuSocketsNative()
        {
            // Nativo: conta RelationProcessorPackage via GetLogicalProcessorInformationEx
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage, IntPtr.Zero, ref len);
                if (len > 0 && len < 1024 * 1024)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)len);
                    try
                    {
                        if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage, buf, ref len))
                        {
                            int count = 0;
                            uint offset = 0;
                            while (offset + 8 <= len)
                            {
                                uint rel = (uint)Marshal.ReadInt32(buf, (int)offset);
                                uint size = (uint)Marshal.ReadInt32(buf, (int)offset + 4);
                                if (size == 0) break;
                                if (rel == (uint)LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorPackage) count++;
                                offset += size;
                            }
                            if (count > 0) return count.ToString();
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch { }
            // Fallback: distinct SocketDesignation via CIM
            try
            {
                var rows = Cim.Query("SELECT SocketDesignation FROM Win32_Processor");
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows) if (r.TryGetValue("SocketDesignation", out var v) && v != null) set.Add(v.ToString()!);
                if (set.Count > 0) return Math.Max(1, set.Count).ToString();
            }
            catch { }
            return "1";
        }

        public static (string l1, string l2, string l3) GetCpuCacheNative()
        {
            // Tenta nativo 100% sem WMI (funciona em WinPE / WMI corrompido)
            var native = TryGetCacheViaLogicalProcessorEx();
            if (native != null) return native.Value;

            var viaOldApi = TryGetCacheViaLogicalProcessorOld();
            if (viaOldApi != null) return viaOldApi.Value;

            // Fallback CIM/WMI (Win32_CacheMemory soma por nível)
            return GetCpuCacheViaCimFallback();
        }

        private static (string l1, string l2, string l3)? TryGetCacheViaLogicalProcessorEx()
        {
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache, IntPtr.Zero, ref len);
                if (len == 0 || len > 2 * 1024 * 1024) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache, buf, ref len)) return null;
                    // CACHE_RELATIONSHIP: Level(1), Assoc(1), LineSize(2), CacheSize(4), Type(4), Reserved[20], GroupCount(2), ...
                    // No buffer ex: header 8 bytes (Relationship, Size) + payload
                    long l1 = 0, l2 = 0, l3 = 0;
                    uint offset = 0;
                    while (offset + 8 <= len)
                    {
                        int rel = Marshal.ReadInt32(buf, (int)offset);
                        uint size = (uint)Marshal.ReadInt32(buf, (int)offset + 4);
                        if (size == 0 || offset + size > len) break;
                        if (rel == (int)LOGICAL_PROCESSOR_RELATIONSHIP.RelationCache)
                        {
                            // payload começa em offset+8
                            byte level = Marshal.ReadByte(buf, (int)offset + 8);
                            // byte assoc = Marshal.ReadByte(buf, (int)offset+9);
                            // ushort line = (ushort)Marshal.ReadInt16(buf, (int)offset+10);
                            uint cacheSize = (uint)Marshal.ReadInt32(buf, (int)offset + 12);
                            // uint type = (uint)Marshal.ReadInt32(buf, (int)offset+16);
                            if (level == 1) l1 += cacheSize;
                            else if (level == 2) l2 += cacheSize;
                            else if (level == 3) l3 = Math.Max(l3, cacheSize); // L3 compartilhado — MAX, não SOMA (hibrídos duplicam)
                        }
                        offset += size;
                    }
                    if (l1 > 0 || l2 > 0 || l3 > 0)
                    {
                        string Fmt(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024} KB";
                        if (l1 == 0) l1 = Environment.ProcessorCount * 64 * 1024; // estimativa
                        return (Fmt(l1), Fmt(l2), l3 > 0 ? Fmt(l3) : "—");
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return null;
        }

        private static (string l1, string l2, string l3)? TryGetCacheViaLogicalProcessorOld()
        {
            try
            {
                uint len = 0;
                GetLogicalProcessorInformation(IntPtr.Zero, ref len);
                if (len == 0 || len > 2 * 1024 * 1024) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetLogicalProcessorInformation(buf, ref len)) return null;
                    const int STRUCT_SIZE = 32; // SYSTEM_LOGICAL_PROCESSOR_INFORMATION = 32 bytes em x64 (union com CACHE_DESCRIPTOR)
                    int count = (int)(len / STRUCT_SIZE);
                    long l1 = 0, l2 = 0, l3 = 0;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr p = IntPtr.Add(buf, i * STRUCT_SIZE);
                        uint relationship = (uint)Marshal.ReadInt32(p);
                        if (relationship != 2) continue; // RelationCache = 2
                        // UNION: após 8 bytes (Mask) vem CACHE_DESCRIPTOR de 12 bytes? Layout: ProcessorMask(8), Relationship(4), pad(4), Cache: Level(1), Assoc(1), LineSize(2), Size(4)
                        // Em x64: offset 12 = CACHE_DESCRIPTOR start
                        byte level = Marshal.ReadByte(p, 12);
                        // byte assoc = Marshal.ReadByte(p,13);
                        // ushort line = (ushort)Marshal.ReadInt16(p,14);
                        uint size = (uint)Marshal.ReadInt32(p, 16);
                        if (level == 1) l1 += size * 1024;
                        else if (level == 2) l2 += size * 1024;
                        else if (level == 3) l3 = Math.Max(l3, size * 1024);
                    }
                    if (l1 > 0 || l2 > 0 || l3 > 0)
                    {
                        string Fmt(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024} KB";
                        return (Fmt(l1), Fmt(l2), l3 > 0 ? Fmt(l3) : "—");
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return null;
        }

        private static (string l1, string l2, string l3) GetCpuCacheViaCimFallback()
        {
            try
            {
                // Fonte primária fallback: Win32_CacheMemory (soma real por nível — funciona em híbridos)
                // Level 3→ L1, Level 4→ L2, Level 5→ L3 (Win32_CacheMemory começa em 3)
                try
                {
                    var rows = Cim.Query("SELECT Level, InstalledSize FROM Win32_CacheMemory");
                    if (rows.Count > 0)
                    {
                        var lvlVals = new Dictionary<int, List<long>>();
                        foreach (var r in rows)
                        {
                            int lvl = Convert.ToInt32(r.TryGetValue("Level", out var lv) ? lv ?? 0 : 0);
                            long sz = Convert.ToInt64(r.TryGetValue("InstalledSize", out var s) ? s ?? 0L : 0L); // KB
                            if (sz <= 0) continue;
                            if (!lvlVals.TryGetValue(lvl, out var lst)) lvlVals[lvl] = lst = new List<long>();
                            lst.Add(sz);
                        }
                        long l1b = 0, l2b = 0, l3m = 0;
                        if (lvlVals.TryGetValue(3, out var v3)) l1b = v3.Sum();
                        if (lvlVals.TryGetValue(4, out var v4)) l2b = v4.Sum();
                        if (lvlVals.TryGetValue(5, out var v5)) l3m = v5.Count > 0 ? v5.Max() : 0;
                        if (l1b > 0 || l2b > 0 || l3m > 0)
                        {
                            string Fmt(long kb) => kb >= 1024 ? $"{kb / 1024.0:F1} MB" : $"{kb} KB";
                            if (l1b == 0) l1b = Environment.ProcessorCount * 64;
                            return (Fmt(l1b), Fmt(l2b), l3m > 0 ? Fmt(l3m) : "—");
                        }
                    }
                }
                catch { }
                // Fallback: Win32_Processor L2/L3
                int l2f = 0, l3f = 0;
                try
                {
                    var rows = Cim.Query("SELECT L2CacheSize,L3CacheSize FROM Win32_Processor");
                    if (rows.Count > 0)
                    {
                        l2f = Convert.ToInt32(rows[0].TryGetValue("L2CacheSize", out var a) ? a ?? 0 : 0);
                        l3f = Convert.ToInt32(rows[0].TryGetValue("L3CacheSize", out var b) ? b ?? 0 : 0);
                    }
                }
                catch { }
                long l1e = Environment.ProcessorCount * 64L;
                string Fmt2(long kb) => kb >= 1024 ? $"{kb / 1024.0:F1} MB" : $"{kb} KB";
                return (Fmt2(l1e), l2f > 0 ? Fmt2(l2f) : "?", l3f > 0 ? Fmt2(l3f) : "—");
            }
            catch { return ("?", "?", "?"); }
        }

        public static string GetVirtualizationNative()
        {
            try
            {
                bool hypervisorRunning = IsProcessorFeaturePresent(PF_HYPERVISOR_PRESENT) || IsHypervisorPresentViaCim();
                if (hypervisorRunning) return "Ativado (hipervisor ativo)";
                bool fwEnabled = IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED);
                if (!fwEnabled)
                {
                    // Fallback CIM: VirtualizationFirmwareEnabled
                    try
                    {
                        var rows = Cim.Query("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
                        if (rows.Count > 0 && rows[0].TryGetValue("VirtualizationFirmwareEnabled", out var v) && v is bool vb && vb) fwEnabled = true;
                    }
                    catch { }
                }
                return fwEnabled ? "Ativado" : "Desativado";
            }
            catch { return "?"; }
        }

        private static bool IsHypervisorPresentViaCim()
        {
            try
            {
                var rows = Cim.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem");
                if (rows.Count > 0 && rows[0].TryGetValue("HypervisorPresent", out var v) && v is bool hb && hb) return true;
            }
            catch { }
            return false;
        }

        // ══════════════════════════════════════════════
        //  RAM — SMBIOS via GetSystemFirmwareTable('RSMB') — 5-10ms, sem WMI
        // ══════════════════════════════════════════════
        public static (string ramSpeed, string ramFF, string slotsUsed, string slotsTotal) GetRamInfoNative()
        {
            // Tenta nativo SMBIOS primeiro (<15ms, funciona com WMI corrompido/WinPE)
            var smbios = TryGetRamViaSmbios();
            if (smbios != null) return smbios.Value;

            // Fallback CIM/WMI (lento mas compatível)
            return GetRamViaCimFallback();
        }

        private static (string, string, string, string)? TryGetRamViaSmbios()
        {
            try
            {
                uint size = GetSystemFirmwareTable(RSMB, 0, IntPtr.Zero, 0);
                if (size == 0 || size > 4 * 1024 * 1024) return null;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    uint read = GetSystemFirmwareTable(RSMB, 0, buf, size);
                    if (read == 0) return null;
                    // Copia para managed array para parsing seguro
                    var raw = new byte[read];
                    Marshal.Copy(buf, raw, 0, (int)read);
                    return ParseSmbiosForRam(raw);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return null; }
        }

        private static (string ramSpeed, string ramFF, string slotsUsed, string slotsTotal)? ParseSmbiosForRam(byte[] raw)
        {
            try
            {
                // RSMB buffer pode ter header de 8 bytes? Em algumas builds o início já é SMBIOS.
                // Heurística: se raw[0] não é um Type válido (0-127) ou Length <4, pula header de 8.
                int offset = 0;
                if (raw.Length > 8 && (raw[0] > 127 || raw[1] < 4))
                {
                    // Tenta detectar header RSMB (Google: raw SMBIOS from GetSystemFirmwareTable começa em 0, mas em Win11 tem 8-byte header com length)
                    // Se os primeiros 4 bytes forem 'R''S''M''B', pula
                    if (raw[0] == (byte)'R' && raw[1] == (byte)'S' && raw[2] == (byte)'M' && raw[3] == (byte)'B')
                        offset = 8;
                }

                int slotsUsed = 0;
                string ffStr = "?";
                string memTypeStr = "";
                int totalSlots = 0;
                uint bestSpeed = 0;

                // Itera estruturas SMBIOS
                while (offset + 4 <= raw.Length)
                {
                    byte type = raw[offset];
                    byte length = raw[offset + 1];
                    if (length < 4) break;
                    if (offset + length > raw.Length) break;
                    if (type == 127 && length == 4) break; // End-of-table

                    // Área de strings termina com 00 00
                    int strStart = offset + length;
                    int strEnd = strStart;
                    while (strEnd + 1 < raw.Length && !(raw[strEnd] == 0 && raw[strEnd + 1] == 0))
                        strEnd++;
                    // strEnd aponta para primeiro 00 do terminador; segundo 00 em strEnd+1

                    if (type == 16) // Physical Memory Array
                    {
                        if (length >= 0x0F && offset + 0x0E < raw.Length)
                        {
                            totalSlots = raw[offset + 0x0D] | (raw[offset + 0x0E] << 8);
                        }
                    }
                    else if (type == 17) // Memory Device
                    {
                        if (length < 0x15) { /* pula */ }
                        else
                        {
                            ushort size = (ushort)(raw[offset + 0x0C] | (raw[offset + 0x0D] << 8));
                            bool hasModule = size != 0 && size != 0xFFFF;
                            // 0x7FFF indica Extended Size em 0x1C
                            if (size == 0x7FFF && length >= 0x20 && offset + 0x1F < raw.Length)
                            {
                                uint ext = (uint)(raw[offset + 0x1C] | (raw[offset + 0x1D] << 8) | (raw[offset + 0x1E] << 16) | (raw[offset + 0x1F] << 24));
                                hasModule = ext != 0 && ext != 0xFFFFFFFF;
                            }
                            if (hasModule)
                            {
                                slotsUsed++;
                                // FormFactor
                                byte ff = raw[offset + 0x0E];
                                ffStr = ff switch { 8 => "DIMM", 12 => "SODIMM", 9 => "RIMM", 10 => "SODIMM", _ => ff.ToString() };
                                // MemoryType
                                byte mt = raw[offset + 0x12];
                                memTypeStr = mt switch { 20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 0 => "", _ => $"DDR?" };
                                // Speed em 0x15
                                ushort spd = (ushort)(raw[offset + 0x15] | (raw[offset + 0x16] << 8));
                                // Configured speed em 0x20 se length >=0x22
                                ushort cfg = 0;
                                if (length >= 0x22 && offset + 0x21 < raw.Length)
                                    cfg = (ushort)(raw[offset + 0x20] | (raw[offset + 0x21] << 8));
                                uint best = cfg > 0 ? cfg : spd;
                                if (best > bestSpeed) bestSpeed = best;
                            }
                        }
                    }

                    // Avança para próxima: após 00 00
                    offset = strEnd + 2;
                    if (offset >= raw.Length) break;
                }

                if (slotsUsed > 0 || totalSlots > 0 || bestSpeed > 0)
                {
                    string speedOut = bestSpeed > 0 ? $"{bestSpeed} MHz" + (string.IsNullOrEmpty(memTypeStr) ? "" : $" {memTypeStr}") : $"?{(string.IsNullOrEmpty(memTypeStr) ? "" : $" {memTypeStr}")}";
                    // Normaliza "? DDR5" -> "DDR5"
                    if (speedOut.StartsWith("? ")) speedOut = speedOut[2..];
                    return (speedOut, ffStr ?? "?", slotsUsed.ToString(), totalSlots > 0 ? totalSlots.ToString() : "?");
                }
            }
            catch { }
            return null;
        }

        private static (string ramSpeed, string ramFF, string slotsUsed, string slotsTotal) GetRamViaCimFallback()
        {
            try
            {
                int used = 0;
                string spd = "?", ff = "?", memType = "";
                var rows = Cim.Query("SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType,FormFactor FROM Win32_PhysicalMemory");
                foreach (var r in rows)
                {
                    used++;
                    uint s1 = 0, s2 = 0;
                    try { s1 = Convert.ToUInt32(r.TryGetValue("Speed", out var a) ? a ?? 0u : 0u); } catch { }
                    try { s2 = Convert.ToUInt32(r.TryGetValue("ConfiguredClockSpeed", out var b) ? b ?? 0u : 0u); } catch { }
                    uint best = s2 > 0 ? s2 : s1;
                    if (best > 0) spd = best.ToString();
                    int ffCode = Convert.ToInt32(r.TryGetValue("FormFactor", out var c) ? c ?? 0 : 0);
                    ff = ffCode switch { 8 => "DIMM", 12 => "SODIMM", _ => ffCode.ToString() };
                    int tp = Convert.ToInt32(r.TryGetValue("SMBIOSMemoryType", out var d) ? d ?? 0 : 0);
                    memType = tp switch { 20 => "DDR", 21 => "DDR2", 22 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 0 => "", _ => "DDR?" };
                }
                int total = 0;
                var rows2 = Cim.Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
                foreach (var r in rows2) try { total += Convert.ToInt32(r.TryGetValue("MemoryDevices", out var v) ? v ?? 0 : 0); } catch { }
                string suffix = string.IsNullOrEmpty(memType) ? "" : $" {memType}";
                // Se spd=="?" mas temos memType, retorna só tipo
                string ramSpeed = spd == "?" && !string.IsNullOrEmpty(memType) ? memType : $"{spd} MHz{suffix}".Replace("? MHz", "?");
                if (ramSpeed.StartsWith("? ")) ramSpeed = ramSpeed[2..];
                return (ramSpeed, ff, used.ToString(), total > 0 ? total.ToString() : "?");
            }
            catch { return ("?", "?", "?", "?"); }
        }

        // ══════════════════════════════════════════════
        //  DISCO — nativo DeviceIoControl + IOCTL, mapping via volume extents
        // ══════════════════════════════════════════════
        public static Dictionary<int, List<string>> MapLogicalToPhysicalNative()
        {
            // 1) Tenta nativo via IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS (funciona sem WMI, WinPE e WMI corrompido)
            var native = TryMapViaVolumeExtents();
            if (native != null && native.Count > 0) return native;
            // 2) Fallback CIM/WMI associações
            return MapLogicalToPhysicalViaCim();
        }

        private static Dictionary<int, List<string>>? TryMapViaVolumeExtents()
        {
            try
            {
                var map = new Dictionary<int, List<string>>();
                // Itera letras C..Z, abre \\.\C: e query extents
                for (char c = 'C'; c <= 'Z'; c++)
                {
                    string vol = $"\\\\.\\{c}:";
                    IntPtr h = CreateFileW(vol, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    if (h == IntPtr.Zero || h == INVALID_HANDLE_VALUE) continue;
                    try
                    {
                        // VOLUME_DISK_EXTENTS: NumberOfDiskExtents (4) + array DISK_EXTENT {DiskNumber(4), StartingOffset(8), ExtentLength(8)}
                        // Buffer 1024 suficiente para até ~10 extents (striped/spanned)
                        IntPtr outBuf = Marshal.AllocHGlobal(1024);
                        try
                        {
                            if (DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, outBuf, 1024, out uint bytes, IntPtr.Zero))
                            {
                                int n = Marshal.ReadInt32(outBuf);
                                for (int i = 0; i < n; i++)
                                {
                                    int diskNum = Marshal.ReadInt32(outBuf, 4 + i * 20);
                                    if (!map.TryGetValue(diskNum, out var list)) map[diskNum] = list = new List<string>();
                                    string letter = $"{c}:";
                                    if (!list.Contains(letter)) list.Add(letter);
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(outBuf); }
                    }
                    finally { try { CloseHandle(h); } catch { } }
                }
                // Se não mapeou nada (sem permissão), devolve null para fallback
                return map.Count > 0 ? map : null;
            }
            catch { return null; }
        }

        public static Dictionary<int, List<string>> MapLogicalToPhysicalViaCim()
        {
            var map = new Dictionary<int, List<string>>();
            try
            {
                // Usa Cim helper (MI → WMI fallback)
                var partToPhys = new Dictionary<string, int>();
                var rows1 = Cim.Query("SELECT Antecedent,Dependent FROM Win32_DiskDriveToDiskPartition");
                foreach (var r in rows1)
                {
                    var ant = r.TryGetValue("Antecedent", out var a) ? a?.ToString() ?? "" : "";
                    var dep = r.TryGetValue("Dependent", out var b) ? b?.ToString() ?? "" : "";
                    // Suporta tanto CIM (DeviceID = "\\.\PHYSICALDRIVE1") quanto WMI legado (\\HOST\root\cimv2:Win32_DiskDrive.DeviceID="\\.\PHYSICALDRIVE1")
                    var mP = System.Text.RegularExpressions.Regex.Match(ant, @"PHYSICALDRIVE(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!mP.Success) mP = System.Text.RegularExpressions.Regex.Match(ant, @"DiskIndex=""(\d+)""");
                    var mM = System.Text.RegularExpressions.Regex.Match(dep, @"DeviceId\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (mP.Success && mM.Success)
                    {
                        string key = mM.Groups[1].Value.Replace("\\\\", "\\");
                        // Normaliza "Disk #1, Partition #0" (CIM/WMI variação)
                        if (partToPhys.ContainsKey(key)) partToPhys[key] = int.Parse(mP.Groups[1].Value);
                        else partToPhys.Add(key, int.Parse(mP.Groups[1].Value));
                    }
                }
                var rows2 = Cim.Query("SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition");
                foreach (var r in rows2)
                {
                    var ant = r.TryGetValue("Antecedent", out var a) ? a?.ToString() ?? "" : "";
                    var dep = r.TryGetValue("Dependent", out var b) ? b?.ToString() ?? "" : "";
                    var mL = System.Text.RegularExpressions.Regex.Match(dep, @"DeviceId\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var mM = System.Text.RegularExpressions.Regex.Match(ant, @"DeviceId\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (mL.Success && mM.Success && partToPhys.TryGetValue(mM.Groups[1].Value.Replace("\\\\", "\\"), out int phys))
                    {
                        string letter = mL.Groups[1].Value;
                        if (!map.TryGetValue(phys, out var list)) map[phys] = list = new List<string>();
                        list.Add(letter);
                    }
                }
            }
            catch { }
            return map;
        }

        public static Dictionary<int, (string model, string sizeGb)> GetDiskStaticInfoNative()
        {
            // Tenta CIM primeiro (rápido, já inclui model+size)
            var result = new Dictionary<int, (string, string)>();
            try
            {
                var rows = Cim.Query("SELECT Model,Size,Index FROM Win32_DiskDrive");
                foreach (var r in rows)
                {
                    int idx = Convert.ToInt32(r.TryGetValue("Index", out var a) ? a ?? 0 : 0);
                    ulong sz = 0; try { sz = Convert.ToUInt64(r.TryGetValue("Size", out var b) ? b ?? (ulong)0 : (ulong)0); } catch { }
                    string model = r.TryGetValue("Model", out var m) ? m?.ToString() ?? "?" : "?";
                    result[idx] = (model, $"{sz / 1024 / 1024 / 1024:N0} GB");
                }
                if (result.Count > 0) return result;
            }
            catch { }

            // Fallback nativo: enumera PhysicalDriveN via CreateFile + geometry
            try
            {
                for (int i = 0; i < 16; i++)
                {
                    string path = $"\\\\.\\PhysicalDrive{i}";
                    IntPtr h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                    if (h == IntPtr.Zero || h == INVALID_HANDLE_VALUE) continue;
                    try
                    {
                        // Tenta geometry
                        IntPtr outBuf = Marshal.AllocHGlobal(32);
                        try
                        {
                            if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, outBuf, 32, out uint br, IntPtr.Zero))
                            {
                                long cyl = Marshal.ReadInt64(outBuf, 0);
                                uint tracks = (uint)Marshal.ReadInt32(outBuf, 8);
                                uint secPerTrack = (uint)Marshal.ReadInt32(outBuf, 12);
                                uint bytesPerSec = (uint)Marshal.ReadInt32(outBuf, 16);
                                long size = cyl * tracks * secPerTrack * bytesPerSec;
                                result[i] = ($"Disco {i}", $"{size / 1024 / 1024 / 1024:N0} GB");
                            }
                        }
                        finally { Marshal.FreeHGlobal(outBuf); }
                    }
                    finally { try { CloseHandle(h); } catch { } }
                }
            }
            catch { }
            return result;
        }

        public static HashSet<string> GetPagefileDrives()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var rows = Cim.Query("SELECT Name FROM Win32_PageFileUsage");
                foreach (var r in rows)
                {
                    string n = r.TryGetValue("Name", out var v) ? v?.ToString() ?? "" : "";
                    var root = System.IO.Path.GetPathRoot(n)?.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(root)) set.Add(root);
                }
            }
            catch { }
            return set;
        }

        // ══════════════════════════════════════════════
        //  REDE — já é nativo via NetworkInterface (iphlpapi), sem WMI
        // ══════════════════════════════════════════════
        // Mantido aqui documentado como nativo aprovado.

        // ══════════════════════════════════════════════
        //  PROCESSO — parent pid helpers com CIM/WMI fallback + nativo Rust
        // ══════════════════════════════════════════════
        public static Dictionary<int, int> GetParentPidsViaCim()
        {
            var dict = new Dictionary<int, int>();
            try
            {
                var rows = Cim.Query("SELECT ProcessId, ParentProcessId FROM Win32_Process");
                foreach (var r in rows)
                {
                    try
                    {
                        int pid = Convert.ToInt32(r.TryGetValue("ProcessId", out var a) ? a ?? 0 : 0);
                        int ppid = Convert.ToInt32(r.TryGetValue("ParentProcessId", out var b) ? b ?? 0 : 0);
                        dict[pid] = ppid;
                    }
                    catch { }
                }
            }
            catch { }
            return dict;
        }
    }
}
