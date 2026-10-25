using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using KitLugia.Core.TaskManager;
using Brushes = System.Windows.Media.Brushes;
using DispatcherTimer = System.Windows.Threading.DispatcherTimer;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using System.Runtime.InteropServices;

namespace KitLugia.GUI.Windows.TaskManager
{
    // Partial: aba DESEMPENHO — dispositivos, gráficos, métricas ricas de hardware.
    public partial class KitTaskManagerWindow
    {
// ══════════════════════════════════════════════
        //  PERFORMANCE GRAPHS
        // ══════════════════════════════════════════════
        // ══════════════════════════════════════════════
        //  PERFORMANCE TAB — estilo Windows 11:
        //  lista de dispositivos à esquerda (CPU, Memória, cada Disco,
        //  cada adaptador de rede, cada GPU) + painel grande à direita.
        // ══════════════════════════════════════════════
        public sealed class PerfDeviceInfo : INotifyPropertyChanged
        {
            public string Key { get; init; } = "";
            public string Name { get; init; } = "";
            public string ColorHex { get; init; } = "#4CAF50";
            private string _summary = "";
            public string Summary { get => _summary; set { if (!string.Equals(_summary, value)) { _summary = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary))); } } }
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly ObservableCollection<PerfDeviceInfo> _perfDevices = new();
        private PerfDeviceInfo? _selectedPerfDevice;
        private readonly Dictionary<string, Queue<float>> _perfHistory = new();
        // Contadores por instância (discos e adaptadores)
        private readonly Dictionary<string, PerformanceCounter> _instanceCounters = new(StringComparer.OrdinalIgnoreCase);
        // Adaptadores sem contador perfmon (virtuais) — taxa calculada por GetIPStatistics
        private readonly Dictionary<string, System.Net.NetworkInformation.NetworkInterface> _netStatsNics = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long totalBytes, DateTime time)> _netLastSample = new(StringComparer.OrdinalIgnoreCase);
        private bool _perfBuilt;
        private float _lastGpuPct = -1;
        // Última amostra do tick de gráficos, consumida pela aba Resumo. Precisa ser
        // cacheada: PerformanceCounter.NextValue() só entrega valor novo 1x por janela —
        // ler o MESMO contador duas vezes no mesmo tick devolve 0 na segunda leitura.
        private float _lastCpuPct, _lastMemPct, _lastDiskReadMBps, _lastDiskWriteMBps;
        private double _lastFreqMhz = -1, _lastTempC = -1, _lastPowerW = -1;
        // Relógio da CPU: o clock ATUAL (turbo) muda a cada amostra; o NOMINAL é fixo
        // (velocidade base) e o PICO é o maior clock visto nesta sessão.
        private double _lastFreqBaseMhz = -1, _lastFreqPeakMhz = -1;
        private double _clockPeakMhz = -1;
        private TextBlock? _perfUsageLine;   // linha "Uso:" dos detalhes, atualizada a cada tick
        private System.Windows.Documents.Run? _perfFreqRun, _perfPeakRun; // linhas vivas do relógio
        // Linhas VIVAS da GPU (engines, VRAM, temperatura) — estilo Gerenciador de Tarefas
        private System.Windows.Documents.Run? _perfGpu3DRun, _perfGpuCopyRun, _perfGpuEncRun, _perfGpuDecRun;
        private System.Windows.Documents.Run? _perfGpuDedRun, _perfGpuSharedRun, _perfGpuTempRun, _perfGpuTotalRun;
        private System.Windows.Documents.Run? _perfGpuDxRun;   // "12 (FL 12.2)"
        private int _selectedGpuIndex = -1;   // adaptador de GPU selecionado no painel
        private int _perfDetailIdx;           // indice da proxima linha na grade de 2 colunas do painel
        private Dictionary<int, GpuAdapterLive>? _lastGpuLive; // último snapshot vivo por adaptador

        // ── PAINEL DA GPU estilo Gerenciador de Tarefas ────────────────────
        // 4 gráficos de engine (3D/Copy/Encode/Decode) + 2 barras de VRAM
        // (dedicada/compartilhada). Antes só existia UM gráfico (a utilização
        // agregada) e as engines eram apenas texto — o TM mostra as 4 curvas
        // separadas, que é onde dá para ver O QUE está usando a GPU.
        private sealed class GpuChartPart
        {
            public Canvas Chart = null!;
            public System.Windows.Documents.Run? Value;
            public System.Windows.Shapes.Rectangle? Bar;
            public Color LineColor;
            public string Key = "";
        }
        private GpuChartPart? _gpuChart3D, _gpuChartCopy, _gpuChartEnc, _gpuChartDec;
        private GpuChartPart? _gpuBarDed, _gpuBarShared;
        private readonly Dictionary<string, Queue<float>> _gpuChartHistory = new();

        /// <summary>Taxa de um adaptador: usa perfmon se existir, senão delta de GetIPStatistics.</summary>
        // FIX congelamento (medido: stalls de 90-930ms por tick): NextValue de PerformanceCounter
        // e as queries nativas (CPU utility, temperatura, potência) custam dezenas-hundreds de ms
        // CADA e bloqueavam a UI thread a cada tick de 1s. A coleta roda em WORKER (CollectSamplesAsync);
        // a UI thread só renderiza o último snapshot (O(canvas), sub-ms).
        private readonly object _counterLock = new();
        private int _collectRunning;

        /// <summary>Snapshot imutável de UMA coleta — nunca toca em elementos WPF.</summary>
        private sealed class PerfSample
        {
            public float Cpu;
            public float Mem;
            public ulong MemUsedBytes;   // RAM física EM USO (bytes) — mesma fonte do % de RAM
            public ulong MemTotalBytes;  // RAM física TOTAL (bytes)
            public float DiskReadMBps;
            public float DiskWriteMBps;
            public float NetMBps;
            public float GpuPct = -1f;
            /// <summary>Dados VIVOS por adaptador de GPU (índice → métricas PDH + temperatura).</summary>
            public Dictionary<int, GpuAdapterLive>? GpuLive;
            public double FreqMhz;
            public double FreqBaseMhz = -1;  // clock NOMINAL (velocidade base)
            public double FreqPeakMhz = -1;  // maior clock visto nesta sessão
            public double TempC = -1;
            public double PowerW = -1;
            public float KernelPct;
            public Dictionary<string, float>? PerDevice;
        }

        /// <summary>Coleta TODAS as métricas do tick de 1s fora da UI thread.</summary>
        private async Task<PerfSample?> CollectSamplesAsync()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _collectRunning, 1, 0) != 0) return null; // coleta anterior ainda rodando
            try
            {
                return await System.Threading.Tasks.Task.Run(() =>
                {
                    var s = new PerfSample();
                    try { double u = NativeMetricsHelper.GetCpuUtilityPercent(); if (u >= 0) s.Cpu = (float)u; } catch { }
                    if (s.Cpu <= 0) { try { lock (_counterLock) { s.Cpu = _cpuCounter?.NextValue() ?? 0; } } catch { } }
                    try { double m = NativeMetricsHelper.GetMemoryUsagePercent(); if (m >= 0) s.Mem = (float)m; } catch { }
                    if (s.Mem <= 0)
                    {
                        try
                        {
                            float avail;
                            lock (_counterLock) { avail = _memAvailable?.NextValue() ?? 0; }
                            s.Mem = _totalMemBytes > 0 ? (float)((1f - avail / (_totalMemBytes / 1024.0 / 1024.0)) * 100.0) : 0f;
                            // Bytes coerentes com o % acima (mesmo contador "Available MBytes")
                            if (_totalMemBytes > 0)
                            {
                                s.MemTotalBytes = (ulong)_totalMemBytes;
                                long availBytes = (long)avail * 1024L * 1024L;
                                s.MemUsedBytes = (ulong)Math.Max(0L, _totalMemBytes - availBytes);
                            }
                        }
                        catch { }
                    }
                    // Bytes de RAM em uso (mesma fonte do %): K32GetPerformanceInfo → GlobalMemoryStatusEx
                    if (s.MemTotalBytes == 0)
                    {
                        try
                        {
                            if (NativeMetricsHelper.TryGetPhysicalMemoryUsage(out var usedB, out var totalB))
                            { s.MemUsedBytes = usedB; s.MemTotalBytes = totalB; }
                        }
                        catch { }
                    }
                    try
                    {
                        float readBytes, writeBytes;
                        lock (_counterLock) { readBytes = _diskReadCounter?.NextValue() ?? 0; writeBytes = _diskWriteCounter?.NextValue() ?? 0; }
                        s.DiskReadMBps = readBytes / (1024f * 1024f);
                        s.DiskWriteMBps = writeBytes / (1024f * 1024f);
                    }
                    catch { }
                    try { double f = NativeMetricsHelper.GetCpuFrequencyMhz(); s.FreqMhz = f; } catch { }
                    try { s.FreqBaseMhz = NativeMetricsHelper.GetCpuBaseFrequencyMhz(); } catch { }
                    // Pico observado na sessão (o TM mostra o turbo máximo alcançado)
                    if (s.FreqMhz > 0)
                    {
                        lock (_counterLock)
                        {
                            if (s.FreqMhz > _clockPeakMhz) _clockPeakMhz = s.FreqMhz;
                            s.FreqPeakMhz = _clockPeakMhz;
                        }
                    }
                    try { double? t = NativeMetricsHelper.GetCpuTemperatureC(); s.TempC = t ?? -1; } catch { }
                    try { double? w = NativeMetricsHelper.GetCpuPowerWatts(); s.PowerW = w ?? -1; } catch { }
                    try { double k = NativeMetricsHelper.GetSystemKernelPercent(); s.KernelPct = k > 0 ? (float)k : 0f; } catch { }
                    try { s.NetMBps = (float)(NetBytesPerSecNow() / (1024.0 * 1024.0)); } catch { }
                    try { s.GpuPct = (float)GpuMonitor.GetTotalGpuUtilization(); } catch { }

                    // ── Métricas VIVAS por adaptador de GPU ──
                    // PDH: engines (3D/Copy/Encode/Decode) + VRAM em uso, casados pelo LUID.
                    // nvidia-smi: temperatura (throttle de 2s dentro do próprio Core).
                    try
                    {
                        var liveByLuid = GpuMonitor.GetAdapterLive();
                        var snap = _hwGpusSnapshot;
                        var temps = GpuMetrics.GetNvidiaSmi();
                        var map = new Dictionary<int, GpuAdapterLive>();
                        for (int gi = 0; gi < snap.Length; gi++)
                        {
                            var a = snap[gi];
                            var lv = new GpuAdapterLive { LuidKey = a.LuidKey };
                            bool has = false;
                            if (!string.IsNullOrEmpty(a.LuidKey) && liveByLuid.TryGetValue(a.LuidKey, out var src))
                            {
                                lv.Util3D = src.Util3D; lv.UtilCopy = src.UtilCopy;
                                lv.UtilEncode = src.UtilEncode; lv.UtilDecode = src.UtilDecode;
                                lv.DedicatedUsedBytes = src.DedicatedUsedBytes;
                                lv.SharedUsedBytes = src.SharedUsedBytes;
                                lv.TotalCommittedBytes = src.TotalCommittedBytes;
                                has = true;
                            }
                            foreach (var sm in temps)
                            {
                                if (!GpuMetrics.NamesMatch(sm.Name, a.Name)) continue;
                                lv.TemperatureC = sm.TemperatureC;
                                if (lv.TemperatureC >= 0) has = true;
                                break;
                            }
                            if (has) map[gi] = lv;
                        }
                        if (map.Count > 0) s.GpuLive = map;
                    }
                    catch { }

                    // Contadores por dispositivo (disk:% Disk Time, net:Bytes Total/sec)
                    var perDev = new Dictionary<string, float>();
                    List<string> missingNet;
                    lock (_counterLock)
                    {
                        foreach (var kv in _instanceCounters)
                        {
                            try { perDev[kv.Key] = kv.Value.NextValue(); } catch { }
                        }
                        missingNet = _netStatsNics.Keys.Where(k => !_instanceCounters.ContainsKey(k)).ToList();
                    }
                    // Fallback NIC (sem contador perfmon): taxa via GetIPStatistics
                    foreach (var key in missingNet)
                    {
                        try
                        {
                            var nic = _netStatsNics[key];
                            var stats = nic.GetIPStatistics();
                            long total = stats.BytesSent + stats.BytesReceived;
                            var now = DateTime.UtcNow;
                            lock (_counterLock)
                            {
                                if (_netLastSample.TryGetValue(key, out var prev) && (now - prev.time).TotalMilliseconds > 200)
                                {
                                    double bps = (total - prev.totalBytes) / (now - prev.time).TotalSeconds;
                                    _netLastSample[key] = (total, now);
                                    perDev[key] = Math.Max(0, (float)bps);
                                }
                                else _netLastSample[key] = (total, now);
                            }
                        }
                        catch { }
                    }
                    s.PerDevice = perDev;
                    return s;
                });
            }
            finally { System.Threading.Interlocked.Exchange(ref _collectRunning, 0); }
        }

        /// <summary>Taxa total de rede (todos os dispositivos) — chamado DENTRO do worker.</summary>
        private double NetBytesPerSecNow()
        {
            double total = 0;
            lock (_counterLock)
            {
                foreach (var kv in _instanceCounters)
                {
                    if (!kv.Key.StartsWith("net:", StringComparison.OrdinalIgnoreCase)) continue;
                    try { total += kv.Value.NextValue(); } catch { }
                }
            }
            return total;
        }

        /// <summary>Formata MAC "AABBCCDDEEFF" → "AA-BB-CC-DD-EE-FF".</summary>
        private static string FormatMac(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "—";
            try
            {
                if (raw.Contains('-') || raw.Contains(':')) return raw;
                var parts = new string[raw.Length / 2];
                for (int i = 0; i < parts.Length; i++) parts[i] = raw.Substring(i * 2, 2);
                return string.Join("-", parts);
            }
            catch { return raw; }
        }

        private static System.Windows.Media.Color FromHex(string hex)
        {
            try { return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex); }
            catch { return System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50); }
        }

        // ─── Cache de informações ricas de hardware (pré-carregado UMA vez, como o Task Manager) ───
        private string _hwCpuName = "Processador";
        private string _hwCpuSockets = "1";
        private string _hwCpuVirtualization = "Desativado";
        private string _hwCpuL1 = "?", _hwCpuL2 = "?", _hwCpuL3 = "?";
        private string _hwCpuCoreClock = "?";
        private readonly List<GpuAdapterInfo> _hwGpus = new();
        // Snapshot atômico dos adaptadores para o WORKER (a lista é montada na UI thread).
        private volatile GpuAdapterInfo[] _hwGpusSnapshot = Array.Empty<GpuAdapterInfo>();
        private readonly Dictionary<string, (string model, string type, string sizeGb, bool isSystemDisk, bool hasPagefile)> _hwDisks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string adapter, string conn, string mac, string speed, string ip)> _hwNets = new(StringComparer.OrdinalIgnoreCase);
        private string _hwRamSpeed = "?", _hwRamFormFactor = "?", _hwRamSlotsUsed = "?", _hwRamSlotsTotal = "?";

        // ══════════════════════════════════════════════
        //  CPU CACHE — NATIVO: GetLogicalProcessorInformationEx (RelationCache) + CIM/WMI fallback
        //  Sucessor WMI: NativeHardware (nativo <15ms, funciona WinPE / WmiPrvSE travado)
        // ══════════════════════════════════════════════
        private static (string l1, string l2, string l3) ReadCpuCacheFromRegistry()
        {
            try { return NativeHardware.GetCpuCacheNative(); }
            catch { return ("?", "?", "?"); }
        }

        // ══════════════════════════════════════════════
        //          // IsProcessorFeaturePresent agora via NativeHardware (mantido alias para compat)
        [DllImport("kernel32.dll")]
        private static extern bool IsProcessorFeaturePresent(uint processorFeature);
        private const uint PF_VIRT_FIRMWARE_ENABLED = 21;
        private const uint PF_HYPERVISOR_PRESENT = 22;

        private static string DetectVirtualizationState()
        {
            try { return NativeHardware.GetVirtualizationNative(); }
            catch { return "?"; }
        }

        private static bool IsHypervisorPresentViaCpuid()
        {
            try { return NativeHardware.GetVirtualizationNative().Contains("hipervisor", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private async Task BuildPerfDevicesAsync()
        {
            if (_perfBuilt) return;
            _perfBuilt = true;
            var data = await Task.Run(() =>
            {
                var disks = new List<string>();
                var nets = new List<string>();
                try { disks.AddRange(new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames()); } catch { }
                try { nets.AddRange(new PerformanceCounterCategory("Network Interface").GetInstanceNames()); } catch { }

                // ── CPU completa — NATIVO (Registry + GetLogicalProcessorInformationEx + IsProcessorFeaturePresent)
                // Sucessor WMI: NativeHardware (CIM WsMan 30% mais rápido) → fallback WMI DCOM
                string cpuName = NativeHardware.GetCpuNameNative();
                string virt = NativeHardware.GetVirtualizationNative();
                string sockets = NativeHardware.GetCpuSocketsNative();
                string clock = NativeHardware.GetCpuClockNative();
                if (string.IsNullOrWhiteSpace(cpuName)) cpuName = $"CPU ({Environment.ProcessorCount} núcleos)";
                var (l1, l2, l3) = ReadCpuCacheFromRegistry(); // nativo + CIM fallback interno

                // ── GPUs completas — CASCATA (DXGI + registro + nvidia-smi + DirectX) ──
                // GpuMetrics.GetAdapters() monta o adaptador combinando as MESMAS fontes
                // que o Gerenciador de Tarefas usa:
                //   DXGI      → nome, LUID (chave PDH), VRAM dedicada/compartilhada
                //   Registro  → versão do driver ("32.0.16.1656"), data, qwMemorySize
                //   nvidia-smi→ nome comercial, temperatura, memória reservada
                //   dxdiag    → "12 (FL 12.2)" (em background, sem travar a UI)
                // Funciona para NVIDIA, AMD, Intel e qualquer GPU WDDM.
                var gpus = GpuMetrics.GetAdapters();
                // O dxdiag (DirectX FL) roda em background — sem travar a UI. O primeiro
                // collect devolve o fallback D3D11 (FL 12.1); o dxdiagsobe para 12.2
                // quando termina, e a linha viva do painel se atualiza sozinha.
                GpuMetrics.StartDirectXProbe();

                // ── Discos completos — NATIVO (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS) + CIM fallback
                // Sucessor WMI: NativeHardware (nativo <10ms, funciona WinPE / WmiPrvSE travado) → CIM → WMI
                var diskInfo = new Dictionary<string, (string, string, string, bool, bool)>(StringComparer.OrdinalIgnoreCase);
                var systemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var pageDrives = NativeHardware.GetPagefileDrives();
                var driveToPhysical = NativeHardware.MapLogicalToPhysicalNative();
                var byIndex = NativeHardware.GetDiskStaticInfoNative();
                try
                {
                    foreach (var inst in disks)
                    {
                        // instância PhysicalDisk: "0 C:" → extrai número físico
                        var numMatch = System.Text.RegularExpressions.Regex.Match(inst, @"^(\d+)\s");
                        int physIdx = numMatch.Success ? int.Parse(numMatch.Groups[1].Value) : -1;
                        var (model, sizeGb) = physIdx >= 0 && byIndex.TryGetValue(physIdx, out var mi) ? mi : ("?", "?");
                        bool isSys = false, hasPage = false;
                        if (driveToPhysical.TryGetValue(physIdx, out var letters))
                        {
                            isSys = letters.Contains(systemDrive, StringComparer.OrdinalIgnoreCase);
                            foreach (var lt in letters) if (pageDrives.Contains(lt + ":")) hasPage = true;
                        }
                        diskInfo[inst] = (model, "—" /*preenchido depois*/, sizeGb, isSys, hasPage);
                    }
                }
                catch { }

                // ── Adaptadores de rede: MESMA LISTA DO ncpa.cpl ("Conexões de Rede") ──
                // O painel do Windows mostra TODAS as conexões (conectadas OU desconectadas),
                // exceto pseudo-adaptadores de driver. Reproduzimos exatamente isso.
                var netInfo = new Dictionary<string, (string, string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
                var allNics = new List<(string id, string desc, string conn, string mac, string speed, string ip, bool isVirtual)>();
                try
                {
                    string[] junkPatterns =
                    {
                        "WAN Miniport", "Wi-Fi Direct", "Kernel Debug", "Teredo", "ISATAP",
                        "Microsoft Hosted", "Bluetooth Device (Personal Area Network)", "Loopback",
                        "KM-TEST", "Microsoft Wi-Fi Direct Virtual Adapter", "QoS Packet Scheduler",
                        "WFP Native MAC Layer", "WFP 802.3 MAC Layer LightWeight Filter",
                        "LLTD Mapper", "LLTD Io", "Microsoft Network Adapter Multiplexor",
                        "VirtualBox Bridged Networking Driver Miniport",
                    };
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        try
                        {
                            // Só CONECTADAS (pedido do usuário — igual ao Task Manager)
                            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                            // Filtra por Description (nome do driver), não pelo Nome amigável:
                            // os filtros QoS/WFP herdam a descrição do pai e criam clones.
                            if (junkPatterns.Any(j => nic.Description.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;
                            var props = nic.GetIPProperties();
                            string ip = props?.UnicastAddresses?.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address?.ToString() ?? "—";
                            long bps = (nic.Speed <= 0 ? 0 : nic.Speed);
                            bool isVirt = nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                                       || nic.Name.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
                                       || nic.Name.Contains("WSL", StringComparison.OrdinalIgnoreCase);
                            allNics.Add((nic.Id, nic.Description, nic.Name, nic.GetPhysicalAddress()?.ToString() ?? "", bps.ToString(), ip, isVirt));
                        }
                        catch { }
                    }
                    // Dedupe: mesma descrição = mesmo adaptador físico/lógico
                    allNics = allNics.GroupBy(x => x.desc, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                }
                catch { }
                foreach (var nic in allNics)
                    netInfo[nic.desc] = (nic.desc, nic.conn, FormatMac(nic.mac), FormatLinkSpeed(nic.speed), nic.ip);
                nets = allNics.Select(x => x.desc).ToList();

                // ── RAM completa — NATIVO SMBIOS (GetSystemFirmwareTable 'RSMB') <15ms vs WMI 80ms
                // Sucessor WMI: NativeHardware (nativo + CIM → WMI fallback)
                var (ramSpeed, ramFF, slotsUsed, slotsTotal) = NativeHardware.GetRamInfoNative();

                return (disks, nets, cpuName, virt, sockets, clock, l1, l2, l3, gpus, diskInfo, netInfo, ramSpeed, ramFF, slotsUsed, slotsTotal);
            });

            // Preenche caches na UI thread-safe (campos simples)
            _hwCpuName = data.cpuName;
            _hwCpuSockets = data.sockets;
            _hwCpuVirtualization = data.virt;
            _hwCpuCoreClock = data.clock;
            _hwCpuL1 = data.l1; _hwCpuL2 = data.l2; _hwCpuL3 = data.l3;
            foreach (var g in data.gpus) _hwGpus.Add(g);
            // Snapshot atômico p/ o WORKER (a lista é montada na UI thread)
            _hwGpusSnapshot = _hwGpus.ToArray();
            foreach (var kv in data.diskInfo) _hwDisks[kv.Key] = kv.Value;
            foreach (var kv in data.netInfo) _hwNets[kv.Key] = kv.Value;
            _hwRamSpeed = data.ramSpeed; _hwRamFormFactor = data.ramFF;
            _hwRamSlotsUsed = data.slotsUsed; _hwRamSlotsTotal = data.slotsTotal;

            // CPU usa o NOME DO PROCESSADOR como título (prioridade, como no Task Manager)
            await Dispatcher.InvokeAsync(() =>
            {
                _perfDevices.Add(new PerfDeviceInfo { Key = "cpu", Name = _hwCpuName, ColorHex = "#4CAF50" });
                _perfDevices.Add(new PerfDeviceInfo { Key = "mem", Name = "Memória", ColorHex = "#2196F3" });
            });

            int di = 0;
            foreach (var d in data.disks.OrderBy(x => x))
            {
                if (d.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue; // pseudo-instância agregada — não é disco
                string key = $"disk:{d}";
                try
                {
                    var ctr = new PerformanceCounter("PhysicalDisk", "% Disk Time", d) { ReadOnly = true };
                    ctr.NextValue();
                    _instanceCounters[key] = ctr;
                }
                catch { }
                var dd = Dispatcher;
                // Usa o índice físico real da instância (\"1 C:\" → 1), não sequencial di — corrige rótulo invertido NVMe vs HDD
                var mNum = System.Text.RegularExpressions.Regex.Match(d, @"^(\d+)");
                int physNum = mNum.Success ? int.Parse(mNum.Groups[1].Value) : di;
                string letters = ExtractDriveLetters(d);
                string label = $"Disco {physNum} ({letters})";
                await dd.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = key, Name = label, ColorHex = "#FF9800" }));
                di++;
            }

            foreach (var n in data.nets.OrderBy(x => x))
            {
                string key = $"net:{n}";
                // Contador perfmon quando a instância existir; senão taxa via GetIPv4Statistics
                try
                {
                    var ctr = new PerformanceCounter("Network Interface", "Bytes Total/sec", n) { ReadOnly = true };
                    ctr.NextValue();
                    _instanceCounters[key] = ctr;
                }
                catch { }
                if (!_instanceCounters.ContainsKey(key))
                {
                    try
                    {
                        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                            .FirstOrDefault(x => x.Description.Equals(n, StringComparison.OrdinalIgnoreCase));
                        if (nic != null) _netStatsNics[key] = nic;
                    }
                    catch { }
                }
                string conn = _hwNets.TryGetValue(n, out var ni2) && !string.IsNullOrEmpty(ni2.conn) ? ni2.conn : Truncate(n, 18);
                bool isVirt = n.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || n.Contains("vEthernet", StringComparison.OrdinalIgnoreCase) || n.Contains("WSL", StringComparison.OrdinalIgnoreCase);
                string label = conn.Equals("Conectado", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(conn)
                    ? (isVirt ? $"Rede virtual ({Truncate(n, 14)})" : $"Ethernet ({Truncate(n, 16)})")
                    : (isVirt && !conn.Contains("virtual", StringComparison.OrdinalIgnoreCase) ? $"{conn} (virtual)" : conn);
                await Dispatcher.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = key, Name = label, ColorHex = "#9C27B0" }));
            }

            int gi = 0;
            foreach (var g in data.gpus)
            {
                var gg = g; int idx = gi;
                await Dispatcher.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = $"gpu:{idx}", Name = gg.Name, ColorHex = "#4FC3F7" }));
                gi++;
            }
            if (gi == 0) await Dispatcher.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = "gpu:-1", Name = "GPU (indisponível)", ColorHex = "#888888" }));

            await Dispatcher.InvokeAsync(() =>
            {
                LstPerfDevices.ItemsSource = _perfDevices;
                if (LstPerfDevices.Items.Count > 0) LstPerfDevices.SelectedIndex = 0;
            });
        }

        /// <summary>Mapeia índice físico do disco → letras (NATIVO IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS + CIM/WMI fallback).</summary>
        private static Dictionary<int, List<string>> MapLogicalToPhysical()
        {
            try { return NativeHardware.MapLogicalToPhysicalNative(); }
            catch { return new Dictionary<int, List<string>>(); }
        }

        private static string ExtractDriveLetters(string physicalDiskInstance)
        {
            // Instância tipo "0 C:" já contém as letras; senão devolve o sufixo
            var m = System.Text.RegularExpressions.Regex.Match(physicalDiskInstance, @"\d+\s+(.+)");
            return m.Success ? m.Groups[1].Value.Trim() : physicalDiskInstance;
        }

        private static string Truncate(string s, int len) => string.IsNullOrEmpty(s) || s.Length <= len ? s : s[..len] + "…";

        private void LstPerfDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedPerfDevice = LstPerfDevices.SelectedItem as PerfDeviceInfo;
            _perfUsageLine = null;
            if (_selectedPerfDevice == null) return;
            PerfDeviceTitle.Text = _selectedPerfDevice.Name;
            PerfBigCanvas.Children.Clear();
            // Troca de dispositivo: os gráficos de GPU pertencem ao adaptador anterior
            _gpuChart3D = _gpuChartCopy = _gpuChartEnc = _gpuChartDec = null;
            _gpuBarDed = _gpuBarShared = null;
            PnlGpuGraphs.Children.Clear();
            PnlGpuGraphs.Visibility = Visibility.Collapsed;
            PerfBigCanvas.Visibility = Visibility.Visible;
            RenderPerfDetails(_selectedPerfDevice);
        }

        private void RenderPerfDetails(PerfDeviceInfo dev)
        {
            PnlPerfDetails.Children.Clear();
            // Duas colunas (como o Gerenciador de Tarefas): as linhas cabem sem rolagem.
            // O indice e COMUM a todas as linhas (estaticas e vivas) para que cada uma
            // ocupe uma celula propria — sem Grid.SetRow todas cairiam na linha 0.
            _perfDetailIdx = 0;
            PnlPerfDetails.RowDefinitions.Clear();
            // Quebra de linha: avança a célula sem desenhar nada. Usado onde a
            // informação precisa ficar EMPILHADA (caches L1/L2/L3) em vez de
            // espalhada nas duas colunas.
            void BreakRow()
            {
                if (_perfDetailIdx % 2 != 0) _perfDetailIdx++;
            }
            void AddDetail(TextBlock tb)
            {
                Grid.SetColumn(tb, _perfDetailIdx % 2);
                Grid.SetRow(tb, _perfDetailIdx / 2);
                EnsureDetailRow(_perfDetailIdx / 2);
                _perfDetailIdx++;
                PnlPerfDetails.Children.Add(tb);
            }
            void Row(string label, out TextBlock valueBlock)
            {
                var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 12, 1), TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
                var val = new System.Windows.Documents.Run("…") { Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold };
                tb.Inlines.Add(val);
                AddDetail(tb);
                valueBlock = tb; // o próprio TextBlock (Tag guarda o Run do valor)
                // guardamos o Run para atualização
                tb.Tag = val;
            }
            void StaticRow(string label, string value)
            {
                var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 12, 1), TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
                tb.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = System.Windows.Media.Brushes.White });
                AddDetail(tb);
            }

            var pid = dev.Key;
            // Só o painel de GPU tem linhas vivas de GPU — reseta ao trocar de dispositivo
            _selectedGpuIndex = -1;
            if (pid == "cpu")
            {
                // Título prioriza o nome do processador
                PerfDeviceTitle.Text = _hwCpuName;
                Row("Uso", out _perfUsageLine);
                // Relógio AO VIVO logo abaixo do uso (como o Gerenciador de Tarefas):
                // o clock ATUAL (turbo) muda a cada amostra; o base é estático.
                _perfFreqRun = AddLiveLine("Velocidade atual:");
                _perfPeakRun = AddLiveLine("Pico na sessão:");
                StaticRow("Velocidade base:", _hwCpuCoreClock);
                StaticRow("Soquetes:", _hwCpuSockets);
                StaticRow("Núcleos:", $"{Environment.ProcessorCount} lógicos");
                StaticRow("Virtualização:", _hwCpuVirtualization);
                // Caches um abaixo do outro (pedido): lado a lado as três linhas
                // ficavam partidas entre as colunas e não dava para comparar.
                BreakRow(); StaticRow("Cache L1:", _hwCpuL1);
                BreakRow(); StaticRow("Cache L2:", _hwCpuL2);
                BreakRow(); StaticRow("Cache L3:", _hwCpuL3);
                FillCpuLive();
            }
            else if (pid == "mem")
            {
                Row("Em uso", out _perfUsageLine);
                long tot = _totalMemBytes;
                if (tot > 0) StaticRow("Total instalado:", $"{tot / 1024 / 1024 / 1024:N0} GB");
                StaticRow("Velocidade:", _hwRamSpeed);
                StaticRow("Formato:", _hwRamFormFactor);
                StaticRow("Slots usados:", $"{_hwRamSlotsUsed} de {_hwRamSlotsTotal}");
                UpdateMemLive();
            }
            else if (pid.StartsWith("disk:"))
            {
                string inst = pid[5..];
                var letters = ExtractDriveLetters(inst);
                Row("Tempo ativo", out _perfUsageLine);
                if (_hwDisks.TryGetValue(inst, out var dinfo))
                {
                    PerfDeviceTitle.Text = $"Disco ({letters}) — {dinfo.model}";
                    StaticRow("Modelo:", dinfo.model);
                    StaticRow("Capacidade:", dinfo.sizeGb);
                    StaticRow("Disco do sistema:", dinfo.isSystemDisk ? "Sim" : "Não");
                    StaticRow("Arquivo de paginação:", dinfo.hasPagefile ? "Sim" : "Não");
                }
                else
                {
                    PerfDeviceTitle.Text = $"Disco ({letters})";
                    StaticRow("Instância:", inst);
                }
                try { StaticRow("Tempo de atividade:", TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss")); } catch { }
                FillDiskLive(inst);
            }
            else if (pid.StartsWith("net:"))
            {
                string inst = pid[4..];
                Row("Taxa", out _perfUsageLine);
                if (_hwNets.TryGetValue(inst, out var nfo))
                {
                    if (!string.IsNullOrEmpty(nfo.adapter)) PerfDeviceTitle.Text = nfo.conn ?? inst;
                    StaticRow("Adaptador:", nfo.adapter);
                    StaticRow("Conexão:", nfo.conn ?? "");
                    StaticRow("Endereço IPv4:", nfo.ip);
                    StaticRow("Endereço MAC:", nfo.mac);
                    StaticRow("Velocidade:", nfo.speed);
                }
                else StaticRow("Interface:", inst);
                FillNetLive(inst);
            }
            else if (pid.StartsWith("gpu:"))
            {
                int idx = int.TryParse(pid[4..], out var i2) ? i2 : -1;
                _selectedGpuIndex = idx;
                Row("Utilização", out _perfUsageLine);
                if (idx >= 0 && idx < _hwGpus.Count)
                {
                    var g = _hwGpus[idx];
                    // Título completo SEM corte (o card da lista tem ellipsis, o painel não)
                    PerfDeviceTitle.Text = g.Name;
                    StaticRow("Adaptador:", $"GPU {idx}");
                    // ── ENGINES VIVAS (as 4 curvas do Gerenciador de Tarefas) ──
                    _perfGpu3DRun = AddLiveLine("3D:");
                    _perfGpuCopyRun = AddLiveLine("Copy:");
                    _perfGpuEncRun = AddLiveLine("Video Encode:");
                    _perfGpuDecRun = AddLiveLine("Video Decode:");
                    // ── MEMÓRIA DA GPU (VIVA: dedicada, compartilhada e o TOTAL) ──
                    _perfGpuDedRun = AddLiveLine("Memória da GPU dedicada:");
                    _perfGpuSharedRun = AddLiveLine("Memória da GPU compartilhada:");
                    _perfGpuTotalRun = AddLiveLine("Memória da GPU (total):");
                    // Temperatura VIVA (nvidia-smi; AMD/Intel não expõem)
                    _perfGpuTempRun = AddLiveLine("Temperatura:");
                    // ── DADOS ESTÁTICOS ──
                    StaticRow("Versão do driver:", g.DriverVersion);
                    StaticRow("Data do driver:", g.DriverDate);
                    // VIVA: o dxdiag (background) sobe o FL 12.1 → 12.2 depois de pronto
                    _perfGpuDxRun = AddLiveLine("Versão do DirectX:");
                    StaticRow("Local físico:", g.PciLocation);
                    StaticRow("Memória reservada para hardware:",
                        g.ReservedBytes > 0 ? $"{g.ReservedBytes / 1024.0 / 1024.0:F0} MB" : "—");
                    // ── PAINEL GRANDE: 4 curvas de engine + 2 barras de VRAM ──
                    // (o TM mostra exatamente isso; espaço já era do gráfico único)
                    PerfBigCanvas.Visibility = Visibility.Collapsed;
                    BuildGpuGraphs();
                    PnlGpuGraphs.Visibility = Visibility.Visible;
                    UpdateGpuLive();
                }
            }
        }

        /// <summary>Atualiza as linhas VIVAS da GPU (engines, VRAM e temperatura) com o último snapshot.</summary>
        private void UpdateGpuLive()
        {
            int idx = _selectedGpuIndex;
            if (idx < 0) return;
            var g = idx < _hwGpus.Count ? _hwGpus[idx] : null;
            GpuAdapterLive? lv = null;
            if (_lastGpuLive != null) _lastGpuLive.TryGetValue(idx, out lv);

            if (_perfGpu3DRun != null) _perfGpu3DRun.Text = $"{lv?.Util3D ?? 0:F0}%";
            if (_perfGpuCopyRun != null) _perfGpuCopyRun.Text = $"{lv?.UtilCopy ?? 0:F0}%";
            if (_perfGpuEncRun != null) _perfGpuEncRun.Text = $"{lv?.UtilEncode ?? 0:F0}%";
            if (_perfGpuDecRun != null) _perfGpuDecRun.Text = $"{lv?.UtilDecode ?? 0:F0}%";

            if (_perfGpuDedRun != null)
            {
                ulong totalDed = g?.VramTotalBytes ?? g?.DedicatedTotalBytes ?? 0;
                _perfGpuDedRun.Text = totalDed > 0
                    ? $"{FormatGb(lv?.DedicatedUsedBytes ?? 0)}/{FormatGb(totalDed)}"
                    : FormatGb(lv?.DedicatedUsedBytes ?? 0);
            }
            if (_perfGpuSharedRun != null)
            {
                ulong totalShr = g?.SharedTotalBytes ?? 0;
                _perfGpuSharedRun.Text = totalShr > 0
                    ? $"{FormatGb(lv?.SharedUsedBytes ?? 0)}/{FormatGb(totalShr)}"
                    : FormatGb(lv?.SharedUsedBytes ?? 0);
            }
            // Total da GPU (dedicada + compartilhada) — a linha "Memória da GPU" do TM:
            // sem ela o usuário via as duas parcelas mas não o quanto a GPU inteira usa.
            if (_perfGpuTotalRun != null)
            {
                ulong totalDed = g?.VramTotalBytes ?? g?.DedicatedTotalBytes ?? 0;
                ulong totalShr = g?.SharedTotalBytes ?? 0;
                ulong usado = lv != null && lv.TotalCommittedBytes > 0
                    ? lv.TotalCommittedBytes
                    : (lv?.DedicatedUsedBytes ?? 0) + (lv?.SharedUsedBytes ?? 0);
                ulong capacidade = totalDed + totalShr;
                _perfGpuTotalRun.Text = capacidade > 0
                    ? $"{FormatGb(usado)}/{FormatGb(capacidade)}"
                    : FormatGb(usado);
            }
            if (_perfGpuTempRun != null)
            {
                double t = lv?.TemperatureC ?? -1;
                _perfGpuTempRun.Text = t >= 0 ? $"{t:F0} °C" : "—";
                _perfGpuTempRun.Foreground = t >= 0 ? GetHeatColor((float)t, 75, 90) : System.Windows.Media.Brushes.White;
            }
            if (_perfGpuDxRun != null)
            {
                try { _perfGpuDxRun.Text = GpuMetrics.GetDirectXVersion(); } catch { }
            }
            // "Utilização" (a mais alta das engines — como o Gerenciador de Tarefas)
            double util = lv?.Utilization ?? -1;
            if (util >= 0) SetUsageLine($"{util:F0}%");

            try { UpdateGpuCharts(g, lv); } catch { }
        }

        /// <summary>
        /// Monta o painel grande da GPU no formato do Gerenciador de Tarefas:
        /// 2x2 com as curvas das engines (3D/Copy/Video Encode/Video Decode) e,
        /// logo abaixo, as duas barras de VRAM (dedicada e compartilhada).
        /// O espaço já existia (era um gráfico único) — aqui ele é dividido.
        /// </summary>
        private void BuildGpuGraphs()
        {
            PnlGpuGraphs.Children.Clear();
            PnlGpuGraphs.ColumnDefinitions.Clear();
            PnlGpuGraphs.RowDefinitions.Clear();
            PnlGpuGraphs.ColumnDefinitions.Add(new ColumnDefinition());
            PnlGpuGraphs.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 2; i++)
                PnlGpuGraphs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PnlGpuGraphs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            PnlGpuGraphs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var rotulo = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var fundoGrafico = new SolidColorBrush(Color.FromRgb(0x0B, 0x11, 0x18));
            var fundoTrilho = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x3A));

            GpuChartPart Engine(string label, string key, Color cor, int row, int col)
            {
                var host = new Grid { Margin = new Thickness(col == 0 ? 0 : 8, 0, col == 0 ? 8 : 0, 3) };
                host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var head = new TextBlock { FontSize = 10, Foreground = rotulo };
                head.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = rotulo });
                var val = new System.Windows.Documents.Run("0%") { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
                head.Inlines.Add(val);

                var cv = new Canvas { Background = fundoGrafico, Margin = new Thickness(0, 1, 0, 0) };
                Grid.SetRow(head, 0);
                Grid.SetRow(cv, 1);
                host.Children.Add(head);
                host.Children.Add(cv);
                Grid.SetRow(host, row);
                Grid.SetColumn(host, col);
                PnlGpuGraphs.Children.Add(host);
                return new GpuChartPart { Chart = cv, Value = val, Key = key, LineColor = cor };
            }

            GpuChartPart MemBar(string label, string key, Color cor, int row)
            {
                var host = new StackPanel { Margin = new Thickness(0, 2, 0, 1) };
                var head = new TextBlock { FontSize = 10, Foreground = rotulo };
                head.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = rotulo });
                var val = new System.Windows.Documents.Run("—") { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
                head.Inlines.Add(val);

                var track = new Canvas { Height = 5, Background = fundoTrilho, Margin = new Thickness(0, 2, 0, 0) };
                var fill = new System.Windows.Shapes.Rectangle
                {
                    Height = 5,
                    Width = 0,
                    Fill = new SolidColorBrush(cor),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    RadiusX = 2,
                    RadiusY = 2
                };
                track.Children.Add(fill);

                host.Children.Add(head);
                host.Children.Add(track);
                Grid.SetRow(host, row);
                Grid.SetColumnSpan(host, 2);
                PnlGpuGraphs.Children.Add(host);
                return new GpuChartPart { Chart = track, Value = val, Bar = fill, Key = key, LineColor = cor };
            }

            _gpuChart3D = Engine("3D", "gpu3d", Color.FromRgb(0xC0, 0x84, 0xFC), 0, 0);
            _gpuChartCopy = Engine("Copy", "gpucopy", Color.FromRgb(0x4F, 0xC3, 0xF7), 0, 1);
            _gpuChartEnc = Engine("Video Encode", "gpue", Color.FromRgb(0xF5, 0x9E, 0x0B), 1, 0);
            _gpuChartDec = Engine("Video Decode", "gpud", Color.FromRgb(0x34, 0xD3, 0x99), 1, 1);
            _gpuBarDed = MemBar("Memória da GPU dedicada", "gpubard", Color.FromRgb(0xC0, 0x84, 0xFC), 2);
            _gpuBarShared = MemBar("Memória da GPU compartilhada", "gpubars", Color.FromRgb(0x4F, 0xC3, 0xF7), 3);
        }

        /// <summary>Empurra a amostra nas 4 curvas de engine e nas 2 barras de VRAM.</summary>
        private void UpdateGpuCharts(GpuAdapterInfo? g, GpuAdapterLive? lv)
        {
            if (_gpuChart3D == null) return;

            void Engine(GpuChartPart? part, double valor)
            {
                if (part == null) return;
                if (!_gpuChartHistory.TryGetValue(part.Key, out var q))
                {
                    q = new Queue<float>(61);
                    _gpuChartHistory[part.Key] = q;
                }
                q.Enqueue((float)Math.Clamp(valor, 0, 100));
                if (q.Count > 60) q.Dequeue();
                if (part.Value != null) part.Value.Text = $"{valor:F0}%";
                DrawLineChart(part.Chart, q, part.LineColor, 100f);
            }

            Engine(_gpuChart3D, lv?.Util3D ?? 0);
            Engine(_gpuChartCopy, lv?.UtilCopy ?? 0);
            Engine(_gpuChartEnc, lv?.UtilEncode ?? 0);
            Engine(_gpuChartDec, lv?.UtilDecode ?? 0);

            void Mem(GpuChartPart? part, ulong usado, ulong total)
            {
                if (part?.Bar == null) return;
                double frac = total > 0 ? Math.Clamp((double)usado / total, 0, 1) : 0;
                double largura = part.Chart.ActualWidth > 1 ? part.Chart.ActualWidth : 0;
                part.Bar.Width = Math.Max(0, largura * frac);
                if (part.Value != null)
                    part.Value.Text = total > 0 ? $"{FormatGb(usado)}/{FormatGb(total)}" : FormatGb(usado);
            }

            ulong totalDed = g?.VramTotalBytes ?? g?.DedicatedTotalBytes ?? 0;
            ulong totalShr = g?.SharedTotalBytes ?? 0;
            Mem(_gpuBarDed, lv?.DedicatedUsedBytes ?? 0, totalDed);
            Mem(_gpuBarShared, lv?.SharedUsedBytes ?? 0, totalShr);
        }

        /// <summary>Bytes → "2,1 GB" (GB em base 1024, cultura do sistema — pt-BR).</summary>
        private static string FormatGb(ulong bytes)
            => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.0} GB";

        /// <summary>
        /// Resumo do card da GPU no estilo do Gerenciador de Tarefas: "12% (45 °C)".
        /// Sem temperatura (AMD/Intel) fica só o "%".
        /// </summary>
        private static string FormatGpuCardSummary(string key, float val, PerfSample s)
        {
            int gi = int.TryParse(key.Length > 4 ? key[4..] : "", out var gix) ? gix : -1;
            GpuAdapterLive? glv = null;
            if (s.GpuLive != null) s.GpuLive.TryGetValue(gi, out glv);
            double temp = glv?.TemperatureC ?? -1;
            string pct = $"{val:F0}%";
            return temp >= 0 ? $"{pct} ({temp:F0} °C)" : pct;
        }

        private void FillNetLive(string instance)
        {
            var sRun = AddLiveLine("Envio:");
            var rRun = AddLiveLine("Recebimento:");
            // Totais desde o boot (paridade TMOG: Total sent/received) — GetIPStatistics é absoluto.
            System.Net.NetworkInformation.NetworkInterface? nicTot = null;
            try { nicTot = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Description.Equals(instance, StringComparison.OrdinalIgnoreCase)); } catch { }
            var totUpRun = AddLiveLine("Total enviado:");
            var totDownRun = AddLiveLine("Total recebido:");
            void RefreshNetTotals()
            {
                try
                {
                    if (nicTot == null) { totUpRun.Text = "—"; totDownRun.Text = "—"; return; }
                    var ts = nicTot.GetIPStatistics();
                    totUpRun.Text = FormatTotalBytes((ulong)Math.Max(0, ts.BytesSent));
                    totDownRun.Text = FormatTotalBytes((ulong)Math.Max(0, ts.BytesReceived));
                }
                catch { totUpRun.Text = "—"; totDownRun.Text = "—"; }
            }
            RefreshNetTotals();
            // Caminho A: contadores perfmon (adaptadores físicos)
            try
            {
                var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance) { ReadOnly = true };
                var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance) { ReadOnly = true };
                sent.NextValue(); recv.NextValue();
                var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                dt.Tick += (_, __) =>
                {
                    try { sRun.Text = FormatBytesSpeed(sent.NextValue()); rRun.Text = FormatBytesSpeed(recv.NextValue()); RefreshNetTotals(); } catch { }
                    if (_selectedPerfDevice?.Key?.StartsWith("net:") != true) dt.Stop();
                };
                dt.Start();
                return;
            }
            catch { }
            // Caminho B: GetIPv4Statistics (adaptadores virtuais sem instância no perfmon)
            try
            {
                var nic = _netStatsNics.TryGetValue($"net:{instance}", out var n1) ? n1
                    : System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(x => x.Description.Equals(instance, StringComparison.OrdinalIgnoreCase));
                if (nic == null) { sRun.Text = "—"; rRun.Text = "—"; return; }
                long lastSent = 0, lastRecv = 0; var lastT = DateTime.UtcNow;
                try { var st0 = nic.GetIPStatistics(); lastSent = st0.BytesSent; lastRecv = st0.BytesReceived; } catch { }
                var dt2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                dt2.Tick += (_, __) =>
                {
                    try
                    {
                        if (_selectedPerfDevice?.Key?.StartsWith("net:") != true) { dt2.Stop(); return; }
                        var st = nic.GetIPStatistics();
                        var now = DateTime.UtcNow;
                        double sec = (now - lastT).TotalSeconds;
                        if (sec > 0.2)
                        {
                            double up = Math.Max(0, (st.BytesSent - lastSent) / sec);
                            double down = Math.Max(0, (st.BytesReceived - lastRecv) / sec);
                            sRun.Text = FormatBytesSpeed(up);
                            rRun.Text = FormatBytesSpeed(down);
                            totUpRun.Text = FormatTotalBytes((ulong)Math.Max(0, st.BytesSent));
                            totDownRun.Text = FormatTotalBytes((ulong)Math.Max(0, st.BytesReceived));
                            lastSent = st.BytesSent; lastRecv = st.BytesReceived; lastT = now;
                        }
                    }
                    catch { }
                };
                dt2.Start();
            }
            catch { sRun.Text = "—"; rRun.Text = "—"; }
        }

        private void FillDiskLive(string instance)
        {
            try
            {
                var readC = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance) { ReadOnly = true };
                var writeC = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance) { ReadOnly = true };
                readC.NextValue(); writeC.NextValue();
                var rRun = AddLiveLine("Leitura:");
                var wRun = AddLiveLine("Escrita:");
                var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                dt.Tick += (_, __) =>
                {
                    try { rRun.Text = FormatBytesSpeed(readC.NextValue()); wRun.Text = FormatBytesSpeed(writeC.NextValue()); } catch { }
                    if (_selectedPerfDevice?.Key?.StartsWith("disk:") != true) dt.Stop();
                };
                dt.Start();
            }
            catch { }
        }

        private void FillCpuLive()
        {
            var run = AddLiveLine("Processos / threads / handles:");
            var upRun = AddLiveLine("Tempo de atividade:");
            var tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tickTimer.Tick += (_, __) =>
            {
                try
                {
                    if (_selectedPerfDevice?.Key != "cpu") { tickTimer.Stop(); return; }
                    int procs; int threads = 0; int handles = 0;
                    lock (_lock)
                    {
                        procs = _allRows.Count;
                        threads = _allRows.Sum(r => int.TryParse(r.Threads, out var t) ? t : 0);
                        handles = _allRows.Sum(r => int.TryParse(r.Handles, out var h) ? h : 0);
                    }
                    run.Text = $"{procs}   {threads:N0}   {handles:N0}";
                    try { upRun.Text = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss"); } catch { }
                    SetUsageLine($"{_lastCpuPct:F0}%");
                    if (_lastFreqMhz > 0 && _perfFreqRun != null) _perfFreqRun.Text = $"{_lastFreqMhz / 1000.0:F2} GHz";
                    if (_lastFreqPeakMhz > 0 && _perfPeakRun != null) _perfPeakRun.Text = $"{_lastFreqPeakMhz / 1000.0:F2} GHz";
                }
                catch { }
            };
            tickTimer.Start();
        }

        private void SetUsageLine(string text)
        {
            if (_perfUsageLine?.Tag is System.Windows.Documents.Run r) r.Text = text;
        }

        /// <summary>Uso da memória com o QUANTO, não só o %: "80% · 25,6 GB de 32 GB".
        /// Sem os bytes, o painel dizia só "80%" e o card só "80% de 32 GB" — nunca
        /// quanto de RAM estava realmente ocupado.</summary>
        private string FormatMemUsage(float pct, ulong usedBytes, ulong totalBytes)
        {
            ulong total = totalBytes;
            double used = usedBytes;
            if (total == 0 && _totalMemBytes > 0)
            {
                total = (ulong)_totalMemBytes;
                used = total * (Math.Clamp(pct, 0f, 100f) / 100.0);
            }
            if (total == 0) return $"{pct:F0}%";
            return $"{pct:F0}% · {used / 1024.0 / 1024.0 / 1024.0:0.#} GB de {total / 1024.0 / 1024.0 / 1024.0:0.#} GB";
        }

        /// <summary>Formato compacto (legenda da barra superior): "25,6 GB de 32 GB".</summary>
        private static string FormatMemUsedText(ulong usedBytes, ulong totalBytes)
        {
            if (usedBytes == 0) return "";
            double usedGb = usedBytes / 1024.0 / 1024.0 / 1024.0;
            if (totalBytes == 0) return $"{usedGb:0.#} GB";
            return $"{usedGb:0.#} GB de {totalBytes / 1024.0 / 1024.0 / 1024.0:0.#} GB";
        }


        private System.Windows.Documents.Run AddLiveLine(string label)
        {
            var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 12, 1), TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
            var val = new System.Windows.Documents.Run("…") { Foreground = System.Windows.Media.Brushes.White };
            tb.Inlines.Add(val);
            // Mesma grade de 2 colunas do painel: o indice e compartilhado com as
            // linhas estaticas para que nada se sobreponha.
            Grid.SetColumn(tb, _perfDetailIdx % 2);
            Grid.SetRow(tb, _perfDetailIdx / 2);
            EnsureDetailRow(_perfDetailIdx / 2);
            _perfDetailIdx++;
            PnlPerfDetails.Children.Add(tb);
            return val;
        }

        /// <summary>
        /// Garante que a linha (Grid.Row) exista na grade do painel de detalhes.
        /// Sem uma RowDefinition correspondente, o WPF LIMITA o Grid.Row em 0 e
        /// todas as linhas caem na MESMA celula (texto sobreposto).
        /// </summary>
        private void EnsureDetailRow(int row)
        {
            while (PnlPerfDetails.RowDefinitions.Count <= row)
                PnlPerfDetails.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        private void UpdateMemLive()
        {
            // Comprometida / em cache / disponível — mesmas métricas do Task Manager
            try
            {
                var committed = new PerformanceCounter("Memory", "Committed Bytes") { ReadOnly = true };
                var commitLimit = new PerformanceCounter("Memory", "Commit Limit") { ReadOnly = true };
                var cached = new PerformanceCounter("Memory", "Cache Bytes") { ReadOnly = true };
                var avail = new PerformanceCounter("Memory", "Available MBytes") { ReadOnly = true };
                _ = committed.NextValue(); _ = commitLimit.NextValue(); _ = cached.NextValue(); _ = avail.NextValue();
                var cRun = AddLiveLine("Comprometida:");
                var kRun = AddLiveLine("Em cache:");
                var aRun = AddLiveLine("Disponível:");
                // Paged/Nonpaged pool
                var paged = new PerformanceCounter("Memory", "Pool Paged Bytes") { ReadOnly = true };
                var nonpaged = new PerformanceCounter("Memory", "Pool Nonpaged Bytes") { ReadOnly = true };
                _ = paged.NextValue(); _ = nonpaged.NextValue();
                var pRun = AddLiveLine("Pool paginado:");
                var npRun = AddLiveLine("Pool não-paginado:");
                var memTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                memTimer.Tick += (_, __) =>
                {
                    try
                    {
                        if (_selectedPerfDevice?.Key != "mem") { memTimer.Stop(); return; }
                        double comm = committed.NextValue(), lim = commitLimit.NextValue();
                        cRun.Text = $"{comm / 1024 / 1024 / 1024:F1}/{lim / 1024 / 1024 / 1024:F1} GB ({comm / lim * 100:F0}%)";
                        kRun.Text = $"{cached.NextValue() / 1024 / 1024 / 1024:F1} GB";
                        aRun.Text = $"{avail.NextValue():F0} MB";
                        pRun.Text = $"{paged.NextValue() / 1024 / 1024:F0} MB";
                        npRun.Text = $"{nonpaged.NextValue() / 1024 / 1024:F0} MB";
                    }
                    catch { }
                };
                memTimer.Start();
            }
            catch { }
        }


        private static System.Windows.Controls.TextBlock MkRow(string label, string value)
        {
            var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
            tb.Inlines.Add(new System.Windows.Documents.Run(value ?? "") { Foreground = System.Windows.Media.Brushes.White });
            return tb;
        }

        private static string FormatLinkSpeed(string bitsPerSecRaw)
        {
            if (double.TryParse(bitsPerSecRaw, out double bps))
            {
                if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000:F1} Gbps";
                if (bps >= 1_000) return $"{bps / 1_000:F0} Mbps";
                return $"{bps:F0} bps";
            }
            return bitsPerSecRaw;
        }

        /// <summary>Renderiza o snapshot coletado no worker — a UI thread faz SÓ desenho (sub-ms).
        /// Antes este método rodava PDH/queries nativas NA UI a cada 1s (stalls medidos de 90-930ms).</summary>
        private void RenderPerfSample(PerfSample s)
        {
            float cpuVal = s.Cpu;
            float ramVal = s.Mem;
            float diskMB = s.DiskReadMBps + s.DiskWriteMBps;
            _lastDiskReadMBps = s.DiskReadMBps;
            _lastDiskWriteMBps = s.DiskWriteMBps;
            _lastCpuPct = cpuVal;
            _lastMemPct = ramVal;
            _lastGpuPct = s.GpuPct;
            _lastGpuLive = s.GpuLive;
            _lastFreqMhz = s.FreqMhz;
            _lastFreqBaseMhz = s.FreqBaseMhz;
            _lastFreqPeakMhz = s.FreqPeakMhz;
            _lastTempC = s.TempC;
            _lastPowerW = s.PowerW;
            _lastKernelPct = s.KernelPct;
            float netMB = s.NetMBps;

            // Resumo da barra superior da aba Processos — escrita ÚNICA aqui (1 tick de 1s).
            // Antes, RefreshAsync e este tick escreviam TxtDiskUsage/TxtNetUsage/TxtGpuUsage com
            // fontes diferentes, e StartResourceMonitor (2s) amostrava os MESMOS contadores — os
            // números piscavam alternando entre valores a cada segundo. SetMetricText só reescreve
            // quando o texto muda, eliminando o flicker de re-render com o mesmo valor.
            SetMetricText(TxtCpuUsage, $"{cpuVal:F0}%", GetHeatColor(cpuVal, 80, 95));
            SetMetricText(TxtMemUsage, $"{ramVal:F0}%", GetHeatColor(ramVal, 70, 90));
            // Legenda: o % sozinho não diz QUANTA RAM está em uso
            SetMetricText(TxtMemUsageDetail, FormatMemUsedText(s.MemUsedBytes, s.MemTotalBytes), _brushGray);
            SetMetricText(TxtDiskUsage, FormatBytesSpeed(diskMB * 1024 * 1024), GetHeatColor(diskMB > 0 ? diskMB : 0f, 50, 200));
            SetMetricText(TxtNetUsage, FormatBytesSpeed(netMB * 1024 * 1024), netMB > 0 ? GetHeatColor(netMB, 5, 50) : _brushGray);
            SetMetricText(TxtGpuUsage, _lastGpuPct >= 0 ? $"{_lastGpuPct:F0}%" : "N/A", _lastGpuPct >= 0 ? GetHeatColor(_lastGpuPct, 70, 90) : _brushGray);
            // TMOG: frequência/temperatura/potência agora vêm do snapshot coletado no worker
            SetMetricText(TxtCpuFreq, s.FreqMhz > 0 ? $"{s.FreqMhz / 1000.0:F2} GHz" : "—", _brushGray);
            SetMetricText(TxtCpuTemp, s.TempC >= 0 ? $"{s.TempC:F0} °C" : "—", s.TempC >= 0 ? GetHeatColor((float)s.TempC, 75, 90) : _brushGray);
            SetMetricText(TxtCpuPower, s.PowerW >= 0 ? $"{s.PowerW:F1} W" : "—", s.PowerW >= 0 ? GetHeatColor((float)s.PowerW, 45, 80) : _brushGray);
            ChartNetText = FormatBytesSpeed(netMB * 1024 * 1024);

            // Anti-pisca/custo: canvases de abas ocultas não são redesenhados. Redesenhar
            // o PerfBigCanvas + sparklines + MiniCpu a cada segundo sem ninguém ver era
            // trabalho jogado fora — e o Children.Clear() com a aba oculta fazia a volta
            // à aba "piscar" (primeiro quadro vazio, depois o desenho).
            bool perfVisible = TabPerformance != null && TabPerformance.Visibility == Visibility.Visible;
            bool procVisible = TabProcesses != null && TabProcesses.Visibility == Visibility.Visible;

            // Atualiza cada dispositivo
            foreach (var dev in _perfDevices)
            {
                float val; float max;
                switch (dev.Key)
                {
                    case "cpu": val = cpuVal; max = 100f; break;
                    case "mem": val = ramVal; max = 100f; break;
                    case var k when k.StartsWith("disk:"):
                        val = Math.Min(100f, s.PerDevice != null && s.PerDevice.TryGetValue(k, out var c1) ? c1 : 0f);
                        max = 100f; break;
                    case var k when k.StartsWith("net:"):
                        val = s.PerDevice != null && s.PerDevice.TryGetValue(k, out var c2) ? c2 / (1024f * 1024f) : 0f;
                        max = 0; break; // autoescala
                    case var k when k.StartsWith("gpu:"):
                    {
                        // Utilização do ADAPTADOR (a engine mais ocupada), não o total da máquina
                        int gix = int.TryParse(k[4..], out var gi3) ? gi3 : -1;
                        GpuAdapterLive? glv = null;
                        if (s.GpuLive != null) s.GpuLive.TryGetValue(gix, out glv);
                        val = glv != null ? (float)Math.Clamp(glv.Utilization, 0, 100) : Math.Max(0f, _lastGpuPct);
                        max = 100f; break;
                    }
                    default: continue;
                }
                if (!_perfHistory.TryGetValue(dev.Key, out var q)) { q = new Queue<float>(61); _perfHistory[dev.Key] = q; }
                q.Enqueue(val);
                if (q.Count > 60) q.Dequeue();

                // resumo na lista lateral
                dev.Summary = dev.Key switch
                {
                    // Estilo Gerenciador de Tarefas: "% + clock ATUAL" ("32% 5,03 GHz").
                    // Antes era só "40% · 20 núcleos" — sem o relógio, que é o que muda.
                    "cpu" => $"{val:F0}% · {(s.FreqMhz > 0 ? (s.FreqMhz / 1000.0).ToString("F2") + " GHz" : "—")}",
                    "mem" => FormatMemUsage(val, s.MemUsedBytes, s.MemTotalBytes),
                    var k when k.StartsWith("net:") => FormatBytesSpeed(val * 1024 * 1024),
                    var k when k.StartsWith("gpu:") => FormatGpuCardSummary(k, val, s),
                    _ => $"{val:F0}% ativo"
                };

                // painel grande só do selecionado (e só com a aba aberta)
                if (perfVisible && dev == _selectedPerfDevice)
                {
                    if (dev.Key.StartsWith("net:"))
                        PerfDeviceUtil.Text = FormatBytesSpeed(val * 1024 * 1024);
                    else
                        PerfDeviceUtil.Text = $"{Math.Max(0f, val):F0}%";
                    PerfDeviceUtil.Foreground = GetHeatColor(val, 60, 90);
                    // Desenho direto no tick (~1s) — o gráfico é redesenhado por amostra.
                    // GPU: quem desenha é o painel de 4 engines + barras de VRAM
                    // (UpdateGpuCharts, chamado pelo UpdateGpuLive), não o canvas único.
                    if (!dev.Key.StartsWith("gpu:"))
                        DrawLineChart(PerfBigCanvas, q, FromHex(dev.ColorHex), max);
                    // Memória: a linha "Em uso" mostra o QUANTO (GB) em uso, não só o %
                    SetUsageLine(dev.Key == "mem" ? FormatMemUsage(val, s.MemUsedBytes, s.MemTotalBytes) : PerfDeviceUtil.Text);
                }
            }

            // Linhas VIVAS do painel de GPU (engines, VRAM, temperatura) — a cada tick
            try { UpdateGpuLive(); } catch { }

            // Mini previews na lista lateral — igual ao Gerenciador de Tarefas Win11
            if (perfVisible) DrawMiniPreviews();

            // Mini CPU graph no painel de detalhes do processo (aba Processos)
            if (procVisible)
            {
                _miniCpuHistory.Enqueue(cpuVal);
                if (_miniCpuHistory.Count > 30) _miniCpuHistory.Dequeue();
                DrawLineChart(MiniCpuCanvas, _miniCpuHistory, System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50), 100f);
            }
        }

        // Valor de rede compartilhado com o botão Copiar
        private string ChartNetText = "—";

        /// <summary>Kernel% do último snapshot (coletado no worker; a UI só lê).</summary>
        private float _lastKernelPct;

        /// <summary>
        /// Desenha o mini-preview do histórico em cada item visível da lista lateral
        /// (como os sparklines do Gerenciador de Tarefas Win11).
        /// </summary>
        private void DrawMiniPreviews()
        {
            if (LstPerfDevices?.Items == null || LstPerfDevices.Items.Count == 0) return;
            try
            {
                for (int i = 0; i < LstPerfDevices.Items.Count; i++)
                {
                    // ContainerFromIndex devolve o ListBoxItem (não o ContentPresenter) —
                    // buscamos o Canvas em toda a subárvore visual do container.
                    if (LstPerfDevices.ItemContainerGenerator.ContainerFromIndex(i) is not System.Windows.DependencyObject container) continue;
                    var canvas = FindVisualChild<System.Windows.Controls.Canvas>(container);
                    if (canvas == null) continue;
                    if (LstPerfDevices.Items[i] is not PerfDeviceInfo dev) continue;
                    // Sparkline redesenhada no tick (~1s). O canvas é reciclado pela
                    // virtualização — o DrawLineChart faz Children.Clear() e redesenha,
                    // então o container reaproveitado nunca fica com desenho velho.
                    bool isNet = dev.Key.StartsWith("net:");
                    _perfHistory.TryGetValue(dev.Key, out var hist);
                    DrawLineChart(canvas, hist ?? new Queue<float>(), FromHex(dev.ColorHex), isNet ? 0f : 100f);
                }
            }
            catch { }
        }

        private static void DrawLineChart(Canvas canvas, Queue<float> data, System.Windows.Media.Color lineColor, float maxVal)
        {
            canvas.Children.Clear();
            if (data == null || data.Count < 2) return;

            // Garante largura/altura mesmo antes do layout passar (evita retângulo preto vazio)
            double w = canvas.ActualWidth > 1 ? canvas.ActualWidth : (double.IsNaN(canvas.Width) ? 0 : canvas.Width);
            double h = canvas.ActualHeight > 1 ? canvas.ActualHeight : (double.IsNaN(canvas.Height) ? 0 : canvas.Height);
            if (w <= 1 || h <= 1)
            {
                // Fallback: usa a largura do pai (Border/ListBoxItem) se disponível
                if (canvas.Parent is FrameworkElement p && p.ActualWidth > 1) w = p.ActualWidth;
                if (h <= 1) h = 26; // fallback razoável
                if (w <= 1) return;
            }

            float actualMax = maxVal > 0 ? maxVal : data.Max();
            if (actualMax <= 0) actualMax = 1;

            var points = new PointCollection();
            var values = data.ToArray();
            int count = values.Length;

            // Mantém a curva DENTRO do canvas: com y = h exato, uma engine em 0%
            // desenhava a linha sobre a borda inferior e ela desaparecia — e os 4
            // gráficos da GPU precisam mostrar o "zero" como o Gerenciador de
            // Tarefas mostra. Agora 100% fica em y=1 e 0% em y=h-1.
            double plotH = Math.Max(1, h - 2);
            for (int i = 0; i < count; i++)
            {
                double x = (double)i / (count - 1) * w;
                double y = h - 1 - (Math.Clamp(values[i], 0f, actualMax) / actualMax * plotH);
                points.Add(new Point(x, y));
            }

            // Fill area
            var fillPoints = new PointCollection(points);
            fillPoints.Add(new Point(w, h));
            fillPoints.Add(new Point(0, h));

            var fillGeom = new StreamGeometry();
            using (var ctx = fillGeom.Open())
            {
                ctx.BeginFigure(fillPoints[0], true, true);
                for (int i = 1; i < fillPoints.Count; i++)
                    ctx.LineTo(fillPoints[i], true, false);
            }
            fillGeom.Freeze();

            var fillBrush = new LinearGradientBrush(
                Color.FromArgb(60, lineColor.R, lineColor.G, lineColor.B),
                Color.FromArgb(10, lineColor.R, lineColor.G, lineColor.B),
                new Point(0, 0), new Point(0, 1));
            fillBrush.Freeze();

            var fillPath = new System.Windows.Shapes.Path { Data = fillGeom, Fill = fillBrush, StrokeThickness = 0 };
            canvas.Children.Add(fillPath);

            // Line
            var lineGeom = new StreamGeometry();
            using (var ctx = lineGeom.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++)
                    ctx.LineTo(points[i], true, false);
            }
            lineGeom.Freeze();

            var lineStroke = new SolidColorBrush(lineColor);
            lineStroke.Freeze();
            var linePath = new System.Windows.Shapes.Path
            {
                Data = lineGeom,
                Stroke = lineStroke,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            canvas.Children.Add(linePath);
        }
    }
}
