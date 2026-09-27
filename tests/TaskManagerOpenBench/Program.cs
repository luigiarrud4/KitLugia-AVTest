// Harness de medição do CAMINHO DE ABERTURA do Gerenciador de Tarefas do KitLugia.
//
// Roda de verdade o pipeline de dados do 1º refresh (a parte que vive no Core) e compara
// as duas formas de compor as tarefas:
//
//   ANTES: await Task.WhenAll(net, gpu, snap)  +  gpuPerPid.Result
//          → o .Result era executado NA UI THREAD e bloqueava até o PDH terminar.
//   AGORA: await Task.WhenAll(net, gpu, gpuPerPid, snap)
//          → a UI thread só espera o mais lento, não a soma/sobra.
//
// Nada aqui altera o sistema: leitura pura (NtQuerySystemInformation, PDH, WTS, Iphlpapi).
// Uso: dotnet run --project tests/TaskManagerOpenBench [-- run=5]

using System.Diagnostics;
using KitLugia.Core.TaskManager;

int runs = 5;
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("run=") && int.TryParse(args[i][4..], out int r) && r > 0) runs = r;
}

Console.WriteLine("=== KitLugia — benchmark do caminho de abertura do Gerenciador de Tarefas ===");
Console.WriteLine($"Reexecuções por formato: {runs}");
Console.WriteLine();

// ── Aquecimento: JIT + caches internos (WTS 15s, PDH init, IO snapshot) ──────────────
// Sem isso a 1ª medição incluiria o custo de inicialização que só acontece uma vez no app.
RunPipeline().GetAwaiter().GetResult();
Thread.Sleep(250);

// ── Custo isolado de cada etapa (execução serial, para atribuir o tempo) ─────────────
var isolated = new List<(string Name, double Ms)>();
void Measure(string name, Action work)
{
    var sw = Stopwatch.StartNew();
    try { work(); } catch { }
    sw.Stop();
    isolated.Add((name, sw.Elapsed.TotalMilliseconds));
}

Measure("Rede (conexões TCP por PID)", () => _ = KitLugia.Core.NetworkTrafficMonitor.GetActiveTcpConnectionsPerPid());
Measure("GPU total (PDH)", () => _ = GpuMonitor.GetTotalGpuUtilization());
Measure("GPU por PID (PDH \\GPU Engine)", () => _ = GpuMonitor.GetGpuUtilizationPerPid());
Measure("Snapshot de processos (NtQSI)", () => _ = NativeMetricsHelper.EnumerateProcesses());
Measure("Janelas visíveis (NtQSI/User32)", () => _ = NativeMetricsHelper.GetPidsWithVisibleWindows());
Measure("Nomes de usuário (WTS)", () => _ = NativeMetricsHelper.GetUserNames());

Console.WriteLine("--- Custo isolado de cada etapa ---");
foreach (var (name, ms) in isolated)
    Console.WriteLine($"  {name,-36} {ms,7:F1} ms");
Console.WriteLine();

// ── Formato NOVO: gpuPerPid dentro do WhenAll ────────────────────────────────────────
var novo = new List<double>();
for (int i = 0; i < runs; i++)
{
    var sw = Stopwatch.StartNew();
    await RunPipeline();
    sw.Stop();
    novo.Add(sw.Elapsed.TotalMilliseconds);
}

// ── Formato ANTIGO: gpuPerPid fora do WhenAll e lido com .Result ─────────────────────
// Mede também quanto tempo o .Result ficaria segurando a UI thread.
var antigo = new List<double>();
var bloqueioAntigo = new List<double>();
for (int i = 0; i < runs; i++)
{
    var sw = Stopwatch.StartNew();
    bloqueioAntigo.Add(await RunPipelineOldShape());
    sw.Stop();
    antigo.Add(sw.Elapsed.TotalMilliseconds);
}

static double Avg(List<double> v) => v.Count == 0 ? 0 : v.Sum() / v.Count;
static double Pior(List<double> v) => v.Count == 0 ? 0 : v.Max();

Console.WriteLine("--- Pipeline completo (1º refresh) ---");
Console.WriteLine($"  ANTES  (gpuPerPid em .Result): média {Avg(antigo),7:F1} ms | pior {Pior(antigo),7:F1} ms");
Console.WriteLine($"  AGORA  (gpuPerPid no WhenAll): média {Avg(novo),7:F1} ms | pior {Pior(novo),7:F1} ms");
Console.WriteLine();

double gain = Avg(antigo) - Avg(novo);
Console.WriteLine($"  Diferença: {(gain >= 0 ? "-" : "+")}{Math.Abs(gain):F1} ms no wall clock " +
                  $"({(Avg(antigo) > 0 ? gain / Avg(antigo) * 100 : 0):F0}%)");
Console.WriteLine($"  Tempo em que a UI THREAD ficava bloqueada no formato antigo: " +
                  $"média {Avg(bloqueioAntigo):F1} ms | pior {Pior(bloqueioAntigo):F1} ms");
Console.WriteLine();

// O que o gate de visibilidade economiza: o tick de 1 s roda os mesmos estágios.
double tickCost = Avg(novo);
Console.WriteLine("--- Gate de visibilidade (janela oculta) ---");
Console.WriteLine($"  Um refresh custa ~{tickCost:F0} ms de CPU. Com o gerenciador oculto");
Console.WriteLine($"  (kit minimizado para a bandeja), o tick de 1 s antes rodava à toa:");
Console.WriteLine($"  {tickCost * 60 / 1000.0:F1} s de CPU por minuto, sem ninguém olhando. Agora: 0.");
Console.WriteLine();

// ── Verificação: o pipeline devolve dados reais? ─────────────────────────────────────
var check = await RunPipelineDetailed();
bool ok = check.Processes > 100 && check.Paths > 0;
Console.WriteLine("--- Sanidade ---");
Console.WriteLine($"  processos={check.Processes} | caminhos resolvidos={check.Paths} | " +
                  $"pids com janela={check.Windows} | usuários={check.Users}");
Console.WriteLine(ok ? "  OK — pipeline devolve dados reais." : "  ATENÇÃO: contagem baixa, máquina em estado incomum.");
Console.WriteLine();
Console.WriteLine($"RESULTADO: {(gain >= 0 ? "melhor" : "pior")} em relação ao formato antigo.");
return ok ? 0 : 1;

// ════════════════════════════════════════════════════════════════════════════════════

async Task RunPipeline()
{
    await Task.WhenAll(
        Task.Run(() => { try { return KitLugia.Core.NetworkTrafficMonitor.GetActiveTcpConnectionsPerPid(); } catch { return new Dictionary<uint, int>(); } }),
        Task.Run(() => { try { return (float)GpuMonitor.GetTotalGpuUtilization(); } catch { return -1f; } }),
        Task.Run(() => { try { return GpuMonitor.GetGpuUtilizationPerPid(); } catch { return new Dictionary<uint, double>(); } }),
        Task.Run(() => { try { return (object)NativeMetricsHelper.EnumerateProcesses(); } catch { return new object(); } }));
}

/// <summary>Formato ANTIGO: gpuPerPid fora do WhenAll e lido com .Result (bloqueia quem chamou).</summary>
async Task<double> RunPipelineOldShape()
{
    var netTask = Task.Run(() => { try { return KitLugia.Core.NetworkTrafficMonitor.GetActiveTcpConnectionsPerPid(); } catch { return new Dictionary<uint, int>(); } });
    var gpuTask = Task.Run(() => { try { return (float)GpuMonitor.GetTotalGpuUtilization(); } catch { return -1f; } });
    var gpuPerPidTask = Task.Run(() => { try { return GpuMonitor.GetGpuUtilizationPerPid(); } catch { return new Dictionary<uint, double>(); } });
    var snapTask = Task.Run(() => { try { return (object)NativeMetricsHelper.EnumerateProcesses(); } catch { return new object(); } });

    await Task.WhenAll(netTask, gpuTask, snapTask);
    var block = Stopwatch.StartNew();
    _ = gpuPerPidTask.Result;   // ← aqui a UI thread esperava o PDH
    block.Stop();
    return block.Elapsed.TotalMilliseconds;
}

async Task<(int Processes, int Paths, int Windows, int Users)> RunPipelineDetailed()
{
    return await Task.Run(() =>
    {
        int processes = 0, paths = 0, windows = 0, users = 0;
        try
        {
            var snap = NativeMetricsHelper.EnumerateProcesses();
            processes = snap.Count;
            var pids = snap.Select(s => s.Pid).ToList();
            paths = SafeProcessHelper.GetProcessPathsBatch(pids).Count;
            windows = NativeMetricsHelper.GetPidsWithVisibleWindows().Count;
            users = NativeMetricsHelper.GetUserNames().Count;
        }
        catch { }
        return (processes, paths, windows, users);
    });
}
