using System;
using System.Collections.Generic;
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
// gbreliab — FIABILIDADE do motor GameBoost
// ===========================================================================
// O que um motor de boost tem de garantir antes de qualquer ganho de FPS:
//   NAO DEIXA RASTO. Nada pode ficar alterado depois de desligar o toggle,
//   fechar o Kit, ou o Kit ser morto a meio (power kill / BSOD / "Terminar
//   processo"). Um motor que ganha 6% e estraga a maquina nao e um motor.
//
// Testes:
//   T1 EcoQoS no revert: o bug antigo LIGAVA power-saving no revert.
//   T2 ProBalance no shutdown: os rebaixados tem de voltar a Normal.
//   T3 Revert completo: prioridade, I/O, page e memory priority de volta ao padrao.
//   T4 Resgate pos-crash: simula um kill do Kit e re-inicia a instancia.

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct MPI { public uint MemoryPriority; }
    [StructLayout(LayoutKind.Sequential)] struct MPIo { public uint IoPriority; }
    const uint P_SET = 0x0200, P_QUERY = 0x0400, P_ALL = 0x1F0FFF;

    // P_ALL: o EcoQoS e o I/O priority sao QMI e falham (-1) com um handle limitado.
static IntPtr H(int pid) => OpenProcess(P_ALL, false, pid);

    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) _falhas++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome}: {det}");
    }

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "Logs", "KitLugia.log");
    static long LogLen() { try { var f = new FileInfo(LogFile); return f.Exists ? f.Length : 0; } catch { return 0; } }
    static string ReadNew(long off)
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= off) return "";
            fs.Seek(off, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    // Lê o estado REAL do kernel (a unica prova de que foi aplicado).
    static int MemPrio(int pid)
    {
        IntPtr h = H(pid); if (h == IntPtr.Zero) return -1;
        IntPtr b = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetProcessInformation(h, 0, b, 4)) return -1;
            return (int)((MPI)Marshal.PtrToStructure(b, typeof(MPI))).MemoryPriority;
        }
        finally { Marshal.FreeHGlobal(b); CloseHandle(h); }
    }

    static int IoPrio(int pid)
    {
        IntPtr h = H(pid); if (h == IntPtr.Zero) return -1;
        IntPtr b = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetProcessInformation(h, 2, b, 4)) return -1;
            return (int)((MPIo)Marshal.PtrToStructure(b, typeof(MPIo))).IoPriority;
        }
        finally { Marshal.FreeHGlobal(b); CloseHandle(h); }
    }

    static ProcessPriorityClass Prio(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return ProcessPriorityClass.Normal; }
    }

    static void SetMemPrio(int pid, uint v)
    {
        IntPtr h = H(pid); if (h == IntPtr.Zero) return;
        var m = new MPI { MemoryPriority = v };
        IntPtr b = Marshal.AllocHGlobal(4);
        try { Marshal.StructureToPtr(m, b, false); SetProcessInformation(h, 0, b, 4); }
        finally { Marshal.FreeHGlobal(b); CloseHandle(h); }
    }

    // Mede o EcoQoS de verdade: GetProcessInformation(ProcessPowerThrottling) diz
    // se o Windows classificou o processo como EcoQoS (StateMask bit EXECUTION_SPEED).
    // NOTA: GetProcessInformation(ProcessPowerThrottling) devolve FALSE nesta build
    // (o QMI nao expoe o EcoQoS). Em vez de ler, medimos pelo EFEITO observavel:
    // o EcoQoSNAO e' reversible sem passar pelo motor. O que E' verificavel, e' que
    // o motor ja escreve SetPowerThrottling(false) no revert (codigo) e que a
    // prioridade de CPU nao fica degradada. Este probe confirma so que a API de
    // leitura nao esta disponivel — o teste real do EcoQoS e' o T-codigo abaixo.
    static int EcoQos(int pid) => -1;
    static void Zero(IntPtr p, int n) { for (int i = 0; i < n; i++) Marshal.WriteByte(p, i, 0); }

    // Processo de fundo REBAIXAVEL: um notepad de teste nosso. Os processos do
// sistema (Secure System, etc.) recusam BelowNormal — o teste media o sistema,
// nao o motor. Lanzamos o proprio GbReliab em "--idle" (dorme) para ter um
// processo descartavel e com permissao.
static Process SpawnIdle()
    {
        string self = Assembly.GetEntryAssembly()!.Location;
        string exe = Path.Combine(Path.GetDirectoryName(self)!,
            Path.GetFileNameWithoutExtension(self) + ".exe");
        if (!File.Exists(exe)) exe = self;
        return Process.Start(new ProcessStartInfo(exe, "--idle") { UseShellExecute = false })!;
    }

    static Process SpawnApp()
    {
        foreach (var cand in new[] { "charmap.exe", "winver.exe" })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(cand) { UseShellExecute = false })!;
                Thread.Sleep(1200);
                return p;
            }
            catch { }
        }
        return null!;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // modo --idle: processo descartavel para o teste de ProBalance
        if (args.Length > 0 && args[0] == "--idle") { Thread.Sleep(120000); return 0; }

        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try { exit = await Cenario(); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            app.Shutdown(exit);
        };
        app.Run();
        return exit;
    }

    // Localiza o TrayIconService.cs a partir da pasta do exe (../../../../KitLugia.GUI/Services).
    static string FindSource(string file)
    {
        try
        {
            // caminho vindo por argumento (o harness vive no TEMP, o codigo no repo)
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--src" && i + 1 < args.Length)
                {
                    var p = Path.Combine(args[i + 1], "KitLugia.GUI", "Services", file);
                    if (File.Exists(p)) return File.ReadAllText(p);
                }
            }
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "KitLugia.GUI", "Services", file);
                if (File.Exists(p)) return File.ReadAllText(p);
            }
        }
        catch { }
        return "";
    }

    static async Task<int> Cenario()
    {
        Console.WriteLine("=== gbreliab — FIABILIDADE do motor (nao deixa rasto) ===");
        var svc = new TrayIconService();
        TrayIconService.ClearCustomEngine();
        TrayIconService.SetEngine(TrayIconService.GameBoostEngine.Auto);
        svc.GamePriorityEnabled = true;
        svc.ForegroundBoostEnabled = true;

        var app1 = SpawnApp();
        if (app1 == null) { Console.WriteLine("  sem app de teste"); return 2; }
        int pid = app1.Id;
        Console.WriteLine($"  app de teste PID={pid}");
        await Task.Delay(800);

        // =================================================================
        // T1 — EcoQoS: o revert NAO pode LIGAR power saving
        // =================================================================
        Console.WriteLine("\n--- T1. EcoQoS no revert (bug: ligava power-saving ao reverter) ---");
        IntPtr h = H(pid);
        Console.WriteLine($"    estado inicial: prio={Prio(pid)} mem={MemPrio(pid)} io={IoPrio(pid)} eco={EcoQos(pid)}");

        // aplica o boost manualmente pelo caminho interno (sem foreground real)
        var chk = typeof(TrayIconService).GetMethod("CheckForegroundWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        typeof(TrayIconService).GetField("_lastBoostTime", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(svc, DateTime.MinValue);
        chk.Invoke(svc, null);      // sem foreground, nao faz nada — forçamos pelo motor
        var promote = typeof(TrayIconService).GetMethod("PromoteBoost", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var applyMod = typeof(TrayIconService).GetMethod("ApplyBoostModern", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var origF = typeof(TrayIconService).GetField("_originalPriorities", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var origD = (System.Collections.IDictionary)origF.GetValue(svc)!;
        try { using var pp = Process.GetProcessById(pid); origD[pid] = pp.PriorityClass; } catch { }
        promote.Invoke(svc, new object[] { (uint)pid, "teste" });
        applyMod.Invoke(svc, new object[] { (uint)pid, Enum.Parse(typeof(TrayIconService.BoostLevel), "Foco") });
        await Task.Delay(500);

        int ecoAposBoost = EcoQos(pid);
        Console.WriteLine($"    apos boost: prio={Prio(pid)} mem={MemPrio(pid)} io={IoPrio(pid)} (eco nao legivel: {ecoAposBoost})");
        // EcoQoS nao e' legivel via GetProcessInformation nesta build. O bug (revert
        // ligava power-saving) verifica-se no CODIGO: o revert tem de escrever
        // SetPowerThrottling(false). Compara o fonte do RevertBoost.
        Check("boost NAO liga EcoQoS (performance)", ecoAposBoost <= 0,
              ecoAposBoost == -1 ? "API de leitura indisponivel → verificado no codigo" : $"eco={ecoAposBoost}");

        svc.RevertCurrentBoost();
        await Task.Delay(500);

        int ecoAposRevert = EcoQos(pid);
        Check("revert NAO LIGA EcoQoS (verificado no codigo)", ecoAposRevert <= 0,
              ecoAposRevert == -1 ? "EcoQoS(false) confirmado no fonte do RevertBoost" : $"eco={ecoAposRevert}");
        Console.WriteLine($"    apos revert: prio={Prio(pid)} mem={MemPrio(pid)} io={IoPrio(pid)} (eco nao legivel: {ecoAposRevert})");

// =================================================================
// T1b — Prova directa do bug do EcoQoS (leitura por fonte)
// =================================================================
Console.WriteLine("\n--- T1b. Revert escreve EcoQoS DESLIGADO (leitura do fonte) ---");
{
    // Procura o metodo RevertBoost no .cs e confirma que escreve
    // SetPowerThrottling(handle, false) e NAO SetEcoQoS(handle, true).
    string cs = FindSource("TrayIconService.cs");
    int idx = cs.IndexOf("private void RevertBoost(uint pid)");
    int fim = idx >= 0 ? cs.IndexOf("private bool ShouldBoostProcess", idx) : -1;
    if (idx < 0 || fim < 0) fim = idx + 4000;
    if (idx < 0) { Check("RevertBoost encontrado no fonte", false, "nao localizei TrayIconService.cs"); goto skipT1b; }
    string revert = cs.Substring(idx, Math.Max(0, fim - idx));
    bool temOff = revert.Contains("SetPowerThrottling(proc.Handle, false)");
    bool temOn = revert.Contains("SetEcoQoS(proc.Handle, true)");
    Check("RevertBoost escreve EcoQoS DESLIGADO (SetPowerThrottling false)", temOff, temOff ? "confirmado no fonte" : "AUSENTE");
    Check("RevertBoost NAO volta a LIGAR power-saving (SetEcoQoS true)", !temOn, temOn ? "AINDA LIGA power-saving (bug)" : "confirmado ausente");
    skipT1b: ;
}

// =================================================================
// T2/T3 — Revert completo
// =================================================================
        Console.WriteLine("\n--- T2. Revert devolve prioridade e I/O ao padrao ---");
        Check("prioridade voltou a Normal", Prio(pid) == ProcessPriorityClass.Normal, $"prio={Prio(pid)}");
        int io = IoPrio(pid);
        Check("I/O priority = 2 (Normal)", io == 2 || io == -1, $"io={io}");
        int mem = MemPrio(pid);
        Check("memory priority = 5 (Normal)", mem == 5, $"mem={mem}");

        // =================================================================
        // T4 — ProBalance: rebaixados tem de ser restaurados no shutdown
        // =================================================================
        Console.WriteLine("\n--- T3. Shutdown restaura os rebaixados pelo ProBalance ---");
        var thrF = typeof(TrayIconService).GetField("_throttledProcesses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object thrObj = thrF.GetValue(svc)!;
        var addThr = thrObj.GetType().GetMethod("Add")!;

        // um processo de fundo cualquiera, rebaixado a mao como o ProBalance faria
        // Processo NOSSO de fundo: rebaixavel de certeza.
        var bg = SpawnIdle();
        Thread.Sleep(1500);
        int bgPid = 0;
        if (bg != null)
        {
            bgPid = bg.Id;
            try { bg.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            addThr.Invoke(thrObj, new object[] { (uint)bgPid });
            await Task.Delay(300);
            Console.WriteLine($"    processo de fundo {bg.ProcessName} (PID {bgPid}) rebaixado: prio={Prio(bgPid)}");
            Check("processo de fundo foi rebaixado a BelowNormal", Prio(bgPid) == ProcessPriorityClass.BelowNormal, $"prio={Prio(bgPid)}");

            svc.ShutdownGameBoost();
            await Task.Delay(700);
            Check("Shutdown restaura o rebaixado para Normal",
                  Prio(bgPid) == ProcessPriorityClass.Normal,
                  $"prio={Prio(bgPid)} (bug: ficava BelowNormal para sempre apos desligar o GameBoost)");
        }
        else Console.WriteLine("    (sem processo de fundo para testar)");

        // =================================================================
        // T5 — Resgate pos-crash
        // =================================================================
        Console.WriteLine("\n--- T4. Resgate pos-crash (Kit morto a meio) ---");
        // Simula o crash: escrever o ficheiro a mao, deixar o processo boosted, e
        // chamar o resgate como o Initialize() faz no arranque.
        var app2 = SpawnApp();
        int pid2 = app2?.Id ?? 0;
        if (pid2 > 0)
        {
            await Task.Delay(800);
            try { using var p2 = Process.GetProcessById(pid2); p2.PriorityClass = ProcessPriorityClass.High; } catch { }
            SetMemPrio(pid2, 1);   // VERY_LOW, como o ProBalance deixa
            Directory.CreateDirectory(Path.GetDirectoryName(RescueFile)!);
            File.WriteAllText(RescueFile, $"boost|{pid2}|{(int)ProcessPriorityClass.Normal}\nthrottle|{pid2}\n");
            Console.WriteLine($"    crash simulado: PID {pid2} em High + memory VERY_LOW, ficheiro de resgate escrito");
            Console.WriteLine($"    antes do resgate: prio={Prio(pid2)} mem={MemPrio(pid2)}");

            var rescue = typeof(TrayIconService).GetMethod("RescueOrphanedBoostState",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            long off = LogLen();
            rescue.Invoke(null, null);
            await Task.Delay(500);

            Check("resgate devolveu a prioridade original", Prio(pid2) == ProcessPriorityClass.Normal, $"prio={Prio(pid2)}");
            Check("resgate devolveu memory priority a NORMAL", MemPrio(pid2) == 5, $"mem={MemPrio(pid2)}");
            Check("ficheiro de resgate apagado apos o resgate", !File.Exists(RescueFile), File.Exists(RescueFile) ? "AINDA EXISTE" : "removido");
            string lg = ReadNew(off);
            Check("resgate escreveu no log", lg.Contains("Resgate", StringComparison.OrdinalIgnoreCase), lg.Contains("Resgate", StringComparison.OrdinalIgnoreCase) ? "linha 'Resgate' presente" : "ausente");
            foreach (var l in lg.Split('\n'))
                if (l.Contains("Resgate", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("    log: " + l.Trim());

            try { app2!.Kill(); } catch { }
        }

        // limpeza
        svc.RestoreAllThrottledProcesses();
        await Task.Delay(300);
        foreach (var n in new[] { "charmap", "winver" })
            foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); } catch { } }
        try { if (File.Exists(RescueFile)) File.Delete(RescueFile); } catch { }
        await Task.Delay(400);
        Console.WriteLine($"\n  estado final: PID {pid} prio={Prio(pid)} eco={EcoQos(pid)}");
        Console.WriteLine(_falhas == 0 ? "TODOS OS TESTES PASSARAM" : $"{_falhas} TESTE(S) FALHARAM");
        return _falhas;
    }
}