// Harness do "Limpar RAM" por processo do KitTaskManager.
// Prova (1) que o trim REALMENTE esvazia o working set de um processo vivo e
// (2) que os casos de falha retornam motivo em vez de "sucesso" mentiroso.
//
//uso:  TrimProcessHarness            (modo pai: cria o filho e valida)
//      TrimProcessHarness --child    (modo filho: aloca RAM e segura)

using System.Diagnostics;
using KitLugia.Core;

internal static class Program
{
    private static int _fail;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--child")
            return Child();

        Console.WriteLine("== Harness: trim de working set por processo ==");

        // ───────────────────────────────────────────────────────────────
        // 1) Processo real com RAM alocada: o trim tem que reduzir o WS
        // ───────────────────────────────────────────────────────────────
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            UseShellExecute = false
        };
        if (psi.FileName.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            psi.ArgumentList.Add("--child");
        }
        else
        {
            psi.ArgumentList.Add("--child");
        }
        psi.RedirectStandardOutput = true;

        using var child = Process.Start(psi) ?? throw new InvalidOperationException("nao iniciou o filho");
        Console.WriteLine($"  filho PID {child.Id}");

        // Espera o filho alocar a memoria (ele avisa por stdout).
        string? line = child.StandardOutput.ReadLine();
        Console.WriteLine($"  filho: {line}");

        double before = ReadWsMB(child.Id);
        Console.WriteLine($"  working set antes: {before:0.0} MB");
        Check(before > 50, $"filho alocou memoria suficiente ({before:0.0} MB)");

        var sw = Stopwatch.StartNew();
        var res = MemoryOptimizer.TrimProcessWorkingSet(child.Id);
        sw.Stop();

        double after = ReadWsMB(child.Id);
        Console.WriteLine($"  resultado: success={res.Success} msg='{res.Message}' " +
                          $"antes={res.WorkingSetBeforeMB:0.0} MB depois={res.WorkingSetAfterMB:0.0} MB " +
                          $"liberados={res.FreedMB:0.0} MB em {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  working set depois: {after:0.0} MB");

        Check(res.Success, "trim do processo vivo retornou sucesso");
        Check(res.FreedMB > 10, $"working set caiu de verdade (liberados {res.FreedMB:0.0} MB)");
        Check(after < before * 0.75, $"working set medido caiu ({before:0.0} -> {after:0.0} MB)");
        Check(!child.HasExited, "o processo continua VIVO depois do trim");

        // ───────────────────────────────────────────────────────────────
        // 2) PID inexistente: precisa FALHAR com motivo (nao "sucesso")
        // ───────────────────────────────────────────────────────────────
        var bad = MemoryOptimizer.TrimProcessWorkingSet(999999);
        Console.WriteLine($"  PID inexistente: success={bad.Success} msg='{bad.Message}'");
        Check(!bad.Success, "PID inexistente retorna falha");
        Check(!string.IsNullOrWhiteSpace(bad.Message), "PID inexistente explica o motivo");

        // 3) PID invalido
        var zero = MemoryOptimizer.TrimProcessWorkingSet(0);
        Check(!zero.Success, "PID 0 retorna falha");

        // 4) Processo protegido do sistema: nao pode estourar exception e
        //    tem que dizer a verdade (sucesso OU o motivo real).
        var sys = MemoryOptimizer.TrimProcessWorkingSet(4);
        Console.WriteLine($"  PID 4 (System): success={sys.Success} msg='{sys.Message}'");
        Check(sys.Success || sys.Message.Contains("permissao") || sys.Message.Contains("protegido")
              || sys.Message.Contains("encerrou"),
              "PID 4 (System) reporta o resultado real");

        // 4b) A mensagem tem que ser coerente com o nivel de elevacao: o KitLugia
        //     SEMPRE roda como admin, entao "rode como administrador" so faz
        //     sentido quando NAO estamos elevados. Os DOIS ramos sao testados
        //     direto (a funcao e pura), sem depender do terminal.
        bool elevado = MemoryOptimizer.IsElevated();
        Console.WriteLine($"  elevado: {elevado}");
        string msgSemAdmin = MemoryOptimizer.BuildTrimAccessDeniedMessage(4, false);
        string msgComAdmin = MemoryOptimizer.BuildTrimAccessDeniedMessage(4, true);
        string msgComAdminOutro = MemoryOptimizer.BuildTrimAccessDeniedMessage(1234, true);
        Console.WriteLine($"  sem elevacao: '{msgSemAdmin}'");
        Console.WriteLine($"  elevado PID 4: '{msgComAdmin}'");
        Check(msgSemAdmin.Contains("administrador"),
              "sem elevacao: a mensagem manda rodar como administrador");
        Check(!msgComAdmin.Contains("administrador"),
              "elevado: a mensagem nao manda 'rode como administrador' (o kit ja e admin)");
        Check(msgComAdmin.Contains("protegido"),
              "elevado PID 4: avisa que o System e protegido pelo Windows");
        Check(msgComAdminOutro.Contains("acesso negado") && !msgComAdminOutro.Contains("administrador"),
              "elevado: processo comum tambem nao recebe a dica de 'administrador'");
        if (elevado)
            Check(sys.Message.Contains("acesso negado"),
                  "a mensagem real do PID 4 (elevado) e a de acesso negado");

        // 5) O metodo antigo nao pode mais "mentir": ele delega para o novo
        //    e nao lanca excecao.
        try
        {
            MemoryOptimizer.EmptyProcessWorkingSet(child.Id);
            Check(true, "EmptyProcessWorkingSet legado nao lanca excecao");
        }
        catch (Exception ex)
        {
            Check(false, "EmptyProcessWorkingSet legado nao lanca excecao (" + ex.Message + ")");
        }

        try { child.Kill(true); } catch { }

        Console.WriteLine(_fail == 0 ? "== ALL-PASS ==" : $"== {_fail} FALHA(S) ==");
        return _fail == 0 ? 0 : 1;
    }

    private static double ReadWsMB(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Refresh();
            return p.WorkingSet64 / (1024.0 * 1024.0);
        }
        catch { return -1; }
    }

    /// <summary>Aloca ~300 MB tocados e segura (working set alto) ate ser morto.</summary>
    private static int Child()
    {
        var blocks = new List<byte[]>();
        for (int i = 0; i < 300; i++)
        {
            var b = new byte[1024 * 1024];
            b.AsSpan().Fill((byte)(i & 0xFF));   // TOCA a memoria (evita lazy commit)
            blocks.Add(b);
        }
        Console.WriteLine($"ALOCADO {blocks.Count} MB");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromMinutes(5));
        return 0;
    }
}
