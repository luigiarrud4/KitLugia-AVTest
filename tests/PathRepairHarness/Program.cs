// Harness do reparo de PATH (read-only: NAO grava no registro).
//
// Prova os dois defeitos que o log real do usuario mostrou em 24/10/2026:
//   1) "[TOGGLE] 'PATH com Caminhos Inexistentes' continua MODIFIED apos a escrita"
//      — a entrada MORTA em %ProgramFiles% era classificada como "caminho de
//      usuario no System PATH" (WrongLocation) ANTES de checar se a pasta existe,
//      entao o reparo a MANTINHA. O scan seguinte acusava MissingDirectory de novo
//      e o item nunca ficava OK.
//   2) O log do reparo tinha 1000+ caracteres de "Mantido (caminho de usuario no
//      System): <cada app instalado>" — Docker/Git/nodejs/NVIDIA eram acusados de
//      estar no PATH do usuario so por nao constarem de um allowlist.
//
// Tudo aqui trabalha com strings + leitura do registro; nada e' escrito.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitLugia.Core;

internal static class Program
{
    private static int _ok, _fail;

    private static void Check(string nome, bool cond, string detalhe)
    {
        if (cond) { _ok++; Console.WriteLine($"  OK    {nome}  ({detalhe})"); }
        else { _fail++; Console.WriteLine($"  FALHA {nome}  ({detalhe})"); }
    }

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Reparo de PATH (System) ===");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // ─────────────────────────────────────────────────────────────
        // 1) CASO SINTETICO: entrada morta em Program Files
        //    (o caso real era C:\Program Files (x86)\Common Files\Oracle\Java\javapath)
        // ─────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 1) Entrada morta em Program Files (antes: 'WrongLocation' e mantida) ==");
        string morta = Path.Combine(programFiles, "KitLugiaPathHarness_nao_existe", "bin");
        Check("a pasta de teste realmente NAO existe", !Directory.Exists(morta), morta);

        string sintetico = string.Join(";", new[]
        {
            $@"{systemRoot}\System32",
            morta,                                   // morta em Program Files
            "%ProgramFiles%",                        // existe, sem variavel -> nao e "de usuario"
            Path.Combine(userProfile, ".dotnet", "tools"), // perfil do usuario -> local errado
            @"%SystemRoot%\System32\Wbem"
        });

        var entradas = PathRepair.DiagnosePath(sintetico, "System");
        foreach (var e in entradas)
            Console.WriteLine($"    {e.Problem,-14} {e.CleanValue}");

        var entradaMorta = entradas.FirstOrDefault(e => e.CleanValue.Equals(morta, StringComparison.OrdinalIgnoreCase));
        Check("a entrada morta foi encontrada", entradaMorta != null, morta);
        Check("a entrada morta e' MISSING (nao WrongLocation)",
            entradaMorta != null && entradaMorta.Problem == PathEntryProblem.Missing,
            entradaMorta?.Problem.ToString() ?? "ausente");

        var entradaPF = entradas.FirstOrDefault(e => e.CleanValue.Equals("%ProgramFiles%", StringComparison.OrdinalIgnoreCase));
        Check("%ProgramFiles% (existe) NAO e' 'caminho de usuario no System'",
            entradaPF != null && entradaPF.Problem == PathEntryProblem.None,
            entradaPF?.Problem.ToString() ?? "ausente");

        var entradaPerfil = entradas.FirstOrDefault(e =>
            e.CleanValue.Contains(".dotnet", StringComparison.OrdinalIgnoreCase));
        Check("caminho sob o perfil do usuario E' WrongLocation",
            entradaPerfil != null && entradaPerfil.Problem == PathEntryProblem.WrongLocation,
            entradaPerfil?.Problem.ToString() ?? "ausente");

        // ─────────────────────────────────────────────────────────────
        // 2) REPARO: remove a morta, mantem a do perfil, sem "Mantido" no log
        // ─────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 2) RepairPathEntries ==");
        var (reparado, acoes) = PathRepair.RepairPathEntries(entradas, "System");
        Console.WriteLine($"    reparado: {reparado}");
        foreach (var a in acoes) Console.WriteLine($"    acao: {a}");

        Check("a entrada morta foi REMOVIDA do PATH",
            !reparado.Split(';').Any(p => p.Trim().Equals(morta, StringComparison.OrdinalIgnoreCase)), reparado);
        Check("a acao da remocao esta no log",
            acoes.Any(a => a.StartsWith("Removida", StringComparison.OrdinalIgnoreCase) && a.Contains("nao_existe")),
            acoes.FirstOrDefault(a => a.Contains("nao_existe")) ?? "nenhuma");
        Check("nenhuma acao 'Mantido' no log (era o que inflava a linha)",
            !acoes.Any(a => a.StartsWith("Mantido", StringComparison.OrdinalIgnoreCase)),
            $"{acoes.Count} acao(oes)");
        Check("o caminho do perfil foi PRESERVADO (nao removemos o que pode ser intencional)",
            reparado.Contains(".dotnet", StringComparison.OrdinalIgnoreCase), reparado.Length + " chars");
        Check("a variavel %ProgramFiles% ficou LITERAL (REG_EXPAND_SZ preservado)",
            reparado.Contains("%ProgramFiles%", StringComparison.OrdinalIgnoreCase), reparado);

        // Idempotencia: reparar o reparado nao muda nada e nao deixa pendencia
        var (reparado2, acoes2) = PathRepair.RepairPathEntries(PathRepair.DiagnosePath(reparado, "System"), "System");
        Check("2a passada e' idempotente", reparado2.Equals(reparado, StringComparison.OrdinalIgnoreCase), acoes2.Count + " acao(oes)");
        Check("2a passada nao tem entrada morta",
            !PathRepair.DiagnosePath(reparado, "System").Any(e => e.Problem == PathEntryProblem.Missing),
            string.Join(", ", PathRepair.DiagnosePath(reparado, "System")
                .Where(e => e.Problem == PathEntryProblem.Missing).Select(e => e.CleanValue)));

        // ─────────────────────────────────────────────────────────────
        // 3) PATH REAL do sistema (somente leitura)
        //    O criterio do usuario: depois do reparo EM MEMORIA nao pode sobrar
        //    pasta inexistente (senao o toggle volta a acusar MODIFIED).
        // ─────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 3) PATH System REAL (leitura apenas) ==");
        string sys = PathRepair.GetSystemPathValue();
        Console.WriteLine($"    valor lido: {sys.Length} chars | contem '%': {sys.Contains('%')}");
        Check("o PATH lido NAO veio expandido (variaveis preserved)",
            sys.Contains('%'), sys.Contains("%SystemRoot%") ? "%SystemRoot% presente" : "sem variaveis!");

        var reais = PathRepair.DiagnosePath(sys, "System");
        var mortas = reais.Where(e => e.Problem == PathEntryProblem.Missing || e.Problem == PathEntryProblem.Orphan).ToList();
        Console.WriteLine($"    entradas: {reais.Count} | mortas: {mortas.Count} | WrongLocation: {reais.Count(e => e.Problem == PathEntryProblem.WrongLocation)}");
        foreach (var e in mortas) Console.WriteLine($"      MORTA: {e.CleanValue}");

        var (sysReparado, sysAcoes) = PathRepair.RepairPathEntries(reais, "System");
        var depois = PathRepair.DiagnosePath(sysReparado, "System");
        Check("depois do reparo (em memoria) nao sobra pasta inexistente",
            !depois.Any(e => e.Problem == PathEntryProblem.Missing),
            string.Join(", ", depois.Where(e => e.Problem == PathEntryProblem.Missing).Select(e => e.CleanValue)));
        Check("as entradas essenciais do Windows continuam no PATH",
            depois.Any(e => e.CleanValue.Contains(@"\system32", StringComparison.OrdinalIgnoreCase)) &&
            depois.Any(e => e.CleanValue.Contains(@"\Wbem", StringComparison.OrdinalIgnoreCase)),
            sysReparado.Length + " chars");
        Check("o reparo nao inventou duplicatas",
            depois.Count == depois.Select(e => e.CleanValue).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            depois.Count + " entradas");
        Console.WriteLine($"    acoes (log curto): {(sysAcoes.Count == 0 ? "(nenhuma)" : string.Join("; ", sysAcoes))}");
        Check("o log do reparo real fica curto (< 400 chars)",
            string.Join("; ", sysAcoes).Length < 400,
            string.Join("; ", sysAcoes).Length + " chars (antes: 1000+)");

        // ─────────────────────────────────────────────────────────────
        // 4) Veredito do scanner para os 3 toggles de PATH
        //    (era o "continua MODIFIED apos a escrita")
        // ─────────────────────────────────────────────────────────────
        Console.WriteLine("\n== 4) Os 3 toggles depois do reparo em memoria ==");
        Check("nenhum problema descrito apos o reparo",
            string.IsNullOrEmpty(PathRepair.DescribeProblems(sysReparado, "System")),
            PathRepair.DescribeProblems(sysReparado, "System"));

        Console.WriteLine("\n===============================================");
        Console.WriteLine($"  OK={_ok}  FALHAS={_fail}");
        Console.WriteLine("===============================================");
        return _fail == 0 ? 0 : 1;
    }
}
