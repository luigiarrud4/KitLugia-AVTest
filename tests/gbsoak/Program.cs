using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;

// ===========================================================================
// gbsoak - SOAK TEST: o motor liga/desliga milhares de vezes
// ===========================================================================
// Os testes anteriores provaram CASOS ISOLADOS (crash, handle invalido,
// elevate). Falta a prova de ACUMULO: que o motor, repetido, nao deixa nada
// preso. E' o teste que separa "funciona uma vez" de "funciona sempre".
//
// PORQUE ESTE HARNESS USA O ApplyBoostModern:
// A primeira versao chamava ApplyBoostV1..V4 por reflection, a saltar o
// registo da prioridade original. Resultado: o revert nao tinha o que
// restaurar e a prioridade ficava presa. Era ARTEFACTO DO HARNESS, nao bug do
// produto: em producao o MonitorTick faz
//      _originalPriorities.TryAdd(pid, prioridade) -> PromoteBoost
//      -> ApplyBoostModern (que escolhe o motor)
// Este harness replica EXATAMENTE essa ordem. Senao nao estaria a testar o
// produto - estaria a testar um caminho que ninguem percorre.
//
// Vigia, ciclo a ciclo:
//   1. prioridade do processo volta ao original depois do revert
//   2. Win32PrioritySeparation volta ao valor ORIGINAL (nao a um fixo)
//   3. nada fica preso em BelowNormal pelo ProBalance
//   4. o ficheiro de resgate nao cresce sem limite
//   5. nao ha fuga de handles (medida depois de aquecimento)

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Console.WriteLine($"  [FALHA] {nome}: {det}"); }
        else Console.WriteLine($"  [OK  ] {nome}: {det}");
    }
    static void Info(string nome, string det) => Console.WriteLine($"  [....] {nome}: {det}");

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    // PriorityClass e' um enum (ProcessPriorityClass), nao-nulavel.
    static uint PrioridadeDe(uint pid)
    {
        var p = Process.GetProcessById((int)pid);
        try { return (uint)p.PriorityClass; }
        finally { p.Dispose(); }
    }

    static int HandlesDoProcesso => Process.GetCurrentProcess().HandleCount;

    const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    public static int Main(string[] args)
    {
        int ciclos = 200;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--ciclos" && int.TryParse(args[i + 1], out int n)) ciclos = n;

        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try { exit = await Cenario(ciclos); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            Console.Out.Flush();
            app.Shutdown(exit);
            // Rede de seguranca: um teste que fica pendurado nao informa nada.
            var killer = new Timer(_ => Environment.Exit(exit), null, 5000, Timeout.Infinite);
        };
        app.Run();
        return exit;
    }

    static async Task<int> Cenario(int ciclos)
    {
        Console.WriteLine($"=== gbsoak - soak test: {ciclos} ciclos liga/desliga ===");
        var elev = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Console.WriteLine($"  build={Environment.OSVersion.Version.Build} elevado={elev}");

        uint sepOriginal = (uint)(Win32Api.ReadWin32PrioritySeparation() ?? 0);
        Console.WriteLine($"  Win32PrioritySeparation original = 0x{sepOriginal:X}");

        var svc = new TrayIconService();

        // Os tres passos privados que a producao encadeia.
        var fOriginais = typeof(TrayIconService).GetField("_originalPriorities", PRIV);
        var mPromote = typeof(TrayIconService).GetMethod("PromoteBoost", PRIV);
        var mModern = typeof(TrayIconService).GetMethod("ApplyBoostModern", PRIV);
        Check("mecanismos de producao encontrados",
              fOriginais != null && mPromote != null && mModern != null,
              $"field={fOriginais != null} Promote={mPromote != null} Modern={mModern != null}");
        if (fOriginais == null || mPromote == null || mModern == null)
        {
            Console.WriteLine("  Nao e' possivel testar sem estes membros - nomes mudaram?");
            return 2;
        }
        var nivelFoco = mModern.GetParameters()[1].ParameterType;
        object valorFoco = Enum.Parse(nivelFoco, "Foco");

        var alvo = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false });
        if (alvo == null) { Console.WriteLine("  nao consegui arrancar o charmap"); return 2; }
        await Task.Delay(1500);
        uint pid = (uint)alvo.Id;
        uint prioInicial = PrioridadeDe(pid);
        Info("processo de teste", $"pid={pid} prioridade inicial={prioInicial}");

        var engines = new List<TrayIconService.GameBoostEngine>
        {
            TrayIconService.GameBoostEngine.Auto,
            TrayIconService.GameBoostEngine.V1_Balanced,
            TrayIconService.GameBoostEngine.V2_StableFPS,
            TrayIconService.GameBoostEngine.V3_Extreme,
            TrayIconService.GameBoostEngine.V4_ExtremePro,
        };

        // Aquecimento: o JIT e o WPF alocam handles so na primeira vez.
        for (int w = 0; w < 5; w++)
        {
            TrayIconService.SetEngine(engines[w % engines.Count]);
            AplicarCiclo(svc, pid, fOriginais, mPromote, mModern, valorFoco);
            ReverterCiclo(svc);
            await Task.Delay(2);
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        int handlesInicio = HandlesDoProcesso;

        int presoPrioridade = 0, presoSeparation = 0, presoThrottle = 0;
        var porEngine = new Dictionary<TrayIconService.GameBoostEngine, int>();

        var sw = Stopwatch.StartNew();
        for (int c = 0; c < ciclos; c++)
        {
            var eng = engines[c % engines.Count];
            TrayIconService.SetEngine(eng);
            uint prioAntes = PrioridadeDe(pid);

            AplicarCiclo(svc, pid, fOriginais, mPromote, mModern, valorFoco);
            ReverterCiclo(svc);
            await Task.Delay(2);

            uint prioDepois = PrioridadeDe(pid);
            if (prioDepois != prioAntes)
            {
                presoPrioridade++;
                porEngine.TryGetValue(eng, out int n);
                porEngine[eng] = n + 1;
                if (presoPrioridade <= 5)
                    Console.WriteLine($"    ciclo {c} ({eng}): prioridade {prioAntes} -> {prioDepois} (PRESA)");
            }

            if (c % 10 == 0)
            {
                uint sep = (uint)(Win32Api.ReadWin32PrioritySeparation() ?? 0);
                if (sep != sepOriginal) presoSeparation++;
            }

            if (prioDepois == 16384) presoThrottle++;
        }
        sw.Stop();

        long rescueSize = File.Exists(RescueFile) ? new FileInfo(RescueFile).Length : 0;
        GC.Collect(); GC.WaitForPendingFinalizers();
        int handlesFim = HandlesDoProcesso;
        int leak = handlesFim - handlesInicio;

        uint sepFinal = (uint)(Win32Api.ReadWin32PrioritySeparation() ?? 0);
        uint prioFinal = PrioridadeDe(pid);

        Console.WriteLine();
        Console.WriteLine("--- RESULTADO ---");
        Console.WriteLine($"  {ciclos} ciclos em {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  prioridade presa em {presoPrioridade} ciclo(s)");
        Console.WriteLine($"  separacao alterada em {presoSeparation} leitura(s)");
        Console.WriteLine($"  processos em BelowNormal apos revert: {presoThrottle}");
        Console.WriteLine($"  Win32PrioritySeparation: 0x{sepOriginal:X} -> 0x{sepFinal:X}");
        Console.WriteLine($"  boost_rescue.txt = {rescueSize} bytes");
        Console.WriteLine($"  handles (apos aquecimento): {handlesInicio} -> {handlesFim} (delta {leak})");
        foreach (var kv in porEngine) Console.WriteLine($"    falhas no motor {kv.Key}: {kv.Value}");

        Check("NENHUM processo ficou preso em prioridade", presoPrioridade == 0, $"{presoPrioridade} ciclo(s)");
        Check("Win32PrioritySeparation voltou ao ORIGINAL",
              presoSeparation == 0 && sepFinal == sepOriginal, $"0x{sepOriginal:X} -> 0x{sepFinal:X}");
        Check("nenhum processo ficou em BelowNormal", presoThrottle == 0, $"{presoThrottle}");
        Check("prioridade final = inicial", prioFinal == prioInicial, $"{prioInicial} -> {prioFinal}");
        Check("sem fuga de handles", leak <= 12, $"delta {leak} (tolerancia 12)");
        Check("boost_rescue.txt nao ficou gigante", rescueSize < 64 * 1024, $"{rescueSize} bytes");

        try { svc.ShutdownGameBoost(); } catch { }
        await Task.Delay(400);
        uint prioAposShutdown = 0;
        try { prioAposShutdown = PrioridadeDe(pid); } catch { }
        Check("shutdown devolveu a prioridade original",
              prioAposShutdown == prioInicial, $"{prioInicial} -> {prioAposShutdown}");

        try { alvo.Kill(); } catch { }
        await Task.Delay(300);
        foreach (var p in Process.GetProcessesByName("charmap")) { try { p.Kill(); } catch { } }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }

    // Espelha a producao: guarda a original -> promove -> aplica o motor.
    static void AplicarCiclo(TrayIconService svc, uint pid, FieldInfo fOriginais,
                             MethodInfo mPromote, MethodInfo mModern, object valorFoco)
    {
        try
        {
            var dict = fOriginais.GetValue(svc);
            var tryAdd = dict.GetType().GetMethod("TryAdd");
            tryAdd.Invoke(dict, new object[] { pid, (ProcessPriorityClass)PrioridadeDe(pid) });

            mPromote.Invoke(svc, new object[] { pid, "charmap" });
            mModern.Invoke(svc, new object[] { pid, valorFoco });
        }
        catch { }
    }

    // Espelha o que o shutdown faz.
    static void ReverterCiclo(TrayIconService svc)
    {
        try { svc.RevertAllBoostTargets(); } catch { }
        try { svc.RestoreAllThrottledProcesses(); } catch { }
    }
}
