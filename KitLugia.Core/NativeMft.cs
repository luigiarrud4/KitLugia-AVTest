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
        private readonly MftNameBlob? _names;
        private readonly int _nameIndex;
        private readonly string? _eagerName;

        // Nome resolvido sob demanda: o blob guarda os bytes UTF-16 crus e so
        // materializa a string quando alguem le .Name (cache por entrada). Sem
        // isso, 4 milhoes de strings no parse = pausa de GC na UI thread.
        public string Name => _eagerName ?? _names?.Get(_nameIndex) ?? string.Empty;

        public MftEntry(uint rec, uint parent, ulong size, ulong lastWrite, ushort flags, string name)
        {
            Rec = rec;
            Parent = parent;
            Size = size;
            LastWrite = lastWrite;
            Flags = flags;
            _eagerName = name;
            _names = null;
            _nameIndex = -1;
        }

        internal MftEntry(uint rec, uint parent, ulong size, ulong lastWrite, ushort flags,
                          MftNameBlob names, int nameIndex)
        {
            Rec = rec;
            Parent = parent;
            Size = size;
            LastWrite = lastWrite;
            Flags = flags;
            _eagerName = null;
            _names = names;
            _nameIndex = nameIndex;
        }
    }

    // Bytes UTF-16 de todos os nomes do blob, concatenados, com offset/tamanho
    // por entrada. Get() decodifica uma vez e cacheia — quem nunca le o nome
    // (FolderSizeMft, por exemplo) nao paga string nenhuma.
    internal sealed class MftNameBlob
    {
        private readonly byte[] _data;
        private readonly int[] _offsets;
        private readonly ushort[] _lengths;
        private readonly string?[] _cache;

        public MftNameBlob(byte[] data, int[] offsets, ushort[] lengths)
        {
            _data = data;
            _offsets = offsets;
            _lengths = lengths;
            _cache = new string?[lengths.Length];
        }

        public string Get(int i)
        {
            string? s = _cache[i];
            if (s != null) return s;
            int chars = _lengths[i];
            s = chars == 0 ? string.Empty
                : System.Text.Encoding.Unicode.GetString(_data, _offsets[i], chars * 2);
            _cache[i] = s;
            return s;
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

        // stage 0 = registros MFT parseados (done/total); stage 1 = indexacao/emissao.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MftProgressCallback(uint stage, ulong done, ulong total);

        [DllImport("rust_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mft_scan_ffi_ex(IntPtr volumeRoot, IntPtr prefixes, IntPtr outBuf,
            int outCapacity, MftProgressCallback? progress);

        // Variante com indice MFT persistente em disco: se o USN do volume
        // nao mudou, o blob sai do arquivo em <1s. Ausente em DLLs antigas —
        // _indexExportOk faz fallback permanente para mft_scan_ffi_ex.
        [DllImport("rust_native.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int mft_scan_index_ffi(IntPtr volumeRoot, IntPtr prefixes, IntPtr outBuf,
            int outCapacity, MftProgressCallback? progress);
        private static int _indexExportOk = 1; // 1 = tenta, 0 = caiu no fallback

        public static List<MftVolumeResult>? ScanAllVolumes(string[] locations, Action<string>? status = null)
        {
            try
            {
                var groups = locations
                    .Where(l => !string.IsNullOrEmpty(l))
                    .GroupBy(l => (Path.GetPathRoot(l) ?? l).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (groups.Count == 0) return null;

                var results = new List<MftVolumeResult>();

                // SEQUENCIAL, não paralelo: cada volume materializa um MftEntry[] com
                // UM ITEM POR ARQUIVO do volume (C:\ com 4 milhões de arquivos ≈ 160 MB
                // de array + ~48 MB de índices). Em paralelo, 2-3 volumes multiplicam
                // o pico de RAM do processo. Volume por vez, o pico fica em UM volume.
                //
                // SustainedLowLatency durante o scan: os arrays grandes do parse
                // (entradas + blob de nomes) estouram o LOH e disparam GCs de pausa
                // total que congelavam a UI por ~2s. Nesse modo o GC pausa menos
                // tempo por coleta (mais coletoras curtas em vez de raras longas).
                var prevLatency = System.Runtime.GCSettings.LatencyMode;
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                try
                {
                    foreach (var g in groups)
                    {
                        status?.Invoke($"Indexando volume {g.Key}...");
                        var res = TryScanVolume(g.Key, g.ToArray(), status);
                        if (res != null) results.Add(res);
                    }
                }
                finally
                {
                    System.Runtime.GCSettings.LatencyMode = prevLatency;
                }
                return results.Count > 0 ? results : null;
            }
            catch (DllNotFoundException) { return null; }
            catch (BadImageFormatException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
        }

        // Portao global: UM scan de MFT por vez no processo inteiro. Cada scan
        // aloca ~500 MB de indice e usa ~75% dos nucleos; dois simultaneos
        // (ex.: portateis + FolderSizeMft medindo programas) deixavam a UI sem
        // CPU — a animacao parava junto com a tela. Quem chega depois espera.
        private static readonly System.Threading.SemaphoreSlim MftScanGate = new(1, 1);

        public static MftVolumeResult? TryScanVolume(string volumeRoot, string[] locations, Action<string>? status = null)
        {
            if (MftScanGate.CurrentCount == 0)
                status?.Invoke("Aguardando outro scan MFT terminar...");
            MftScanGate.Wait();
            try
            {
                return TryScanVolumeCore(volumeRoot, locations, status);
            }
            finally
            {
                MftScanGate.Release();
            }
        }

        private static MftVolumeResult? TryScanVolumeCore(string volumeRoot, string[] locations, Action<string>? status = null)
        {
            try
            {
                string prefixes = string.Join(";", locations.Select(l =>
                    l.Substring(volumeRoot.Length).TrimStart('\\').Replace('\\', '/')));
                IntPtr rootPtr = Marshal.StringToCoTaskMemUni(volumeRoot);
                IntPtr prePtr = Marshal.StringToCoTaskMemUni(prefixes);
                int cap = 1 << 20;
                IntPtr buf = Marshal.AllocHGlobal(cap);
                // Callback nativo -> status na UI. Throttle aqui dentro: o Rust
                // ja limita a ~8 chamadas/s, mas o marshal ainda custa; e o
                // delegate precisa ficar vivo durante a chamada P/Invoke.
                MftProgressCallback? nativeCb = null;
                var lastStatus = 0L;
                var swStatus = System.Diagnostics.Stopwatch.StartNew();
                if (status != null)
                {
                    nativeCb = (stage, done, total) =>
                    {
                        try
                        {
                            if (swStatus.ElapsedMilliseconds - lastStatus < 150) return;
                            lastStatus = swStatus.ElapsedMilliseconds;
                            if (stage == 0 && total > 0)
                                status($"Lendo MFT de {volumeRoot} — {done * 100 / total}%");
                            else if (stage == 1)
                                status($"Montando índice de {volumeRoot}...");
                        }
                        catch { }
                    };
                }
                int ScanOnce()
                {
                    if (_indexExportOk == 1)
                    {
                        try { return mft_scan_index_ffi(rootPtr, prePtr, buf, cap, nativeCb); }
                        catch (EntryPointNotFoundException) { _indexExportOk = 0; }
                        catch (DllNotFoundException) { _indexExportOk = 0; }
                    }
                    return mft_scan_ffi_ex(rootPtr, prePtr, buf, cap, nativeCb);
                }

                try
                {
                    int rc = ScanOnce();
                    int tries = 0;
                    for (; rc <= -1000 && tries < MaxBufferTries; tries++)
                    {
                        int needed = -rc - 1000;
                        cap = Math.Max(needed, cap * 2);
                        Marshal.FreeHGlobal(buf);
                        buf = Marshal.AllocHGlobal(cap);
                        rc = ScanOnce();
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
                    status?.Invoke($"Volume {volumeRoot}: {entries.Length:N0} entradas indexadas");
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
            // rc = bytes efetivamente gravados pelo nativo. NUNCA confiar no
            // cabecalho do blob: um cnt corrompido causava new uint[bilhoes] e
            // leituras Marshal.Read* alem da memoria alocada (access violation).
            if (buf == IntPtr.Zero || rc < 4)
                return (Array.Empty<MftEntry>(), Array.Empty<uint>());

            int pos = 0;
            uint cnt = ReadU32(buf, ref pos);
            uint maxPrefixos = (uint)((rc - pos) / 4);
            if (cnt > maxPrefixos)
                return (Array.Empty<MftEntry>(), Array.Empty<uint>());

            var prefixRecs = new uint[cnt];
            for (int i = 0; i < cnt; i++) prefixRecs[i] = ReadU32(buf, ref pos);

            // Layout da entrada no blob: u64 rec, u64 parent, u64 size,
            // u64 lastWrite, u16 flags, u16 nameLen, name UTF-16 — 36 bytes
            // fixos + nome. O nome NAO e materializado como string: o volume
            // inteiro traz milhoes de entradas e cada string custava ~40 MB de
            // lixo no GC (pausa na UI thread). Em vez de descartar o nome
            // (quebrava o scanner de portateis: Name="" -> 0 resultados), os
            // bytes UTF-16 vao crus para o MftNameBlob e viram string sob
            // demanda, com cache por entrada.
            //
            // Pass 1: conta entradas e bytes de nome para alocar arrays exatos
            // (sem List/doubling). Pass 2: copia os nomes. Pass 3: monta as
            // MftEntry apontando para o blob. Tres varreduras de ponteiro sobre
            // o blob sao baratas perto de alocar/copiar strings.
            int entryCount = 0;
            long nameBytesTotal = 0;
            int scan = pos;
            while (scan + 36 <= rc)
            {
                int nlen = Marshal.ReadInt16(buf, scan + 34);
                if (nlen < 0 || (long)scan + 36 + nlen * 2L > rc) break;
                nameBytesTotal += nlen * 2L;
                scan += 36 + nlen * 2;
                entryCount++;
            }
            if (entryCount == 0)
                return (Array.Empty<MftEntry>(), prefixRecs);

            // Nome total absurdo (> ~2 GB): desiste dos nomes em vez de estourar
            // na alocacao — as entradas continuam validas, so .Name fica vazio
            // (mesmo comportamento do fallback classico de scan).
            bool namesOk = nameBytesTotal <= int.MaxValue - 16;
            var nameData = namesOk ? new byte[nameBytesTotal] : Array.Empty<byte>();
            var nameOffs = new int[namesOk ? entryCount : 0];
            var nameLens = new ushort[namesOk ? entryCount : 0];
            var blob = namesOk ? new MftNameBlob(nameData, nameOffs, nameLens) : null;

            scan = pos;
            int namePos = 0, idx = 0;
            if (namesOk)
            {
                while (scan + 36 <= rc && idx < entryCount)
                {
                    int nlen = Marshal.ReadInt16(buf, scan + 34);
                    if (nlen < 0 || (long)scan + 36 + nlen * 2L > rc) break;
                    nameOffs[idx] = namePos;
                    nameLens[idx] = (ushort)nlen;
                    int nbytes = nlen * 2;
                    if (nbytes > 0)
                        Marshal.Copy(IntPtr.Add(buf, scan + 36), nameData, namePos, nbytes);
                    namePos += nbytes;
                    scan += 36 + nbytes;
                    idx++;
                }
            }

            var entries = new MftEntry[entryCount];
            scan = pos;
            for (int i = 0; i < entryCount; i++)
            {
                ulong rec = ReadU64(buf, ref scan);
                ulong parent = ReadU64(buf, ref scan);
                ulong size = ReadU64(buf, ref scan);
                ulong lw = ReadU64(buf, ref scan);
                ushort flags = ReadU16(buf, ref scan);
                ushort nlen = ReadU16(buf, ref scan);
                scan += nlen * 2;
                entries[i] = blob != null
                    ? new MftEntry((uint)rec, (uint)parent, size, lw, flags, blob, i)
                    : new MftEntry((uint)rec, (uint)parent, size, lw, flags, "");
            }
            return (entries, prefixRecs);
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