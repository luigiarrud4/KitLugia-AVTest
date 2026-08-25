using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public static class MftFlags
    {
        public const ushort Directory = 0x1;
        public const ushort Reparse = 0x2;
        public const ushort HiddenSystem = 0x4;
    }

    public readonly struct MftEntry
    {
        public readonly uint Rec;
        public readonly uint Parent;
        public readonly ulong Size;
        public readonly ulong LastWrite;
        public readonly ushort Flags;
        public readonly string Name;

        public MftEntry(uint rec, uint parent, ulong size, ulong lastWrite, ushort flags, string name)
        {
            Rec = rec;
            Parent = parent;
            Size = size;
            LastWrite = lastWrite;
            Flags = flags;
            Name = name;
        }
    }

    public sealed class MftIndex
    {
        public MftEntry[] Entries { get; }
        public int[] Starts { get; }
        public int[] RecToIdx { get; }

        public MftIndex(MftEntry[] entries)
        {
            Entries = entries;
            uint maxRec = 0;
            foreach (var e in entries)
            {
                if (e.Rec > maxRec) maxRec = e.Rec;
                if (e.Parent > maxRec) maxRec = e.Parent;
            }
            // Guard: prevent integer overflow when maxRec is near uint.MaxValue
            if (maxRec > 0x7FFF_FFFE) { Starts = Array.Empty<int>(); RecToIdx = Array.Empty<int>(); return; }
            int rc = (int)maxRec + 1;
            var cnt = new int[rc + 1];
            foreach (var e in entries)
            {
                if (e.Parent < (uint)rc) cnt[e.Parent + 1]++;
            }
            var starts = new int[rc + 2];
            int acc = 0;
            for (int i = 0; i < rc; i++)
            {
                acc += cnt[i];
                starts[i + 1] = acc;
            }
            var rti = new int[rc];
            Array.Fill(rti, -1);
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Rec < (uint)rc) rti[entries[i].Rec] = i;
            }
            Starts = starts;
            RecToIdx = rti;
        }
    }

    public sealed class MftVolumeResult
    {
        public string VolumeRoot = string.Empty;
        public bool VolumeFailed = true;
        public int ErrorCode = 0;
        public MftEntry[] Entries = Array.Empty<MftEntry>();
        public uint[] PrefixRecs = Array.Empty<uint>();
        public string[] Locations = Array.Empty<string>();
        public MftIndex? Index;
    }

    public static class NativeMft
    {
        public const uint PrefixNotFound = 0xFFFF_FFFF;

        private const int MaxBufferTries = 8;

        [DllImport("rust_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mft_scan_ffi(IntPtr volumeRoot, IntPtr prefixes, IntPtr outBuf, int outCapacity);

        public static List<MftVolumeResult>? ScanAllVolumes(string[] locations)
        {
            try
            {
                var groups = locations
                    .Where(l => !string.IsNullOrEmpty(l))
                    .GroupBy(l => (Path.GetPathRoot(l) ?? l).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (groups.Count == 0) return null;

                var results = new List<MftVolumeResult>();
                var gate = new object();
                Parallel.ForEach(groups, g =>
                {
                    var res = TryScanVolume(g.Key, g.ToArray());
                    if (res != null)
                    {
                        lock (gate) results.Add(res);
                    }
                });
                return results.Count > 0 ? results : null;
            }
            catch (DllNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        public static MftVolumeResult? TryScanVolume(string volumeRoot, string[] locations)
        {
            try
            {
                string prefixes = string.Join(";", locations.Select(l =>
                    l.Substring(volumeRoot.Length).TrimStart('\\').Replace('\\', '/')));
                IntPtr rootPtr = Marshal.StringToCoTaskMemUni(volumeRoot);
                IntPtr prePtr = Marshal.StringToCoTaskMemUni(prefixes);
                int cap = 1 << 20;
                IntPtr buf = Marshal.AllocHGlobal(cap);
                try
                {
                    int rc = mft_scan_ffi(rootPtr, prePtr, buf, cap);
                    int tries = 0;
                    for (; rc <= -1000 && tries < MaxBufferTries; tries++)
                    {
                        int needed = -rc - 1000;
                        cap = Math.Max(needed, cap * 2);
                        Marshal.FreeHGlobal(buf);
                        buf = Marshal.AllocHGlobal(cap);
                        rc = mft_scan_ffi(rootPtr, prePtr, buf, cap);
                    }

                    var res = new MftVolumeResult
                    {
                        VolumeRoot = volumeRoot,
                        Locations = locations,
                        VolumeFailed = rc < 0,
                        ErrorCode = rc < 0 ? rc : 0
                    };
                    if (rc < 0)
                    {
                        Logger.Log($"[MFT] Volume {volumeRoot}: FALHOU rc={rc} apos {tries} tentativa(s) de buffer - fallback classico neste volume");
                        return res;
                    }

                    var (entries, prefixRecs) = ParseBlob(buf, rc);
                    res.Entries = entries;
                    res.PrefixRecs = prefixRecs;
                    res.Index = new MftIndex(entries);
                    Logger.Log($"[MFT] Volume {volumeRoot}: OK rc={rc} ({tries} tentativa(s)), blob {rc / 1048576.0:N1} MB, {entries.Length:N0} entradas, prefixos [{string.Join(", ", prefixRecs.Select(p => p == PrefixNotFound ? "?" : p.ToString()))}]");
                    return res;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                    Marshal.FreeCoTaskMem(rootPtr);
                    Marshal.FreeCoTaskMem(prePtr);
                }
            }
            catch (DllNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        private static (MftEntry[] Entries, uint[] PrefixRecs) ParseBlob(IntPtr buf, int rc)
        {
            int pos = 0;
            uint cnt = ReadU32(buf, ref pos);
            var prefixRecs = new uint[cnt];
            for (int i = 0; i < cnt; i++) prefixRecs[i] = ReadU32(buf, ref pos);

            var list = new List<MftEntry>();
            while (pos + 36 <= rc)
            {
                ulong rec = ReadU64(buf, ref pos);
                ulong parent = ReadU64(buf, ref pos);
                ulong size = ReadU64(buf, ref pos);
                ulong lw = ReadU64(buf, ref pos);
                ushort flags = ReadU16(buf, ref pos);
                ushort nlen = ReadU16(buf, ref pos);
                if (pos + nlen * 2 > rc) break;
                string name = Marshal.PtrToStringUni(IntPtr.Add(buf, pos), nlen) ?? "";
                pos += nlen * 2;
                list.Add(new MftEntry((uint)rec, (uint)parent, size, lw, flags, name));
            }
            return (list.ToArray(), prefixRecs);
        }

        private static uint ReadU32(IntPtr b, ref int pos)
        {
            uint v = unchecked((uint)Marshal.ReadInt32(b, pos));
            pos += 4;
            return v;
        }

        private static ulong ReadU64(IntPtr b, ref int pos)
        {
            ulong lo = unchecked((uint)Marshal.ReadInt32(b, pos));
            ulong hi = unchecked((uint)Marshal.ReadInt32(b, pos + 4));
            pos += 8;
            return lo | (hi << 32);
        }

        private static ushort ReadU16(IntPtr b, ref int pos)
        {
            ushort v = unchecked((ushort)Marshal.ReadInt16(b, pos));
            pos += 2;
            return v;
        }
    }
}