using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Mede latência DPC/ISR e analisa performance do sistema em tempo real
    /// Similar ao LatencyMon mas integrado ao KitLugia
    /// </summary>
    public static class LatencyAnalyzer
    {
        #region Windows API Imports

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);

        [DllImport("ntdll.dll")]
        private static extern int NtPowerInformation(int InformationLevel, IntPtr InputBuffer, uint InputBufferLength, IntPtr OutputBuffer, uint OutputBufferLength);

        // SystemInformationClass usados (ntexapi.h / Geoff Chappell):
        //  2  = SystemPerformanceInformation          -> PageFaultCount (page faults do sistema)
        //  8  = SystemProcessorPerformanceInformation  -> DpcTime/InterruptTime reais por CPU (100ns)
        //  23 = SystemInterruptInformation             -> ContextSwitches / DpcCount por CPU
        private const int SystemPerformanceInformation = 2;
        private const int SystemProcessorPerformanceInformation = 8;
        private const int SystemInterruptInformation = 23;
        private const int SystemProcessorInformation = 11;

        /// <summary>Janela (ms) da amostragem de spikes de DPC/ISR (pior janela).</summary>
        private const int SpikeWindowMs = 250;

        /// <summary>
        /// SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION - 48 bytes por CPU.
        /// DpcTime/InterruptTime sao tempo ACUMULADO real, em unidades de 100ns.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPerformance
        {
            public long IdleTime;
            public long KernelTime;     // inclui idle
            public long UserTime;
            public long DpcTime;        // 100ns acumulados em DPC
            public long InterruptTime;  // 100ns acumulados em ISR
            public ulong InterruptCount;
        }

        /// <summary>
        /// SYSTEM_INTERRUPT_INFORMATION - 24 bytes por CPU. Campos usados:
        /// ContextSwitches (0x00), DpcCount (0x04) e TimeIncrement (0x0C).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct InterruptInfo
        {
            public uint ContextSwitches;
            public uint DpcCount;
            public uint DpcRate;
            public uint TimeIncrement;   // 100ns entre ticks do timer
            public uint DpcBypassCount;
            public uint ApcBypassCount;
        }

        /// <summary>
        /// PROCESSOR_POWER_INFORMATION (24 bytes por CPU): velocidade ATUAL/MAXIMA do processador,
        /// lida via NtPowerInformation(SystemProcessorInformation) - mostra throttling real.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPower
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        #endregion

        #region Data Models

        /// <summary>
        /// Resultado de UMA medicao. Todo campo vem de fonte real do Windows -
        /// nao existe valor estimado, simulado ou randomico aqui.
        /// </summary>
        public class LatencyMeasurement
        {
            public DateTime Timestamp { get; set; }

            // --- Latencia de despertar: sonda real com QueryPerformanceCounter ---
            public double CurrentLatencyUs { get; set; }   // ultima amostra
            public double MinLatencyUs { get; set; }
            public double AvgLatencyUs { get; set; }       // media aparada (5% de cada ponta)
            public double P95LatencyUs { get; set; }
            public double MaxLatencyUs { get; set; }
            public double StdDevLatencyUs { get; set; }
            public int SampleCount { get; set; }

            // --- DPC/ISR reais (NtQuerySystemInformation) ---
            public double DpcTimeUs { get; set; }          // microssegundos de DPC por segundo de relogio
            public double IsrTimeUs { get; set; }          // microssegundos de ISR por segundo de relogio
            public double DpcPercent { get; set; }         // % do tempo total de CPU gasto em DPC
            public double IsrPercent { get; set; }         // % do tempo total de CPU gasto em ISR
            public double DpcsPerSec { get; set; }
            public double InterruptsPerSec { get; set; }
            public double ContextSwitchesPerSec { get; set; }

            // --- Sistema ---
            public double PageFaultsPerSec { get; set; }     // page faults/s do sistema (todas)
            public double HardPageFaultsPerSec { get; set; } // paginas lidas do DISCO p/ resolver hard faults
            public double MaxHardPageFaultsPerSec { get; set; }
            public double TimerResolutionMs { get; set; }    // resolucao ATUAL do timer do sistema
            public double TimerFinestMs { get; set; }        // menor espera possivel
            public double TimerCoarsestMs { get; set; }      // maior espera possivel

            // --- Percentis extras da sonda de despertar ---
            public double P50LatencyUs { get; set; }         // mediana
            public double P99LatencyUs { get; set; }

            // --- Pior janela (spike) de DPC/ISR: equivalente ao "highest reported" do LatencyMon ---
            public double WorstDpcWindowPercent { get; set; }
            public double WorstIsrWindowPercent { get; set; }
            public int SpikeWindowMs { get; set; }
            public int SpikeWindowCount { get; set; }

            public List<CpuLatencyInfo> Cpus { get; set; } = new();
            public List<string> Sources { get; set; } = new();
            public bool HasDpcData { get; set; }
        }

        /// <summary>Carga real de DPC/ISR por processador logico.</summary>
        public class CpuLatencyInfo
        {
            public int Index { get; set; }
            public double DpcPercent { get; set; }
            public double IsrPercent { get; set; }
            public double InterruptsPerSec { get; set; }
            public double CurrentMhz { get; set; }   // velocidade agora (NtPowerInformation)
            public double MaxMhz { get; set; }       // maxima suportada
        }

        public class SystemLatencyReport
        {
            public LatencyMeasurement Baseline { get; set; } = new();
            public LatencyMeasurement Optimized { get; set; } = new();
            public double ImprovementPercentage { get; set; }
            public string Recommendation { get; set; } = "";
            public Dictionary<string, string> SuggestedTweaks { get; set; } = new();
        }

        /// <summary>Snapshot cru dos contadores do kernel (antes/depois da medicao).</summary>
        private sealed class RawSnapshot
        {
            public ProcessorPerformance[] Cpus { get; set; } = Array.Empty<ProcessorPerformance>();
            public InterruptInfo[] Intr { get; set; } = Array.Empty<InterruptInfo>();
            public long PageFaults { get; set; } = -1;
            public bool HasPerf { get; set; }
            public bool HasIntr { get; set; }
        }

        #endregion

        #region Measurement Methods

        private static long _frequency;
        private static bool _hasFrequency;
        private const int MaxJitterSamples = 20000;

        static LatencyAnalyzer()
        {
            _hasFrequency = QueryPerformanceFrequency(out _frequency) && _frequency > 0;
        }

        /// <summary>
        /// A sonda nao guarda estado entre medicoes (tudo e local). Mantido publico de
        /// proposito: o limpador do Kit chama este metodo em vez de acessar campos
        /// privados por reflection (como fazia com _latencySamples/_driverStats).
        /// </summary>
        public static void ReleaseBuffers() { }

        /// <summary>
        /// Mede a latencia REAL do sistema em dois eixos:
        /// 1) latencia de despertar - sonda QPC com Thread.Sleep(1) em thread propria;
        /// 2) carga de DPC/ISR - contadores acumulados do kernel (NtQuerySystemInformation).
        /// Tambem le a resolucao atual do timer (NtQueryTimerResolution).
        /// </summary>
        public static async Task<LatencyMeasurement> MeasureLatencyAsync(int durationSeconds = 10, CancellationToken cancellationToken = default)
        {
            if (durationSeconds < 1) durationSeconds = 1;

            var measurement = new LatencyMeasurement { Timestamp = DateTime.Now };
            measurement.Sources.Add("Despertar: sonda QPC (QueryPerformanceCounter + Thread.Sleep(1))");

            var before = TakeSnapshot();
            var wall = Stopwatch.StartNew();

            // Amostrador paralelo: pior janela (spike) de DPC/ISR + hard page faults reais.
            using var spikeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var spikeTask = SampleSpikesAsync(durationSeconds, spikeCts.Token);

            var samples = await ProbeWakeLatencyAsync(durationSeconds, cancellationToken).ConfigureAwait(false);

            wall.Stop();
            spikeCts.Cancel();
            var spikes = await spikeTask.ConfigureAwait(false);
            var after = TakeSnapshot();

            FillJitterStats(measurement, samples);
            FillCounterStats(measurement, before, after, wall.Elapsed);
            FillSpikeStats(measurement, spikes);
            ReadCpuSpeeds(measurement);
            ReadTimerResolution(measurement);

            Logger.Log($"Latencia medida (real): despertar media={measurement.AvgLatencyUs:F1}us p95={measurement.P95LatencyUs:F1}us max={measurement.MaxLatencyUs:F1}us | " +
                       $"DPC={measurement.DpcPercent:F2}% ISR={measurement.IsrPercent:F2}% | timer={measurement.TimerResolutionMs:F3}ms | amostras={measurement.SampleCount}");

            return measurement;
        }

        /// <summary>
        /// Sonda de latencia de despertar: pede Sleep(1) e mede com QPC quanto o SO
        /// realmente levou para acordar a thread. Roda em thread dedicada com
        /// prioridade AboveNormal (nunca RealTime: poderia travar o sistema).
        /// </summary>
        private static Task<List<double>> ProbeWakeLatencyAsync(int seconds, CancellationToken cancellationToken)
        {
            var samples = new List<double>(4096);
            if (!_hasFrequency) return Task.FromResult(samples);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<List<double>>(cancellationToken);

            // TaskCompletionSource em vez de Task.Run + thread.Join: a sonda roda em thread
            // dedicada e nenhuma thread do pool fica bloqueada esperando ela terminar.
            var tcs = new TaskCompletionSource<List<double>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
                catch { Logger.LogWarning("LatencyProbe", "Nao foi possivel elevar a prioridade da sonda"); }

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds && !cancellationToken.IsCancellationRequested && samples.Count < MaxJitterSamples)
                {
                    QueryPerformanceCounter(out long start);
                    Thread.Sleep(1);
                    QueryPerformanceCounter(out long end);

                    double us = (end - start) * 1_000_000.0 / _frequency;
                    if (us > 0 && us < 5_000_000) samples.Add(us);
                }

                if (cancellationToken.IsCancellationRequested) tcs.TrySetCanceled(cancellationToken);
                else tcs.TrySetResult(samples);
            })
            { IsBackground = true, Name = "KitLugia.LatencyProbe" };

            thread.Start();
            return tcs.Task;
        }

        private static void FillJitterStats(LatencyMeasurement m, List<double> samples)
        {
            m.SampleCount = samples.Count;
            if (samples.Count == 0) return;

            m.MinLatencyUs = samples.Min();
            m.MaxLatencyUs = samples.Max();
            m.CurrentLatencyUs = samples[^1];

            var sorted = samples.OrderBy(x => x).ToList();
            m.P50LatencyUs = sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * 0.50))];
            m.P95LatencyUs = sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * 0.95))];
            m.P99LatencyUs = sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * 0.99))];

            // Media aparada: descarta 5% de cada ponta (picos de agendamento do proprio Windows).
            int cut = (int)(sorted.Count * 0.05);
            var trimmed = sorted.Count > cut * 2 ? sorted.Skip(cut).Take(sorted.Count - cut * 2).ToList() : sorted;
            m.AvgLatencyUs = trimmed.Average();

            double mean = m.AvgLatencyUs;
            m.StdDevLatencyUs = Math.Sqrt(trimmed.Sum(v => (v - mean) * (v - mean)) / trimmed.Count);
        }

        private static void FillCounterStats(LatencyMeasurement m, RawSnapshot a, RawSnapshot b, TimeSpan wall)
        {
            double secs = wall.TotalSeconds;
            if (secs <= 1e-6) return;

            if (a.HasPerf && b.HasPerf && a.Cpus.Length > 0 && a.Cpus.Length == b.Cpus.Length)
            {
                int n = a.Cpus.Length;
                double dpcTicks = 0, isrTicks = 0, interrupts = 0;
                double cpuWallTicks = secs * 10_000_000.0;   // 1s = 10.000.000 unidades de 100ns por CPU
                var cpus = new List<CpuLatencyInfo>(n);

                for (int i = 0; i < n; i++)
                {
                    double dpc = Math.Max(0, b.Cpus[i].DpcTime - a.Cpus[i].DpcTime);
                    double isr = Math.Max(0, b.Cpus[i].InterruptTime - a.Cpus[i].InterruptTime);
                    double intr = Math.Max(0, (double)b.Cpus[i].InterruptCount - a.Cpus[i].InterruptCount);

                    dpcTicks += dpc;
                    isrTicks += isr;
                    interrupts += intr;

                    cpus.Add(new CpuLatencyInfo
                    {
                        Index = i,
                        DpcPercent = cpuWallTicks > 0 ? dpc / cpuWallTicks * 100.0 : 0,
                        IsrPercent = cpuWallTicks > 0 ? isr / cpuWallTicks * 100.0 : 0,
                        InterruptsPerSec = intr / secs
                    });
                }

                double totalWallTicks = cpuWallTicks * n;
                m.DpcPercent = totalWallTicks > 0 ? dpcTicks / totalWallTicks * 100.0 : 0;
                m.IsrPercent = totalWallTicks > 0 ? isrTicks / totalWallTicks * 100.0 : 0;
                m.DpcTimeUs = dpcTicks / 10.0 / secs;   // 100ns -> us, por segundo de relogio
                m.IsrTimeUs = isrTicks / 10.0 / secs;
                m.InterruptsPerSec = interrupts / secs;
                m.Cpus = cpus.OrderByDescending(c => c.DpcPercent + c.IsrPercent).ToList();
                m.HasDpcData = true;
                m.Sources.Add("DPC/ISR reais: NtQuerySystemInformation(SystemProcessorPerformanceInformation)");
            }

            if (a.HasIntr && b.HasIntr && a.Intr.Length > 0 && a.Intr.Length == b.Intr.Length)
            {
                double dpcCount = 0, ctx = 0;
                for (int i = 0; i < a.Intr.Length; i++)
                {
                    dpcCount += Math.Max(0, (double)b.Intr[i].DpcCount - a.Intr[i].DpcCount);
                    ctx += Math.Max(0, (double)b.Intr[i].ContextSwitches - a.Intr[i].ContextSwitches);
                }
                m.DpcsPerSec = dpcCount / secs;
                m.ContextSwitchesPerSec = ctx / secs;
                m.Sources.Add("DPCs/context switches: NtQuerySystemInformation(SystemInterruptInformation)");
            }

            if (a.PageFaults >= 0 && b.PageFaults >= a.PageFaults)
            {
                m.PageFaultsPerSec = (b.PageFaults - a.PageFaults) / secs;
                m.Sources.Add("Page faults: NtQuerySystemInformation(SystemPerformanceInformation)");
            }
        }

        /// <summary>Agregado do amostrador de spikes (pior janela + hard faults).</summary>
        private sealed class SpikeStats
        {
            public double WorstDpcPercent { get; set; }
            public double WorstIsrPercent { get; set; }
            public int Windows { get; set; }
            public double HardFaultSum { get; set; }
            public int HardFaultSamples { get; set; }
            public double HardFaultMax { get; set; }
        }

        /// <summary>
        /// Amostra os contadores de DPC/ISR a cada <see cref="SpikeWindowMs"/> ms durante a medicao para
        /// capturar a PIOR janela (spike) - o equivalente do "highest reported DPC/ISR execution time"
        /// do LatencyMon no nivel que da para medir sem driver proprio. Tambem le os hard page faults
        /// reais (Memory\Pages Input/sec = paginas lidas do disco para resolver falhas).
        /// </summary>
        private static async Task<SpikeStats> SampleSpikesAsync(int seconds, CancellationToken cancellationToken)
        {
            var stats = new SpikeStats();
            PerformanceCounter? hardFaults = null;
            try
            {
                try
                {
                    hardFaults = new PerformanceCounter("Memory", "Pages Input/sec");
                    hardFaults.NextValue(); // primeira leitura e sempre 0: serve so para armar o contador
                }
                catch (Exception ex)
                {
                    hardFaults?.Dispose();
                    hardFaults = null;
                    Logger.LogWarning("LatencyHardFaults", $"PerformanceCounter Pages Input/sec indisponivel: {ex.Message}");
                }

                var prev = TakeSnapshot();
                var sw = Stopwatch.StartNew();
                double prevMs = 0;
                while (!cancellationToken.IsCancellationRequested && sw.Elapsed.TotalSeconds < seconds)
                {
                    try { await Task.Delay(SpikeWindowMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }

                    double nowMs = sw.Elapsed.TotalMilliseconds;
                    double winSec = (nowMs - prevMs) / 1000.0;
                    prevMs = nowMs;

                    var now = TakeSnapshot();
                    if (winSec > 0.05 && prev.HasPerf && now.HasPerf && prev.Cpus.Length > 0 && prev.Cpus.Length == now.Cpus.Length)
                    {
                        int n = prev.Cpus.Length;
                        double wallTicks = winSec * 10_000_000.0;
                        double dpc = 0, isr = 0;
                        for (int i = 0; i < n; i++)
                        {
                            dpc += Math.Max(0, now.Cpus[i].DpcTime - prev.Cpus[i].DpcTime);
                            isr += Math.Max(0, now.Cpus[i].InterruptTime - prev.Cpus[i].InterruptTime);
                        }
                        double dpcPct = wallTicks > 0 ? dpc / (wallTicks * n) * 100.0 : 0;
                        double isrPct = wallTicks > 0 ? isr / (wallTicks * n) * 100.0 : 0;
                        if (dpcPct > stats.WorstDpcPercent) stats.WorstDpcPercent = dpcPct;
                        if (isrPct > stats.WorstIsrPercent) stats.WorstIsrPercent = isrPct;
                        stats.Windows++;
                    }

                    if (hardFaults != null)
                    {
                        double v = hardFaults.NextValue();
                        stats.HardFaultSum += v;
                        stats.HardFaultSamples++;
                        if (v > stats.HardFaultMax) stats.HardFaultMax = v;
                    }

                    prev = now;
                }
            }
            catch (Exception ex) { Logger.LogWarning("LatencySpikes", ex.Message); }
            finally { hardFaults?.Dispose(); }

            return stats;
        }

        private static void FillSpikeStats(LatencyMeasurement m, SpikeStats s)
        {
            m.SpikeWindowMs = SpikeWindowMs;
            m.SpikeWindowCount = s.Windows;
            m.WorstDpcWindowPercent = s.WorstDpcPercent;
            m.WorstIsrWindowPercent = s.WorstIsrPercent;
            if (s.Windows > 0)
                m.Sources.Add($"Spikes de DPC/ISR: pior janela de {SpikeWindowMs}ms (NtQuerySystemInformation)");
            if (s.HardFaultSamples > 0)
            {
                m.HardPageFaultsPerSec = s.HardFaultSum / s.HardFaultSamples;
                m.MaxHardPageFaultsPerSec = s.HardFaultMax;
                m.Sources.Add("Hard page faults: PerformanceCounter(Memory\\Pages Input/sec)");
            }
        }

        private static void ReadCpuSpeeds(LatencyMeasurement m)
        {
            try
            {
                int cpus = Math.Max(1, Environment.ProcessorCount);
                int size = Marshal.SizeOf<ProcessorPower>();
                IntPtr buf = Marshal.AllocHGlobal(size * cpus);
                try
                {
                    if (NtPowerInformation(SystemProcessorInformation, IntPtr.Zero, 0, buf, (uint)(size * cpus)) == 0)
                    {
                        for (int i = 0; i < cpus; i++)
                        {
                            var info = Marshal.PtrToStructure<ProcessorPower>(IntPtr.Add(buf, i * size));
                            var cpu = m.Cpus.FirstOrDefault(c => c.Index == (int)info.Number);
                            if (cpu != null && info.MaxMhz > 0)
                            {
                                cpu.CurrentMhz = info.CurrentMhz;
                                cpu.MaxMhz = info.MaxMhz;
                            }
                        }
                        m.Sources.Add("Velocidade da CPU: NtPowerInformation(SystemProcessorInformation)");
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { Logger.LogWarning("LatencyCpuSpeed", ex.Message); }
        }

        private static void ReadTimerResolution(LatencyMeasurement m)
        {
            try
            {
                if (NtQueryTimerResolution(out uint min, out uint max, out uint cur) == 0)
                {
                    // Nomes da API sao invertidos em relacao a intuicao:
                    // MinimumResolution = maior espera (15.625ms tipico);
                    // MaximumResolution = menor espera (0.5ms tipico).
                    m.TimerCoarsestMs = min / 10000.0;
                    m.TimerFinestMs = max / 10000.0;
                    m.TimerResolutionMs = cur / 10000.0;
                    m.Sources.Add("Resolucao do timer: NtQueryTimerResolution");
                }
            }
            catch (Exception ex) { Logger.LogWarning("LatencyTimer", ex.Message); }
        }

        private static RawSnapshot TakeSnapshot()
        {
            var snap = new RawSnapshot();
            int cpus = Math.Max(1, Environment.ProcessorCount);

            try
            {
                int size = Marshal.SizeOf<ProcessorPerformance>();
                IntPtr buf = Marshal.AllocHGlobal(size * cpus);
                try
                {
                    if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, size * cpus, out int ret) == 0 && ret >= size)
                    {
                        int got = Math.Min(ret / size, cpus);
                        var arr = new ProcessorPerformance[got];
                        for (int i = 0; i < got; i++) arr[i] = Marshal.PtrToStructure<ProcessorPerformance>(IntPtr.Add(buf, i * size));
                        snap.Cpus = arr;
                        snap.HasPerf = got > 0;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { Logger.LogWarning("LatencyPerfInfo", ex.Message); }

            try
            {
                int size = Marshal.SizeOf<InterruptInfo>();
                IntPtr buf = Marshal.AllocHGlobal(size * cpus);
                try
                {
                    if (NtQuerySystemInformation(SystemInterruptInformation, buf, size * cpus, out int ret) == 0 && ret >= size)
                    {
                        int got = Math.Min(ret / size, cpus);
                        var arr = new InterruptInfo[got];
                        for (int i = 0; i < got; i++) arr[i] = Marshal.PtrToStructure<InterruptInfo>(IntPtr.Add(buf, i * size));
                        snap.Intr = arr;
                        snap.HasIntr = got > 0;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { Logger.LogWarning("LatencyIntrInfo", ex.Message); }

            try
            {
                // SYSTEM_PERFORMANCE_INFORMATION: PageFaultCount fica no offset 60
                // (4 LARGE_INTEGER + 7 ULONG antes dele) e e um contador de 32 bits.
                IntPtr buf = Marshal.AllocHGlobal(512);
                try
                {
                    if (NtQuerySystemInformation(SystemPerformanceInformation, buf, 512, out int ret) == 0 && ret >= 64)
                        snap.PageFaults = Marshal.ReadInt32(IntPtr.Add(buf, 60)) & 0xFFFFFFFFL;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { Logger.LogWarning("LatencyPageFaults", ex.Message); }

            return snap;
        }

        #endregion

        #region Analysis & Recommendations

        public static async Task<SystemLatencyReport> GenerateOptimizationReportAsync(CancellationToken cancellationToken = default)
        {
            var report = new SystemLatencyReport();

            Logger.Log("Iniciando análise de latência do sistema...");

            // Mede baseline (estado atual)
            report.Baseline = await MeasureLatencyAsync(10, cancellationToken);

            // Analisa hardware para recomendações
            var hardwareProfile = AnalyzeHardware();
            
            // Gera recomendações personalizadas
            report.SuggestedTweaks = GenerateTweakRecommendations(report.Baseline, hardwareProfile);
            report.Recommendation = GenerateRecommendationText(report.Baseline, hardwareProfile);

            Logger.Log($"Analise completa (real). Despertar medio: {report.Baseline.AvgLatencyUs:F2}us | DPC: {report.Baseline.DpcPercent:F2}% | ISR: {report.Baseline.IsrPercent:F2}% | timer: {report.Baseline.TimerResolutionMs:F2}ms");

            return report;
        }

        private static HardwareProfile AnalyzeHardware()
        {
            var profile = new HardwareProfile();

            try
            {
                // Detecta CPU
                using (var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            profile.CpuName = obj["Name"]?.ToString() ?? "Unknown";
                            profile.CpuCores = Convert.ToInt32(obj["NumberOfCores"]);
                            break;
                        }
                    }
                }

                // Detecta GPU
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            string gpuName = obj["Name"]?.ToString() ?? "Unknown";
                            if (profile.GpuName == null)
                            {
                                profile.GpuName = gpuName;
                                profile.HasNvidia = gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                                profile.HasAmd = gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || 
                                               gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
                                profile.HasIntel = gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                }

                // Detecta RAM
                using (var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory"))
                using (var results = searcher.Get())
                {
                    ulong totalRam = 0;
                    foreach (ManagementObject obj in results)
                    {
                        using (obj)
                        {
                            totalRam += Convert.ToUInt64(obj["Capacity"]);
                        }
                    }
                    profile.TotalRamGB = (int)(totalRam / (1024 * 1024 * 1024));
                }

                // Detecta versão do Windows
                try
                {
                    profile.WindowsVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "Unknown")?.ToString() ?? "Unknown";
                }
                catch
                {
                    profile.WindowsVersion = "Unknown";
                }
                // Windows 11 = build 22000+. API atual (OperatingSystem.*) em vez de
                // ler Environment.OSVersion e comparar build a mao.
                profile.IsWindows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

                Logger.Log($"Hardware detectado: {profile.CpuName}, {profile.GpuName}, {profile.TotalRamGB}GB RAM");
            }
            catch (Exception ex)
            {
                Logger.LogError("AnalyzeHardware", ex.Message);
            }

            return profile;
        }

        private static Dictionary<string, string> GenerateTweakRecommendations(LatencyMeasurement baseline, HardwareProfile hardware)
        {
            var tweaks = new Dictionary<string, string>();

            // Timer Coalescing: so vale desligar quando a carga REAL de DPC e relevante.
            tweaks["TimerCoalescing"] = baseline.DpcPercent > 0.5 ? "AGGRESSIVE_0" : "DEFAULT";

            // Win32PrioritySeparation pelo numero real de cores logicos.
            tweaks["Win32PrioritySeparation"] = hardware.CpuCores >= 8 ? "0x26"
                                              : hardware.CpuCores >= 4 ? "0x1A"
                                              : "0x18";

            tweaks["CoreParking"] = hardware.CpuCores >= 6 ? "DISABLE" : "ENABLE";
            tweaks["SystemResponsiveness"] = "0";

            // GPU detectada de verdade (Win32_VideoController), nao por lista simulada de drivers.
            if (hardware.HasNvidia) tweaks["NvidiaOptimizations"] = "APPLY";
            if (hardware.IsWindows11) tweaks["Win11Optimizations"] = "APPLY";

            return tweaks;
        }

        private static string GenerateRecommendationText(LatencyMeasurement baseline, HardwareProfile hardware)
        {
            var rec = new List<string>();
            double wakeMs = baseline.AvgLatencyUs / 1000.0;

            if (!baseline.HasDpcData)
                rec.Add("⚠️ Nao foi possivel ler os contadores de DPC/ISR do kernel - parte da analise ficou indisponivel.");

            // O numero que domina a responsividade e a quantizacao do timer.
            if (baseline.TimerResolutionMs >= 15.0)
            {
                rec.Add($"🔴 Resolucao do timer em {baseline.TimerResolutionMs:F2} ms (padrao do Windows): a thread so acorda no proximo tick, entao o despertar fica preso em ~{wakeMs:F2} ms.");
                rec.Add($"💡 O Windows suporta ate {baseline.TimerFinestMs:F2} ms. Ativar 'Global Timer Resolution Requests' (perfil CONSERVADOR ou acima) derruba a latencia de resposta de entrada.");
            }
            else if (baseline.TimerResolutionMs > 1.0)
            {
                rec.Add($"🟡 Timer em {baseline.TimerResolutionMs:F2} ms (alguem ja pediu alta resolucao no sistema). Despertar medido: {wakeMs:F2} ms.");
            }
            else
            {
                rec.Add($"✅ Timer em {baseline.TimerResolutionMs:F2} ms e despertar em {wakeMs:F2} ms - responsividade excelente.");
            }

            if (baseline.DpcPercent >= 1.0)
                rec.Add($"⚠️ DPC consumindo {baseline.DpcPercent:F2}% da CPU ({baseline.DpcTimeUs:F0} us/s com {baseline.InterruptsPerSec:F0} interrupcoes/s): driver de dispositivo pesado no caminho. Atualize GPU/chipset/rede.");
            else if (baseline.DpcPercent > 0.25)
                rec.Add($"ℹ️ DPC em {baseline.DpcPercent:F2}% da CPU - normal, mas vale acompanhar.");

            if (baseline.Cpus.Count > 0)
            {
                var worst = baseline.Cpus[0];
                if (worst.DpcPercent + worst.IsrPercent > 1.0)
                    rec.Add($"⚠️ CPU #{worst.Index} concentra a maior carga (DPC {worst.DpcPercent:F2}% + ISR {worst.IsrPercent:F2}%) - interrupcoes presas em um unico core.");
            }

            if (baseline.HardPageFaultsPerSec > 50)
                rec.Add($"⚠️ Hard page faults: {baseline.HardPageFaultsPerSec:F0}/s (pico {baseline.MaxHardPageFaultsPerSec:F0}/s) - falhas que vao AO DISCO adicionam latencia real; considere mais RAM ou aliviar o disco.");
            if (baseline.WorstDpcWindowPercent >= 2.0 || baseline.WorstIsrWindowPercent >= 2.0)
                rec.Add($"⚠️ Pico de DPC {baseline.WorstDpcWindowPercent:F1}% / ISR {baseline.WorstIsrWindowPercent:F1}% em janela de {baseline.SpikeWindowMs}ms - um driver travou o core por um instante (spike).");

            if (baseline.SampleCount < 20)
                rec.Add("ℹ️ Poucas amostras de despertar - rode a analise novamente fora de um jogo/compilacao.");

            if (hardware.TotalRamGB > 0 && hardware.TotalRamGB < 16)
                rec.Add("💡 RAM abaixo de 16 GB - evite desativar paging.");

            return string.Join("\n", rec);
        }

        #endregion

        #region Profile Classes

        private class HardwareProfile
        {
            public string CpuName { get; set; } = "";
            public int CpuCores { get; set; }
            public string GpuName { get; set; } = "";
            public bool HasNvidia { get; set; }
            public bool HasAmd { get; set; }
            public bool HasIntel { get; set; }
            public int TotalRamGB { get; set; }
            public string WindowsVersion { get; set; } = "";
            public bool IsWindows11 { get; set; }
        }

        #endregion

        #region Auto-Optimization

        public static async Task<(bool Success, string Message, LatencyMeasurement After)> AutoOptimizeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Logger.Log("Iniciando otimização automática baseada em análise...");

                // Gera relatório
                var report = await GenerateOptimizationReportAsync(cancellationToken);

                // Aplica tweaks recomendados
                int appliedCount = 0;
                var results = new List<string>();

                foreach (var tweak in report.SuggestedTweaks)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        switch (tweak.Key)
                        {
                            case "TimerCoalescing":
                                if (tweak.Value == "AGGRESSIVE_0")
                                {
                                    SystemTweaks.DisableTimerCoalescing();
                                    appliedCount++;
                                }
                                break;

                            case "CoreParking":
                                if (tweak.Value == "DISABLE")
                                {
                                    SystemTweaks.DisableCoreParking();
                                    appliedCount++;
                                }
                                break;

                            case "SystemResponsiveness":
                                SystemTweaks.OptimizeSystemResponsiveness();
                                appliedCount++;
                                break;

                            case "Win11Optimizations":
                                if (tweak.Value == "APPLY")
                                {
                                    SystemTweaks.OptimizeEnergySaver();
                                    appliedCount++;
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add($"❌ {tweak.Key}: {ex.Message}");
                    }
                }

                // Aguarda sistema estabilizar
                await Task.Delay(2000, cancellationToken);

                // Mede novamente
                var afterMeasurement = await MeasureLatencyAsync(5, cancellationToken);

                double improvement = report.Baseline.AvgLatencyUs > 0
                    ? ((report.Baseline.AvgLatencyUs - afterMeasurement.AvgLatencyUs) / report.Baseline.AvgLatencyUs) * 100
                    : 0;
                
                string message = $"Otimização concluída!\n\n" +
                               $"Tweaks aplicados: {appliedCount}\n" +
                               $"Latência antes: {report.Baseline.AvgLatencyUs:F2}µs\n" +
                               $"Latência depois: {afterMeasurement.AvgLatencyUs:F2}µs\n" +
                               $"Melhoria: {improvement:F1}%\n" +
                               $"DPC: {afterMeasurement.DpcPercent:F2}% | ISR: {afterMeasurement.IsrPercent:F2}% | timer: {afterMeasurement.TimerResolutionMs:F2}ms\n\n" +
                               $"Recomendações:\n{report.Recommendation}";

                Logger.Log($"Auto-otimização concluída. Melhoria: {improvement:F1}%");

                return (true, message, afterMeasurement);
            }
            catch (Exception ex)
            {
                Logger.LogError("AutoOptimize", ex.Message);
                return (false, $"Erro na otimização automática: {ex.Message}", new LatencyMeasurement());
            }
        }

        #endregion

        #region Intelligent Benchmark

        public class BenchmarkProfile
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public int Win32Priority { get; set; }
            public bool DisableCoreParking { get; set; }
            public bool DisableTimerCoalescing { get; set; }
            public bool OptimizeInputQueue { get; set; }
            public bool EnableGlobalTimer { get; set; }
            public bool OptimizeSystemResponsiveness { get; set; }
            public bool DisableNetworkThrottling { get; set; }
            public bool DisableGdiScaling { get; set; }
            public bool DisablePowerThrottling { get; set; }
        }

        public class BenchmarkResult
        {
            public BenchmarkProfile Profile { get; set; } = new();
            public LatencyMeasurement Measurement { get; set; } = new();
            public double Score { get; set; }
            public bool IsStable { get; set; }
        }

        public class SystemStateSnapshot
        {
            public bool CoreParking { get; set; }
            public bool TimerCoalescing { get; set; }
            public bool InputQueue { get; set; }
            public bool GlobalTimer { get; set; }
            public bool SystemResponsiveness { get; set; }
            public bool NetworkThrottling { get; set; }
            public bool GdiScaling { get; set; }
            public bool PowerThrottling { get; set; }
            public int Win32Priority { get; set; }
        }

        private static SystemStateSnapshot SaveCurrentState()
        {
            var status = SystemTweaks.CheckGamingLatencyStatus();
            return new SystemStateSnapshot
            {
                CoreParking = status["CoreParking"],
                TimerCoalescing = status["TimerCoalescing"],
                InputQueue = status["InputQueue"],
                GlobalTimer = status["GlobalTimerResolution"],
                SystemResponsiveness = status["SystemResponsiveness"],
                NetworkThrottling = SystemTweaks.IsNetworkThrottlingDisabled(),
                GdiScaling = SystemTweaks.IsGdiScalingDisabled(),
                PowerThrottling = SystemTweaks.IsPowerThrottlingDisabled(),
                Win32Priority = GetCurrentWin32Priority()
            };
        }

        private static int GetCurrentWin32Priority()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 24);
                return value is int intVal ? intVal : 24;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 24; }
        }

        private static void RestoreState(SystemStateSnapshot state)
        {
            try
            {
                // Restore Core Parking
                if (state.CoreParking)
                    SystemTweaks.DisableCoreParking();
                else
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583", true);
                    key?.SetValue("Attributes", 1, RegistryValueKind.DWord);
                }

                // Restore Timer Coalescing
                if (!state.TimerCoalescing)
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true);
                    key?.DeleteValue("CoalescingTimerInterval", false);
                }

                // Restore Input Queue
                if (!state.InputQueue)
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 100, RegistryValueKind.DWord);
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 100, RegistryValueKind.DWord);
                }

                // Restore Global Timer
                if (!state.GlobalTimer)
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "GlobalTimerResolutionRequests", 0, RegistryValueKind.DWord);
                }

                // Restore System Responsiveness
                if (!state.SystemResponsiveness)
                {
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, RegistryValueKind.DWord);
                }

                // Restore Win32Priority
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", state.Win32Priority, RegistryValueKind.DWord);

                Logger.Log("Estado do sistema restaurado");
            }
            catch (Exception ex)
            {
                Logger.LogError("RestoreState", ex.Message);
            }
        }

        private static void ResetToDefaults()
        {
            try
            {
                // Reverte todos os tweaks para padrão
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\0cc5b647-c1df-4637-891a-dec35c318583", true))
                    key?.SetValue("Attributes", 1, RegistryValueKind.DWord);

                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true))
                    key?.DeleteValue("CoalescingTimerInterval", false);

                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", 100, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", 100, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "GlobalTimerResolutionRequests", 0, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, RegistryValueKind.DWord);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 24, RegistryValueKind.DWord);

                Logger.Log("Sistema resetado para valores padrão");
            }
            catch (Exception ex)
            {
                Logger.LogError("ResetToDefaults", ex.Message);
            }
        }

        private static void ApplyProfile(BenchmarkProfile profile)
        {
            try
            {
                // Win32Priority
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", profile.Win32Priority, RegistryValueKind.DWord);

                // Core Parking
                if (profile.DisableCoreParking)
                    SystemTweaks.DisableCoreParking();

                // Timer Coalescing
                if (profile.DisableTimerCoalescing)
                    SystemTweaks.DisableTimerCoalescing();

                // Input Queue
                if (profile.OptimizeInputQueue)
                    SystemTweaks.OptimizeInputQueue();

                // Global Timer
                if (profile.EnableGlobalTimer)
                    Registry.SetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);

                // System Responsiveness
                if (profile.OptimizeSystemResponsiveness)
                    SystemTweaks.OptimizeSystemResponsiveness();

                // Network Throttling
                if (profile.DisableNetworkThrottling)
                    SystemTweaks.DisableNetworkThrottling();

                // GDI Scaling
                if (profile.DisableGdiScaling)
                    SystemTweaks.DisableGdiScaling();

                // Power Throttling
                if (profile.DisablePowerThrottling)
                    SystemTweaks.DisablePowerThrottling();

                Logger.Log($"Perfil '{profile.Name}' aplicado");
            }
            catch (Exception ex)
            {
                Logger.LogError($"ApplyProfile_{profile.Name}", ex.Message);
            }
        }

        public static async Task<(bool Success, List<BenchmarkResult> Results, BenchmarkResult Best, string Report)> RunIntelligentBenchmarkAsync(
            IProgress<string>? progress = null, 
            CancellationToken cancellationToken = default)
        {
            var results = new List<BenchmarkResult>();
            SystemStateSnapshot? originalState = null;

            try
            {
                progress?.Report("Salvando estado atual...");
                originalState = SaveCurrentState();

                // Define perfis de teste
                var profiles = new List<BenchmarkProfile>
                {
                    new BenchmarkProfile
                    {
                        Name = "PADRÃO",
                        Description = "Configurações originais do Windows",
                        Win32Priority = 24,
                        DisableCoreParking = false,
                        DisableTimerCoalescing = false,
                        OptimizeInputQueue = false,
                        EnableGlobalTimer = false,
                        OptimizeSystemResponsiveness = false,
                        DisableNetworkThrottling = false,
                        DisableGdiScaling = false,
                        DisablePowerThrottling = false
                    },
                    new BenchmarkProfile
                    {
                        Name = "CONSERVADOR",
                        Description = "Otimizações seguras, mínimo risco",
                        Win32Priority = 26,
                        DisableCoreParking = true,
                        DisableTimerCoalescing = false,
                        OptimizeInputQueue = true,
                        EnableGlobalTimer = true,
                        OptimizeSystemResponsiveness = true,
                        DisableNetworkThrottling = false,
                        DisableGdiScaling = false,
                        DisablePowerThrottling = false
                    },
                    new BenchmarkProfile
                    {
                        Name = "EQUILIBRADO",
                        Description = "Balanceamento performance/estabilidade",
                        Win32Priority = 38,
                        DisableCoreParking = true,
                        DisableTimerCoalescing = true,
                        OptimizeInputQueue = true,
                        EnableGlobalTimer = true,
                        OptimizeSystemResponsiveness = true,
                        DisableNetworkThrottling = true,
                        DisableGdiScaling = true,
                        DisablePowerThrottling = true
                    },
                    new BenchmarkProfile
                    {
                        Name = "AGRESSIVO",
                        Description = "Máxima performance, tweaks avançados",
                        Win32Priority = 42,
                        DisableCoreParking = true,
                        DisableTimerCoalescing = true,
                        OptimizeInputQueue = true,
                        EnableGlobalTimer = true,
                        OptimizeSystemResponsiveness = true,
                        DisableNetworkThrottling = true,
                        DisableGdiScaling = true,
                        DisablePowerThrottling = true
                    }
                };

                // Testa cada perfil
                foreach (var profile in profiles)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    progress?.Report($"Testando perfil: {profile.Name}...");
                    
                    // Reseta para padrão e aplica perfil
                    ResetToDefaults();
                    await Task.Delay(500, cancellationToken); // Aguarda estabilizar
                    ApplyProfile(profile);
                    await Task.Delay(1000, cancellationToken); // Aguarda aplicar

                    // Mede performance
                    var measurement = await MeasureLatencyAsync(8, cancellationToken);
                    
                    // Score real: latencia de despertar (menor = melhor) atenuada pela
                    // carga de DPC/ISR medida na MESMA janela (maior = pior).
                    double baseScore = 1_000_000.0 / (measurement.AvgLatencyUs + 50.0);
                    double loadFactor = 1.0 + (measurement.DpcPercent + measurement.IsrPercent) / 100.0 * 10.0;
                    double score = baseScore / loadFactor;
                    // Estavel = amostras suficientes + variacao relativa baixa (CV < 1.0).
                    bool isStable = measurement.SampleCount >= 20 && measurement.AvgLatencyUs > 0 &&
                                    (measurement.StdDevLatencyUs / measurement.AvgLatencyUs) < 1.0;

                    var result = new BenchmarkResult
                    {
                        Profile = profile,
                        Measurement = measurement,
                        Score = score,
                        IsStable = isStable
                    };

                    results.Add(result);
                    progress?.Report($"{profile.Name}: despertar {measurement.AvgLatencyUs:F1}us | DPC {measurement.DpcPercent:F2}% | ISR {measurement.IsrPercent:F2}% (Score: {score:F0})");
                    
                    Logger.Log($"Benchmark {profile.Name}: despertar={measurement.AvgLatencyUs:F2}us, DPC={measurement.DpcPercent:F2}%, ISR={measurement.IsrPercent:F2}%, timer={measurement.TimerResolutionMs:F2}ms, Score={score:F2}, Estavel={isStable}");
                }

                // Cancelado no meio do caminho: nao escolhe perfil nenhum e devolve o
                // estado que o usuario tinha antes (antes aplicava um 'melhor' parcial).
                if (cancellationToken.IsCancellationRequested || results.Count == 0)
                {
                    if (originalState != null) RestoreState(originalState);
                    return (false, results, new BenchmarkResult(), "Benchmark cancelado - configuracoes originais restauradas.");
                }

                // Encontra o melhor perfil (maior score entre os estaveis)
                var validResults = results.Where(r => r.IsStable).ToList();
                if (validResults.Count == 0) validResults = results; // Se nenhum estavel, usa todos

                var best = validResults.OrderByDescending(r => r.Score).First();

                // Gera relatório
                var report = GenerateBenchmarkReport(results, best, originalState);

                // Restaura o melhor perfil
                progress?.Report($"Aplicando melhor perfil: {best.Profile.Name}...");
                ResetToDefaults();
                await Task.Delay(500, cancellationToken);
                ApplyProfile(best.Profile);

                return (true, results, best, report);
            }
            catch (Exception ex)
            {
                Logger.LogError("RunIntelligentBenchmark", ex.Message);
                
                // Restaura estado original em caso de erro
                if (originalState != null)
                {
                    RestoreState(originalState);
                }
                
                return (false, results, new BenchmarkResult(), $"Erro no benchmark: {ex.Message}");
            }
        }

        private static string GenerateBenchmarkReport(List<BenchmarkResult> results, BenchmarkResult best, SystemStateSnapshot original)
        {
            var sb = new System.Text.StringBuilder(4096);
            sb.AppendLine("┌──────────────────────────────────────────────────────────────┐");
            sb.AppendLine("│  LATENCIA - MEDICAO REAL (nenhum valor estimado)             │");
            sb.AppendLine("└──────────────────────────────────────────────────────────────┘");
            sb.AppendLine("Fontes: NtQuerySystemInformation(8/23/2) + NtQueryTimerResolution");
            sb.AppendLine("        + sonda de despertar QPC (QueryPerformanceCounter + Sleep 1ms)");
            sb.AppendLine();
            sb.AppendLine($"{"Perfil",-14}│{"Despertar",-16}│{"P95",-9}│{"Max",-9}│{"DPC%",-6}│{"ISR%",-6}│{"Score",-6}");
            sb.AppendLine("──────────────┼────────────────┼─────────┼─────────┼──────┼──────┼──────");

            foreach (var r in results)
            {
                string marker = r.Profile.Name == best.Profile.Name ? "★" : " ";
                string wake = $"{r.Measurement.AvgLatencyUs / 1000.0:F2} ms";
                string p95 = $"{r.Measurement.P95LatencyUs / 1000.0:F2} ms";
                string max = $"{r.Measurement.MaxLatencyUs / 1000.0:F2} ms";
                string dpc = $"{r.Measurement.DpcPercent:F2}";
                string isr = $"{r.Measurement.IsrPercent:F2}";
                string score = $"{r.Score:F0}";
                sb.AppendLine($"{marker + r.Profile.Name,-14}│{wake,-16}│{p95,-9}│{max,-9}│{dpc,-6}│{isr,-6}│{score,-6}");
            }

            sb.AppendLine();
            sb.AppendLine($"🏆 MELHOR PERFIL: {best.Profile.Name} - {best.Profile.Description}");

            var baseline = results.FirstOrDefault(r => r.Profile.Name == "PADRÃO");
            if (baseline != null && baseline.Measurement.AvgLatencyUs > 0)
            {
                double delta = CalculateImprovement(baseline, best);
                sb.AppendLine(delta > 0.5
                    ? $"✅ Despertar {delta:F1}% mais rapido que o perfil PADRAO ({baseline.Measurement.AvgLatencyUs / 1000.0:F2} ms -> {best.Measurement.AvgLatencyUs / 1000.0:F2} ms)."
                    : $"⚠️ Diferenca dentro do ruido (~{Math.Abs(delta):F1}%) - o ganho real vem da resolucao do timer.");
            }

            var b = best.Measurement;
            sb.AppendLine();
            sb.AppendLine("Numeros do melhor perfil:");
            sb.AppendLine($"   • Despertar: min {b.MinLatencyUs / 1000.0:F2} ms | media {b.AvgLatencyUs / 1000.0:F2} ms | p95 {b.P95LatencyUs / 1000.0:F2} ms | max {b.MaxLatencyUs / 1000.0:F2} ms ({b.SampleCount} amostras)");
            sb.AppendLine($"   • Resolucao do timer: {b.TimerResolutionMs:F2} ms (minima suportada {b.TimerFinestMs:F2} ms)");
            if (b.HasDpcData)
                sb.AppendLine($"   • DPC: {b.DpcPercent:F2}% da CPU ({b.DpcTimeUs:F0} us/s) | ISR: {b.IsrPercent:F2}% ({b.IsrTimeUs:F0} us/s) | interrupcoes: {b.InterruptsPerSec:F0}/s");
            if (b.ContextSwitchesPerSec > 0)
                sb.AppendLine($"   • Context switches: {b.ContextSwitchesPerSec:F0}/s | DPCs: {b.DpcsPerSec:F0}/s | page faults: {b.PageFaultsPerSec:F0}/s");
            if (b.HardPageFaultsPerSec > 0)
                sb.AppendLine($"   • Hard page faults: {b.HardPageFaultsPerSec:F0}/s (pico {b.MaxHardPageFaultsPerSec:F0}/s) | pior janela: DPC {b.WorstDpcWindowPercent:F2}% / ISR {b.WorstIsrWindowPercent:F2}% ({b.SpikeWindowMs}ms)");
            if (b.Cpus.Count > 0 && b.Cpus[0].MaxMhz > 0)
                sb.AppendLine($"   • CPU: {b.Cpus[0].CurrentMhz:F0} MHz agora (max {b.Cpus[0].MaxMhz:F0} MHz)");
            if (b.Cpus.Count > 0)
                sb.AppendLine($"   • CPU mais carregada: #{b.Cpus[0].Index} (DPC {b.Cpus[0].DpcPercent:F2}% + ISR {b.Cpus[0].IsrPercent:F2}%)");

            sb.AppendLine();
            sb.AppendLine("Configuracoes aplicadas:");
            sb.AppendLine($"   • Win32PrioritySeparation: 0x{best.Profile.Win32Priority:X2} ({best.Profile.Win32Priority})");
            sb.AppendLine($"   • Core Parking: {(best.Profile.DisableCoreParking ? "Desativado" : "Ativado")} | Timer Coalescing: {(best.Profile.DisableTimerCoalescing ? "Desativado" : "Ativado")}");
            sb.AppendLine($"   • Input Queue: {(best.Profile.OptimizeInputQueue ? "Otimizado (30)" : "Padrao (100)")} | Global Timer: {(best.Profile.EnableGlobalTimer ? "Ativado" : "Desativado")}");
            sb.AppendLine($"   • System Responsiveness: {(best.Profile.OptimizeSystemResponsiveness ? "0 (Max)" : "20 (Padrao)")}");

            return sb.ToString();
        }

        private static double CalculateImprovement(BenchmarkResult baseline, BenchmarkResult improved)
        {
            return ((baseline.Measurement.AvgLatencyUs - improved.Measurement.AvgLatencyUs) / baseline.Measurement.AvgLatencyUs) * 100;
        }

        #endregion
    }
}
