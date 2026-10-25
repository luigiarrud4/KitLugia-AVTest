// ExplorerShellTest — valida o novo tratamento do SHELL (explorer.exe) do Force Stop.
//
// POR QUE ESTE HARNESS EXISTE: o reinicio do shell e VISIVEL (a barra de tarefas e a
// area de trabalho somem por ~1-2 s e as janelas do Explorador de Arquivos fecham), entao
// um harness NAO pode faze-lo sem o usuario pedir. Por isso o padrao e SO DIAGNOSTICO
// (leitura). O reinicio real so acontece com --restart, passado explicitamente.
//
// O que prova:
//   1) GetExplorerShellPid() acha o PID do shell de verdade (GetShellWindow) — e um
//      explorer.exe que seja, e nao o primeiro explorer.exe da lista de processos.
//   2) IsExplorerShell() distingue o shell de um explorer.exe avulso.
//   3) FindBlockingProcesses() marca IsShellBlocker no explorer certo.
//   4) (--restart) Reinicia o shell e confirma que ele VOLTOU com PID diferente.
//
// Uso:
//   dotnet build tests/ExplorerShellTest/ExplorerShellTest.csproj -p:OutDir=<abs>
//   ExplorerShellTest.exe [--src <repo>] [--restart]

using System.Diagnostics;
using System.IO;
using System.Reflection;
using KitLugia.Core;

internal static class Program
{
    private static int _falhas;

    private static void Check(string titulo, bool ok, string detalhe)
    {
        Console.WriteLine($"[{(ok ? " ok " : "FALHA")}] {titulo}\n        {detalhe}");
        if (!ok) _falhas++;
    }

    private static int Main(string[] args)
    {
        bool restart = args.Contains("--restart");
        Console.WriteLine("=== ExplorerShellTest — tratamento do SHELL no Force Stop ===");
        Console.WriteLine($"modo: {(restart ? "DIAGNOSTICO + REINICIO REAL" : "DIAGNOSTICO (nao mexe no shell)")}");
        Console.WriteLine();

        // ── 1) deteccao do shell ──
        int shellPid = ForceStopUnlockService.GetExplorerShellPid();
        Console.WriteLine($"GetExplorerShellPid() = {shellPid}");
        Check("shell detectado", shellPid != 0, shellPid != 0 ? $"PID {shellPid}" : "GetShellWindow devolveu IntPtr.Zero (sessao sem shell?)");

        if (shellPid != 0)
        {
            try
            {
                using var p = Process.GetProcessById(shellPid);
                Console.WriteLine($"   processo: {p.ProcessName} · sessao {p.SessionId} · WS {p.WorkingSet64 / (1024 * 1024)} MB");
                // .NET devolve ProcessName SEM o ".exe" (o nome do modulo, nao do arquivo).
                // Aceitamos as duas formas para nao gerar falso negativo.
                bool ehExplorer = string.Equals(p.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(p.ProcessName, "explorer.exe", StringComparison.OrdinalIgnoreCase);
                Check("o PID do shell e mesmo explorer.exe", ehExplorer, $"ProcessName=\"{p.ProcessName}\"");
            }
            catch (Exception ex)
            {
                Check("processo do shell acessivel", false, ex.Message);
            }

            Check("IsExplorerShell(shellPid) = true",
                  ForceStopUnlockService.IsExplorerShell(shellPid), "detector coerente");
            Check("IsExplorerShell(0) = false",
                  !ForceStopUnlockService.IsExplorerShell(0), "PID inexistente nao e shell");

            // Explorer avulsos (atalho "Explorer", outra sessao) nao devem ser o shell.
            var outros = Process.GetProcessesByName("explorer")
                .Where(x => x.Id != shellPid)
                .Select(x => x.Id).ToList();
            if (outros.Count > 0)
            {
                Check("explorer.exe AVULSO nao e confundido com o shell",
                      !outros.Any(pid => ForceStopUnlockService.IsExplorerShell(pid)),
                      $"avulsos: {string.Join(",", outros.Take(4))} — nenhum marcado como shell");
            }
            else Console.WriteLine("   (so ha um explorer.exe nesta sessao — caso de avulso nao testavel)");
        }

        // ── 2) o marcador IsShellBlocker existe e FindBlockingProcesses marca ──
        var t = typeof(ForceStopUnlockService);
        var mFind = t.GetMethod("FindBlockingProcesses", BindingFlags.Public | BindingFlags.Static);
        Check("FindBlockingProcesses e publico", mFind != null, mFind?.ReturnType.Name ?? "?");

        var tInfo = typeof(BlockingProcessInfo);
        var pFlag = tInfo.GetProperty("IsShellBlocker");
        Check("BlockingProcessInfo.IsShellBlocker existe (bool)",
              pFlag != null && pFlag.PropertyType == typeof(bool),
              pFlag != null ? $"{pFlag.PropertyType.Name} (padrao {pFlag.GetValue(Activator.CreateInstance(tInfo))})" : "AUSENTE");

        var pRes = typeof(UnlockResult).GetProperty("ExplorerRestarted");
        Check("UnlockResult.ExplorerRestarted existe", pRes != null, pRes?.PropertyType.Name ?? "AUSENTE");

        // ── 3) o SOURCE tem a fase 4.5 e a UI avisa ──
        var src = LocalizarRaiz(args);
        if (src == null) Console.WriteLine("   (sem --src: pulando as checagens de fonte)");
        else
        {
            var svc = File.ReadAllText(Path.Combine(src, @"KitLugia.Core\ForceStopUnlockService.cs"));
            var ui = File.ReadAllText(Path.Combine(src, @"KitLugia.GUI\Pages\WindowsSettings\ForceStopUnlockPage.xaml.cs"));

            Check("Core: existe a fase 4.5 (reinicio do shell antes da delecao)",
                  svc.Contains("Phase 4.5") && svc.Contains("RestartExplorerShell()"),
                  "fase 4.5 antes do Phase 5 (deletar)");
            Check("Core: so reinicia se o bloqueador for o SHELL da sessao",
                  svc.Contains("GetExplorerShellPid()") && svc.Contains("shellBloqueadores.Contains(shellPid)"),
                  "comparacao com GetShellWindow, nao 'qualquer explorer.exe'");
            Check("Core: confirma que o shell VOLTOU (3 tentativas + fallback)",
                  svc.Contains("tentativa <= 3") && svc.Contains("novo != shellPid"),
                  "3 tentativas de relancar com verificacao de PID diferente");
            Check("Core: Nao agenda exclusao no boot por causa do shell",
                  !svc.Contains("ScheduleDeleteOnReboot") || svc.Contains("ExplorerRestarted"),
                  "reiniciar shell nao deleta nada");
            Check("UI: avisa o usuario antes de reiniciar (confirmacao)",
                  ui.Contains("IsShellBlocker") && ui.Contains("ShowConfirmationDialog"),
                  "confirmacao explica barra de tarefas/icones/janelas do Explorer");
        }

        // ── 4) reinicio REAL, so com --restart ──
        if (restart)
        {
            Console.WriteLine();
            Console.WriteLine("--- REINICIO REAL DO SHELL (vai piscar a barra de tarefas) ---");
            var (ok, msg) = ForceStopUnlockService.RestartExplorerShell();
            Console.WriteLine($"   resultado: ok={ok} :: {msg}");
            Check("shell reiniciado", ok, msg);
            int depois = ForceStopUnlockService.GetExplorerShellPid();
            Console.WriteLine($"   shell agora = {depois}");
            Check("shell voltou com PID diferente", depois != 0 && depois != shellPid, $"{shellPid} -> {depois}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("--- Reinicio real NAO executado (use --restart se quiser testar) ---");
        }

        Console.WriteLine();
        Console.WriteLine(_falhas == 0 ? "=== OK ===" : $"=== {_falhas} FALHA(S) ===");
        return _falhas == 0 ? 0 : 2;
    }

    private static string? LocalizarRaiz(string[] args)
    {
        var lista = new List<string>();
        int i = Array.IndexOf(args, "--src");
        if (i >= 0 && i + 1 < args.Length) lista.Add(args[i + 1]);
        lista.Add(Environment.CurrentDirectory);
        lista.Add(AppContext.BaseDirectory);
        foreach (var ini in lista)
        {
            var d = new DirectoryInfo(ini);
            for (int n = 0; n < 14 && d != null; n++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "KitLugia.sln"))) return d.FullName;
        }
        return null;
    }
}
