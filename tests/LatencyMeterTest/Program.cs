// Teste real do medidor de latencia do KitLugia (LatencyAnalyzer).
// Roda duas medicoes (10s + 5s) e imprime TODOS os campos para conferencia manual.
// Modo "deep": LatencyDriverAnalyzer (ETW por driver). Modo "raw": probe cru de StartTraceW.
using System.ComponentModel;
using System.Runtime.InteropServices;
using KitLugia.Core;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && args[0].Equals("deep", StringComparison.OrdinalIgnoreCase))
        {
            await RunDeepAsync();
            return;
        }

        if (args.Length > 0 && args[0].Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            RunRawProbe();
            return;
        }

        Console.WriteLine("============================================================");
        Console.WriteLine(" KitLugia - teste do LatencyAnalyzer (medicao REAL)");
        Console.WriteLine($" CPUs logicas: {Environment.ProcessorCount}");
        Console.WriteLine($" PID deste processo: {Environment.ProcessId}");
        Console.WriteLine("============================================================");

        var m = await LatencyAnalyzer.MeasureLatencyAsync(10);
        Print("MEDICAO 1 (10s)", m);

        var m2 = await LatencyAnalyzer.MeasureLatencyAsync(5);
        Print("MEDICAO 2 (5s)", m2);

        Console.WriteLine();
        Console.WriteLine("Fontes usadas na medicao 1:");
        foreach (var s in m.Sources) Console.WriteLine("  - " + s);
    }

    private static void Print(string title, LatencyAnalyzer.LatencyMeasurement m)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
        Console.WriteLine($"  Despertar: min {Ms(m.MinLatencyUs)} | p50 {Ms(m.P50LatencyUs)} | media {Ms(m.AvgLatencyUs)} | p95 {Ms(m.P95LatencyUs)} | p99 {Ms(m.P99LatencyUs)} | max {Ms(m.MaxLatencyUs)} | desvio {Ms(m.StdDevLatencyUs)}");
        Console.WriteLine($"  Amostras: {m.SampleCount}");
        Console.WriteLine($"  DPC: {m.DpcPercent:F3}% ({m.DpcTimeUs:F0} us/s) | ISR: {m.IsrPercent:F3}% ({m.IsrTimeUs:F0} us/s) | {m.DpcsPerSec:F0} DPCs/s | {m.InterruptsPerSec:F0} interrupcoes/s");
        Console.WriteLine($"  Pior janela ({m.SpikeWindowMs}ms, {m.SpikeWindowCount} janelas): DPC {m.WorstDpcWindowPercent:F3}% | ISR {m.WorstIsrWindowPercent:F3}%");
        Console.WriteLine($"  Hard page faults: {m.HardPageFaultsPerSec:F1}/s (pico {m.MaxHardPageFaultsPerSec:F1}/s)");
        Console.WriteLine($"  Context switches: {m.ContextSwitchesPerSec:F0}/s | Page faults (todas): {m.PageFaultsPerSec:F0}/s");
        Console.WriteLine($"  Timer: atual {m.TimerResolutionMs:F3} ms | menor {m.TimerFinestMs:F3} ms | maior {m.TimerCoarsestMs:F3} ms");
        Console.WriteLine($"  HasDpcData: {m.HasDpcData}");
        if (m.Cpus.Count > 0)
        {
            Console.WriteLine("  CPUs (top 6 por carga DPC+ISR):");
            foreach (var c in m.Cpus.Take(6))
                Console.WriteLine($"    CPU #{c.Index}: DPC {c.DpcPercent:F3}% | ISR {c.IsrPercent:F3}% | {c.InterruptsPerSec:F0} int/s | {c.CurrentMhz:F0}/{c.MaxMhz:F0} MHz");
        }
    }

    private static async Task RunDeepAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine(" LatencyDriverAnalyzer - ETW por driver (DPC/ISR reais)");
        Console.WriteLine(" (precisa de administrador; sem admin deve falhar de forma tratada)");
        Console.WriteLine("============================================================");

        var report = await LatencyDriverAnalyzer.MeasureAsync(5);
        Console.WriteLine($"  Success: {report.Success} | Canceled: {report.Canceled} | Eventos perdidos: {report.EventsLost} | Segundos: {report.Seconds}");
        if (!string.IsNullOrEmpty(report.Error)) Console.WriteLine($"  Erro: {report.Error}");

        if (report.Drivers.Count == 0)
        {
            Console.WriteLine("  (nenhum driver medido)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"Driver",-28} | {"DPC max",-10} | {"DPC total",-11} | {"ISR max",-10} | {"ISR total",-11} | eventos");
        Console.WriteLine(new string('-', 100));
        foreach (var d in report.Drivers.Take(15))
        {
            Console.WriteLine($"  {d.Name,-28} | {d.DpcMaxMs * 1000.0,8:F1}us | {d.DpcTotalMs,9:F1}ms | {d.IsrMaxMs * 1000.0,8:F1}us | {d.IsrTotalMs,9:F1}ms | dpc={d.DpcCount} isr={d.IsrCount}");
        }

        Console.WriteLine();
        foreach (var s in report.Sources) Console.WriteLine("  fonte: " + s);
    }

    private static string Ms(double us) => $"{us / 1000.0:F2} ms";

    // ================= probe cru de StartTraceW (diagnostico do modo ETW) =================

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public ulong TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    private static void RunRawProbe()
    {
        Console.WriteLine("=== Probe CRU de StartTraceW (ETW) ===");
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var p = new System.Security.Principal.WindowsPrincipal(id);
            bool admin = p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Console.WriteLine($"  Usuario: {id.Name} | Administrador: {admin}");
        }

        // SystemTraceProvider GUID (mesmo do TraceEvent):
        var systemTraceProvider = new Guid("9e814aad-3204-11d2-9a82-006008a86939");

        const uint realtime = 0x08000100;   // INDEPENDENT_SESSION | REAL_TIME (como o TraceEvent)
        const uint syslogger = 0x0A000100;  // realtime | SYSTEM_LOGGER_MODE (0x2000000)

        TryStart("1) classico  NT Kernel Logger  flags=0x40  mode=0x08000100 guid=0", "NT Kernel Logger", 0x40, realtime, Guid.Empty);
        TryStart("2) classico  NT Kernel Logger  flags=0x60  mode=0x08000100 guid=0", "NT Kernel Logger", 0x60, realtime, Guid.Empty);
        TryStart("3) classico  NT Kernel Logger  flags=0x40  mode=0x08000100 guid=STP", "NT Kernel Logger", 0x40, realtime, systemTraceProvider);
        TryStart("4) normal    KitLugiaProbe      flags=0     mode=0x08000100 guid=0", "KitLugiaProbe", 0, realtime, Guid.Empty);
        TryStart("5) syslogger KitLugiaProbe      flags=0     mode=0x0A000100 guid=0", "KitLugiaProbe", 0, syslogger, Guid.Empty);
        TryStart("6) syslogger KitLugiaProbe      flags=0x40  mode=0x0A000100 guid=0", "KitLugiaProbe", 0x40, syslogger, Guid.Empty);
        TryStart("7) syslogger KitLugiaProbe      flags=0x40  mode=0x0A000100 guid=STP", "KitLugiaProbe", 0x40, syslogger, systemTraceProvider);
        TryStart("8) syslogger NT Kernel Logger  flags=0x40  mode=0x0A000100 guid=STP", "NT Kernel Logger", 0x40, syslogger, systemTraceProvider);
    }

    private static void TryStart(string label, string name, uint enableFlags, uint logFileMode, Guid guid)
    {
        int size = Marshal.SizeOf<EventTraceProperties>();
        int total = size + 4096 + 256;
        IntPtr props = Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);

            var p = new EventTraceProperties
            {
                // mesmo setup do TraceEvent.GetProperties (realtime, sem arquivo)
                Wnode = new WnodeHeader { BufferSize = (uint)total, Flags = 0x20000, ClientContext = 1, Guid = guid },
                BufferSize = 64,
                MinimumBuffers = 64,
                MaximumBuffers = 64 * 5 / 4 + 10,
                LogFileMode = logFileMode,
                FlushTimer = 1,
                EnableFlags = enableFlags,
                LoggerNameOffset = (uint)size,
                LogFileNameOffset = (uint)size + 2048
            };
            Marshal.StructureToPtr(p, props, false);
            Marshal.Copy((name + "\0").ToCharArray(), 0, IntPtr.Add(props, size), name.Length + 1);

            int err = StartTraceW(out ulong handle, name, props);
            string msg = err == 0 ? "OK" : new Win32Exception(err).Message;
            Console.WriteLine($"  {label} -> err={err} ({msg})");
            if (err == 0)
            {
                int stopErr = ControlTraceW(handle, null, props, 1); // 1 = EVENT_TRACE_CONTROL_STOP
                Console.WriteLine($"      sessao iniciada! stop(handle)={stopErr}");
            }
        }
        finally { Marshal.FreeHGlobal(props); }
    }
}
