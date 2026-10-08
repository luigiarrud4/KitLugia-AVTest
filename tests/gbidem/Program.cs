using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using KitLugia.GUI.Services;

// ===========================================================================
// gbidem - IDEMPOTENCIA + FUGA DE HANDLES (item 3.4 do checklist)
// ===========================================================================
// O soak (gbsoak) prova que o ciclo liga/desliga 1000x nao deixa nada preso.
// Falta a pergunta COMPLEMENTAR, que e' a que causa dano em producao:
//
//   "e se o boost for aplicado VARIAS VEZES SEM REVERTER entre elas?"
//
// Isso acontece de verdade: o MonitorTick corre a cada ~2s e, enquanto o jogo
// esta' em foco, RE-aplica o boost a cada ciclo. Se a aplicacao nao fosse
// idempotente, a prioridade subiria a cada tick (Normal -> AboveNormal ->
// High -> ...) ou o estado interno acumularia entradas (mapa _boostTargets a
// crescer, handles a vazar) num motor que roda o dia inteiro.
//
// PROVA 1 (idempotencia): aplicar N vezes SEM reverter tem de deixar a
//   prioridade EXATAMENTE igual apos a 1a aplicacao. Nada de escalada.
// PROVA 2 (sem acumulo): o mapa _boostTargets nao pode ganhar entradas com
//   reaplicacoes do MESMO processo.
// PROVA 3 (revert unico basta): UM revert depois de N aplicacoes devolve a
//   prioridade original (nao e' preciso reverter N vezes).
// PROVA 4 (handles): N aplicacoes nao podem vazar handles.
//
// Espelha a producao (MonitorTick):
//   _originalPriorities.TryAdd(pid, prioridade) -> PromoteBoost -> ApplyBoostModern
// ===========================================================================

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Console.WriteLine($"  [FALHA] {nome}: {det}"); }
        else Console.WriteLine($"  [OK  ] {nome}: {det}");
    }
    static void Info(string nome, string det) => Console.WriteLine($"  [....] {nome}: {det}");

    const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance;

    static uint Prio(uint pid)
    {
        var p = Process.GetProcessById((int)pid);
        try { return (uint)p.PriorityClass; }
        finally { p.Dispose(); }
    }

    // Bitmasks, nao escala: BelowNormal(0x4000) e" numericamente MAIOR que
    // High(0x80). Comparar os valores crus da' resultados errados.
    static int RankDe(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => 0,
        ProcessPriorityClass.BelowNormal => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.AboveNormal => 3,
        ProcessPriorityClass.High => 4,
        ProcessPriorityClass.RealTime => 5,
        _ => 9
    };
    static int Rank(uint pid)
    {
        var p = Process.GetProcessById((int)pid);
        try { return RankDe(p.PriorityClass); }
        finally { p.Dispose(); }
    }
    static int Handles => Process.GetCurrentProcess().HandleCount;

    [STAThread]
    public static int Main(string[] args)
    {
        int n = 200;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--n" && int.TryParse(args[i + 1], out int v)) n = v;

        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try { exit = Cenario(n); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            Console.Out.Flush();
            app.Shutdown(exit);
            var killer = new Timer(_ => Environment.Exit(exit), null, 5000, Timeout.Infinite);
        };
        app.Run();
        return exit;
    }

    static int Cenario(int n)
    {
        Console.WriteLine($"=== gbidem - idempotencia e fuga de handles ({n} aplicacoes seguidas) ===");
        var elev = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        Console.WriteLine($"  build={Environment.OSVersion.Version.Build} elevado={elev}");

        var svc = new TrayIconService();
        var fOriginais = typeof(TrayIconService).GetField("_originalPriorities", PRIV);
        var fTargets = typeof(TrayIconService).GetField("_boostTargets", PRIV);
        var mPromote = typeof(TrayIconService).GetMethod("PromoteBoost", PRIV);
        var mModern = typeof(TrayIconService).GetMethod("ApplyBoostModern", PRIV);
        Check("mecanismos de producao encontrados",
              fOriginais != null && fTargets != null && mPromote != null && mModern != null,
              $"orig={fOriginais != null} targets={fTargets != null} Promote={mPromote != null} Modern={mModern != null}");
        if (fOriginais == null || fTargets == null || mPromote == null || mModern == null) return 2;

        var nivelFoco = mModern.GetParameters()[1].ParameterType;
        object valorFoco = Enum.Parse(nivelFoco, "Foco");

        var originais = (IDictionary)fOriginais.GetValue(svc);
        var alvoMapa = (IDictionary)fTargets.GetValue(svc);

        var alvo = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false });
        if (alvo == null) { Console.WriteLine("  nao consegui arrancar o charmap"); return 2; }
        Thread.Sleep(1500);
        uint pid = (uint)alvo.Id;
        uint prioInicial = Prio(pid);
        Info("processo de teste", $"pid={pid} prioridade inicial={prioInicial}");

        // Aquecimento: JIT/WPF alocam handles so na primeira vez.
        for (int w = 0; w < 5; w++) { Aplicar(svc, pid, originais, mPromote, mModern, valorFoco); Thread.Sleep(2); }
        svc.RevertAllBoostTargets();
        Thread.Sleep(50);
        GC.Collect(); GC.WaitForPendingFinalizers();
        int handlesInicio = Handles;

        // ---------- PROVA 1/2: N aplicacoes SEM reverter ----------
        Console.WriteLine();
        Console.WriteLine("--- aplicacoes consecutivas sem revert (como o MonitorTick em foco) ---");

        uint esperada = 0;
        int escalou = 0, mudouAlvo = 0;
        int entradasAntes = Contar(originais);      // tipicamente 0: o revert anterior limpou
        int entradasApos1 = -1;                     // 1 alvo -> 1 entrada original, na 1a aplicacao
        var valores = new List<uint>();

        for (int i = 0; i < n; i++)
        {
            Aplicar(svc, pid, originais, mPromote, mModern, valorFoco);
            uint p = Prio(pid);
            valores.Add(p);

            if (i == 0)
            {
                esperada = p;
                // A PRIMEIRA aplicacao cria a entrada. Depois disso o mapa tem de
                // ficar CONGELADO: reaplicar o MESMO alvo nao pode voltar a crescer.
                entradasApos1 = Contar(originais);
            }
            else if (p != esperada) escalou++;

            if (i > 0 && Contar(originais) != entradasApos1) mudouAlvo++;
        }

        uint prioDepoisN = Prio(pid);
        Info("prioridade apos a 1a aplicacao", esperada.ToString());
        Info("prioridade apos a ultima", prioDepoisN.ToString());
        Info("prioridade inicial era", prioInicial.ToString());
        Info("valores distintos observados", string.Join(", ", ValoresDistintos(valores)));

        Check("PROVA 1: a prioridade NAO escala com reaplicacoes",
              escalou == 0, $"{escalou} mudanca(s) em {n} aplicacoes (esperado 0)");
        Check("PROVA 1b: o boost SALTOU para uma prioridade acima da inicial",
              Rank(pid) > RankDe((ProcessPriorityClass)prioInicial),
              $"rank {RankDe((ProcessPriorityClass)prioInicial)} -> {Rank(pid)} ({prioInicial} -> {prioDepoisN})");
        Info("entradas em _originalPriorities", $"antes={entradasAntes} apos 1a aplicacao={entradasApos1} agora={Contar(originais)}");
        Check("PROVA 2: o mapa _originalPriorities ficou CONGELADO apos a 1a aplicacao",
              mudouAlvo == 0, $"{mudouAlvo} alteracao(oes) depois da 1a aplicacao ({n - 1} reaplicacoes)");
        Check("PROVA 2b: 1 alvo = exatamente 1 entrada (nem 0 nem N)",
              entradasApos1 == entradasAntes + 1 && entradasApos1 == 1,
              $"antes={entradasAntes} apos1={entradasApos1} (esperado {entradasAntes + 1})");

        // ---------- PROVA 3: UM revert basta ----------
        Console.WriteLine();
        Console.WriteLine("--- revert unico ---");
        svc.RevertAllBoostTargets();
        Thread.Sleep(120);
        uint prioAposRevert = Prio(pid);
        Check("PROVA 3: UM revert devolve a prioridade original",
              prioAposRevert == prioInicial, $"{prioInicial} -> {prioAposRevert}");
        Check("PROVA 3b: o alvo saiu do mapa _boostTargets",
              !TemChave(alvoMapa, pid), $"contem pid: {TemChave(alvoMapa, pid)}");

        // ---------- PROVA 4: handles ----------
        GC.Collect(); GC.WaitForPendingFinalizers();
        int handlesFim = Handles;
        int delta = handlesFim - handlesInicio;
        Check("PROVA 4: sem fuga de handles", delta <= 12,
              $"{handlesInicio} -> {handlesFim} (delta {delta}, tolerancia 12)");

        try { svc.ShutdownGameBoost(); } catch { }
        Thread.Sleep(400);
        try { alvo.Kill(); } catch { }
        Thread.Sleep(300);
        foreach (var p in Process.GetProcessesByName("charmap")) { try { p.Kill(); } catch { } }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }

    static void Aplicar(TrayIconService svc, uint pid, IDictionary originais,
                        MethodInfo mPromote, MethodInfo mModern, object valorFoco)
    {
        try
        {
            // TryAdd: so' grava a original na PRIMEIRA vez (igual a producao).
            if (!originais.Contains(pid)) originais[pid] = (ProcessPriorityClass)Prio(pid);
            mPromote.Invoke(svc, new object[] { pid, "charmap" });
            mModern.Invoke(svc, new object[] { pid, valorFoco });
        }
        catch { }
    }

    static int Contar(IDictionary d)
    {
        int c = 0; foreach (var _ in d.Keys) c++; return c;
    }

    static bool TemChave(IDictionary d, uint pid)
    {
        // As chaves do mapa sao uint; comparar por valor, nao por reference equality.
        foreach (object k in d.Keys)
            if (Convert.ToUInt32(k) == pid) return true;
        return false;
    }

    static List<uint> ValoresDistintos(List<uint> v)
    {
        var r = new List<uint>();
        foreach (var x in v)
            if (!r.Contains(x)) r.Add(x);
        return r;
    }
}
