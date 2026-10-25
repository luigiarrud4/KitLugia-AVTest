// ThreadFixBench — trava as correcoes de travessia de thread contra regressao.
//
// CONTEXTO: nesta sessao o usuario reclamou de travamento ao clicar na tabela de DNS
// ("[UI-FREEZE] Thread de UI travada por ~3512 ms"). A causa encontrada foi um
// DispatcherTimer que chamava trabalho pesado em SINCRONO na thread de UI. Uma auditoria
// do projeto inteiro (39 DispatcherTimer, 100 Dispatcher.Invoke) achou mais copias
// estruturais do MESMO bug. Este harness falha se alguem reintroduzir qualquer um deles.
//
// O QUE ELE FAZ: le o FONTE e afirma que cada padrao corrigido continua corrigido.
// E barato (sem P/Invoke, sem UI, sem Privilegio) e pega a regressao no CI.
//
// Como o harness compila para um OutDir TEMPORARIO (fora do repo), a raiz do repo vem
// de --src ou da varredura ate achar KitLugia.sln.
//
// Uso:
//   dotnet build tests/ThreadFixBench/ThreadFixBench.csproj -p:OutDir=<abs>
//   ThreadFixBench.exe --src "<raiz do repo>"

using System.IO;

internal static class Program
{
    private static int _falhas;

    private static void Check(string titulo, bool ok, string detalhe)
    {
        Console.WriteLine($"[{(ok ? " ok " : "FALHA")}] {titulo}\n        {detalhe}");
        if (!ok) _falhas++;
    }

    private static string Ler(string raiz, string rel)
    {
        var p = Path.Combine(raiz, rel);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    /// <summary>Trecho da linha que contem `alvo`, util para provar como o Tick chama.</summary>
    private static string LinhaDoTick(string src, string alvo)
    {
        int pos = src.IndexOf(alvo, StringComparison.Ordinal);
        if (pos < 0) return "(Tick nao encontrado: " + alvo + ")";
        int fim = src.IndexOf('\n', pos);
        if (fim < 0) fim = src.Length;
        return src.Substring(pos, Math.Min(fim - pos, 200)).Trim();
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("=== ThreadFixBench — trava contra regressao de travessia de thread ===");
        Console.WriteLine();

        var raiz = LocalizarRaiz(args);
        if (raiz == null)
        {
            Console.WriteLine("!! nao achei a raiz do repo — passe --src <dir>");
            return 2;
        }
        Console.WriteLine("repo: " + raiz);
        Console.WriteLine();

        var tray = Ler(raiz, @"KitLugia.GUI\Services\TrayIconService.cs");
        var bcd = Ler(raiz, @"KitLugia.Core\BcdRepairManager.cs");
        var apps = Ler(raiz, @"KitLugia.GUI\Pages\AppsPage.xaml.cs");
        var icons = Ler(raiz, @"KitLugia.GUI\Helpers\AppIconHelper.cs");
        var net = Ler(raiz, @"KitLugia.GUI\Pages\NetworkPage.xaml.cs");

        if (tray.Length == 0 || bcd.Length == 0)
        {
            Console.WriteLine("!! fontes nao encontradas");
            return 2;
        }

        Console.WriteLine("--- A) DispatcherTimer nao pode chamar trabalho pesado em sincrono ---");

        var tickRam = LinhaDoTick(tray, "_ramLimiterTimer.Tick");
        Check("RAM Limiter: Tick despacha para QueueRamLimiterCycle",
              tickRam.Contains("QueueRamLimiterCycle") && !tickRam.Contains("ApplyProcessRamLimits()"),
              tickRam);

        var tickAdv = LinhaDoTick(tray, "_advancedMonitorTimer.Tick");
        Check("Monitor Avancado: Tick aponta so para o handler (que ja vai para o pool)",
              tickAdv.Contains("AdvancedMonitor_Tick"),
              tickAdv);
        Check("Monitor Avancado: o handler vai para o pool com guarda Interlocked",
              tray.Contains("Interlocked.Exchange(ref _advancedMonitorBusy, 1)") &&
              tray.Contains("Interlocked.Exchange(ref _advancedMonitorBusy, 0)"),
              "Task.Run + guarda contra sobreposicao");

        var tickPb = LinhaDoTick(tray, "_proBalanceTimer.Tick");
        Check("ProBalance: Tick aponta so para o handler (que ja vai para o pool)",
              tickPb.Contains("ProBalanceTimerTick"),
              tickPb);
        Check("ProBalance: o handler vai para o pool com guarda Interlocked",
              tray.Contains("Interlocked.Exchange(ref _proBalanceTickBusy, 1)") &&
              tray.Contains("Interlocked.Exchange(ref _proBalanceTickBusy, 0)"),
              "Task.Run + guarda contra sobreposicao");

        Console.WriteLine();
        Console.WriteLine("--- B) Dispatcher.Invoke BLOQUEANTE fora de situacao segura ---");

        Check("AppIconHelper: nao chama Dispatcher.Invoke dentro do lock (deadlock duro)",
              !EhInvokeDentroDoLock(icons),
              "Invoke movido para fora do _genericIconLock");
        // A assercao precisa ser ESPECIFICA: o AppsPage tem Dispatcher.Invoke legitimos
        // (ex.: Cleanup(), que roda na propria thread de UI e executa inline).
        // O que nao pode voltar e o Invoke DENTRO do callback de progresso do scan,
        // que vem de varias threads do pool (o Core usa Parallel.ForEach).
        var blocoScan = TrechoApos(apps, "PortableAppScanner.Scan(", 600);
        Check("AppsPage: progresso do scan usa InvokeAsync (callback vem de varias threads)",
              blocoScan.Contains("Dispatcher.InvokeAsync") && !blocoScan.Contains("Dispatcher.Invoke("),
              blocoScan.Contains("Dispatcher.InvokeAsync") ? "InvokeAsync dentro do callback" : blocoScan.Trim());
        Check("NetworkPage: progresso do MAC usa InvokeAsync",
              !net.Contains("progress => Dispatcher.Invoke(") &&
              net.Contains("Dispatcher.InvokeAsync"),
              "Invoke trocado por InvokeAsync");

        Console.WriteLine();
        Console.WriteLine("--- C) async sem Task.Run no prefixo (trava a UI no await) ---");

        Check("BcdRepairManager.DiagnoseAsync: deteccao de firmware fora da thread de UI",
              bcd.Contains("Task.Run(DetectFirmwareMode)"),
              "await Task.Run(() => DetectFirmwareMode())");
        Check("BcdRepairManager.DiagnoseAsync: FindEspPartition fora da thread de UI",
              bcd.Contains("Task.Run(FindEspPartition)"),
              "await Task.Run(FindEspPartition)");
        Check("BcdRepairManager.RebuildAsync: BackupBcdStore fora da thread de UI",
              bcd.Contains("Task.Run(() => BackupBcdStore(espLetter))"),
              "await Task.Run(() => BackupBcdStore(espLetter))");

        Console.WriteLine();
        Console.WriteLine(_falhas == 0 ? "=== OK: nenhum padrao voltou ===" : $"=== {_falhas} FALHA(S) ===");
        return _falhas == 0 ? 0 : 2;
    }

    /// <summary>Trecho de `fonte` a partir de `marcador`, ate `maxChars`. Vazio se nao achar.</summary>
    private static string TrechoApos(string fonte, string marcador, int maxChars)
    {
        int p = fonte.IndexOf(marcador, StringComparison.Ordinal);
        if (p < 0) return "";
        int n = Math.Min(maxChars, fonte.Length - p);
        return fonte.Substring(p, n);
    }

    /// <summary>
    /// True se existir "Dispatcher.Invoke(" dentro do bloco que comeca em
    /// "lock (_genericIconLock)" e termina na chave de fechamento correspondente.
    /// </summary>
    private static bool EhInvokeDentroDoLock(string src)
    {
        int lockPos = src.IndexOf("lock (_genericIconLock)", StringComparison.Ordinal);
        if (lockPos < 0) return false;
        int abre = src.IndexOf('{', lockPos);
        if (abre < 0) return false;
        int fecha = src.IndexOf('}', abre);
        if (fecha < 0) return false;
        string bloco = src.Substring(abre, fecha - abre);
        return bloco.Contains("Dispatcher.Invoke(");
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
            {
                if (File.Exists(Path.Combine(d.FullName, "KitLugia.sln"))) return d.FullName;
            }
        }
        return null;
    }
}
