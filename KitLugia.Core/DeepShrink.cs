using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// DeepShrink — método do hop (github.com/Arjun-Ingole/hop, MIT).
    ///
    /// FILOSOFIA: em vez de "tentar encolher e ver no que dá", o Windows é interrogado
    /// para dizer QUAL arquivo está no caminho (evento 259), e então cada tipo de
    /// bloqueador recebe o tratamento específico:
    ///
    ///   \hiberfil.sys      → powercfg /h off          (reversível)
    ///   \pagefile.sys      → registro PagingFiles     (reversível)
    ///   System Volume Info → vssadmin delete shadows  (IRREVERSÍVEL)
    ///   \$UsnJrnl          → fsutil usn deletejournal (Windows recria)
    ///   \$Mft::$BITMAP     → defrag.exe C: /X + retry (o caso mais duro)
    ///   arquivo comum      → defrag.exe C: /X
    ///
    /// Depois de cada tentativa o limite é RECONSULTADO, porque o próximo bloqueador
    /// pode ser diferente do anterior. Repete até o limite parar de melhorar.
    ///
    /// ⚠️ TUDO aqui é destrutivo-exceto-defrag: os métodos *Prepare* alteram o sistema.
    /// NÃO executar em máquina de produção sem snapshot. Testado pelo usuário em VM.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class DeepShrink
    {
        // Constantes herdadas do hop (nomes e valores idênticos)
        private const int DefragLimitSeconds = 900;   // 15 min total entre todas as tentativas
        private const int DefragTries = 4;
        private const int DefragRetrySeconds = 30;
        private const int DefragCancelled = 1223;     // "operação cancelada pelo usuário"
        private const long WindowsReserve = 5L * 1024 * 1024 * 1024; // 5 GB que o Windows precisa

        public sealed class StepResult
        {
            public string Step { get; set; } = "";
            public bool Ok { get; set; }
            public string Detail { get; set; } = "";
            public long MaxShrinkMbBefore { get; set; }
            public long MaxShrinkMbAfter { get; set; }
            public override string ToString() =>
                $"[{(Ok ? "OK" : "FALHOU")}] {Step}" +
                (MaxShrinkMbAfter != MaxShrinkMbBefore ? $" — {MaxShrinkMbBefore:N0} → {MaxShrinkMbAfter:N0} MB" : "") +
                (string.IsNullOrEmpty(Detail) ? "" : $" — {Detail}");
        }

        public sealed class RunResult
        {
            public char Drive { get; set; }
            public long MaxShrinkMbStart { get; set; }
            public long MaxShrinkMbEnd { get; set; }
            public bool Gained => MaxShrinkMbEnd > MaxShrinkMbStart;
            public List<StepResult> Steps { get; set; } = new();
            public string BlockerAtEnd { get; set; } = "";
            public bool NeedsOffline { get; set; }
            public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            public string ToText()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=========== KitLugia — Deep Shrink (método hop) ===========");
                sb.AppendLine($"Drive: {Drive}:    Início: {Timestamp}");
                sb.AppendLine($"Ganho: {MaxShrinkMbStart:N0} MB  →  {MaxShrinkMbEnd:N0} MB" +
                              $"   ({(MaxShrinkMbEnd - MaxShrinkMbStart):+0;-0;0} MB)");
                sb.AppendLine();
                sb.AppendLine("--- PASSOS ---");
                foreach (var s in Steps) sb.AppendLine(s.ToString());
                sb.AppendLine();
                sb.AppendLine($"Bloqueador final: {(string.IsNullOrEmpty(BlockerAtEnd) ? "(nenhum)" : BlockerAtEnd)}");
                sb.AppendLine($"Precisa de offline (ntfsresize): {(NeedsOffline ? "SIM" : "não")}");
                sb.AppendLine("==========================================================");
                return sb.ToString();
            }
        }

        // =====================================================================
        // Defrag — porta fiel de Start-DefragRun / Invoke-Defrag do hop
        // =====================================================================

        /// <summary>
        /// Roda `defrag.exe C: /X` com limite de tempo e código de saída.
        /// Retorna null se estourou o tempo (o que moveu antes ainda conta).
        /// </summary>
        public static async Task<int?> StartDefragRunAsync(string drive, int seconds, CancellationToken ct)
        {
            var psi = new ProcessStartInfo("defrag.exe", $"{drive} /X")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;

            // drena a saída para o defrag não travar em buffer cheio
            var drain = Task.Run(async () =>
            {
                try
                {
                    while (!p.StandardOutput.EndOfStream)
                        await p.StandardOutput.ReadLineAsync(ct);
                }
                catch { }
            }, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                await p.WaitForExitAsync(cts.Token);
                return p.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                return null;   // estourou o tempo
            }
        }

        /// <summary>
        /// Invoke-Defrag do hop: até 4 tentativas, todas dentro do mesmo prazo de 900 s.
        /// O código 1223 aparece logo após o login e significa "nada foi movido" — vale retentar.
        /// Depois do defrag o Windows leva um instante para recalcular o limite, então
        /// consultamos o limite com retry (Wait-ShrinkLimit).
        /// </summary>
        public static async Task<StepResult> InvokeDefragAsync(char drive, long before,
            IProgress<string>? log = null, CancellationToken ct = default)
        {
            var step = new StepResult { Step = $"Defrag em {drive}:", MaxShrinkMbBefore = before };
            var deadline = DateTime.UtcNow.AddSeconds(DefragLimitSeconds);
            int cancelled = 0;
            int? code = null;

            for (int attempt = 1; attempt <= DefragTries; attempt++)
            {
                int left = (int)Math.Floor((deadline - DateTime.UtcNow).TotalSeconds);
                if (left <= 0) break;
                log?.Report($"Defrag tentativa {attempt}/{DefragTries} (limite {left}s)...");
                code = await StartDefragRunAsync(drive.ToString(), left, ct);
                log?.Report(code == null
                    ? "Defrag parou pelo limite de tempo (o que moveu já conta)."
                    : $"Defrag terminou com código {code}.");
                if (code != DefragCancelled) break;
                cancelled++;
                if (attempt < DefragTries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(DefragRetrySeconds, Math.Max(1, left))), ct);
            }

            if (cancelled == DefragTries)
            {
                step.Ok = false;
                step.Detail = $"O Windows cancelou as {DefragTries} tentativas — nada foi movido.";
            }
            else
            {
                step.Ok = code == 0;
                if (code != 0 && code != null) step.Detail = $"código {code}";
            }

            // O Windows não consegue calcular o limite imediatamente após o defrag.
            step.MaxShrinkMbAfter = await WaitShrinkLimitAsync(drive, deadline, ct);
            return step;
        }

        /// <summary>
        /// Wait-ShrinkLimit do hop: até 6 tentativas, 5 s entre elas, dentro do prazo.
        /// Devolve o novo máximo encolhível em MB.
        /// </summary>
        public static async Task<long> WaitShrinkLimitAsync(char drive, DateTime deadline, CancellationToken ct)
        {
            long last = 0;
            for (int i = 0; i < 6; i++)
            {
                var (ok, mb) = await TryGetMaxShrinkMbAsync(drive, ct);
                if (ok) { last = mb; break; }
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            return last;
        }

        /// <summary>Consulta Get-PartitionSupportedSize. Não altera nada.</summary>
        public static async Task<(bool ok, long maxMb)> TryGetMaxShrinkMbAsync(char drive, CancellationToken ct = default)
        {
            string ps = "$ErrorActionPreference='Stop'; " +
                        $"$p=Get-Partition -DriveLetter '{char.ToUpperInvariant(drive)}'; " +
                        "$s=$p|Get-PartitionSupportedSize; " +
                        "'M=' + [math]::Floor(($p.Size - $s.SizeMin)/1MB)";
            var (rc, output) = await RunAsync("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + ps.Replace("\"", "\\\"") + "\"", ct, 120);
            if (rc != 0) return (false, 0);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("M=") && long.TryParse(t.Substring(2), out long v)) return (true, Math.Max(0, v));
            }
            return (false, 0);
        }

        // =====================================================================
        // Orquestração — a cascata completa
        // =====================================================================

        /// <summary>
        /// Executa a cascata do hop até o limite parar de melhorar.
        ///
        /// ⚠️ ALTERA O SISTEMA: desliga hibernação, remove pagefile da config,
        /// pode apagar pontos de restauração e o journal USN. Use em VM com snapshot.
        /// </summary>
        public static async Task<RunResult> RunAsync(char drive, bool allowDestructive,
            IProgress<string>? log = null, CancellationToken ct = default)
        {
            var res = new RunResult { Drive = char.ToUpperInvariant(drive) };

            var (ok0, startMb) = await TryGetMaxShrinkMbAsync(res.Drive, ct);
            res.MaxShrinkMbStart = startMb;
            long current = startMb;
            log?.Report($"Limite inicial: {current:N0} MB encolhíveis.");

            for (int round = 1; round <= 4; round++)
            {
                // ---- descobre o bloqueador ATUAL ----
                var diag = await ShrinkDiagnostics.AnalyzeAsync(res.Drive, ct);
                string blocker = diag.Blocker?.FilePath ?? "";
                string kind = diag.Blocker?.Kind ?? "none";
                log?.Report($"Rodada {round}: bloqueador = {(string.IsNullOrEmpty(blocker) ? "(nenhum)" : blocker)} [{kind}]");

                if (kind == "none")
                {
                    res.BlockerAtEnd = "";
                    break;
                }

                // ---- trata cada tipo ----
                var step = new StepResult { Step = $"Tratar {blocker} ({kind})", MaxShrinkMbBefore = current };
                bool acted = false;

                switch (kind)
                {
                    case "hibernation":
                        if (allowDestructive)
                        {
                            var (rc, _) = await RunAsync("powercfg.exe", "/h off", ct, 60);
                            step.Ok = rc == 0; step.Detail = rc == 0 ? "hibernação desligada" : $"powercfg rc={rc}";
                        }
                        else step.Detail = "ignorado (allowDestructive=false)";
                        acted = true;
                        break;

                    case "paging":
                        // removido pelo OnlineShrinkManager (registro PagingFiles)
                        step.Detail = "pagefile: tratado pelo fluxo deep-shrink (reboot necessário)";
                        step.Ok = true;
                        acted = false;   // não melhora sem reboot
                        break;

                    case "restore":
                        if (allowDestructive)
                        {
                            var (rc, _) = await RunAsync("vssadmin.exe",
                                $"delete shadows /for={res.Drive}: /all /quiet", ct, 120);
                            step.Ok = rc == 0;
                            step.Detail = rc == 0 ? "pontos de restauração apagados (IRREVERSÍVEL)" : $"vssadmin rc={rc}";
                        }
                        else step.Detail = "ignorado (allowDestructive=false)";
                        acted = true;
                        break;

                    case "journal":
                        if (allowDestructive)
                        {
                            var (rc, _) = await RunAsync("fsutil.exe",
                                $"usn deletejournal /d {res.Drive}:", ct, 120);
                            step.Ok = rc == 0;
                            step.Detail = rc == 0 ? "journal removido (Windows recria)" : $"fsutil rc={rc}";
                        }
                        else step.Detail = "ignorado (allowDestructive=false)";
                        acted = true;
                        break;

                    case "ntfs":
                    case "file":
                        // o caso duro: só defrag move (o hop usa C# nativo para $MFT::$BITMAP)
                        var dstep = await InvokeDefragAsync(res.Drive, current, log, ct);
                        step.Ok = dstep.Ok;
                        step.Detail = dstep.Detail + " (defrag)";
                        step.MaxShrinkMbAfter = dstep.MaxShrinkMbAfter;
                        current = Math.Max(current, dstep.MaxShrinkMbAfter);
                        acted = false;   // já mediu
                        break;
                }

                if (acted)
                {
                    // reconsulta depois de mexer
                    var (ok, mb) = await TryGetMaxShrinkMbAsync(res.Drive, ct);
                    if (ok)
                    {
                        step.MaxShrinkMbAfter = mb;
                        current = Math.Max(current, mb);
                    }
                    else step.MaxShrinkMbAfter = current;
                }
                else if (step.MaxShrinkMbAfter == 0) step.MaxShrinkMbAfter = current;

                res.Steps.Add(step);
                log?.Report(step.ToString());

                // se nada melhorou e não é defrag, não adianta repetir o mesmo tratamento
                if (step.MaxShrinkMbAfter <= step.MaxShrinkMbBefore && kind != "ntfs")
                {
                    res.BlockerAtEnd = blocker;
                    if (kind == "ntfs") res.NeedsOffline = true;
                    break;
                }
            }

            res.MaxShrinkMbEnd = current;
            if (res.BlockerAtEnd == "") res.BlockerAtEnd = (await ShrinkDiagnostics.AnalyzeAsync(res.Drive, ct)).Blocker?.FilePath ?? "";
            res.NeedsOffline = res.BlockerAtEnd.Contains("$Mft") || res.BlockerAtEnd.Contains("$Bitmap");
            return res;
        }

        private static async Task<(int rc, string output)> RunAsync(string file, string args,
            CancellationToken ct, int timeoutSeconds)
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                string outp = await p.StandardOutput.ReadToEndAsync(cts.Token);
                string err = await p.StandardError.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
                return (p.ExitCode, outp + (string.IsNullOrWhiteSpace(err) ? "" : "\n" + err));
            }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                return (-2, "(tempo esgotado)");
            }
        }
    }
}
