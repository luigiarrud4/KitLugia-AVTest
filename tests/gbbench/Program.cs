using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using KitLugia.GUI.Services;

// ===========================================================================
// gbbench — BENCHMARK DOS MOTORES DO GAMEBOOST
// ===========================================================================
// Pergunta que responde: "os motores DAO ganho mensuravel, e qual?"
//
// Como medir honestamente:
//   1. PRIORIDADE SO IMPORTA SOB CONTENCAO. Numa maquina ociosa, High e Normal
//      dao exatamente o mesmo throughput. Por isso o harness cria CARGA DE FUNDO
//      (threads a 100%) e compara com a maquina ociosa. Um boost que so ganha na
//      maquina carregada e' o ganho real.
//   2. TRABALHO SINTETICO, NAO UM JOGO. Isto medeContentencao de CPU (o que a
//      prioridade altera). NAO mede GPU, render, I/O de disco ou frame pacing.
//      Um jogo GPU-bound nao vera ganho aqui — e honesto dizer isso.
//   3. CAMINHO REAL. O boost e' aplicado por CheckForegroundWindow, o mesmo
//      metodo que o SetWinEventHook e o polling de 250 ms chamam. Nada de
//      chamar ApplyBoostV4 diretamente.
//   4. ORDEM INTERCALADA. As repeticoes alternam a ordem dos motores para
//      cancelar deriva termica/turbo da maquina.
//
// Metricas por corrida:
//   FPS         - quadros (lotes de trabalho) por segundo
//   p99         - 99o percentil do tempo de quadro (proxy de 1% low / stutter)
//   CPU%        - fracao de CPU do processo (GetProcessTimes): share real do scheduler
// ===========================================================================

internal static class Program
{
    // ---------- P/Invoke ----------
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out FT c, out FT e, out FT k, out FT u);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct FT { public uint Lo, Hi; }
    delegate bool EnumProc(IntPtr h, IntPtr p);

    // ---------- configuracao ----------
    const int ITERS_PER_FRAME = 40_000;   // trabalho por "quadro"
    static double CpuSeconds()
    {
        if (!GetProcessTimes(Process.GetCurrentProcess().Handle, out _, out _, out var k, out var u))
            return 0;
        return ((k.Hi << 32 | k.Lo) + (u.Hi << 32 | u.Lo)) / 1e7;
    }

    // Trabalho sintetico: mistura de inteiros (branchy) e double (FMA).
    // Nao e' otimizavel para fora — o resultado alimenta o proximo passo.
    static double Work(double x, uint seed)
    {
        for (int k = 0; k < ITERS_PER_FRAME; k++)
        {
            seed ^= seed << 13; seed ^= seed >>> 17; seed ^= seed << 5;
            x = Math.Sqrt(x * 1.0000001 + (seed & 1023));
            if (x > 1e9) x = 1.0;
        }
        return x;
    }

    // =====================================================================
    // MODO WORKER: processo separado = a "aplicacao em foreground"
    // =====================================================================
    static int RunWorker(string outFile, string goFile, int warmupMs, int measureMs)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 0;
        app.Startup += (s, e) =>
        {
            var w = new Window
            {
                Width = 420, Height = 260, Title = "gbbench-worker",
                WindowStyle = WindowStyle.ToolWindow, ShowInTaskbar = true,
                Left = 80, Top = 80
            };
            w.Show();
            w.Activate();

            // Espera o "go". O StartUp handler corre no thread do dispatcher; um
            // Dispatcher.PushFrame aqui aninhava mal e nao deixava o startup terminar.
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(goFile) && DateTime.UtcNow < until)
                Thread.Sleep(25);

            if (!File.Exists(goFile))
            {
                File.WriteAllText(outFile, "erro=timeout_go");
                app.Shutdown(3);
                return;
            }

            double warmCpu0 = CpuSeconds(), warmWall0 = DateTime.UtcNow.Ticks;
            Thread.Sleep(warmupMs);
            double warmCpu = CpuSeconds() - warmCpu0;
            double warmWall = (DateTime.UtcNow.Ticks - warmWall0) / 1e7;

            // ---- MEDICAO ----
            var frameMs = new List<double>();
            double cpu0 = CpuSeconds();
            long wall0 = Stopwatch.GetTimestamp();
            double acc = 1.0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < measureMs)
            {
                var f0 = Stopwatch.GetTimestamp();
                acc = Work(acc, 0x9E3779B1);
                double ms = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency;
                frameMs.Add(ms);
                if (acc > 1e12) acc = 1.0;
            }
            double wall = sw.Elapsed.TotalSeconds;
            double cpu = CpuSeconds() - cpu0;

            frameMs.Sort();
            int frames = frameMs.Count;
            var res = new StringBuilder();
            res.Append("frames=").Append(frames).Append(';');
            res.Append("wall=").Append(wall.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
            res.Append("fps=").Append((frames / wall).ToString("F2", CultureInfo.InvariantCulture)).Append(';');
            res.Append("cpu=").Append(cpu.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
            res.Append("cpuPct=").Append((wall > 0 ? cpu / wall * 100 : 0).ToString("F1", CultureInfo.InvariantCulture)).Append(';');
            res.Append("warmCpu=").Append(warmCpu.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
            res.Append("warmWall=").Append(warmWall.ToString("F4", CultureInfo.InvariantCulture)).Append(';');
            if (frames > 0)
            {
                res.Append("avgMs=").Append(frameMs.Average().ToString("F3", CultureInfo.InvariantCulture)).Append(';');
                res.Append("p50=").Append(frameMs[(int)(frames * 0.50)].ToString("F3", CultureInfo.InvariantCulture)).Append(';');
                res.Append("p99=").Append(frameMs[Math.Min(frames - 1, (int)(frames * 0.99))].ToString("F3", CultureInfo.InvariantCulture)).Append(';');
                res.Append("maxMs=").Append(frameMs[frames - 1].ToString("F3", CultureInfo.InvariantCulture)).Append(';');
            }
            File.WriteAllText(outFile, res.ToString());
            app.Shutdown(exit);
        };
        app.Run();
        return exit;
    }

    // =====================================================================
    // MODO ORQUESTRADOR
    // =====================================================================
    static bool TryForeground(IntPtr hwnd, string label)
    {
        AllowSetForegroundWindow(-1);
        GetWindowThreadProcessId(hwnd, out uint want);
        for (int i = 0; i < 8; i++)
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
            Thread.Sleep(350);
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            if (fg == want) return true;
        }
        Console.WriteLine($"      [aviso] nao consegui trazer {label} para foreground");
        return false;
    }

    static IntPtr FindVisibleWindow(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, p) =>
        {
            GetWindowThreadProcessId(h, out uint w);
            if (w == pid && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static ProcessPriorityClass Prio(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.PriorityClass; }
        catch { return ProcessPriorityClass.High; } // morto -> irrelevante
    }

    // carga de fundo: N threads a 100%
    static List<Thread> _load = new();
    static volatile bool _loadOn = false;
    static void StartLoad(int threads)
    {
        _loadOn = true;
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(() =>
            {
                double x = 1.0; int s = i * 7919 + 13;
                while (_loadOn)
                {
                    for (int k = 0; k < 20_000; k++)
                    {
                        s ^= s << 13; s ^= s >>> 17; s ^= s << 5;
                        x = x * 1.0000001 + (s & 255);
                        if (x > 1e9) x = 1.0;
                    }
                }
                GC.KeepAlive(x);
            })
            { IsBackground = true };
            t.Start();
            _load.Add(t);
        }
    }
    static void StopLoad() { _loadOn = false; }

    class Medida
    {
        public string Motor;
        public double Fps, P99, CpuPct, AvgMs;
        public ProcessPriorityClass Prio;
    }

    static Medida RunOnce(TrayIconService svc, string workerExe, string motor,
                          int warmupMs, int measureMs, string dir)
    {
        string outFile = Path.Combine(dir, "out.txt");
        string goFile = Path.Combine(dir, "go.txt");
        if (File.Exists(outFile)) File.Delete(outFile);
        if (File.Exists(goFile)) File.Delete(goFile);

        var psi = new ProcessStartInfo(workerExe,
            $"--work \"{outFile}\" \"{goFile}\" {warmupMs} {measureMs}")
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(workerExe)! };
        var w = Process.Start(psi)!;

        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(200); hwnd = FindVisibleWindow((uint)w.Id); }
        if (hwnd == IntPtr.Zero) { try { w.Kill(); } catch { } return null; }

        TryForeground(hwnd, "worker");

        // ---- boost pelo CAMINHO REAL ----
        if (motor == "NORMAL")
        {
            try { using var p = Process.GetProcessById(w.Id); p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        }
        else
        {
            TrayIconService.ClearCustomEngine();
            TrayIconService.SetEngine(int.Parse(motor));
            svc.GamePriorityEnabled = true;
            // o cooldown de 50 ms impede re-aplicacao seguida; forca uma janela limpa
            typeof(TrayIconService).GetField("_boostCooldown", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(svc, TimeSpan.Zero);
            typeof(TrayIconService).GetField("_lastBoostTime", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(svc, DateTime.MinValue);
            typeof(TrayIconService).GetMethod("CheckForegroundWindow", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(svc, null);
        }

        var prio = Prio(w.Id);
        File.WriteAllText(goFile, "1");

        string content = "";
        for (int i = 0; i < 200 && !File.Exists(outFile); i++) { Thread.Sleep(100); w.WaitForExit(100); }
        try { content = File.ReadAllText(outFile); } catch { }
        try { if (!w.HasExited) w.Kill(); } catch { }
        if (content.Length == 0) return null;

        var d = content.Split(';')
                        .Where(x => x.Contains('='))
                        .Select(x => x.Split('='))
                        .ToDictionary(x => x[0], x => x[1]);
        return new Medida
        {
            Motor = motor,
            Fps = double.Parse(d["fps"], CultureInfo.InvariantCulture),
            P99 = double.Parse(d["p99"], CultureInfo.InvariantCulture),
            AvgMs = double.Parse(d["avgMs"], CultureInfo.InvariantCulture),
            CpuPct = double.Parse(d["cpuPct"], CultureInfo.InvariantCulture),
            Prio = prio
        };
    }

    static void Table(string titulo, List<Medida> ms, Medida? baseM)
    {
        Console.WriteLine($"\n=== {titulo} ===");
        if (ms.Count == 0) { Console.WriteLine("  (sem medicoes)"); return; }
        Console.WriteLine($"  {"Motor",-10} {"prio",-12} {"FPS",9} {"vs base",9} {"p99 ms",9} {"CPU%",7}  n");
        foreach (var grp in ms.GroupBy(x => x.Motor).OrderBy(g => g.Key))
        {
            var l = grp.ToList();
            double fps = Median(l.Select(x => x.Fps));
            double p99 = Median(l.Select(x => x.P99));
            double cpu = Median(l.Select(x => x.CpuPct));
            string rel = baseM != null && baseM.Fps > 0
                ? (((fps / baseM.Fps - 1) * 100) >= 0 ? "+" : "") +
                  ((fps / baseM.Fps - 1) * 100).ToString("F2", CultureInfo.InvariantCulture) + "%"
                : "     -";
            Console.WriteLine($"  {grp.Key,-10} {l[0].Prio.ToString(),-12} {fps,9:F1} {rel,9} {p99,9:F2} {cpu,7:F1}  {l.Count}");
        }
    }

    static double Median(IEnumerable<double> v)
    {
        var l = v.OrderBy(x => x).ToList();
        if (l.Count == 0) return 0;
        return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
    }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // O atalho/apphost pode entrar como MTA; a WPF exige STA. Forca sempre.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            int r = 0;
            var t = new Thread(() => r = MainSTA(args).GetAwaiter().GetResult());
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            return r;
        }
        return await MainSTA(args);
    }

    private static async Task<int> MainSTA(string[] args)
    {
        if (args.Length > 0 && args[0] == "--work")
            return RunWorker(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]));

        string asmPath = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        string selfDir = Path.GetDirectoryName(asmPath)!;
        // Sob "dotnet run" o entry assembly e' o .dll e o processo filho nao encontra o
        // runtimeconfig (da o FileNotFoundException de System.Runtime). O apphost .exe e'
        // auto-suficiente: preferimos sempre o .exe com o mesmo nome base.
        string selfExe = Path.Combine(selfDir,
            Path.GetFileNameWithoutExtension(asmPath) + ".exe");
        string self = File.Exists(selfExe) ? selfExe : Environment.ProcessPath!;
        Console.WriteLine($" exe = {self}");
        Console.WriteLine($" dir = {selfDir}");
        string dir = Path.Combine(Path.GetTempPath(), "gbbench_run");
        Directory.CreateDirectory(dir);

        int warmupMs = 1000, measureMs = 2500, reps = 3;
        var svc = new TrayIconService();
        TrayIconService.ClearCustomEngine();

        // motores: 5=Auto, 4=V4, 3=V3, 2=V2, 1=V1 (+ NORMAL sem boost como base)
        string[] motores = { "NORMAL", "5", "4", "3", "2", "1" };
        string Nome(string m) => m switch
        {
            "NORMAL" => "NORMAL", "5" => "Auto", "4" => "V4", "3" => "V3", "2" => "V2", _ => "V1"
        };

        Console.WriteLine("=================================================================");
        Console.WriteLine(" gbbench — benchmark dos motores GameBoost");
        Console.WriteLine($" CPUs = {Environment.ProcessorCount} | repeticoes = {reps} | medicao = {measureMs} ms");
        Console.WriteLine(" Metodologia: caminho REAL (CheckForegroundWindow); ordem intercalada;");
        Console.WriteLine(" carga sintetica de CPU (nao e' um jogo: nao mede GPU/IO).");
        Console.WriteLine("=================================================================");

        int cpus = Environment.ProcessorCount;
        var resultados = new List<Medida>();

        int cargaMult = args.Length > 0 ? int.Parse(args[0]) : 2;
        Console.WriteLine($" multiplicador de carga = {cargaMult}x CPUs ({cpus * cargaMult} threads)");
        foreach (var cenario in new[] { ($"CARGA{cargaMult}x", cpus * cargaMult), ("OCIOSA", 0) })
        {
            var (nome, threads) = cenario;
            Console.WriteLine($"\n########## CENARIO: {nome} (threads de fundo = {threads}) ##########");
            _load.Clear();
            if (threads > 0) StartLoad(threads);
            await Task.Delay(1500); // deixa a carga assentar

            var ms = new List<Medida>();
            for (int r = 0; r < reps; r++)
            {
                // intercalado: rep par na ordem, rep impar invertida
                var ordem = r % 2 == 0 ? motores : motores.Reverse().ToArray();
                foreach (var m in ordem)
                {
                    var med = RunOnce(svc, self, m, warmupMs, measureMs, dir);
                    if (med == null) { Console.WriteLine($"  [aviso] corrida {Nome(m)} falhou"); continue; }
                    ms.Add(med);
                    Console.WriteLine($"  rep{r + 1} {Nome(m),-7} prio={med.Prio,-12} fps={med.Fps,8:F1} p99={med.P99,7:F2}ms cpu={med.CpuPct,6:F1}%");
                }
            }
            StopLoad();
            _load.Clear();

            var baseM = ms.Where(x => x.Motor == "NORMAL").ToList();
            Medida? b = baseM.Count > 0
                ? new Medida { Motor = "NORMAL", Fps = Median(baseM.Select(x => x.Fps)) }
                : null;
            Table(nome, ms, b);
            resultados.AddRange(ms);
            await Task.Delay(800);
        }

        // estado final: nada left behind
        svc.RevertCurrentBoost();
        // NUNCA matar o proprio processo: o nome do worker E' o mesmo ("gbbench"),
        // e matava-se a si mesmo no fim (exit 127 e o relatorio final nunca saia).
        int meuPid = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == meuPid) { p.Dispose(); continue; }
                if (p.ProcessName.Equals("gbbench", StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch { }
            finally { p.Dispose(); }
        }

        Console.WriteLine("\n=================================================================");
        Console.WriteLine(" RESULTADO GLOBAL (mediana de todas as corridas, por motor)");
        Console.WriteLine("=================================================================");
        var gBase = resultados.Where(x => x.Motor == "NORMAL").ToList();
        double gf = gBase.Count > 0 ? Median(gBase.Select(x => x.Fps)) : 0;
        foreach (var grp in resultados.GroupBy(x => x.Motor))
        {
            double fps = Median(grp.Select(x => x.Fps));
            double p99 = Median(grp.Select(x => x.P99));
            double cpu = Median(grp.Select(x => x.CpuPct));
            Console.WriteLine($"  {grp.Key,-8} prio={grp.First().Prio,-12} FPS={fps,8:F1} ({(gf > 0 ? (fps / gf - 1) * 100 : 0),+6:F1}%)  p99={p99,7:F2}ms  CPU={cpu,6:F1}%");
        }
        Console.WriteLine("=================================================================");
        Console.WriteLine(" LEITURA: 'vs base' negativo = PIOR que nao fazer nada. CPU% mostra");
        Console.WriteLine(" quanto o scheduler deu ao processo. Prioridade so muda algo SOB CARGA.");
        return 0;
    }
}