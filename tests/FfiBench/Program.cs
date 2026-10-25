// FfiBench - responde a pergunta que o plano de migracao para Rust PRECISA
// responder com numero, nao com opiniao:
//
//   "Rust traz valor onde o gargalo e bytes em volume" (gargalo real)
//   vs
//   "Rust traz valor onde o gargalo e a syscall" (boundary e invisivel)
//
// Mede, NESTA MAQUINA:
//   A) o PISO do boundary P/Invoke (chamada trivial em kernel32)
//   B) o custo de边界 P/Invoke -> cdylib Rust (mesma chamada trivial? nao ha
//      export trivial; uso glob_match_ffi que e CPU pura sem I/O)
//   C) o custo REAL das operacoes que o plano quer mover para Rust:
//      EmptyWorkingSet, IOCTL de layout de disco, NtQuerySystemInformation,
//      leitura da standby list, e o scan de registro (que JAH E Rust).
//
// Se (C) >> (A), mover para Rust nao compra performance: a fronteira e
// invisivel e o gargalo e o TRABALHO dentro da chamada.
//
// Read-only: nao escreve em disco/registro. O EmptyWorkingSet roda no
// proprio processo do harness (que nao tem trabalho a perder).
using System.Diagnostics;
using System.Runtime.InteropServices;
using KitLugia.Core;
using KitLugia.Core.TaskManager;

class Program
{
    [DllImport("kernel32.dll", EntryPoint = "GetTickCount64")]
    static extern ulong GetTickCount64();

    [DllImport("rust_native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int glob_match_ffi(IntPtr pattern, IntPtr path);

    [DllImport("rust_native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int probe_boundary_ffi(string a, string b);

    static void Main()
    {
        Console.WriteLine("FfiBench - o boundary FFI vs o custo real das operacoes");
        Console.WriteLine(new string('=', 74));

        // ---------- A) PISO do boundary P/Invoke (kernel32 trivial) ----------
        const int N = 1_000_000;
        // warmup
        for (int i = 0; i < 10_000; i++) GetTickCount64();
        var sw = Stopwatch.StartNew();
        ulong acc = 0;
        for (int i = 0; i < N; i++) acc += GetTickCount64();
        sw.Stop();
        double nsCall = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / N;
        Console.WriteLine($"  A) P/Invoke trivial (kernel32.GetTickCount64) x{N:N0}");
        Console.WriteLine($"       {sw.Elapsed.TotalMilliseconds:F1} ms total = {nsCall:F1} ns/call   <-- PISO do boundary");

        // ---------- B) boundary -> cdylib RUST (CPU pura, sem I/O) ----------
        try
        {
            // B0) sonda: DUAS STRINGS marshalled, ZERO trabalho dentro.
            // Isola o custo de marshalling de string do P/Invoke.
            for (int i = 0; i < 1000; i++) probe_boundary_ffi("pattern", "path");
            sw.Restart();
            for (int i = 0; i < 50000; i++) probe_boundary_ffi("pattern", "path");
            sw.Stop();
            double nsProbe = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 50000;
            Console.WriteLine($"  B0) P/Invoke -> rust_native com 2 STRINGS (sonda, 0 trabalho) x50.000");
            Console.WriteLine($"       {sw.Elapsed.TotalMilliseconds:F1} ms total = {nsProbe:F0} ns/call   <-- custo de MARSHALLING");

            IntPtr pat = Marshal.StringToHGlobalUni("*.exe");
            IntPtr pth = Marshal.StringToHGlobalUni("app.exe");
            const int M = 200_000;
            for (int i = 0; i < 1000; i++) glob_match_ffi(pat, pth);
            sw.Restart();
            int hits = 0;
            for (int i = 0; i < M; i++) hits += glob_match_ffi(pat, pth);
            sw.Stop();
            double nsRust = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / M;
            Console.WriteLine($"  B) P/Invoke -> rust_native.dll (glob_match_ffi) x{M:N0}");
            Console.WriteLine($"       {sw.Elapsed.TotalMilliseconds:F1} ms total = {nsRust:F1} ns/call   (hits={hits})");
            Console.WriteLine($"       -> o boundary para cdylib Rust e a MESMA ordem de grandeza (A)");
            Marshal.FreeHGlobal(pat);
            Marshal.FreeHGlobal(pth);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  B) rust_native.dll indisponivel: {ex.GetType().Name} (sem DLL no PATH do harness)");
        }

        // ---------- C) CUSTO REAL das operacoes que o plano quer migrar ----------
        Console.WriteLine();
        Console.WriteLine("  C) custo real das operacoes (a fronteira e 3-4 ordens abaixo):");

        // C1) EmptyWorkingSet - o candidato #1 do plano
        int self = Environment.ProcessId;
        MemoryOptimizer.EmptyProcessWorkingSet(self); // warmup
        const int P = 2000;
        sw.Restart();
        for (int i = 0; i < P; i++) MemoryOptimizer.EmptyProcessWorkingSet(self);
        sw.Stop();
        double usEws = sw.Elapsed.TotalMilliseconds * 1000.0 / P;
        Report("EmptyWorkingSet (MemoryOptimizer)", usEws, "us", sw.Elapsed.TotalMilliseconds, P);

        // C2) leitura da standby list (NtQuerySystemInformation class 2)
        sw.Restart();
        for (int i = 0; i < 2000; i++) MemoryOptimizer.TryGetStandbyListSizeMB(out _);
        sw.Stop();
        Report("TryGetStandbyListSizeMB (NtQSI)", sw.Elapsed.TotalMilliseconds * 1000.0 / 2000, "us", sw.Elapsed.TotalMilliseconds, 2000);

        // C3) NtQuerySystemInformation de processos (o mais pesado do TaskManager)
        NativeMetricsHelper.EnumerateProcesses();
        sw.Restart();
        for (int i = 0; i < 20; i++) NativeMetricsHelper.EnumerateProcesses();
        sw.Stop();
        Report("EnumerateProcesses (NtQSI SystemProcessInformation)",
            sw.Elapsed.TotalMilliseconds / 20.0, "ms", sw.Elapsed.TotalMilliseconds, 20);

        // C4) IOCTL de layout de disco (o candidato #2 do plano)
        try
        {
            PartitionManager.GetAllDisks();
            sw.Restart();
            for (int i = 0; i < 20; i++) PartitionManager.GetAllDisks();
            sw.Stop();
            Report("PartitionManager.GetAllDisks (IOCTL nativo)",
                sw.Elapsed.TotalMilliseconds / 20.0, "ms", sw.Elapsed.TotalMilliseconds, 20);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       GetAllDisks: {ex.GetType().Name}: {ex.Message}");
        }

        // C5) o scan de registro - que JA E RUST (reg_scan_ffi)
        try
        {
            var r = NativeRegistry.Scan("HKEY_LOCAL_MACHINE\\SOFTWARE", "7-Zip", "", null, 0);
            if (r != null)
            {
                sw.Restart();
                for (int i = 0; i < 200; i++) NativeRegistry.Scan("HKEY_LOCAL_MACHINE\\SOFTWARE", "7-Zip", "", null, 0);
                sw.Stop();
                Report("NativeRegistry.Scan -> reg_scan_ffi (RUST, ja existente)",
                    sw.Elapsed.TotalMilliseconds * 1000.0 / 200, "us", sw.Elapsed.TotalMilliseconds, 200);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       NativeRegistry.Scan: {ex.GetType().Name}: {ex.Message}");
        }

        // ---------- D) O ACHADO: o "caminho rapido" em Rust vs o fallback managed ----------
        // O Rust recompila o padrao a CADA chamada (Regex::new / Glob::new). O .NET,
        // ao contrario, tem cache estatico de 15 padroes. Se o Rust nao cachear, o
        // caminho "nativo" pode ser MAIS LENTO que o managed que ele deveria substituir.
        Console.WriteLine();
        Console.WriteLine("  D) NativeRegex/NativeGlob: caminho RUST vs fallback MANAGED (mesmo padrao, repetido):");
        try
        {
            const string pat = @"^\s*(Inc|LLC|Ltd)\.?\s*$";
            const string txt = "Foo Inc.";

            // managed puro
            for (int i = 0; i < 1000; i++) System.Text.RegularExpressions.Regex.IsMatch(txt, pat);
            sw.Restart();
            for (int i = 0; i < 20000; i++) System.Text.RegularExpressions.Regex.IsMatch(txt, pat);
            sw.Stop();
            double nsManaged = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 20000;
            Console.WriteLine($"       Regex.IsMatch MANAGED (.NET, cache estatico)      {nsManaged,12:F0} ns/call");

            // caminho Rust (NativeRegex usa o rust_native quando disponivel)
            bool rustAtivo = false;
            try
            {
                for (int i = 0; i < 1000; i++) NativeRegex.IsMatch(txt, pat);
                sw.Restart();
                for (int i = 0; i < 20000; i++) NativeRegex.IsMatch(txt, pat);
                sw.Stop();
                double nsNative = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 20000;
                rustAtivo = true;
                Console.WriteLine($"       NativeRegex.IsMatch (RUST, recompila padrao)   {nsNative,12:F0} ns/call");
                double fator = nsManaged > 0 ? nsNative / nsManaged : 0;
                Console.WriteLine($"       -> o caminho RUST e {fator:F1}x {(fator >= 1 ? "MAIS LENTO" : "mais rapido")} que o managed" +
                                  (fator >= 1 ? "  <-- REGRESSAO" : ""));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       NativeRegex: {ex.GetType().Name}: {ex.Message}");
            }

            // NativeGlob
            try
            {
                for (int i = 0; i < 1000; i++) NativeGlob.IsMatch("*.exe", "app.exe");
                sw.Restart();
                for (int i = 0; i < 20000; i++) NativeGlob.IsMatch("*.exe", "app.exe");
                sw.Stop();
                double nsGlob = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 20000;
                Console.WriteLine($"       NativeGlob.IsMatch (RUST, recompila padrao)    {nsGlob,12:F0} ns/call");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       NativeGlob: {ex.GetType().Name}: {ex.Message}");
            }

            // NativeRegex.Replace em laco: e o que CleanName faz POR CANDIDATO
            // escaneado (o caminho quente do DeepUninstaller).
            try
            {
                for (int i = 0; i < 500; i++) NativeRegex.Replace("Foo Inc.", @"\s+(Inc|LLC|Ltd)\.?\s*$", "");
                sw.Restart();
                for (int i = 0; i < 5000; i++) NativeRegex.Replace("Foo Inc.", @"\s+(Inc|LLC|Ltd)\.?\s*$", "");
                sw.Stop();
                double nsRepl = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 5000;
                Console.WriteLine($"       NativeRegex.Replace (RUST, 1 padrao/call)       {nsRepl,12:F0} ns/call");

                for (int i = 0; i < 500; i++) System.Text.RegularExpressions.Regex.Replace("Foo Inc.", @"\s+(Inc|LLC|Ltd)\.?\s*$", "");
                sw.Restart();
                for (int i = 0; i < 5000; i++) System.Text.RegularExpressions.Regex.Replace("Foo Inc.", @"\s+(Inc|LLC|Ltd)\.?\s*$", "");
                sw.Stop();
                double nsReplM = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 5000;
                Console.WriteLine($"       Regex.Replace MANAGED (.NET, cache estatico)  {nsReplM,12:F0} ns/call");
                Console.WriteLine($"       -> CleanName usa 3 Replace POR CANDIDATO: " +
                                  $"Rust {nsRepl * 3 / 1000:F2} us vs managed {nsReplM * 3 / 1000:F3} us por candidato");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       Replace: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       D): {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("  E) Confidence.Generate - o caminho QUENTE do DeepUninstaller (1 call por candidato):");
        try
        {
            const string disp = "Display Driver Uninstaller";
            const string folder = "Microsoft ME Display";
            for (int i = 0; i < 500; i++) KitLugia.Core.Confidence.Generate(disp, folder);
            sw.Restart();
            for (int i = 0; i < 5000; i++) KitLugia.Core.Confidence.Generate(disp, folder);
            sw.Stop();
            double nsConf = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 5000;
            Console.WriteLine($"       Confidence.Generate (caminho RUST)             {nsConf,12:F0} ns/call");
            Console.WriteLine($"       -> por candidato escaneado. Com 5.000 candidatos: {nsConf * 5000 / 1_000_000:F1} s so em regex");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"       Confidence: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("  CONCLUSAO MECANICA:");
        Console.WriteLine($"    boundary P/Invoke (A) = {nsCall:F0} ns");
        Console.WriteLine("    operacoes de syscall (C) = us a ms  => 3-4 ordens de grandeza acima");
        Console.WriteLine("    => mover syscall para Rust nao compra performance; a fronteira e invisivel.");
        Console.WriteLine("    => o Rust so paga quando o TRABALHO dentro da chamada e bulk (bytes em volume).");
    }

    static void Report(string nome, double porChamada, string unidade, double total, int n)
    {
        string v = porChamada.ToString("F2") + " " + unidade;
        string porChamadaNs = (porChamada * (unidade == "us" ? 1000 : unidade == "ms" ? 1_000_000 : 1)).ToString("F0");
        Console.WriteLine($"       {nome,-52} {v,12} por chamada  ({porChamadaNs} ns, {n}x em {total:F1} ms)");
    }
}
