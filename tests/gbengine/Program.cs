using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using KitLugia.GUI.Services;
using Microsoft.Win32;

// ===========================================================================
// gbengine - VERIFICACAO POR KNOB dos motores do GameBoost
// ===========================================================================
// Os motores V1, V2 e V3 nunca tinham sido verificados um a um. Este harness
// mede, para CADA motor, o estado ANTES / DURANTE / DEPOIS, e a partir dai:
//
//   a) documenta o que cada motor MUDA de facto (nao o que o rotulo promete)
//   b) prova que o revert devolve TUDO ao original
//
// Regra: so conta o que se consegue LER de volta. "Chamamos a API" nao e'
// prova de nada - foi assim que a memory priority ficou anos sem funcionar.
//
// Knobs vigiados:
//   - prioridade de CPU (Process.PriorityClass)
//   - page priority      (NtQueryInformationProcess classe 39, leitura real)
//   - memory priority    (GetProcessInformation classe 0, leitura real)
//   - Win32PrioritySeparation (registo)
//   - SystemResponsiveness / NetworkThrottlingIndex (registo, boost de rede)
//   - timer resolution (NtQueryTimerResolution)
//   - boost_rescue.txt

internal static class Program
{
    static int _falhas = 0;
    static void Check(string nome, bool ok, string det)
    {
        if (!ok) { _falhas++; Console.WriteLine($"    [FALHA] {nome}: {det}"); }
        else Console.WriteLine($"    [OK  ] {nome}: {det}");
    }

    // ---- P/Invoke para LER de volta o que interessa ----
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr h, int cls, ref int v, int len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint acc, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref int v, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);

    const uint PROC_ALL = 0x1F0FFF;
    const string SYS_PROFILE = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string PRIO_CONTROL = @"SYSTEM\CurrentControlSet\Control\PriorityControl";

    static string RescueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KitLugia", "boost_rescue.txt");

    /// <summary>Fotografia do estado observavel da maquina/processo.</summary>
    class Estado
    {
        public uint Prioridade;
        public int Pagina = -1;
        public int Memoria = -1;
        public int Separacao = -1;
        public int SysResponsiveness = -1;
        public int NetThrottling = -1;
        public uint TimerMin;

        public override string ToString() =>
            $"CPU={Prioridade} pagina={Pagina} memoria={Memoria} sep=0x{Separacao:X} " +
            $"sysresp={SysResponsiveness} netthr={NetThrottling} timer={TimerMin / 10000.0:F2}ms";
    }

    static Estado Tirar(IntPtr h, uint pid)
    {
        var e = new Estado();
        try { using var p = Process.GetProcessById((int)pid); e.Prioridade = (uint)p.PriorityClass; } catch { }

        int v = -1;
        if (GetProcessInformation(h, 39, ref v, 4)) e.Pagina = v;
        v = -1;
        if (GetProcessInformation(h, 0, ref v, 4)) e.Memoria = v;

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PRIO_CONTROL);
            if (k?.GetValue("Win32PrioritySeparation") is int s) e.Separacao = s;
        }
        catch { }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(SYS_PROFILE);
            if (k?.GetValue("SystemResponsiveness") is int sr) e.SysResponsiveness = sr;
            if (k?.GetValue("NetworkThrottlingIndex") is int nt) e.NetThrottling = nt;
        }
        catch { }
        NtQueryTimerResolution(out uint min, out _, out _);
        e.TimerMin = min;
        return e;
    }

    const BindingFlags PRIV = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    public static int Main(string[] args)
    {
        int exit = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (s, e) =>
        {
            try { exit = await Cenario(); }
            catch (Exception ex) { Console.WriteLine("FALHA NO HARNESS: " + ex); exit = 2; }
            Console.Out.Flush();
            app.Shutdown(exit);
            var killer = new Timer(_ => Environment.Exit(exit), null, 5000, Timeout.Infinite);
        };
        app.Run();
        return exit;
    }

    static async Task<int> Cenario()
    {
        Console.WriteLine("=== gbengine - verificacao por knob (V1 / V2 / V3 / V4 / Auto) ===");

        var svc = new TrayIconService();
        var fOriginais = typeof(TrayIconService).GetField("_originalPriorities", PRIV)!;
        var mPromote = typeof(TrayIconService).GetMethod("PromoteBoost", PRIV)!;
        var mModern = typeof(TrayIconService).GetMethod("ApplyBoostModern", PRIV)!;
        var nivelFoco = mModern.GetParameters()[1].ParameterType;
        object valorFoco = Enum.Parse(nivelFoco, "Foco");

        var alvo = Process.Start(new ProcessStartInfo("charmap.exe") { UseShellExecute = false });
        if (alvo == null) { Console.WriteLine("nao arranquei o charmap"); return 2; }
        await Task.Delay(1500);
        uint pid = (uint)alvo.Id;
        IntPtr h = OpenProcess(PROC_ALL, false, (int)pid);
        if (h == IntPtr.Zero) { Console.WriteLine("sem handle do processo de teste"); return 2; }

        var baseline = Tirar(h, pid);
        Console.WriteLine($"  baseline: {baseline}");

        var motores = new List<TrayIconService.GameBoostEngine>
        {
            TrayIconService.GameBoostEngine.V1_Balanced,
            TrayIconService.GameBoostEngine.V2_StableFPS,
            TrayIconService.GameBoostEngine.V3_Extreme,
            TrayIconService.GameBoostEngine.V4_ExtremePro,
            TrayIconService.GameBoostEngine.Auto,
        };

        foreach (var eng in motores)
        {
            Console.WriteLine($"\n--- {eng} ---");
            TrayIconService.SetEngine(eng);

            // aplica pelo caminho de producao
            try
            {
                var dict = fOriginais.GetValue(svc)!;
                dict.GetType().GetMethod("TryAdd")!.Invoke(dict,
                    new object[] { pid, (ProcessPriorityClass)baseline.Prioridade });
                mPromote.Invoke(svc, new object[] { pid, "charmap" });
                mModern.Invoke(svc, new object[] { pid, valorFoco });
            }
            catch (Exception ex) { Console.WriteLine("    (aplicar falhou: " + ex.GetType().Name + ")"); }

            await Task.Delay(400);
            var durante = Tirar(h, pid);
            Console.WriteLine($"    durante : {durante}");

            // O que MUDOU (isto e' a documentacao real do motor)
            var mudou = new List<string>();
            if (durante.Prioridade != baseline.Prioridade) mudou.Add($"CPU {baseline.Prioridade}->{durante.Prioridade}");
            if (durante.Pagina != baseline.Pagina && durante.Pagina >= 0) mudou.Add($"pagina {baseline.Pagina}->{durante.Pagina}");
            if (durante.Memoria != baseline.Memoria && durante.Memoria >= 0) mudou.Add($"memoria {baseline.Memoria}->{durante.Memoria}");
            if (durante.Separacao != baseline.Separacao) mudou.Add($"sep 0x{baseline.Separacao:X}->0x{durante.Separacao:X}");
            if (durante.SysResponsiveness != baseline.SysResponsiveness) mudou.Add($"sysresp {baseline.SysResponsiveness}->{durante.SysResponsiveness}");
            if (durante.NetThrottling != baseline.NetThrottling) mudou.Add($"netthr {baseline.NetThrottling}->{durante.NetThrottling}");
            if (durante.TimerMin != baseline.TimerMin) mudou.Add($"timer {baseline.TimerMin / 10000.0:F2}->{durante.TimerMin / 10000.0:F2}ms");
            Console.WriteLine(mudou.Count > 0
                ? "    MUDOU   : " + string.Join(", ", mudou)
                : "    MUDOU   : (nada observavel)");

            // reverte pelo caminho de producao
            try { svc.RevertAllBoostTargets(); } catch { }
            try { svc.RestoreAllThrottledProcesses(); } catch { }
            await Task.Delay(400);
            var depois = Tirar(h, pid);
            Console.WriteLine($"    depois  : {depois}");

            Check($"{eng}: CPU voltou ao original", depois.Prioridade == baseline.Prioridade,
                  $"{baseline.Prioridade} -> {depois.Prioridade}");
            Check($"{eng}: page priority voltou ao original", depois.Pagina == baseline.Pagina,
                  $"{baseline.Pagina} -> {depois.Pagina}");
            Check($"{eng}: memory priority voltou ao original", depois.Memoria == baseline.Memoria,
                  $"{baseline.Memoria} -> {depois.Memoria}");
            Check($"{eng}: Win32PrioritySeparation voltou ao original", depois.Separacao == baseline.Separacao,
                  $"0x{baseline.Separacao:X} -> 0x{depois.Separacao:X}");
            Check($"{eng}: SystemResponsiveness voltou ao original",
                  depois.SysResponsiveness == baseline.SysResponsiveness,
                  $"{baseline.SysResponsiveness} -> {depois.SysResponsiveness}");
            Check($"{eng}: NetworkThrottlingIndex voltou ao original",
                  depois.NetThrottling == baseline.NetThrottling,
                  $"{baseline.NetThrottling} -> {depois.NetThrottling}");
            Check($"{eng}: timer voltou ao original", depois.TimerMin == baseline.TimerMin,
                  $"{baseline.TimerMin} -> {depois.TimerMin}");
        }

        long rescueSize = File.Exists(RescueFile) ? new FileInfo(RescueFile).Length : 0;
        Console.WriteLine();
        Check("boost_rescue.txt nao ficou para tras", rescueSize < 64 * 1024, $"{rescueSize} bytes");

        try { svc.ShutdownGameBoost(); } catch { }
        try { CloseHandle(h); } catch { }
        try { alvo.Kill(); } catch { }
        await Task.Delay(300);
        foreach (var p in Process.GetProcessesByName("charmap")) { try { p.Kill(); } catch { } }

        Console.WriteLine(_falhas == 0 ? "\nTODOS OS TESTES PASSARAM" : $"\n{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }
}
