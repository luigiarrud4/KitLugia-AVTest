using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace KitLugia.Core
{
    /// <summary>
    /// Tamanho de pastas via MFT — o MESMO mecanismo do Everything:
    /// le a master file table uma vez por volume e soma os arquivos sob cada
    /// pasta-alvo. Muito mais rapido que enumerar o disco via DirectoryInfo.
    /// Volumes sem MFT / resultados incompletos caem para o fallback classico.
    /// </summary>
    public static class FolderSizeMft
    {
        private static readonly string WindowsRoot =
            Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');

        /// <summary>
        /// Devolve um dicionario pasta -> bytes. Pastas inexistentes / sem permissao /
        /// sob a pasta do Windows sao ignoradas. Nunca lanca excecao.
        /// </summary>
        public static Dictionary<string, long> Compute(IEnumerable<string> folders, CancellationToken ct = default)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var byVolume = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in folders)
            {
                string full;
                try { full = Path.GetFullPath((raw ?? "").Trim().Trim('"', ' ').TrimEnd('\\')); }
                catch { continue; }
                if (!IsCandidate(full)) continue;
                if (!seen.Add(full)) continue;
                if (!Directory.Exists(full)) continue;
                string root = Path.GetPathRoot(full) ?? "";
                if (root.Length == 0) continue;
                if (!byVolume.TryGetValue(root, out var list)) { list = new List<string>(); byVolume[root] = list; }
                list.Add(full);
            }

            foreach (var kv in byVolume)
            {
                ct.ThrowIfCancellationRequested();
                var res = NativeMft.TryScanVolume(kv.Key, kv.Value.ToArray());
                if (res == null || res.VolumeFailed || res.Index == null)
                {
                    ClassicFallback(result, kv.Value, ct);
                    continue;
                }
                for (int i = 0; i < kv.Value.Count && i < res.PrefixRecs.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    uint rec = res.PrefixRecs[i];
                    if (rec == NativeMft.PrefixNotFound)
                    {
                        ClassicFallback(result, new[] { kv.Value[i] }, ct);
                        continue;
                    }
                    result[kv.Value[i]] = SumFolder(res.Index, res.Entries, rec, ct);
                }
            }
            return result;
        }

        private static bool IsCandidate(string full)
        {
            string root = Path.GetPathRoot(full) ?? "";
            if (root.Length == 0 || full.Length <= root.Length) return false;  // a raiz do drive
            if (full.Length < 6) return false;                                   // lixo quase vazio
            if (full.StartsWith(WindowsRoot, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static long SumFolder(MftIndex index, MftEntry[] entries, uint rootRec, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<uint>();
            stack.Push(rootRec);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                uint rec = stack.Pop();
                int s = index.Starts[(int)rec + 1];
                int e = index.Starts[(int)rec + 2];
                if (e > entries.Length) e = entries.Length;
                for (int j = s; j < e; j++)
                {
                    var c = entries[j];
                    if ((c.Flags & MftFlags.Reparse) != 0) continue; // junction: nao duplicar contagem
                    if ((c.Flags & MftFlags.Directory) != 0) { stack.Push(c.Rec); continue; }
                    total += (long)c.Size;
                }
            }
            return total;
        }

        private static void ClassicFallback(Dictionary<string, long> result, IEnumerable<string> folders, CancellationToken ct)
        {
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                long total = 0;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    }))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { total += new FileInfo(file).Length; } catch { }
                    }
                }
                catch { }
                result[folder] = total;
            }
        }
    }
}
