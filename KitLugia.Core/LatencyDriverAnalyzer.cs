using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;

namespace KitLugia.Core
{
    /// <summary>
    /// Analise profunda POR DRIVER (estilo LatencyMon): inicia a sessao ETW "NT Kernel Logger"
    /// direto pela API nativa (StartTraceW - o caminho de start do TraceEvent falha nesta build do
    /// Windows com ERROR_WMI_INSTANCE_NOT_FOUND), consome os eventos com o parser do TraceEvent
    /// (PerfInfoDPC/PerfInfoISR com ElapsedTimeMSec real de cada rotina) e resolve o endereco de
    /// cada rotina para o modulo do kernel (NtQuerySystemInformation SystemModuleInformation).
    /// Requer executar como administrador; falhas viram mensagem tratada no relatorio.
    /// </summary>
    public static class LatencyDriverAnalyzer
    {
        public sealed class DriverLatencyInfo
        {
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public int DpcCount { get; set; }
            public double DpcTotalMs { get; set; }
            public double DpcMaxMs { get; set; }
            public int IsrCount { get; set; }
            public double IsrTotalMs { get; set; }
            public double IsrMaxMs { get; set; }

            /// <summary>Pior execucao registrada (DPC ou ISR), em ms.</summary>
            public double WorstMs => Math.Max(DpcMaxMs, IsrMaxMs);
        }

        public sealed class DriverLatencyReport
        {
            public bool Success { get; set; }
            public bool Canceled { get; set; }
            public string? Error { get; set; }
            public double Seconds { get; set; }
            public int EventsLost { get; set; }
            public List<DriverLatencyInfo> Drivers { get; set; } = new();
            public List<string> Sources { get; set; } = new();
        }

        private const int SystemModuleInformation = 11;
        private const int ModuleEntrySize = 296; // RTL_PROCESS_MODULE_INFORMATION (x64)
        private const double MaxPlausibleMs = 60_000; // acima de 1 min = suspensao/clock invalido
        private const string KernelSessionName = "NT Kernel Logger";
        private const uint EventTraceFlagDpc = 0x20;       // EVENT_TRACE_FLAG_DPC
        private const uint EventTraceFlagInterrupt = 0x40; // EVENT_TRACE_FLAG_INTERRUPT
        private const uint LogFileModeRealtime = 0x08000100; // INDEPENDENT_SESSION | REAL_TIME
        private const uint EventTraceControlStop = 1;

        private static readonly SemaphoreSlim Gate = new(1, 1);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

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

        /// <summary>Coleta o tempo REAL de execucao de DPC/ISR por driver durante N segundos.</summary>
        public static async Task<DriverLatencyReport> MeasureAsync(int seconds = 5, CancellationToken cancellationToken = default)
        {
            if (seconds < 1) seconds = 1;

            var report = new DriverLatencyReport { Seconds = seconds };
            report.Sources.Add("ETW: sessao NT Kernel Logger iniciada via StartTraceW (DPC + Interrupt)");
            report.Sources.Add("Rotina -> driver: NtQuerySystemInformation(SystemModuleInformation)");

            if (!await Gate.WaitAsync(0).ConfigureAwait(false))
                return Fail(report, "Ja existe uma analise de drivers em andamento.");

            var modules = GetKernelModules();
            var acc = new Accumulator(modules);
            ETWTraceEventSource? source = null;
            IntPtr props = IntPtr.Zero;
            try
            {
                props = BuildProperties(KernelSessionName, EventTraceFlagDpc | EventTraceFlagInterrupt);
                int err = StartTraceW(out _, KernelSessionName, props);
                if (err == 183)
                    return Fail(report, "A sessao 'NT Kernel Logger' ja esta em uso por outra ferramenta (LatencyMon/WPR). Feche-a e tente novamente.");
                if (err == 5)
                    return Fail(report, "Acesso negado: a analise por driver (ETW) precisa executar como administrador.");
                if (err != 0)
                    return Fail(report, $"StartTraceW falhou: {new Win32Exception(err).Message} ({err})");

                source = new ETWTraceEventSource(KernelSessionName, TraceEventSourceType.Session);
                source.Kernel.PerfInfoDPC += d => acc.AddDpc(d.Routine, d.ElapsedTimeMSec);
                source.Kernel.PerfInfoTimerDPC += d => acc.AddDpc(d.Routine, d.ElapsedTimeMSec);
                source.Kernel.PerfInfoThreadedDPC += d => acc.AddDpc(d.Routine, d.ElapsedTimeMSec);
                source.Kernel.PerfInfoISR += d => acc.AddIsr(d.Routine, d.ElapsedTimeMSec);

                // Garante que o Process() ja iniciou antes de qualquer Dispose.
                using var started = new ManualResetEventSlim(false);
                var pump = Task.Run(() =>
                {
                    started.Set();
                    source.Process();
                });
                started.Wait(TimeSpan.FromSeconds(2));

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    report.Canceled = true;
                }

                // Lido ANTES do Dispose: o getter acessa os handles internos.
                report.EventsLost = source.EventsLost;

                source.Dispose(); // seta stopProcessing e fecha os handles: o Process() retorna

                var drained = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
                if (drained == pump)
                {
                    try { await pump.ConfigureAwait(false); }
                    catch (Exception ex) { Logger.LogWarning("LatencyDeepEtw", ex.Message); }
                }
                else
                {
                    Logger.LogWarning("LatencyDeepEtw", "O consumo ETW nao encerrou em 5s; seguindo com o parcial.");
                }

                // Encerra a sessao aqui para pegar as estatisticas finais (EventsLost) do buffer de propriedades.
                try
                {
                    ControlTraceW(0, KernelSessionName, props, EventTraceControlStop);
                    int offset = Marshal.OffsetOf<EventTraceProperties>(nameof(EventTraceProperties.EventsLost)).ToInt32();
                    report.EventsLost = Math.Max(report.EventsLost, Marshal.ReadInt32(props, offset));
                }
                catch { }

                report.Drivers = acc.Results;
                report.Success = true;

                var top = report.Drivers.Take(5)
                    .Select(d => $"{d.Name}: dpc max {d.DpcMaxMs * 1000.0:F0}us / isr max {d.IsrMaxMs * 1000.0:F0}us");
                Logger.Log($"Analise por driver (ETW, {seconds}s): {string.Join(" | ", top)} | eventos perdidos: {report.EventsLost}");
                return report;
            }
            catch (Exception ex)
            {
                return Fail(report, $"Falha na analise por driver (ETW): {ex.Message}");
            }
            finally
            {
                try { source?.Dispose(); } catch { }
                if (props != IntPtr.Zero)
                {
                    try { ControlTraceW(0, KernelSessionName, props, EventTraceControlStop); } catch { }
                    Marshal.FreeHGlobal(props);
                }
                Gate.Release();
            }
        }

        /// <summary>
        /// Monta o EVENT_TRACE_PROPERTIES da sessao de kernel (realtime, sem arquivo) com o nome
        /// dentro do buffer, igual ao setup que o StartTraceW aceita de fato nesta build do Windows.
        /// </summary>
        private static IntPtr BuildProperties(string name, uint enableFlags)
        {
            int size = Marshal.SizeOf<EventTraceProperties>();
            int total = size + 4096 + 256;
            IntPtr props = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);

            var p = new EventTraceProperties
            {
                Wnode = new WnodeHeader { BufferSize = (uint)total, Flags = 0x20000, ClientContext = 1 },
                BufferSize = 64,
                MinimumBuffers = 64,
                MaximumBuffers = 64 * 5 / 4 + 10,
                LogFileMode = LogFileModeRealtime,
                FlushTimer = 1,
                EnableFlags = enableFlags,
                LoggerNameOffset = (uint)size,
                LogFileNameOffset = (uint)size + 2048
            };
            Marshal.StructureToPtr(p, props, false);
            Marshal.Copy((name + "\0").ToCharArray(), 0, IntPtr.Add(props, size), name.Length + 1);
            return props;
        }

        private static DriverLatencyReport Fail(DriverLatencyReport report, string error)
        {
            report.Success = false;
            report.Error = error;
            Logger.LogWarning("LatencyDriverAnalyzer", error);
            return report;
        }

        /// <summary>Agrega os eventos por modulo do kernel (cache de rotina -> driver).</summary>
        private sealed class Accumulator
        {
            private readonly ModuleRange[] _modules;
            private readonly Dictionary<ulong, DriverLatencyInfo> _byRoutine = new();
            private readonly Dictionary<string, DriverLatencyInfo> _byModule = new();

            public Accumulator(ModuleRange[] modules) => _modules = modules;

            public void AddDpc(ulong routine, double elapsedMs) => Add(routine, elapsedMs, isDpc: true);
            public void AddIsr(ulong routine, double elapsedMs) => Add(routine, elapsedMs, isDpc: false);

            public List<DriverLatencyInfo> Results => _byModule.Values
                .OrderByDescending(d => d.WorstMs)
                .ThenByDescending(d => d.DpcTotalMs + d.IsrTotalMs)
                .ToList();

            private void Add(ulong routine, double elapsedMs, bool isDpc)
            {
                if (elapsedMs < 0 || elapsedMs > MaxPlausibleMs) return;

                if (!_byRoutine.TryGetValue(routine, out var driver))
                {
                    driver = Resolve(routine);
                    _byRoutine[routine] = driver;
                }

                if (isDpc)
                {
                    driver.DpcCount++;
                    driver.DpcTotalMs += elapsedMs;
                    if (elapsedMs > driver.DpcMaxMs) driver.DpcMaxMs = elapsedMs;
                }
                else
                {
                    driver.IsrCount++;
                    driver.IsrTotalMs += elapsedMs;
                    if (elapsedMs > driver.IsrMaxMs) driver.IsrMaxMs = elapsedMs;
                }
            }

            private DriverLatencyInfo Resolve(ulong routine)
            {
                int lo = 0, hi = _modules.Length - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var m = _modules[mid];
                    if (routine < m.Base) hi = mid - 1;
                    else if (routine >= m.End) lo = mid + 1;
                    else return GetOrAdd(m.Name, m.Path);
                }
                return GetOrAdd($"(rotina sem modulo 0x{routine:X})", "");
            }

            private DriverLatencyInfo GetOrAdd(string name, string path)
            {
                if (!_byModule.TryGetValue(name, out var info))
                {
                    info = new DriverLatencyInfo { Name = name, Path = path };
                    _byModule[name] = info;
                }
                return info;
            }
        }

        private sealed class ModuleRange
        {
            public ulong Base { get; set; }
            public ulong End { get; set; }
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
        }

        /// <summary>Mapa dos modulos carregados no kernel (base/tamanho/nome) para resolver rotinas.</summary>
        private static ModuleRange[] GetKernelModules()
        {
            try
            {
                NtQuerySystemInformation(SystemModuleInformation, IntPtr.Zero, 0, out int needed);
                if (needed <= 8) return Array.Empty<ModuleRange>();

                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQuerySystemInformation(SystemModuleInformation, buf, needed, out int ret) != 0 || ret < 8)
                        return Array.Empty<ModuleRange>();

                    int count = Marshal.ReadInt32(buf);
                    var list = new List<ModuleRange>(Math.Max(0, count));
                    for (int i = 0; i < count; i++)
                    {
                        if (8 + (i + 1) * ModuleEntrySize > ret) break;
                        IntPtr p = IntPtr.Add(buf, 8 + i * ModuleEntrySize);

                        ulong imageBase = (ulong)Marshal.ReadInt64(p, 16);
                        uint imageSize = (uint)Marshal.ReadInt32(p, 24);
                        if (imageBase == 0 || imageSize == 0) continue;

                        string path = Marshal.PtrToStringAnsi(IntPtr.Add(p, 40)) ?? "";
                        list.Add(new ModuleRange
                        {
                            Base = imageBase,
                            End = imageBase + imageSize,
                            Name = Path.GetFileName(path),
                            Path = path
                        });
                    }
                    return list.OrderBy(m => m.Base).ToArray();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("LatencyModules", ex.Message);
                return Array.Empty<ModuleRange>();
            }
        }
    }
}
