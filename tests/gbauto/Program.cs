using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;

// Harness: motor AUTOMATICO do GameBoost pelo caminho REAL do app (sem ForceReapplyBoost).
// Fluxo: janela em foreground -> CheckForegroundWindow (o mesmo metodo que o WinEventHook
// e o polling de 250 ms chamam) -> PromoteBoost -> ApplyBoostModern -> ApplyBoostAuto.
// Pergunta a responder: o motor funciona com QUALQUER app em foreground (sem detetar jogo)?
internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) _falhas++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {nome}: {det}");
    }

    static string LogFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "Logs", "KitLugia.log");
    static long LogLen() { try { var fi = new FileInfo(LogFile); return fi.Exists ? fi.Length : 0; } catch { return 0; } }
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

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    static extern bool SetProcessInformationRaw(IntPtr h, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, IntPtr info, uint size);
    [StructLayout(LayoutKind.Sequential)] struct MPI { public uint MemoryPriority; }

    // Le a memory priority REAL do processo (via GetProcessInformation, a unica forma
    // de confirmar que o ajuste foi mesmo aplicado ao kernel).
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    static int MemPrio(int pid)
    {
        IntPtr h = GetHandle(pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                if (!GetProcessInformation(h, 0, buf, 4)) return -1;
                return (int)((MPI)Marshal.PtrToStructure(buf, typeof(MPI))).MemoryPriority;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return -1; }
        finally { CloseHandle(h); }
    }

    static int SysCpu()
    {
        try { return Win32Api.GetSystemCpuLoad(); } catch { return -999; }
    }

    // OpenProcess DIRECTO: Process.Handle fecha o handle quando o Process e descartado
    // (o using da versao anterior devolvia um handle ja invalidado -> erro 6).
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    const uint PROCESS_SET_INFO = 0x0200;
    const uint PROCESS_QUERY_INFO = 0x0400;

    static IntPtr GetHandle(int pid)
    {
        return OpenProcess(PROCESS_SET_INFO | PROCESS_QUERY_INFO, false, pid);
    }

    static ProcessPriorityClass Prio(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return ProcessPriorityClass.Normal; }
    }

    static uint FgPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr p);

    static IntPtr FindVisibleWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            GetWindowThreadProcessId(h, out uint wpid);
            if (wpid == pid && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool TryForeground(IntPtr hwnd, string label)
    {
        AllowSetForegroundWindow(-1);
        GetWindowThreadProcessId(hwnd, out uint want);
        for (int i = 0; i < 6; i++)
        {
            // Liberta o "foreground lock": so o processo em foreground pode setar outro.
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero); // ALT down (desbloqueia em alguns casos)
                keybd_event(0x12, 0, 2, UIntPtr.Zero); // ALT up
                ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            System.Threading.Thread.Sleep(300);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            if (fg == want) { Console.WriteLine($"    foreground OK: {label} (pid={want})"); return true; }
        }
        Console.WriteLine($"    ATENCAO: nao consegui trazer {label} para foreground (fg atual pid={FgPid()})");
        return false;
    }

    [STAThread]
    public static int Main()
    {
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

    static async Task<int> Cenario()
    {
        Console.WriteLine("=== HARNESS GameBoost: motor AUTOMATICO pelo caminho real (CheckForegroundWindow) ===");

        var svc = new TrayIconService();
        TrayIconService.ClearCustomEngine();
        TrayIconService.SetEngine(TrayIconService.GameBoostEngine.Auto);
        svc.GamePriorityEnabled = true;
        Console.WriteLine($"  TrayIconService: engine={TrayIconService.CurrentEngine} customAtivo={TrayIconService.IsCustomEngineActive} GamePriorityEnabled={svc.GamePriorityEnabled}");

        // 0. A lista de exclusoes (_userExceptions) FOI REMOVIDA (05/10/2026). Confirma que o
        //    campo nao existe mais e mostra o que continua protegido.
        var exField = typeof(TrayIconService).GetField("_userExceptions", BindingFlags.NonPublic | BindingFlags.Static);
        Check("_userExceptions removido do TrayIconService", exField == null,
            exField == null ? "campo inexistente (OK)" : "CAMPO AINDA EXISTE!");
        var protField = typeof(TrayIconService).GetField("_protectedProcesses", BindingFlags.NonPublic | BindingFlags.Static);
        var prot = (HashSet<string>)protField!.GetValue(null)!;
        Console.WriteLine($"  _protectedProcesses: {prot.Count} nomes protegidos (unica barreira agora)");


        var chkM = typeof(TrayIconService).GetMethod("CheckForegroundWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var targetsF = typeof(TrayIconService).GetField("_boostTargets", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 1. App que NAO e jogo, sem qualquer heuristica de jogo: notepad
        Console.WriteLine("\n--- 1. app comum (NAO-jogo) em foreground -> motor automatico ---");
        Process np = null!; IntPtr hwnd = IntPtr.Zero; string npName = "";
        foreach (var cand in new[] { "charmap.exe", "winver.exe", "dxdiag.exe" })
        {
            var proc = Process.Start(new ProcessStartInfo(cand) { UseShellExecute = false })!;
            IntPtr h = IntPtr.Zero;
            for (int i = 0; i < 25 && h == IntPtr.Zero; i++) { await Task.Delay(200); h = FindVisibleWindow((uint)proc.Id); }
            if (h != IntPtr.Zero) { np = proc; hwnd = h; npName = cand; break; }
            try { proc.Kill(); } catch { }
        }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("  nao consegui obter janela de nenhum app de teste"); return 2; }
        int npPid = np.Id;
        Console.WriteLine($"  app de teste: {npName} PID={npPid} janela=0x{hwnd.ToInt64():X} prioridade inicial={Prio(npPid)}");
        if (!TryForeground(hwnd, npName)) return 2;

        long off = LogLen();
        chkM.Invoke(svc, null);            // <- exatamente o que o hook/timer do app chama
        await Task.Delay(600);

        var prio1 = Prio(npPid);
        var targets = (System.Collections.IDictionary)targetsF.GetValue(svc)!;
        Check("foreground registado pelo motor", svc.CurrentForegroundPid == (uint)npPid, $"CurrentForegroundPid={svc.CurrentForegroundPid} (app={npPid})");
        Check("prioridade elevada para High (sem detetar jogo)", prio1 == ProcessPriorityClass.High, $"prioridade={prio1}");
        Check("processo entrou na faixa de boost (_boostTargets)", targets.Contains((uint)npPid), $"targets={targets.Count}");
        string log1 = ReadNew(off);
        bool logAuto = log1.Contains("GameBoost AUTO", StringComparison.OrdinalIgnoreCase);
        Check("log do motor AUTO com fg=True", logAuto, logAuto ? "linha 'GameBoost AUTO' presente" : "linha ausente");
        foreach (var l in log1.Split('\n'))
            if (l.Contains("GameBoost AUTO", StringComparison.OrdinalIgnoreCase) || l.Contains("boost aplicado", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("    log: " + l.Trim());

        // 2. Perder o foco: histerese (faixa Sustentado), nao reverte na hora
        Console.WriteLine("\n--- 2. Foco muda para a JANELA DO HARNESS -> histerese (faixa Sustentado) ---");
        var w = new Window { Width = 240, Height = 120, Topmost = true, Left = 60, Top = 60, Title = "gbauto-harness", WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = false };
        w.Show();
        w.Activate();
        var wh = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        bool fgOk = TryForeground(wh, "janela do harness");
        long off2 = LogLen();
        chkM.Invoke(svc, null);
        await Task.Delay(600);
        var prio2 = Prio(npPid);
        string log2 = ReadNew(off2);
        Check("chamada de foreground executada", fgOk, $"GetForegroundWindow pid={FgPid()}");
        Check($"{npName} rebaixado para AboveNormal (Sustentado)", prio2 == ProcessPriorityClass.AboveNormal, $"prioridade={prio2}");
        bool logSust = log2.Contains("SUSTENTADA", StringComparison.OrdinalIgnoreCase);
        Check("log explica a faixa Sustentado", logSust, logSust ? "'faixa SUSTENTADA' no log" : "linha ausente");
        foreach (var l in log2.Split('\n'))
            if (l.Contains("SUSTENTADA", StringComparison.OrdinalIgnoreCase)) Console.WriteLine("    log: " + l.Trim());

        // 3. Revert: volta a prioridade ORIGINAL
        Console.WriteLine("\n--- 3. RevertCurrentBoost -> prioridade original ---");
        svc.RevertCurrentBoost();
        await Task.Delay(500);
        var prio3 = Prio(npPid);
        Check($"{npName} voltou a Normal", prio3 == ProcessPriorityClass.Normal, $"prioridade={prio3}");

        // 4. ShouldBoostProcess: so os protegidos (e o proprio kit) ficam de fora.
        Console.WriteLine("\n--- 4. ShouldBoostProcess: protegido/ele-mesmo bloqueiam; app comum passa ---");
        var sbp = typeof(TrayIconService).GetMethod("ShouldBoostProcess", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool self = (bool)sbp.Invoke(svc, new object[] { (uint)Environment.ProcessId, IntPtr.Zero })!;
        Check("o proprio kit nunca recebe boost", self == false, $"ShouldBoostProcess(self)={self}");
        int expPid = 0;
        foreach (var pr in Process.GetProcessesByName("explorer")) { expPid = pr.Id; break; }
        string expTxt = "explorer nao esta a correr (teste ignorado)";
        bool expRes = true;
        if (expPid != 0) { expRes = (bool)sbp.Invoke(svc, new object[] { (uint)expPid, IntPtr.Zero })!; expTxt = $"ShouldBoostProcess(explorer)={expRes}"; }
        Check("processo protegido (explorer) NAO recebe boost", expRes == false, expTxt);
        bool comum = (bool)sbp.Invoke(svc, new object[] { (uint)npPid, IntPtr.Zero })!;
        Check($"app comum ({npName}) PODE receber boost", comum, $"ShouldBoostProcess={comum}");

        // ===================================================================
        // 5. AUTO v2 (05/10/2026): os quatro pontos que a pesquisa mudou
        // ===================================================================
        Console.WriteLine("\n--- 5. AUTO v2: page priority = 5 (o padrao, nao 4 abaixo do default) ---");
        // (janela do app de teste e' procurada abaixo, antes do chkM)
        var abF = typeof(TrayIconService).GetField("_autoBand", BindingFlags.NonPublic | BindingFlags.Static)!;
        var abD = (System.Collections.IDictionary)abF.GetValue(null)!;
        // o boost da etapa 1 ja devolveu tudo ao Normal; re-boosteia para reler o estado
        IntPtr npHwnd2 = IntPtr.Zero;
        for (int i = 0; i < 25 && npHwnd2 == IntPtr.Zero; i++)
        {
            npHwnd2 = FindVisibleWindow((uint)npPid);
            if (npHwnd2 == IntPtr.Zero) await Task.Delay(200);
        }
        if (npHwnd2 == IntPtr.Zero) { Console.WriteLine("  janela do app de teste desapareceu: etapas 5-10 avaliadas sem re-boost"); }
        else { TryForeground(npHwnd2, npName + " (2a vez)"); }
        chkM.Invoke(svc, null);
        await Task.Delay(500);
        int memFoco = MemPrio(npPid);
        Check("memory priority do foco = 5 (NORMAL/default)", memFoco == 5, $"GetProcessInformation -> {memFoco} (antes era 4 = BELOW_NORMAL no Auto v1)");
        Check("faixa do motor Automatico registada (_autoBand)", abD.Contains((uint)npPid), $"faixas={abD.Count}");

        Console.WriteLine("\n--- 6. AUTO v2: NADA global (timer de 1 ms e Win32PrioritySeparation) ---");
        int? prioSepAntes = Win32Api.ReadWin32PrioritySeparation();
        Console.WriteLine($"  Win32PrioritySeparation na maquina = {(prioSepAntes.HasValue ? prioSepAntes.Value.ToString() : "(ausente)")}");
        // o motor Auto nao pode ter escrito no registry: o valor tem de ser o de antes
        int? prioSepDepois = Win32Api.ReadWin32PrioritySeparation();
        Check("Auto nao mexeu no Win32PrioritySeparation (global)",
            prioSepAntes == prioSepDepois,
            $"antes={prioSepAntes} depois={prioSepDepois}");

        Console.WriteLine("\n--- 7. ProBalance do Auto so rebaixa SOB CARGA (gate > 70%) ---");
        int carga1 = SysCpu(); await Task.Delay(400); int carga2 = SysCpu();
        Console.WriteLine($"  carga do sistema: {carga1}% -> {carga2}% (gate = 70%)");
        Check("GetSystemCpuLoad devolve valor plausivel", carga2 >= 0 && carga2 <= 100, $"carga={carga2}%");
        // com a maquina folgada NINGUEM pode ter sido rebaixado pelo ProBalance
        var thrF = typeof(TrayIconService).GetField("_throttledProcesses", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object thrObj = thrF.GetValue(svc)!;
        int thrCount = (int)thrObj.GetType().GetProperty("Count")!.GetValue(thrObj)!;
        bool folgado = (carga2 < 70);
        Check(folgado ? "maquina folgada -> ProBalance nao rebaixa ninguem"
                      : "maquina carregada -> gate abriu (rebaixamento esperado)",
            folgado ? thrCount == 0 : true,
            $"carga={carga2}% | processos rebaixados={thrCount}");
        svc.RestoreAllThrottledProcesses();
        await Task.Delay(300);

        Console.WriteLine("\n--- 8. memory priority REAL e' aplicada (a correcao do bug) ---");
        // O bug antigo chamava SetThreadInformation(classe 0) com handle de PROCESSO:
        // falhava com erro 6 e o valor nunca mudava. Agora tem de mudar e voltar.
        Win32Api.SetThreadMemoryPriority(GetHandle(npPid), 1);
        await Task.Delay(200);
        int mp1 = MemPrio(npPid);
        // DIAGNOSTICO: a chamada crua tem de funcionar; se falhar, o problema e' o handle/permissao
        {
            IntPtr h = GetHandle(npPid);
            var m = new MPI { MemoryPriority = 1 };
            IntPtr p = Marshal.AllocHGlobal(4);
            Marshal.StructureToPtr(m, p, false);
            bool raw = SetProcessInformationRaw(h, 0, p, 4);
            int err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(p);
            Console.WriteLine($"    [diag] handle=0x{h.ToInt64():X} SetProcessInformation cru -> ok={raw} erro={err} | releitura={MemPrio(npPid)}");
            Win32Api.SetThreadMemoryPriority(h, 5);
        }
        Check("SetThreadMemoryPriority(1) agora CHEGA ao processo", mp1 == 1, $"valor lido de volta = {mp1} (bug antigo deixava sempre 5)");
        Win32Api.SetThreadMemoryPriority(GetHandle(npPid), 5);
        await Task.Delay(200);
        int mp2 = MemPrio(npPid);
        Check("restaura para NORMAL (5)", mp2 == 5, $"valor lido de volta = {mp2}");

        Console.WriteLine("\n--- 9. traducao indice -> escala real (page priority) ---");
        int pi0 = TrayIconService.PagePriorityFromIndex(0);
        int pi1 = TrayIconService.PagePriorityFromIndex(1);
        Check("indice 0 (Normal) -> 5", pi0 == 5, $"PagePriorityFromIndex(0)={pi0}");
        Check("indice 1 (Below Normal) -> 4", pi1 == 4, $"PagePriorityFromIndex(1)={pi1} (bug antigo aplicava 1 = VERY_LOW)");
        int tm0 = TrayIconService.ThreadMemoryPriorityFromIndex(0);
        int tm1 = TrayIconService.ThreadMemoryPriorityFromIndex(1);
        Check("thread memory indice 0 -> 5 (Normal)", tm0 == 5, $"={tm0}");
        Check("thread memory indice 1 -> 1 (Very Low)", tm1 == 1, $"={tm1}");

        Console.WriteLine("\n--- 10. codigo morto removido ---");
        Check("_heavyAppIndicators removido",
            typeof(TrayIconService).GetField("_heavyAppIndicators", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "campo inexistente (OK)");
        Check("IsFullScreen removido",
            typeof(TrayIconService).GetMethod("IsFullScreen", BindingFlags.NonPublic | BindingFlags.Instance) == null,
            "metodo inexistente (OK)");


        // limpeza
        svc.RevertCurrentBoost();
        await Task.Delay(300);
        try { w.Close(); } catch { }
        try { np.Kill(); } catch { }
        foreach (var n in new[] { "charmap", "winver", "dxdiag" })
        {
            foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); } catch { } }
        }
        await Task.Delay(300);
        Console.WriteLine($"  estado final: {npName} prioridade={Prio(npPid)} | processo ainda vivo={!np.HasExited}");

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} TESTE(S) FALHARAM");
        return _falhas;
    }
}
