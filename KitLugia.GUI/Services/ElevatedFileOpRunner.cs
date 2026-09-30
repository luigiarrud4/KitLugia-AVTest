using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KitLugia.Core;

namespace KitLugia.GUI.Services;

/// <summary>
/// Executa uma operação de arquivo (Force Stop / Take Ownership / Delete) "sempre",
/// pedindo elevação UMA vez e SEM tirar o usuário da tela.
///
/// Antes: a página detectava que o Kit não estava elevado, perguntava "relançar elevado?"
/// e **parava** — a instância elevada abria outra janela (ou ia para o worker headless) e a
/// página ficava parada, sem resultado. Se o usuário respondesse "não", a operação rodava
/// sem privilégio e falhava em item protegido.
///
/// Agora: se o processo já é admin, roda in-process (progresso ao vivo). Se não é, dispara
/// este exe em modo <c>--worker</c> elevado (um único UAC), acompanha o progresso por
/// arquivo e devolve o resultado para a MESMA página. Se o UAC for cancelado, a operação
/// continua in-process no melhor esforço e o resultado diz o que exigia administrador —
/// nunca um beco sem saída.
/// </summary>
public static class ElevatedFileOpRunner
{
    /// <summary>
    /// Elevação UAC real (TokenElevation). Não usar `IsInRole(Administrator)`: ele pode marcar
    /// "true" num token filtrado (UAC ligado) — aí o app não elevaria o worker e o item
    /// protegido falharia, que é exatamente o "não funciona sempre" que essa mudança resolve.
    /// </summary>
    public static bool IsAdmin => FileOpGuarantee.IsElevated();

    /// <summary>Teto de segurança para o worker (operações em árvores grandes).</summary>
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Ponto de entrada usado pela UI: garante que a operação aconteça — elevada quando
    /// possível, in-process caso contrário. Nunca devolve null.
    /// </summary>
    public static async Task<(GuaranteeResult Result, bool RanElevated)> RunGuaranteedAsync(
        string path, GuaranteedAction action, bool recursive, bool grantFullControl,
        Action<GuaranteeStep>? onStep = null, CancellationToken ct = default)
    {
        if (IsAdmin)
            return (await RunInProcessAsync(path, action, recursive, grantFullControl, onStep, ct), false);

        var elevated = await RunElevatedAsync(path, action, recursive, grantFullControl, onStep, ct)
            .ConfigureAwait(false);
        if (elevated != null)
            return (elevated, true);

        // UAC cancelado/indisponível: faz o possível aqui e informa o que exigia admin.
        var local = await RunInProcessAsync(path, action, recursive, grantFullControl, onStep, ct);
        if (!local.Ok)
            local.NeedsAdmin = true;
        return (local, false);
    }

    /// <summary>Roda o pipeline garantido neste processo (já elevado ou em melhor esforço).</summary>
    public static Task<GuaranteeResult> RunInProcessAsync(string path, GuaranteedAction action,
        bool recursive, bool grantFullControl, Action<GuaranteeStep>? onStep = null,
        CancellationToken ct = default)
        => Task.Run(() => FileOpGuarantee.Run(path, action, recursive, grantFullControl, onStep, ct), ct);

    /// <summary>
    /// Dispara o worker elevado. Devolve null quando a elevação não foi possível
    /// (UAC negado, política, exe indisponível) — o chamador decide o fallback.
    /// </summary>
    public static async Task<GuaranteeResult?> RunElevatedAsync(string path, GuaranteedAction action,
        bool recursive, bool grantFullControl, Action<GuaranteeStep>? onStep = null,
        CancellationToken ct = default)
    {
        string? host = Environment.ProcessPath;
        string dll = typeof(ElevatedFileOpRunner).Assembly.Location;

        // Executar em DEBUG via `dotnet run`: ProcessPath é o dotnet.exe — é preciso passar o
        // .dll do app como primeiro argumento (senão o runas abriria um dotnet.exe sem app).
        bool viaDotnet = host != null &&
            Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        string? exe = viaDotnet ? host : (host ?? dll);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = dll;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Logger.Log("[ELEV OP] Executável do Kit não encontrado — não é possível elevar.");
            return null;
        }

        string stamp = Guid.NewGuid().ToString("N")[..10];
        string resultFile = Path.Combine(Path.GetTempPath(), $"kl_fileop_{stamp}.result");
        string progressFile = Path.Combine(Path.GetTempPath(), $"kl_fileop_{stamp}.progress");

        string actionArg = action switch
        {
            GuaranteedAction.TakeOwnership => "takeown",
            GuaranteedAction.Delete => "delete",
            _ => "force-stop",
        };

        string arguments =
            (viaDotnet ? Quote(dll) + " " : "") +
            $"--worker --action {actionArg} --path {Quote(path)} " +
            $"--result {Quote(resultFile)} --progress {Quote(progressFile)}" +
            (recursive ? " --recursive" : "") +
            (grantFullControl ? " --full-control" : "");

        Process? proc;
        try
        {
            Logger.Log($"[ELEV OP] Elevando worker: {actionArg} em {path}");
            proc = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,   // exigido pelo Verb=runas
                Verb = "runas",
                Arguments = arguments,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Logger.Log("[ELEV OP] UAC cancelado pelo usuário.");
            TryDelete(progressFile); TryDelete(resultFile);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[ELEV OP] Falha ao elevar: {ex.Message}");
            TryDelete(progressFile); TryDelete(resultFile);
            return null;
        }

        if (proc == null)
        {
            TryDelete(progressFile); TryDelete(resultFile);
            return null;
        }

        try
        {
            int reported = 0;
            var started = DateTime.UtcNow;
            while (!proc.HasExited)
            {
                reported = await DrainProgressAsync(progressFile, reported, onStep).ConfigureAwait(false);
                if (DateTime.UtcNow - started > WorkerTimeout)
                {
                    Logger.Log("[ELEV OP] Timeout do worker — encerrando.");
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    break;
                }
                try { await Task.Delay(150, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            await DrainProgressAsync(progressFile, reported, onStep).ConfigureAwait(false);
            try { proc.WaitForExit(5000); } catch { }

            var parsed = ParseResult(resultFile);
            if (parsed == null)
            {
                Logger.Log("[ELEV OP] Worker terminou sem arquivo de resultado.");
                parsed = new GuaranteeResult { Summary = "A operação elevada terminou sem resultado detalhado." };
            }
            return parsed;
        }
        finally
        {
            try { proc.Dispose(); } catch { }
            TryDelete(progressFile);
            TryDelete(resultFile);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static async Task<int> DrainProgressAsync(string file, int alreadyReported, Action<GuaranteeStep>? onStep)
    {
        try
        {
            if (!File.Exists(file)) return alreadyReported;
            string text;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                text = await sr.ReadToEndAsync().ConfigureAwait(false);

            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = alreadyReported; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                onStep?.Invoke(new GuaranteeStep { Index = i + 1, Message = line, Ok = true });
            }
            return lines.Length;
        }
        catch { return alreadyReported; }
    }

    private static GuaranteeResult? ParseResult(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var lines = new List<string>();
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
                while (!sr.EndOfStream)
                {
                    string? l = sr.ReadLine();
                    if (!string.IsNullOrEmpty(l)) lines.Add(l);
                }
            if (lines.Count == 0) return null;
            return GuaranteeResult.FromWire(lines);
        }
        catch { return null; }
    }

    /// <summary>Escapa um argumento: aspas + barra final duplicada (regra do CommandLineToArgvW).</summary>
    private static string Quote(string value)
    {
        string v = value ?? "";
        bool endsWithSlash = v.EndsWith("\\", StringComparison.Ordinal) || v.EndsWith("/", StringComparison.Ordinal);
        return "\"" + v + (endsWithSlash ? "\\" : "") + "\"";
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}
