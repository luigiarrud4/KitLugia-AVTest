using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Indexador nativo de arquivos via USN/MFT (o mesmo metodo da Everything
    /// voidtools: le a Master File Table com FSCTL_ENUM_USN_DATA em vez de
    /// enumerar diretorios). 100% embutido - NENHUMA dependencia externa;
    /// requer apenas elevacao (abertura crua do volume \\.\C:).
    ///
    /// Otimizacoes de performance (17/08):
    /// - CACHE EM DISCO do resultado por volume (estilo Everything DB): a 1a
    ///   leitura da MFT (7-8s num volume de ~4M registros) roda UMA vez; as
    ///   proximas consultas carregam o cache em ~10-50ms. Validacao dupla:
    ///   serial do volume + File/Directory.Exists em cada caminho + miss de
    ///   nome -> invalida e rescaneia (frescor automatico).
    /// - ZERO alocacoes de string durante o scan: nomes de diretorio vao como
    ///   bytes crus num pool unico; so os poucos matches de arquivo decodificam.
    /// - Buffer de ioctl de 8 MB e comparacao byte-a-byte dos nomes procurados.
    /// - Volumes escaneados em PARALELO (Parallel.ForEach).
    /// </summary>
    public static class NativeUsn
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_ALWAYS = 4;
        private const uint FSCTL_ENUM_USN_DATA = 0x000900b3;
        private const int ERROR_HANDLE_EOF = 38;
        private const int ERROR_ACCESS_DENIED = 5;

        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const int MaxDirRecords = 2_000_000; // so diretorios no cache (cadeia de pais); arquivos so casam nome. Memoria ~10x menor que cachear tudo.
        private const int BufferSize = 1 << 23; // 8 MB por ioctl - menos syscalls na MFT inteira (antes 1 MB)
        private const ulong UsnMaximum = 0x00000fffffff0000UL; // USN_MAXIMUM (winioctl.h) - max VALIDO; 0xFFFFFFFFFFFFFFFF retorna EOF

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "NativeUsnCache");

        private static bool _probed;
        private static bool _available;
        private static string _probeMessage = "";

        /// <summary>True se a abertura crua do volume funcionou (rodando elevado).</summary>
        public static bool IsAvailable
        {
            get
            {
                Probe();
                return _available;
            }
        }

        /// <summary>Motivo da indisponibilidade (diagnostico no log/hint).</summary>
        public static string UnavailableReason => _probeMessage;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            IntPtr h = CreateFile(@"\\.\C:", GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_ALWAYS, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                _available = false;
                _probeMessage = err == ERROR_ACCESS_DENIED
                    ? "elevacao insuficiente (abertura crua do volume negada)"
                    : $"erro {err} ao abrir o volume";
                return;
            }
            CloseHandle(h);
            _available = true;
        }

        /// <summary>
        /// Localiza diretorios de executaveis lendo a MFT de TODOS os volumes
        /// NTFS fixos, em PARALELO. Retorna por nome de arquivo a lista de
        /// caminhos completos (limitada a maxPerName), ordenada por preferencia:
        /// dirs 7-Zip/7zip primeiro, depois os mais rasos. Vazio se nao elevado.
        ///
        /// Fluxo: tenta o cache em disco (10-50ms) -> se algum nome procurado
        /// ficou sem caminho valido OU o serial do volume mudou, faz o scan
        /// completo da MFT (7-8s no 1o acesso) e REESCREVE o cache.
        /// </summary>
        public static Dictionary<string, List<string>> FindFileDirectories(IReadOnlyCollection<string> fileNames, int maxPerName = 8)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Probe();
            if (!_available || fileNames == null || fileNames.Count == 0) return result;

            var wanted = new HashSet<string>(fileNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return result;

            // 1) Cache em disco (estilo Everything DB): le e valida caminhos.
            bool missingAny = false;
            if (TryLoadCache(wanted, maxPerName, result, out missingAny, out DateTime cacheBuiltUtc))
            {
                if (!missingAny) return result; // todos os nomes com caminho - pronto em ms
                // Algum nome nao encontrado. Nao pode rescandear a cada chamada
                // (alvo genuinamente ausente - ex: cargo nao instalado - viraria
                // scan de 8s por consulta). So rescaneia quando o cache ESTA
                // VELHO (TTL) - frescor de horas, custo de ms no dia a dia.
                if (DateTime.UtcNow - cacheBuiltUtc < CacheTtlHours)
                    return result;
            }

            // 2) Scan completo da MFT (lento so aqui) + grava o cache por volume.
            var fresh = ScanAllVolumes(wanted, maxPerName);
            if (fresh != null && fresh.Count > 0)
            {
                result.Clear();
                foreach (var kvp in fresh)
                {
                    if (!result.TryGetValue(kvp.Key, out var list))
                    {
                        list = new List<string>();
                        result[kvp.Key] = list;
                    }
                    foreach (var p in kvp.Value)
                        if (list.Count < maxPerName && !list.Contains(p))
                            list.Add(p);
                }
                return result;
            }

            // Scan falhou (ex: elevacao perdida): fica com o que o cache tinha.
            return result;
        }

        private static readonly TimeSpan CacheTtlHours = TimeSpan.FromHours(6);

        /// <summary>
        /// Carrega os caches por volume (arquivo por serial) e preenche result
        /// com os caminhos VALIDOS (Directory.Exists) para os nomes pedidos.
        /// missingAny = algum nome pedido nao tem caminho no cache.
        /// cacheBuiltUtc = BuiltUtc mais recente entre os volumes carregados.
        /// </summary>
        private static bool TryLoadCache(HashSet<string> wanted, int maxPerName,
            Dictionary<string, List<string>> result, out bool missingAny, out DateTime cacheBuiltUtc)
        {
            missingAny = false;
            cacheBuiltUtc = DateTime.MinValue;
            bool loadedAny = false;
            if (!Directory.Exists(CacheDir)) return false;
            try
            {
                foreach (var file in Directory.GetFiles(CacheDir, "usn_*.json"))
                {
                    try
                    {
                        var data = System.Text.Json.JsonSerializer.Deserialize<CacheVolume>(File.ReadAllText(file));
                        if (data == null || data.Entries == null) { TryDelete(file); continue; }
                        // Serial do volume mudou? (cache de outra maquina/pendrive)
                        string serial = GetVolumeSerial(data.VolumeLetter);
                        if (string.IsNullOrEmpty(serial) || !string.Equals(serial, data.VolumeSerial, StringComparison.OrdinalIgnoreCase))
                        {
                            TryDelete(file); // volume nao existe mais ou mudou - descarta
                            continue;
                        }
                        loadedAny = true;
                        if (DateTime.TryParse(data.BuiltUtc, out var bt) && bt > cacheBuiltUtc)
                            cacheBuiltUtc = bt;
                        foreach (var name in wanted)
                        {
                            if (!data.Entries.TryGetValue(name, out var paths)) continue;
                            foreach (var p in paths)
                            {
                                if (!Directory.Exists(p)) continue; // app saiu/desinstalado - valida
                                if (!result.TryGetValue(name, out var list))
                                {
                                    list = new List<string>();
                                    result[name] = list;
                                }
                                if (list.Count >= maxPerName) break;
                                if (!list.Contains(p)) list.Add(p);
                            }
                        }
                    }
                    catch
                    {
                        TryDelete(file); // cache corrompido/incompleto - recria no scan
                    }
                }
            }
            catch
            {
                // sem acesso a pasta - segue para o scan
            }
            if (!loadedAny) { missingAny = true; return false; }
            foreach (var n in wanted)
                if (!result.ContainsKey(n)) { missingAny = true; break; }
            return true;
        }

        /// <summary>Scan completo de todos os volumes NTFS fixos (paralelo).</summary>
        private static Dictionary<string, List<string>>? ScanAllVolumes(HashSet<string> wanted, int maxPerName)
        {
            var drives = DriveInfo.GetDrives()
                .Where(d =>
                {
                    if (d.DriveType != DriveType.Fixed) return false;
                    try { return string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })
                .Select(d => d.Name[0])
                .ToList();
            if (drives.Count == 0) return null;

            // Volumes em paralelo: a MFT de cada volume e independente.
            var perVolume = new ConcurrentDictionary<char, Dictionary<string, List<string>>>();
            Parallel.ForEach(drives, letter =>
            {
                try
                {
                    var matches = ScanVolume(letter, wanted);
                    if (matches != null && matches.Count > 0)
                    {
                        perVolume[letter] = matches;
                        SaveVolumeCache(letter, matches);
                    }
                }
                catch { /* volume falhou - segue para o proximo */ }
            });
            if (perVolume.IsEmpty) return null;

            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in perVolume)
            {
                foreach (var inner in kvp.Value)
                {
                    if (!merged.TryGetValue(inner.Key, out var list))
                    {
                        list = new List<string>();
                        merged[inner.Key] = list;
                    }
                    foreach (var p in inner.Value)
                        if (list.Count < maxPerName && !list.Contains(p))
                            list.Add(p);
                }
            }
            return merged;
        }

        private static string GetVolumeSerial(char letter)
        {
            var vol = new StringBuilder(32);
            var fs = new StringBuilder(32);
            if (GetVolumeInformation($@"{letter}:\", vol, (uint)vol.Capacity, out uint serial,
                out _, out _, fs, (uint)fs.Capacity))
                return serial.ToString("X8");
            return "";
        }

        private static void SaveVolumeCache(char letter, Dictionary<string, List<string>> entries)
        {
            try
            {
                if (!Directory.Exists(CacheDir)) Directory.CreateDirectory(CacheDir);
                var data = new CacheVolume
                {
                    VolumeLetter = letter,
                    VolumeSerial = GetVolumeSerial(letter),
                    BuiltUtc = DateTime.UtcNow.ToString("o"),
                    Entries = entries
                };
                File.WriteAllText(Path.Combine(CacheDir, $"usn_{data.VolumeSerial}.json"),
                    System.Text.Json.JsonSerializer.Serialize(data));
            }
            catch { /* cache e otimizacao - falha nao aborta */ }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        /// <summary>
        /// Le a MFT do volume e devolve nome -&gt; lista de caminhos completos
        /// (todas as ocorrencias de arquivo). Null se o volume nao suportar
        /// USN V3 (ReFS/raros) ou abrir falhar.
        ///
        /// Otimizacao chave: nomes de DIRETORIO sao copiados como bytes crus
        /// para um pool unico (byte[] de crescimento exponencial) com offsets -
        /// NENHUMA string alocada no caminho quente. So os arquivos que casam
        /// com wanted decodificam nome; a cadeia de pais decodifica no resolve.
        /// </summary>
        private static Dictionary<string, List<string>>? ScanVolume(char letter, HashSet<string> wanted)
        {
            IntPtr h = CreateFile($@"\\.\{letter}:", GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_ALWAYS, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE_VALUE)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                    Logger.Log($"NativeUsn: volume {letter}: sem elevacao (erro 5) - usando scan de disco.");
                return null;
            }
            try
            {
                // Cache de diretorios: FRN -> (Parent, idx) onde idx aponta para o
                // pool de nomes. Nome curto 8.3 e descartado (mantem o mais longo).
                var dirCache = new Dictionary<ulong, (ulong Parent, int NameIdx)>(262144);
                var nameMeta = new List<(int Off, int Len)>(65536);  // offsets no pool
                var namePool = new byte[1 << 20];                    // pool de bytes, cresce sob demanda
                int poolPos = 0;
                var matchFrns = new List<(ulong Frn, ulong Parent, int NameIdx)>();
                bool dirCapped = false;

                var buf = new byte[BufferSize];
                var mft = new byte[28];
                WriteU64(mft, 0, 0);              // StartFileReferenceNumber
                WriteU64(mft, 8, 0);              // LowUsn
                WriteU64(mft, 16, UsnMaximum);    // HighUsn (toda a MFT)
                WriteU16(mft, 24, 3);             // pedir registros V3
                WriteU16(mft, 26, 3);

                // Nomes procurados em BYTES (UTF-16LE, como vem da MFT) com
                // comparacao case-insensitive ASCII - ZERO alocacao por registro
                // de arquivo (antes: 1 string + HashSet por arquivo = ~2M allocs).
                var wantedBytes = new byte[wanted.Count][];
                var wantedLens = new int[wanted.Count];
                int wi = 0;
                foreach (var n in wanted)
                {
                    wantedBytes[wi] = Encoding.Unicode.GetBytes(n);
                    wantedLens[wi] = wantedBytes[wi].Length;
                    wi++;
                }
                int wantedCount = wanted.Count;

                while (true)
                {
                    if (!DeviceIoControl(h, FSCTL_ENUM_USN_DATA, mft, (uint)mft.Length, buf, (uint)buf.Length, out uint got, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == ERROR_HANDLE_EOF) break;
                        Logger.Log($"NativeUsn: volume {letter}: FSCTL_ENUM_USN_DATA falhou (erro {err}) - parcial.");
                        break;
                    }
                    if (got < 8) break;
                    ulong nextFrn = BitConverter.ToUInt64(buf, 0);
                    WriteU64(mft, 0, nextFrn);

                    int off = 8;
                    while (off + 60 <= got)
                    {
                        uint recLen = BitConverter.ToUInt32(buf, off);
                        if (recLen < 60 || off + recLen > got) break;

                        ulong frn = BitConverter.ToUInt64(buf, off + 8);
                        ulong parent = BitConverter.ToUInt64(buf, off + 16);
                        ushort nameLen = BitConverter.ToUInt16(buf, off + 56); // bytes
                        ushort nameOff = BitConverter.ToUInt16(buf, off + 58);
                        // FileNameOffset e relativo ao INICIO DO REGISTRO (off), nao ao buffer.
                        if (nameLen > 0 && off + nameOff + nameLen <= got)
                        {
                            uint attrs = BitConverter.ToUInt32(buf, off + 52);
                            bool isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
                            if (isDir)
                            {
                                if (dirCache.Count >= MaxDirRecords)
                                {
                                    dirCapped = true;
                                    break;
                                }
                                // Mesmo FRN aparece 2x com 8.3 habilitado (nome curto + longo):
                                // manter o nome mais longo (o nome curto quebraria o caminho).
                                // Comparacao por COMPRIMENTO de bytes - sem decodificar string.
                                if (dirCache.TryGetValue(frn, out var existing))
                                {
                                    if (nameMeta[existing.NameIdx].Len >= nameLen)
                                    {
                                        off += (int)recLen;
                                        continue;
                                    }
                                    // nome novo e mais longo: aponta o MESMO FRN para o novo offset
                                }
                                int idx = nameMeta.Count;
                                nameMeta.Add((poolPos, nameLen));
                                if (poolPos + nameLen > namePool.Length)
                                    Array.Resize(ref namePool, Math.Max(namePool.Length * 2, poolPos + nameLen));
                                Array.Copy(buf, off + nameOff, namePool, poolPos, nameLen);
                                poolPos += nameLen;
                                dirCache[frn] = (parent, idx);
                            }
                            else if (NameMatches(buf.AsSpan(off + nameOff, nameLen), wantedBytes, wantedLens, wantedCount))
                            {
                                // Copia o nome do match para o pool: o buffer do ioctl
                                // e REUTILIZADO entre iteracoes - offsets do buf nao valem
                                // depois do loop. Pool separado: matches sao poucos.
                                int mOff = poolPos;
                                if (poolPos + nameLen > namePool.Length)
                                    Array.Resize(ref namePool, Math.Max(namePool.Length * 2, poolPos + nameLen));
                                Array.Copy(buf, off + nameOff, namePool, poolPos, nameLen);
                                poolPos += nameLen;
                                int mIdx = nameMeta.Count;
                                nameMeta.Add((mOff, nameLen));
                                matchFrns.Add((frn, parent, mIdx));
                            }
                        }
                        off += (int)recLen;
                    }
                    if (dirCapped) break;
                }
                if (dirCapped)
                    Logger.Log($"NativeUsn: volume {letter}: cap de {MaxDirRecords} diretorios atingido - scan parcial.");

                // 3) Resolve caminhos completos caminhando a cadeia de pais (diretorios).
                //    So AQUI os nomes do pool decodificam (poucos: os ancestrais dos matches).
                var perName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (frn, parent, nameIdx) in matchFrns)
                {
                    var mMeta = nameMeta[nameIdx];
                    string name = Encoding.Unicode.GetString(namePool, mMeta.Off, mMeta.Len);
                    string? full = ResolvePath(letter, frn, parent, name, dirCache, nameMeta, namePool);
                    if (full == null) continue;
                    string dir = Path.GetDirectoryName(full) ?? "";
                    if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                    if (!perName.TryGetValue(name, out var list))
                    {
                        list = new List<string>();
                        perName[name] = list;
                    }
                    if (!list.Contains(dir)) list.Add(dir);
                }

                // 4) Ordena por preferencia: 7-Zip/7zip primeiro, depois o mais raso.
                foreach (var kvp in perName)
                {
                    kvp.Value.Sort((a, b) =>
                    {
                        string al = a.ToLowerInvariant(), bl = b.ToLowerInvariant();
                        bool ag = al.Contains("7-zip") || al.Contains("7zip");
                        bool bg = bl.Contains("7-zip") || bl.Contains("7zip");
                        if (ag != bg) return ag ? -1 : 1;
                        return al.Count(c => c == '\\').CompareTo(bl.Count(c => c == '\\'));
                    });
                }
                return perName;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        private static string? ResolvePath(char letter, ulong frn, ulong parent, string name,
            Dictionary<ulong, (ulong Parent, int NameIdx)> cache,
            List<(int Off, int Len)> nameMeta, byte[] namePool)
        {
            var parts = new List<string> { name };
            var seen = new HashSet<ulong>();
            ulong cur = parent;
            int guard = 0;
            while (cur != 0 && guard++ < 64 && seen.Add(cur))
            {
                if (!cache.TryGetValue(cur, out var rec)) break;
                if (rec.NameIdx < 0 || rec.NameIdx >= nameMeta.Count) break;
                var meta = nameMeta[rec.NameIdx];
                if (meta.Len <= 0) break; // raiz do volume
                if (meta.Off < 0 || meta.Off + meta.Len > namePool.Length) break;
                string pname = Encoding.Unicode.GetString(namePool, meta.Off, meta.Len);
                if (pname.Length == 0) break; // raiz do volume
                parts.Add(pname);
                cur = rec.Parent;
            }
            parts.Reverse();
            string full = $"{letter}:\\{string.Join("\\", parts)}";
            return File.Exists(full) ? full : null;
        }

        private static void WriteU64(byte[] b, int off, ulong v) => Array.Copy(BitConverter.GetBytes(v), 0, b, off, 8);
        private static void WriteU16(byte[] b, int off, ushort v) => Array.Copy(BitConverter.GetBytes(v), 0, b, off, 2);

        /// <summary>
        /// Casamento case-insensitive de nome UTF-16LE da MFT contra os alvos
        /// pre-computados em bytes. Sem alocacao: compara comprimento e depois
        /// byte a byte (somente o byte baixo, `| 0x20` normaliza A-Z - todos
        /// os nomes procurados sao ASCII).
        /// </summary>
        private static bool NameMatches(ReadOnlySpan<byte> name, byte[][] wanted, int[] lens, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (name.Length != lens[i]) continue;
                var w = wanted[i];
                bool eq = true;
                for (int j = 0; j < name.Length; j += 2)
                {
                    if ((name[j] | 0x20) != (w[j] | 0x20))
                    {
                        eq = false;
                        break;
                    }
                }
                if (eq) return true;
            }
            return false;
        }

        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode,
            byte[]? lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer,
            uint volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
            out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, uint fileSystemNameSize);

        /// <summary>Cache em disco do resultado do scan por volume (arquivo por serial).</summary>
        private sealed class CacheVolume
        {
            public char VolumeLetter { get; set; }
            public string VolumeSerial { get; set; } = "";
            public string BuiltUtc { get; set; } = "";
            public Dictionary<string, List<string>> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
