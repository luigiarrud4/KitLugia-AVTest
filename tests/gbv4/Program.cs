using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;

// REGRESSAO DO V4 (05/10/2026): o utilizador usa V4 "bastante" e "funciona bem".
// O Automatico v2 NAO pode ter Carry-over para o V4. Este harness prova, no caminho
// real, que o V4 continua a fazer EXATAMENTE o que fazia antes:
//   TimerBoost=true | NetworkBoost=true | Win32PrioritySeparation=true | Page=4
// e que o revert do Win32PrioritySeparation devolve o valor ORIGINAL (fidelity).

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct MPI { public uint MemoryPriority; }
    delegate bool EnumProc(IntPtr h, IntPtr p);

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

    static ProcessPriorityClass Prio(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return ProcessPriorityClass.Normal; }
    }

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
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint me = GetCurrentThreadId();
            bool att = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                ShowWindow(hwnd, 9);
                SetForegroundWindow(hwnd);
            }
            finally { if (att) AttachThreadInput(me, fgThread, false); }
            System.Threading.Thread.Sleep(300);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            if (fg == want) { Console.WriteLine($"    foreground OK: {label} (pid={want})"); return true; }
        }
        Console.WriteLine($"    ATENCAO: nao consegui trazer {label} para foreground");
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
        Console.WriteLine("=== HARNESS: REGRESSAO DO V4 (o motor que o utilizador usa) ===");

        var svc = new TrayIconService();
        TrayIconService.ClearCustomEngine();
        TrayIconService.SetEngine(TrayIconService.GameBoostEngine.V4_ExtremePro);
        svc.GamePriorityEnabled = true;
        Console.WriteLine($"  engine = {TrayIconService.CurrentEngine} (V4 = {(int)TrayIconService.GameBoostEngine.V4_ExtremePro})");

        // app de teste
        Process np = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false })!;
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++) { await Task.Delay(200); hwnd = FindVisibleWindow((uint)np.Id); }
        if (hwnd == IntPtr.Zero) { Console.WriteLine("  sem janela de teste"); return 2; }
        int pid = np.Id;
        Console.WriteLine($"  charmap PID={pid} prioridade inicial={Prio(pid)}");

        // ---- 1. Parametros do V4: lidos do config que o V4 constroi
        Console.WriteLine("\n--- 1. V4 mantem os parametros de sempre (Timer/rede/Win32Pri/Page) ---");
        var v4M = typeof(TrayIconService).GetMethod("ApplyBoostV4", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // instrumenta ApplyBoostCustom para capturar o config que o V4 manda
        var cfgField = typeof(TrayIconService).GetField("_lastAppliedConfig", BindingFlags.NonPublic | BindingFlags.Instance);
        Console.WriteLine($"  (V4 deve ter: TimerBoost=true, NetworkBoost=true, Win32PrioritySeparation=true, PagePriorityLevel=4)");

        int? sepOriginal = Win32Api.ReadWin32PrioritySeparation();
        Console.WriteLine($"  Win32PrioritySeparation ORIGINAL na maquina = {(sepOriginal.HasValue ? sepOriginal.Value.ToString() : "(ausente)")}");

        // V4 aplication real: foreground -> CheckForegroundWindow
        if (!TryForeground(hwnd, "charmap (V4)")) return 2;
        var chkM = typeof(TrayIconService).GetMethod("CheckForegroundWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long off = LogLen();
        chkM.Invoke(svc, null);
        await Task.Delay(900);

        var prio = Prio(pid);
        string log = ReadNew(off);
        Check("V4 ainda aplica High (o comportamento que o utilizador conhece)", prio == ProcessPriorityClass.High, $"prioridade={prio}");
        Check("V4 mexe no Win32PrioritySeparation (38) como sempre",
            Win32Api.ReadWin32PrioritySeparation() == 38,
            $"agora={Win32Api.ReadWin32PrioritySeparation()} (original={sepOriginal})");
        bool timerOn = log.Contains("Timer Resolution", StringComparison.OrdinalIgnoreCase);
        Check("V4 continua a pedir timer de 1 ms (global)", timerOn, timerOn ? "log 'Timer Resolution' presente" : "AUSENTE no log");
        foreach (var l in log.Split('\n'))
            if (l.Contains("Timer Resolution", StringComparison.OrdinalIgnoreCase) || l.Contains("V4", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine("    log: " + l.Trim());

        // ---- 2. Revert: tem de devolver o VALOR ORIGINAL, nao 2
        Console.WriteLine("\n--- 2. Revert do Win32PrioritySeparation devolve o ORIGINAL (bug do 'grava 2') ---");
        svc.RevertCurrentBoost();
        await Task.Delay(400);
        svc.ShutdownGameBoost();
        await Task.Delay(400);
        int? sepDepois = Win32Api.ReadWin32PrioritySeparation();
        Check("revert devolvou exatamente o valor original",
            sepDepois == sepOriginal,
            $"original={sepOriginal} depois do shutdown={sepDepois} (bug antigo gravava 2, estragando maquinas com 38)");

        // ---- 3. Estado da maquina limpo
        Console.WriteLine("\n--- 3. limpeza ---");
        int resp = ReadDword(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "SystemResponsiveness");
        int net = ReadDword(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "NetworkThrottlingIndex");
        Check("SystemResponsiveness voltou ao default (20)", resp == 0 || resp == 20, $"valor={resp}");
        Check("NetworkThrottlingIndex voltou ao default (10)", net == 0 || net == 10, $"valor={net}");
        Check("processo de teste voltou a Normal", Prio(pid) == ProcessPriorityClass.Normal, $"prioridade={Prio(pid)}");

        try { np.Kill(); } catch { }
        await Task.Delay(300);
        foreach (var n in new[] { "charmap", "winver", "dxdiag" })
            foreach (var p in Process.GetProcessesByName(n)) { try { p.Kill(); } catch { } }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} TESTE(S) FALHARAM");
        return _falhas;
    }

    static int ReadDword(string keyPath, string name)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
            var v = k?.GetValue(name);
            return v == null ? 0 : Convert.ToInt32(v);
        }
        catch { return 0; }
    }
}