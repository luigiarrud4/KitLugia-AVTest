using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// Diagnóstico de shrink SOMENTE LEITURA.
    ///
    /// Nenhum método desta classe altera o sistema: só consulta o Windows e lê logs
    /// e o registro. Serve para responder "por que este disco não encolhe mais?".
    ///
    /// Técnica do bloqueador (evento 259 do Microsoft-Windows-Defrag): o Windows grava
    /// no Event Log o caminho do arquivo que limita o shrink sempre que calcula o
    /// limite. Basta forçar o cálculo (Get-PartitionSupportedSize) e ler o evento.
    /// Fonte: implementação do hop (engine/MakeRoom.ps1, Get-ShrinkBlocker).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class ShrinkDiagnostics
    {
        private const string PagingKeyPath =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        public sealed class BlockerInfo
        {
            public string FilePath { get; set; } = "";
            public string Kind { get; set; } = "unknown";
            public string Explanation { get; set; } = "";
            public bool ClearableOnline { get; set; }
        }

        public sealed class Report
        {
            public string DriveLetter { get; set; } = "";
            public bool Ok { get; set; }
            public string Error { get; set; } = "";
            public long CurrentSizeBytes { get; set; }
            public long SizeMinBytes { get; set; }
            public long MaxShrinkMb { get; set; }
            public long FreeBytes { get; set; }
            public string FreePercent { get; set; } = "";
            public BlockerInfo? Blocker { get; set; }

            // Estado dos bloqueios conhecidos (só leitura)
            public bool HibernationOn { get; set; }
            public long HiberfilBytes { get; set; }
            public long PagefileBytes { get; set; }
            public List<string> PagingFilesRaw { get; set; } = new();
            public string ShadowStorageMax { get; set; } = "";
            public long ShadowStorageUsedBytes { get; set; }
            public int ShadowCopies { get; set; }
            public long UsnJournalBytes { get; set; }
            public string DiskType { get; set; } = "";
            public string FileSystem { get; set; } = "";
            public string HealthStatus { get; set; } = "";
            public List<string> Warnings { get; set; } = new();
            public string Timestamp { get; set; } = "";

            public string ToText()
            {
                var sb = new StringBuilder();
                sb.AppendLine("================ KitLugia — Diagnóstico de Shrink ================");
                sb.AppendLine($"Gerado em: {Timestamp}");
                sb.AppendLine($"Drive analisado: {DriveLetter}");
                sb.AppendLine();
                if (!Ok)
                {
                    sb.AppendLine($"ERRO: {Error}");
                    return sb.ToString();
                }

                sb.AppendLine("--- PARTIÇÃO ---");
                sb.AppendLine($"Tamanho atual      : {Gb(CurrentSizeBytes)}");
                sb.AppendLine($"Mínimo pós-shrink  : {Gb(SizeMinBytes)}");
                sb.AppendLine($"Máximo encolhível  : {MaxShrinkMb:N0} MB");
                sb.AppendLine($"Espaço livre       : {Gb(FreeBytes)} ({FreePercent})");
                sb.AppendLine($"Sistema de arquivos: {FileSystem}");
                sb.AppendLine($"Tipo de disco      : {DiskType}");
                sb.AppendLine($"Saúde              : {HealthStatus}");
                sb.AppendLine();
                sb.AppendLine("--- BLOQUEADOR PRINCIPAL ---");
                if (Blocker == null || string.IsNullOrEmpty(Blocker.FilePath))
                {
                    sb.AppendLine("Nenhum arquivo bloqueador reportado pelo Windows.");
                    sb.AppendLine("(O Windows só grava o evento 259 quando há um bloqueador real.)");
                }
                else
                {
                    sb.AppendLine($"Arquivo : {Blocker.FilePath}");
                    sb.AppendLine($"Tipo    : {Blocker.Kind}");
                    sb.AppendLine($"Detalhe : {Blocker.Explanation}");
                    sb.AppendLine($"Liberável online: {(Blocker.ClearableOnline ? "SIM" : "NÃO — precisa do modo profundo/offline")}");
                }
                sb.AppendLine();
                sb.AppendLine("--- BLOQUEIOS CONHECIDOS ---");
                sb.AppendLine($"Hibernação          : {(HibernationOn ? "LIGADA" : "desligada")}" +
                              (HiberfilBytes > 0 ? $" (hiberfil.sys = {Gb(HiberfilBytes)})" : ""));
                sb.AppendLine($"Pagefile.sys        : {(PagefileBytes > 0 ? Gb(PagefileBytes) : "ausente")}");
                sb.AppendLine($"PagingFiles (reg)   : {(PagingFilesRaw.Count == 0 ? "(vazio)" : string.Join(" | ", PagingFilesRaw))}");
                sb.AppendLine($"Limite VSS          : {ShadowStorageMax}");
                sb.AppendLine($"Sombras VSS         : {ShadowCopies} ponto(s), {Gb(ShadowStorageUsedBytes)} alocados");
                sb.AppendLine($"Journal USN         : {(UsnJournalBytes > 0 ? Gb(UsnJournalBytes) : "ausente")}");
                sb.AppendLine();
                if (Warnings.Count > 0)
                {
                    sb.AppendLine("--- AVISOS ---");
                    foreach (var w in Warnings) sb.AppendLine($"- {w}");
                    sb.AppendLine();
                }
                sb.AppendLine("============== fim do diagnóstico ==============");
                return sb.ToString();
            }

            private static string Gb(long bytes) => $"{bytes / 1073741824.0:N2} GB ({bytes:N0} bytes)";
        }

        /// <summary>
        /// Executa o diagnóstico completo. SOMENTE LEITURA — nada é alterado.
        /// </summary>
        public static async Task<Report> AnalyzeAsync(char driveLetter, CancellationToken ct = default)
        {
            var r = new Report
            {
                DriveLetter = driveLetter.ToString().ToUpperInvariant(),
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            try
            {
                // ---- 1. Tamanho e limite de shrink (força o Windows a calcular) ----
                string ps = BuildAnalyzePs1(driveLetter);
                var (rc, output) = await RunAsync("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", ct);

                if (rc != 0 && string.IsNullOrWhiteSpace(output))
                {
                    r.Ok = false;
                    r.Error = $"PowerShell falhou (rc={rc}). Certifique-se de estar como administrador.";
                    return r;
                }

                ParseKeyValues(output, r);
                r.Ok = true;

                // ---- 2. Bloqueador via evento 259 (só leitura do Event Log) ----
                r.Blocker = await ReadBlockerAsync(driveLetter, ct);

                // ---- 3. Estado dos bloqueios conhecidos (só leitura) ----
                ReadKnownBlockers(r);
                BuildWarnings(r);
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = ex.Message;
            }

            return r;
        }

        private static string BuildAnalyzePs1(char drive)
        {
            // Somente consulta. Get-PartitionSupportedSize é a mesma chamada que o
            // Gerenciador de Disco do Windows faz — não altera nada.
            string d = drive.ToString().ToUpperInvariant();
            return string.Join("; ", new[]
            {
                "$ErrorActionPreference='SilentlyContinue'",
                $"$p=Get-Partition -DriveLetter '{d}'",
                "if(-not $p){ 'ERROR=particao nao encontrada'; exit 1 }",
                "$s=$p | Get-PartitionSupportedSize",
                "'SIZE=' + $p.Size",
                "'SIZEMIN=' + $s.SizeMin",
                "'SIZEMAX=' + $s.SizeMax",
                "$v=Get-Volume -DriveLetter '" + d + "'",
                "'FREE=' + $v.SizeRemaining",
                "'TOTAL=' + $v.Size",
                "'FS=' + $v.FileSystem",
                "'HEALTH=' + $v.HealthStatus",
                "$disk=Get-Disk -Number $p.DiskNumber",
                "'DISKTYPE=' + $disk.MediaType",
                "'PARTNUMBER=' + $p.PartitionNumber",
                "'DISKNUMBER=' + $p.DiskNumber"
            }) + "; exit 0";
        }

        private static void ParseKeyValues(string output, Report r)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "SIZE": if (long.TryParse(v, out var sz)) r.CurrentSizeBytes = sz; break;
                    case "SIZEMIN": if (long.TryParse(v, out var sm)) r.SizeMinBytes = sm; break;
                    case "FREE": if (long.TryParse(v, out var fr)) r.FreeBytes = fr; break;
                    case "FS": r.FileSystem = v; break;
                    case "HEALTH": r.HealthStatus = v; break;
                    case "DISKTYPE": r.DiskType = v; break;
                }
            }
            r.MaxShrinkMb = Math.Max(0, (r.CurrentSizeBytes - r.SizeMinBytes) / (1024 * 1024));
            r.FreePercent = r.CurrentSizeBytes > 0
                ? $"{(r.FreeBytes * 100.0 / r.CurrentSizeBytes):N1}%"
                : "";
        }

        /// <summary>Lê o evento 259 do Microsoft-Windows-Defrag (caminho do bloqueador).</summary>
        private static async Task<BlockerInfo?> ReadBlockerAsync(char drive, CancellationToken ct)
        {
            // O evento é escrito quando o Windows calcula o limite. Como acabamos de
            // chamar Get-PartitionSupportedSize, o evento é recente. Procuramos os
            // últimos 60 segundos e pegamos o mais novo.
            string ps =
                "$t=(Get-Date).AddSeconds(-60); " +
                "$e=Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='Microsoft-Windows-Defrag';Id=259;StartTime=$t} -MaxEvents 1 -ErrorAction SilentlyContinue; " +
                "if($e){ 'MSGSTART'; $e.Message; 'MSGEND' }";
            var (rc, output) = await RunAsync("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", ct);
            if (rc != 0 || string.IsNullOrEmpty(output)) return null;

            string? msg = ExtractBetween(output, "MSGSTART", "MSGEND");
            if (string.IsNullOrWhiteSpace(msg)) return null;

            string? path = ExtractBlockerPath(msg!);
            if (string.IsNullOrEmpty(path)) path = "?";

            var (kind, explanation, clearable) = ClassifyBlocker(path!);
            return new BlockerInfo
            {
                FilePath = path!,
                Kind = kind,
                Explanation = explanation,
                ClearableOnline = clearable
            };
        }

        /// <summary>
        /// Extrai o caminho do arquivo da mensagem (que é traduzida).
        /// Um arquivo comum vem como "\pagefile.sys::$DATA" — o sufixo é removido.
        /// </summary>
        private static string? ExtractBlockerPath(string message)
        {
            // procura algo que comece com \ e não seja UNC
            var m = System.Text.RegularExpressions.Regex.Match(message, @":\s*(\\(?!\\)[^\r\n]+)");
            if (!m.Success) return null;
            string p = m.Groups[1].Value.Trim();
            // remove o nome do stream padrão
            int idx = p.IndexOf("::$DATA", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) p = p.Substring(0, idx);
            return p;
        }

        private static (string kind, string explanation, bool clearable) ClassifyBlocker(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.Contains("hiberfil"))
                return ("hibernation", "Arquivo de hibernação. `powercfg /h off` remove e restaura.", true);
            if (lower.Contains("pagefile") || lower.Contains("swapfile"))
                return ("paging", "Arquivo de paginação. Removível pelo registro PagingFiles (volta no reboot).", true);
            if (lower.Contains("system volume information"))
                return ("restore", "Pontos de restauração (VSS). `vssadmin delete shadows` remove — IRREVERSÍVEL.", true);
            if (lower.Contains("usnjrnl"))
                return ("journal", "Journal de mudanças NTFS. `fsutil usn deletejournal` remove; o Windows recria.", true);
            if (lower.StartsWith("\\$") || lower.Contains("$mft") || lower.Contains("$bitmap"))
                return ("ntfs", "Estrutura interna do NTFS ($MFT/$BITMAP). NÃO é liberável online — " +
                                "precisa de defrag ou ntfsresize (modo offline).", false);
            return ("file", "Arquivo comum. Pode ser movível com defrag.", false);
        }

        /// <summary>Lê o estado de todos os bloqueios conhecidos. Nenhuma escrita.</summary>
        private static void ReadKnownBlockers(Report r)
        {
            // Hibernação + hiberfil.sys
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power");
                if (key?.GetValue("HibernateEnabled") is int he) r.HibernationOn = he != 0;
            }
            catch { }
            r.HiberfilBytes = SafeFileLen(@"C:\hiberfil.sys");

            // Pagefile
            r.PagefileBytes = SafeFileLen(@"C:\pagefile.sys");
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(PagingKeyPath);
                if (key?.GetValue("PagingFiles") is string[] arr)
                    r.PagingFilesRaw = arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch { }

            // Journal USN — usa fsutil (o caminho $Extend não é acessível por FileInfo)
            r.UsnJournalBytes = ReadUsnJournalSize();
        }

        private static long SafeFileLen(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Tamanho do journal USN via `fsutil usn queryjournal`.
        /// O caminho $Extend\$UsnJrnl:$J não é acessível via FileInfo (é stream de metadado).
        /// SOMENTE LEITURA.
        /// </summary>
        private static long ReadUsnJournalSize()
        {
            try
            {
                var psi = new ProcessStartInfo("fsutil.exe", "usn queryjournal C:")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return 0;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                // Saída pt-BR: "Tamanho Maximo  : 0x0000000002000000"
                // Saída en-US: "Maximum Size   : 0x0000000002000000"
                var m = System.Text.RegularExpressions.Regex.Match(
                    outp,
                    @"(?:Tamanho\s+Max|Maximum\s+Size)\s*:\s*0x([0-9A-Fa-f]+)");
                if (m.Success && long.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.HexNumber, null, out long v))
                    return v;
            }
            catch { }
            return 0;
        }

        private static void BuildWarnings(Report r)
        {
            if (r.MaxShrinkMb < 100)
                r.Warnings.Add($"Só {r.MaxShrinkMb:N0} MB encolhíveis — abaixo do mínimo útil (100 MB).");
            if (r.Blocker is { ClearableOnline: false })
                r.Warnings.Add("O bloqueador é interno ao NTFS: online não resolve. Precisa de defrag ou offline (ntfsresize).");
            if (r.Blocker == null)
                r.Warnings.Add("O Windows não gravou evento de bloqueador nos últimos 60s. " +
                               "Isso pode significar que o limite é apenas fragmentação, ou que o log foi limpo.");
            if (r.HiberfilBytes > 0)
                r.Warnings.Add($"hiberfil.sys ocupa {r.HiberfilBytes / 1073741824.0:N1} GB e trava o fim do volume.");
            if (r.PagefileBytes > 0)
                r.Warnings.Add($"pagefile.sys ocupa {r.PagefileBytes / 1073741824.0:N1} GB (só sai de fato após reboot).");
        }

        private static string? ExtractBetween(string text, string start, string end)
        {
            int a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return null;
            a += start.Length;
            int b = text.IndexOf(end, a, StringComparison.Ordinal);
            if (b < 0) return null;
            return text.Substring(a, b - a).Trim();
        }

        private static async Task<(int rc, string output)> RunAsync(string file, string args, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");

            // Timeout proprio: o chamador pode passar CancellationToken.None.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var tok = timeout.Token;

            Task<string>? errTask = null;
            try
            {
                // Drena os DOIS pipes AO MESMO TEMPO. Ler stdout primeiro e
                // stderr depois travava para sempre quando o PowerShell enchia
                // o buffer de stderr: stdout nunca fechava e o await nao voltava.
                errTask = p.StandardError.ReadToEndAsync(tok);
                string outp = await p.StandardOutput.ReadToEndAsync(tok);
                string err = await errTask;
                await p.WaitForExitAsync(tok);
                return (p.ExitCode, outp + (string.IsNullOrEmpty(err) ? "" : "\n" + err));
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                if (errTask != null)
                    _ = errTask.ContinueWith(t => { _ = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                return (-1, ct.IsCancellationRequested
                    ? "operacao cancelada"
                    : "timeout (120s) no diagnostico");
            }
        }
    }
}
