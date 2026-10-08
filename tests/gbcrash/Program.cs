using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;

// ===========================================================================
// gbcrash — SIMULACAO DE CRASH REAL do motor GameBoost
// ===========================================================================
// A versao anterior do teste escrevia o ficheiro de resgate A MAO. Isso provava
// que o resgate funcionava, mas nao provava que o motor o ESCREVE a tempo.
// Aqui o teste e' de verdade:
//
//   --crash  : o proprio servico aplica o boost pelo caminho real (que escreve
//              boost_rescue.txt) e depois MORRE COM TerminateProcess — sem
//              finally, sem handlers de ProcessExit, sem ShutdownGameBoost.
//              E' o mais perto de um power kill / BSOD / "Terminar processo".
//   --rescue : um processo NOVO (instancia limpa do TrayIconService) chama
//              RescueOrphanedBoostState() — o mesmo que o Initialize() faz —
//              e verifica que o processo vitima voltou ao estado original.
//
// Verificacao: o ficheiro tem de existir depois do crash (provando que o motor
// o escreveu no caminho real, e nao a mao) e a victima tem de estar restaurada.

internal static class Program
{
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct MPI { public uint MemoryPriority; }
    const uint P_ALL = 0x1F0FFF;

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) _falhas++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome}: {det}");
    }

    static ProcessPriorityClass Prio(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return ProcessPriorityClass.Normal; }
    }
    static int MemPrio(int pid)
    {
        IntPtr h = OpenProcess(P_ALL, false, pid); if (h == IntPtr.Zero) return -1;
        IntPtr b = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetProcessInformation(h, 0, b, 4)) return -1;
            return (int)((MPI)Marshal.PtrToStructure(b, typeof(MPI))).MemoryPriority;
        }
        finally { Marshal.FreeHGlobal(b); CloseHandle(h); }
    }

    static void KillThem(string name)
    {
        // BUG CORRIGIDO (05/10/2026): GetProcessesByName devolve TAMBEM este
        // processo. Sem o skip, o harness matava-se a si proprio antes de
        // imprimir o resultado e de devolver o exit code - o "4/4" que se lia no
        // ecra era so' texto, o exit code era lixo (127).
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); } catch { }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--idle") { Thread.Sleep(120000); return 0; }

        string mode = args.Length > 0 ? args[0] : "--rescue";
        string pidArg = args.Length > 1 ? args[1] : "0";
        string selfDir = AppContext.BaseDirectory;

        // ---- modo CRASH: aplica boost pelo caminho real e MORRE ----
        if (mode == "--crash")
        {
            return ModoCrash(selfDir);
        }

        // ---- modo RESCUE: arranque limpo, executa o resgate ----
        return ModoRescue(int.Parse(pidArg));
    }

    static int ModoCrash(string selfDir)
    {
        Console.WriteLine("=== gbcrash: Aplica boost pelo caminho REAL e MORRE sem cleanup ===");
        if (File.Exists(RescueFile)) File.Delete(RescueFile);

        var svc = new TrayIconService();
        TrayIconService.ClearCustomEngine();
        TrayIconService.SetEngine(TrayIconService.GameBoostEngine.Auto);
        svc.GamePriorityEnabled = true;
        svc.ForegroundBoostEnabled = true;

        // Alvo descartavel: o proprio exe em --idle (nao e' protegido).
        string self = Assembly.GetEntryAssembly()!.Location;
        string exe = Path.Combine(Path.GetDirectoryName(self)!,
            Path.GetFileNameWithoutExtension(self) + ".exe");
        if (!File.Exists(exe)) exe = self;
        // REDIRECIONAR O STDOUT DAS VITIMAS E' OBRIGATORIO: um filho criado com
        // UseShellExecute=false HERDA os handles std do pai. Como as vitimas vivem
        // minutos, quem capture o output do gbcrash com um pipe (o `&` do
        // PowerShell, por exemplo) so' recebe EOF quando ELAS morrerem - o teste
        // ficava pendurado 5 minutos. Com redirecionamento, as vitimas ganham
        // pipes proprios e o pai termina quando deve.
        var psiVitima = new ProcessStartInfo(exe, "--idle")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var alvo = Process.Start(psiVitima)!;
        Thread.Sleep(1500);

        Console.WriteLine($"  alvo PID={alvo.Id} estado inicial: prio={Prio(alvo.Id)} mem={MemPrio(alvo.Id)}");

        // Aplica o boost pelo caminho INTERNO REAL (PromoteBoost + ApplyBoostModern),
        // que e' o mesmo que CheckForegroundWindow chama quando a janela esta em foco.
        var promote = typeof(TrayIconService).GetMethod("PromoteBoost", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var apply = typeof(TrayIconService).GetMethod("ApplyBoostModern", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var origF = typeof(TrayIconService).GetField("_originalPriorities", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var origD = (System.Collections.IDictionary)origF.GetValue(svc)!;
        try { using var p = Process.GetProcessById(alvo.Id); origD[alvo.Id] = p.PriorityClass; } catch { }
        promote.Invoke(svc, new object[] { (uint)alvo.Id, "vitima" });
        apply.Invoke(svc, new object[] { (uint)alvo.Id, Enum.Parse(typeof(TrayIconService.BoostLevel), "Foco") });
        Thread.Sleep(800);

        // Simula o ProBalance a rebaixar OUTRO processo (memoria VERY_LOW).
        var bg = Process.Start(psiVitima)!;
        Thread.Sleep(1200);
        try { bg.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        Win32Api.SetThreadMemoryPriority(Handle(bg.Id), Win32Api.MEMORY_PRIORITY_VERY_LOW);
        var thrF = typeof(TrayIconService).GetField("_throttledProcesses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object thr = thrF.GetValue(svc)!;
        var save = typeof(TrayIconService).GetMethod("SaveCrashRescue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        thr.GetType().GetMethod("Add")!.Invoke(thr, new object[] { (uint)bg.Id });
        save.Invoke(svc, new object[] { true });   // grava o estado real

        Console.WriteLine($"  boost aplicado: alvo prio={Prio(alvo.Id)} | fundo prio={Prio(bg.Id)} mem={MemPrio(bg.Id)}");

        bool ficheiro = File.Exists(RescueFile);
        string conteudo = ficheiro ? File.ReadAllText(RescueFile) : "";
        Check("motor ESCREVEU boost_rescue.txt no caminho real", ficheiro,
              ficheiro ? $"{conteudo.Split('\n').Length} linha(s)" : "FICHEIRO AUSENTE");
        Check("o ficheiro regista a vitima boosted", conteudo.Contains($"boost|{alvo.Id}|"), $"contem 'boost|{alvo.Id}|': {conteudo.Contains($"boost|{alvo.Id}|")}");
        Check("o ficheiro regista o fundo throttled", conteudo.Contains($"throttle|{bg.Id}"), $"contem 'throttle|{bg.Id}|': {conteudo.Contains($"throttle|{bg.Id}")}");

        // Guarda os PIDs para o processo de resgate saber o que verificar.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "victim.txt"),
            $"{alvo.Id}\n{bg.Id}\n");

        Console.WriteLine("\n  >>> MORRENDO AGORA (TerminateProcess: sem finally, sem ShutdownGameBoost) <<<");
        Console.Out.Flush();

        // ---- CRASH REAL ----
        TerminateProcess(Process.GetCurrentProcess().Handle, 0xDEAD);
        Thread.Sleep(5000);   // se nao morreu, o teste falhou
        Console.WriteLine("  FALHA: o processo nao morreu");
        return 9;
    }

    static IntPtr Handle(int pid) => OpenProcess(P_ALL, false, pid);

    static int ModoRescue(int _ignorado)
    {
        Console.WriteLine("=== gbcrash: arranque NOVO, executa o resgate (como o Initialize faz) ===");
        string vf = Path.Combine(AppContext.BaseDirectory, "victim.txt");
        if (!File.Exists(vf)) { Console.WriteLine("  sem victim.txt (o modo --crash correu?)"); return 2; }
        var ids = File.ReadAllLines(vf).Where(l => l.Trim().Length > 0).Select(int.Parse).ToArray();
        int boostPid = ids[0], throttlePid = ids[1];

        Console.WriteLine($"  vitima boosted PID={boostPid}: prio={Prio(boostPid)}");
        Console.WriteLine($"  vitima throttled PID={throttlePid}: prio={Prio(throttlePid)} mem={MemPrio(throttlePid)}");
        Console.WriteLine($"  ficheiro de resgate existe: {File.Exists(RescueFile)}");

        Check("heranca: a vitima ficou High apos o crash",
              Prio(boostPid) == ProcessPriorityClass.High, $"prio={Prio(boostPid)}");
        Check("heranca: o fundo ficou BelowNormal apos o crash",
              Prio(throttlePid) == ProcessPriorityClass.BelowNormal, $"prio={Prio(throttlePid)}");

        // Instancia NOVA do servico = arranque limpo do Kit.
        var svc = new TrayIconService();
        var rescue = typeof(TrayIconService).GetMethod("RescueOrphanedBoostState",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        rescue.Invoke(null, null);
        Thread.Sleep(700);

        Check("RESGATE: a vitima boosted voltou ao estado original",
              Prio(boostPid) == ProcessPriorityClass.Normal, $"prio={Prio(boostPid)}");
        Check("RESGATE: o fundo throttled voltou a Normal",
              Prio(throttlePid) == ProcessPriorityClass.Normal, $"prio={Prio(throttlePid)}");
        Check("RESGATE: memory priority do fundo voltou a 5",
              MemPrio(throttlePid) == 5, $"mem={MemPrio(throttlePid)}");
        Check("RESGATE: apagou o ficheiro", !File.Exists(RescueFile),
              File.Exists(RescueFile) ? "AINDA EXISTE" : "removido");

        // limpeza
        foreach (var id in ids) { try { Process.GetProcessById(id).Kill(); } catch { } }
        KillThem("GbCrash");
        Thread.Sleep(600);

        Console.WriteLine(_falhas == 0 ? "\nCRASH + RESGATE: TODOS OS TESTES PASSARAM" : $"\n{_falhas} TESTE(S) FALHARAM");
        return _falhas;
    }
}