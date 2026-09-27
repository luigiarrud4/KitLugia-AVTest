using System.Text;
using KitLugia.Core.Diagnostics;

// Harness de execução real da Central de Diagnóstico.
// Não é teste de unidade: é o "rodar de verdade" para descobrir coletor quebrado em
// runtime (WMI, event log, registro) — o compilador não pega isso.
//   dotnet run --project tests/DiagnosticCenterRun

Console.OutputEncoding = Encoding.UTF8;

int failures = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "OK  " : "FALHA")} {what}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures++;
}

Console.WriteLine("=== KitLugia — execução real da Central de Diagnóstico ===");
Console.WriteLine();

var steps = new List<string>();
var progress = new Progress<string>(s => steps.Add(s));

var sw = System.Diagnostics.Stopwatch.StartNew();
DiagnosticReport report;
try
{
    report = await DiagnosticCenter.RunAsync(progress);
}
catch (Exception ex)
{
    Console.WriteLine($"FALHA FATAL: {ex}");
    return 1;
}
sw.Stop();

Console.WriteLine($"Coleta em {sw.Elapsed.TotalSeconds:F1}s   elevado: {report.Elevated}");
Console.WriteLine($"Passos reportados ({steps.Count}): {string.Join(" · ", steps)}");
Console.WriteLine();
Console.WriteLine(report.BuildText());
Console.WriteLine();

Console.WriteLine("=== VERIFICAÇÕES ===");
Check("relatório devolvido", report != null);
Check("tem carimbo de tempo", report.GeneratedAt != default);
Check("duração medida > 0", report.Duration > TimeSpan.Zero, $"{report.Duration.TotalMilliseconds:F0} ms");
Check("evidências coletadas (>= 3 blocos)", report.Evidence.Count >= 3, $"{report.Evidence.Count} blocos");
Check("todos os achados têm título", report.Findings.All(f => f.Title.Trim().Length > 0));
Check("todos os achados têm categoria", report.Findings.All(f => f.Category.Trim().Length > 0));
Check("achados ordenados por gravidade",
    report.Sorted().Select(f => (int)f.Severity).SequenceEqual(report.Sorted().Select(f => (int)f.Severity).OrderBy(x => x)));

var evTitles = report.Evidence.Select(e => e.Title).ToList();
Check("evidência de ambiente presente", evTitles.Any(t => t.Contains("Ambiente")), string.Join(" | ", evTitles));
Check("evidência de volumes presente", evTitles.Any(t => t.Contains("Volumes")));
Check("evidência de eventos presente", evTitles.Any(t => t.Contains("Eventos")));
Check("evidência de áudio presente", evTitles.Any(t => t.Contains("Áudio")));
Check("evidência do diário do Kit presente", evTitles.Any(t => t.Contains("diário", StringComparison.OrdinalIgnoreCase)));
Check("evidência de latência presente", evTitles.Any(t => t.Contains("Latência")));

Console.WriteLine();
Console.WriteLine($"Resumo: {report.BuildTitle()}");
Console.WriteLine($"        {report.BuildSummary()}");
Console.WriteLine();

if (report.CollectorFailures.Count > 0)
{
    Console.WriteLine($"COLETORES QUE FALHARAM ({report.CollectorFailures.Count}) — precisam de reparo:");
    foreach (var f in report.CollectorFailures) Console.WriteLine("  ! " + f);
}
else
{
    Console.WriteLine("Nenhum coletor falhou.");
}

// Amostra de cada severidade, para conferir a forma do achado.
foreach (var sev in new[] { DiagnosticSeverity.Critical, DiagnosticSeverity.Warning, DiagnosticSeverity.Info })
{
    var f = report.Sorted().FirstOrDefault(x => x.Severity == sev);
    Console.WriteLine();
    Console.WriteLine($"--- exemplo {sev} ---");
    Console.WriteLine(f == null ? "(nenhum)" : f.ToBlock());
}

// A cópia "só o importante" não pode conter achado INFO.
var important = report.BuildClipboardText(importantOnly: true);
Check("cópia importante não traz INFO",
    !report.Findings.Any(f => f.Severity == DiagnosticSeverity.Info && important.Contains(f.Title)));

// Cópia completa precisa conter o cabeçalho e as evidências.
var full = report.BuildClipboardText(importantOnly: false);
Check("cópia completa tem cabeçalho", full.Contains("CENTRAL DE DIAGNÓSTICO"));
Check("cópia completa tem evidências", full.Contains("EVIDÊNCIAS / LOGS BRUTOS"));

Console.WriteLine();
Console.WriteLine(failures == 0 ? "TODAS AS VERIFICAÇÕES PASSARAM." : $"{failures} VERIFICAÇÃO(ÕES) FALHARAM.");
return failures == 0 ? 0 : 1;
