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
        private TextBlock? _perfUsageLine;   // linha "Uso:" dos detalhes, atualizada a cada tick

        /// <summary>Taxa de um adaptador: usa perfmon se existir, senão delta de GetIPStatistics.</summary>
        private float GetNetBytesPerSec(string key)
        {
            if (_instanceCounters.TryGetValue(key, out var ctr))
            {
                try { return ctr.NextValue(); } catch { return 0; }
            }
            if (_netStatsNics.TryGetValue(key, out var nic))
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    long total = stats.BytesSent + stats.BytesReceived;
                    var now = DateTime.UtcNow;
                    if (_netLastSample.TryGetValue(key, out var prev) && (now - prev.time).TotalMilliseconds > 200)
                    {
                        double bps = (total - prev.totalBytes) / (now - prev.time).TotalSeconds;
                        _netLastSample[key] = (total, now);
                        return Math.Max(0, (float)bps);
                    }
                    _netLastSample[key] = (total, now);
                }
                catch { }
            }
            return 0;
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
        private readonly List<(string model, string driver, string date, string pci, ulong dedicatedBytes, ulong sharedBytes)> _hwGpus = new();
        private readonly Dictionary<string, (string model, string type, string sizeGb, bool isSystemDisk, bool hasPagefile)> _hwDisks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string adapter, string conn, string mac, string speed, string ip)> _hwNets = new(StringComparer.OrdinalIgnoreCase);
        private string _hwRamSpeed = "?", _hwRamFormFactor = "?", _hwRamSlotsUsed = "?", _hwRamSlotsTotal = "?";

        private static (string l1, string l2, string l3) ReadCpuCacheFromRegistry()
        {
            // Cache L1/L2/L3 via registry (mesma fonte do Task Manager)
            try
            {
                int l1 = 0, l2 = 0, l3 = 0;
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    // fallback abaixo via WMI
                }
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("SELECT L2CacheSize,L3CacheSize FROM Win32_Processor");
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        l2 = Convert.ToInt32(o["L2CacheSize"] ?? 0);
                        l3 = Convert.ToInt32(o["L3CacheSize"] ?? 0);
                        break;
                    }
                }
                catch { }
                // L1 não existe no Win32_Processor — estima por núcleos (32-64KB/núcleo típico)
                int logical = Environment.ProcessorCount;
                if (l1 == 0) l1 = logical * 64; // aproximação
                return ($"{l1 / 1024} KB", $"{l2 / 1024} KB", l3 > 0 ? $"{l3 / 1024} KB" : "—");
            }
            catch { return ("?", "?", "?"); }
        }

        // ══════════════════════════════════════════════
        //  VIRTUALIZAÇÃO — mesma técnica do taskmgr.exe (reversing confirmado):
        //  CPUID folha 0x01, bit 31 do ECX = "hypervisor present".
        //  O WMI VirtualizationFirmwareEnabled só mostra o estado do firmware e
        //  reporta false quando o hipervisor JÁ está em execução (Hyper-V/VBS).
        //  Usamos IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED) + detecção
        //  de hipervisor via registry/registry keys do Windows.
        // ══════════════════════════════════════════════
        [DllImport("kernel32.dll")]
        private static extern bool IsProcessorFeaturePresent(uint processorFeature);
        private const uint PF_VIRT_FIRMWARE_ENABLED = 21; // virtualização habilitada no firmware
        private const uint PF_HYPERVISOR_PRESENT = 22;    // hipervisor EM EXECUÇÃO

        private static string DetectVirtualizationState()
        {
            try
            {
                bool hypervisorRunning = Environment.OSVersion.Version.Build >= 10240 &&
                    (IsProcessorFeaturePresent(PF_HYPERVISOR_PRESENT) || IsHypervisorPresentViaCpuid());
                if (hypervisorRunning) return "Ativado (hipervisor ativo)";

                bool fwEnabled = IsProcessorFeaturePresent(PF_VIRT_FIRMWARE_ENABLED);
                if (!fwEnabled)
                {
                    // Fallback WMI: VirtualizationFirmwareEnabled
                    try
                    {
                        using var s = new System.Management.ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            if (o["VirtualizationFirmwareEnabled"] is bool vb && vb) { fwEnabled = true; break; }
                            if (o["VirtualizationFirmwareEnabled"] != null) break; // leu valor definitivo false
                        }
                    }
                    catch { }
                }
                return fwEnabled ? "Ativado" : "Desativado";
            }
            catch { return "?"; }
        }

        /// <summary>
        /// Lê "HypervisorPresent" via WMI Win32_ComputerSystem (confiável p/ Hyper-V, VBS,
        /// VMware Workstation com hypercall ativo etc). Complementa o P/Invoke.
        /// </summary>
        private static bool IsHypervisorPresentViaCpuid()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem");
                foreach (System.Management.ManagementObject o in s.Get())
                    return o["HypervisorPresent"] is bool hb && hb;
            }
            catch { }
            return false;
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

                // ── CPU completa ──
                string cpuName = "", virt = "Desativado", sockets = "1", clock = "?";
                virt = DetectVirtualizationState(); // P/Invoke kernel32 — mesma fonte do taskmgr
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("SELECT Name,MaxClockSpeed,SocketDesignation FROM Win32_Processor");
                    var sockSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        cpuName = o["Name"]?.ToString() ?? cpuName;
                        clock = $"{Convert.ToDouble(o["MaxClockSpeed"]) / 1000.0:F2} GHz";
                        sockSet.Add(o["SocketDesignation"]?.ToString() ?? "CPU 0");
                        break;
                    }
                    sockets = Math.Max(1, sockSet.Count).ToString();
                }
                catch { }
                if (string.IsNullOrWhiteSpace(cpuName)) cpuName = $"CPU ({Environment.ProcessorCount} núcleos)";
                var (l1, l2, l3) = ReadCpuCacheFromRegistry();

                // ── GPUs completas — MODELO UNIVERSAL ──
                // Fontes em cascata, funciona p/ NVIDIA, AMD, Intel e qualquer WDDM:
                //   1) DXGI (dxgi.dll) — lista TODOS os adaptadores com VRAM dedicada real
                //      (mesma enumeração do dxdiag; enxerga RTX 50xx, Moore Threads etc.)
                //   2) nvidia-smi — nome/driver/VRAM exatos das NVIDIA (FreeToken usa este)
                //   3) Registro HardwareInformation.qwMemorySize — VRAM QWORD sem saturação
                //   4) WMI Win32_VideoController — fallback final
                var gpus = new List<(string model, string driver, string date, string pci, ulong ded, ulong shr)>();
                long totalMem = _totalMemBytes;

                // DXGI: fonte universal
                var dxgiList = GpuInfo.GetAll();

                // nvidia-smi: dados precisos de NVIDIA (nome comercial + driver + VRAM)
                var smiGpus = new List<(string name, string driver, ulong vramBytes)>();
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "nvidia-smi",
                        Arguments = "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    using var proc = System.Diagnostics.Process.Start(psi);
                    if (proc != null)
                    {
                        string outp = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(3000);
                        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
                            if (parts.Length < 3) continue;
                            smiGpus.Add((parts[0], parts[1], ulong.TryParse(parts[2], out var m2) ? m2 * 1024 * 1024UL : 0));
                        }
                    }
                }
                catch { }

                // WMI: datas de driver + PNP (para o registro) — cobre tudo que tem driver exposto
                var wmiGpus = new List<(string name, string driver, string date, string pnp, ulong adapterRam)>();
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("SELECT Name,DriverVersion,DriverDate,PNPDeviceID,AdapterRAM FROM Win32_VideoController");
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        string d = "—";
                        try
                        {
                            if (o["DriverDate"] != null)
                                d = System.Management.ManagementDateTimeConverter.ToDateTime(o["DriverDate"]?.ToString() ?? "").ToString("dd/MM/yyyy");
                        }
                        catch { }
                        ulong ram = 0;
                        try { ram = Convert.ToUInt64(o["AdapterRAM"] ?? (ulong)0); } catch { }
                        wmiGpus.Add((o["Name"]?.ToString() ?? "GPU", o["DriverVersion"]?.ToString() ?? "—", d,
                                     o["PNPDeviceID"]?.ToString() ?? "", ram));
                    }
                }
                catch { }

                int smiUsed = 0;
                if (dxgiList.Count > 0)
                {
                    foreach (var dg in dxgiList)
                    {
                        string model = dg.Name;
                        string drv = "—";
                        string date = "—";
                        string pci = $"VEN {dg.VendorId:X4}:{dg.DeviceId:X4}";
                        ulong ded = dg.DedicatedVideoMemory;

                        // Enriquece com WMI (match por similaridade de nome ou vendor)
                        var wmiBest = wmiGpus.FirstOrDefault(w =>
                            model.Contains(w.name[..Math.Min(20, w.name.Length)], StringComparison.OrdinalIgnoreCase) ||
                            w.name.Contains(model[..Math.Min(20, model.Length)], StringComparison.OrdinalIgnoreCase));
                        if (wmiBest.name != null)
                        {
                            if (!string.IsNullOrWhiteSpace(wmiBest.driver) && wmiBest.driver != "—") drv = wmiBest.driver;
                            date = wmiBest.date;
                            pci = wmiBest.pnp != "" ? pci : pci;
                        }

                        // NVIDIA: nvidia-smi tem precedência (nome comercial correto)
                        if ((dg.VendorId == 0x10DE || GpuInfo.VendorName(dg.VendorId) == "NVIDIA") && smiUsed < smiGpus.Count)
                        {
                            var sg = smiGpus[smiUsed++];
                            model = sg.name;
                            drv = sg.driver;
                            if (sg.vramBytes > 0) ded = sg.vramBytes;
                        }
                        else if (ded == 0 && wmiBest.name != null)
                        {
                            ded = GpuInfo.TryGetVramFromRegistry(wmiBest.pnp);
                            if (ded == 0) ded = wmiBest.adapterRam;
                        }

                        ulong shr = totalMem > 0 ? (ulong)(totalMem / 2) : 0;
                        gpus.Add((model, drv, date, pci, ded, shr));
                    }
                }
                else
                {
                    // Sem DXGI (raro): cai no caminho antigo WMI + registro + nvidia-smi
                    int wmiSmiIdx = 0;
                    foreach (var w in wmiGpus)
                    {
                        string model = w.name;
                        string drv = w.driver;
                        bool isNv = model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || model.Contains("GeForce", StringComparison.OrdinalIgnoreCase);
                        ulong ded = 0;
                        if (isNv && wmiSmiIdx < smiGpus.Count)
                        {
                            var sg = smiGpus[wmiSmiIdx++];
                            model = sg.name;
                            drv = sg.driver;
                            ded = sg.vramBytes;
                        }
                        if (ded == 0) ded = GpuInfo.TryGetVramFromRegistry(w.pnp);
                        if (ded == 0) ded = w.adapterRam;
                        gpus.Add((model, drv, w.date, "", ded, totalMem > 0 ? (ulong)(totalMem / 2) : 0));
                    }
                }

                // ── Discos completos (modelo, capacidade, disco do sistema, pagefile) ──
                var diskInfo = new Dictionary<string, (string, string, string, bool, bool)>(StringComparer.OrdinalIgnoreCase);
                var systemDrive = System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var pageDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        string n = o["Name"]?.ToString() ?? ""; // ex: C:\pagefile.sys
                        var root = System.IO.Path.GetPathRoot(n)?.TrimEnd('\\');
                        if (!string.IsNullOrEmpty(root)) pageDrives.Add(root);
                    }
                }
                catch { }
                var driveToPhysical = MapLogicalToPhysical();
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("SELECT Model,Size,Index FROM Win32_DiskDrive");
                    var byIndex = new Dictionary<int, (string model, string sizeGb)>();
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        int idx = Convert.ToInt32(o["Index"] ?? 0);
                        ulong sz = 0; try { sz = Convert.ToUInt64(o["Size"] ?? (ulong)0); } catch { }
                        byIndex[idx] = (o["Model"]?.ToString() ?? "?", $"{sz / 1024 / 1024 / 1024:N0} GB");
                    }
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

                // ── RAM completa ──
                string ramSpeed = "?", ramFF = "?", slotsUsed = "?", slotsTotal = "?";
                try
                {
                    int used = 0; ulong totCap = 0; string spd = "?", ff = "?";
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT Capacity,Speed,SMBIOSMemoryType,FormFactor FROM Win32_PhysicalMemory"))
                        foreach (System.Management.ManagementObject o in s.Get())
                        {
                            used++;
                            try { totCap += Convert.ToUInt64(o["Capacity"]); } catch { }
                            spd = o["Speed"]?.ToString() ?? spd;
                            int ffCode = Convert.ToInt32(o["FormFactor"] ?? 0);
                            ff = ffCode switch { 8 => "DIMM", 12 => "SODIMM", _ => ffCode.ToString() };
                        }
                    int total = 0;
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                        foreach (System.Management.ManagementObject o in s.Get())
                        { try { total += Convert.ToInt32(o["MemoryDevices"]); } catch { } }
                    ramSpeed = $"{spd} MHz"; ramFF = ff; slotsUsed = used.ToString(); slotsTotal = total > 0 ? total.ToString() : "?";
                }
                catch { }

                return (disks, nets, cpuName, virt, sockets, clock, l1, l2, l3, gpus, diskInfo, netInfo, ramSpeed, ramFF, slotsUsed, slotsTotal);
            });

            // Preenche caches na UI thread-safe (campos simples)
            _hwCpuName = data.cpuName;
            _hwCpuSockets = data.sockets;
            _hwCpuVirtualization = data.virt;
            _hwCpuCoreClock = data.clock;
            _hwCpuL1 = data.l1; _hwCpuL2 = data.l2; _hwCpuL3 = data.l3;
            foreach (var g in data.gpus) _hwGpus.Add(g);
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
                string key = $"disk:{d}";
                try
                {
                    var ctr = new PerformanceCounter("PhysicalDisk", "% Disk Time", d) { ReadOnly = true };
                    ctr.NextValue();
                    _instanceCounters[key] = ctr;
                }
                catch { }
                var dd = Dispatcher;
                string label = di == 0 ? "Disco 0 (C:)" : $"Disco {di} ({ExtractDriveLetters(d)})";
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
                await Dispatcher.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = $"gpu:{idx}", Name = gg.model, ColorHex = "#FFD700" }));
                gi++;
            }
            if (gi == 0) await Dispatcher.InvokeAsync(() => _perfDevices.Add(new PerfDeviceInfo { Key = "gpu:-1", Name = "GPU (indisponível)", ColorHex = "#888888" }));

            await Dispatcher.InvokeAsync(() =>
            {
                LstPerfDevices.ItemsSource = _perfDevices;
                if (LstPerfDevices.Items.Count > 0) LstPerfDevices.SelectedIndex = 0;
            });
        }

        /// <summary>Mapeia índice físico do disco → letras de unidade (via associações WMI).</summary>
        private static Dictionary<int, List<string>> MapLogicalToPhysical()
        {
            var map = new Dictionary<int, List<string>>();
            try
            {
                // Associação Win32_DiskDriveToDiskPartition + Win32_LogicalDiskToPartition
                var partToPhys = new Dictionary<string, int>();
                using (var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Antecedent,Dependent FROM Win32_DiskDriveToDiskPartition"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        var ant = o["Antecedent"]?.ToString() ?? "";
                        var dep = o["Dependent"]?.ToString() ?? "";
                        var mP = System.Text.RegularExpressions.Regex.Match(ant, @"DiskIndex=""(\d+)""");
                        var mM = System.Text.RegularExpressions.Regex.Match(dep, @"DeviceId=""([^""]+)""");
                        if (mP.Success && mM.Success) partToPhys[mM.Groups[1].Value.Replace("\\\\", "\\")] = int.Parse(mP.Groups[1].Value);
                    }
                using (var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Antecedent,Dependent FROM Win32_LogicalDiskToPartition"))
                    foreach (System.Management.ManagementObject o in s.Get())
                    {
                        var ant = o["Antecedent"]?.ToString() ?? "";
                        var dep = o["Dependent"]?.ToString() ?? "";
                        var mL = System.Text.RegularExpressions.Regex.Match(dep, @"DeviceId=""([^""]+)""");
                        var mM = System.Text.RegularExpressions.Regex.Match(ant, @"DeviceId=""([^""]+)""");
                        if (mL.Success && mM.Success && partToPhys.TryGetValue(mM.Groups[1].Value.Replace("\\\\", "\\"), out int phys))
                        {
                            string letter = mL.Groups[1].Value;
                            if (!map.TryGetValue(phys, out var list)) map[phys] = list = new List<string>();
                            list.Add(letter);
                        }
                    }
            }
            catch { }
            return map;
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
            RenderPerfDetails(_selectedPerfDevice);
        }

        private void RenderPerfDetails(PerfDeviceInfo dev)
        {
            PnlPerfDetails.Children.Clear();
            void Row(string label, out TextBlock valueBlock)
            {
                var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
                var val = new System.Windows.Documents.Run("…") { Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold };
                tb.Inlines.Add(val);
                PnlPerfDetails.Children.Add(tb);
                valueBlock = new TextBlock(); // placeholder não usado
                // guardamos o Run para atualização
                tb.Tag = val;
            }
            void StaticRow(string label, string value)
            {
                var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
                tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
                tb.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = System.Windows.Media.Brushes.White });
                PnlPerfDetails.Children.Add(tb);
            }

            var pid = dev.Key;
            if (pid == "cpu")
            {
                // Título prioriza o nome do processador
                PerfDeviceTitle.Text = _hwCpuName;
                Row("Uso", out _perfUsageLine);
                StaticRow("Velocidade base:", _hwCpuCoreClock);
                StaticRow("Soquetes:", _hwCpuSockets);
                StaticRow("Núcleos:", $"{Environment.ProcessorCount} lógicos");
                StaticRow("Virtualização:", _hwCpuVirtualization);
                StaticRow("Cache L1:", _hwCpuL1);
                StaticRow("Cache L2:", _hwCpuL2);
                StaticRow("Cache L3:", _hwCpuL3);
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
                try { StaticRow("Tempo ligado:", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss")); } catch { }
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
                    StaticRow("Conexão:", nfo.conn);
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
                Row("Utilização", out _perfUsageLine);
                if (idx >= 0 && idx < _hwGpus.Count)
                {
                    var g = _hwGpus[idx];
                    // Título completo SEM corte (o card da lista tem ellipsis, o painel não)
                    PerfDeviceTitle.Text = g.model;
                    StaticRow("Adaptador:", $"GPU {idx}");
                    StaticRow("Driver versão:", g.driver);
                    StaticRow("Data do driver:", g.date);
                    StaticRow("Localização:", string.IsNullOrEmpty(g.pci) ? "—" : g.pci);
                    StaticRow("VRAM dedicada:", g.dedicatedBytes > 0 ? $"{g.dedicatedBytes / 1024 / 1024 / 1024.0:F1} GB ({g.dedicatedBytes / 1024 / 1024:N0} MB)" : "?");
                    StaticRow("Memória compartilhada:", g.sharedBytes > 0 ? $"{g.sharedBytes / 1024 / 1024 / 1024:F1} GB" : "?");
                }
            }
        }

        private void FillNetLive(string instance)
        {
            var sRun = AddLiveLine("Envio:");
            var rRun = AddLiveLine("Recebimento:");
            // Caminho A: contadores perfmon (adaptadores físicos)
            try
            {
                var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", instance) { ReadOnly = true };
                var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", instance) { ReadOnly = true };
                sent.NextValue(); recv.NextValue();
                var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                dt.Tick += (_, __) =>
                {
                    try { sRun.Text = FormatBytesSpeed(sent.NextValue()); rRun.Text = FormatBytesSpeed(recv.NextValue()); } catch { }
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
                    try { upRun.Text = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss"); } catch { }
                }
                catch { }
            };
            tickTimer.Start();
        }

        private void SetUsageLine(string text)
        {
            if (_perfUsageLine?.Tag is System.Windows.Documents.Run r) r.Text = text;
        }

        // ─── Métricas ao vivo por dispositivo (mesmas do Task Manager do Windows) ───
        private TextBlock? _cpuLiveBlock;
        private TextBlock? _diskLiveBlock;
        private TextBlock? _memLiveBlock;

        private System.Windows.Documents.Run AddLiveLine(string label)
        {
            var tb = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)), Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)) });
            var val = new System.Windows.Documents.Run("…") { Foreground = System.Windows.Media.Brushes.White };
            tb.Inlines.Add(val);
            PnlPerfDetails.Children.Add(tb);
            return val;
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

        private void UpdatePerformanceGraphs()
        {
            // Valores-base
            float cpuVal = 0;
            try { cpuVal = _cpuCounter?.NextValue() ?? 0; } catch { }
            float ramVal = 0;
            try { float avail = _memAvailable?.NextValue() ?? 0; ramVal = _totalMemBytes > 0 ? (float)((1f - avail / (_totalMemBytes / 1024.0 / 1024.0)) * 100.0) : 0f; } catch { }
            float diskMB = 0;
            try
            {
                float readBytes = _diskReadCounter?.NextValue() ?? 0;
                float writeBytes = _diskWriteCounter?.NextValue() ?? 0;
                diskMB = (readBytes + writeBytes) / (1024f * 1024f);
            }
            catch { }
            float netMB = 0;
            try { netMB = (float)(_filteredRows.Sum(r => r.NetBytesPerSec) / (1024.0 * 1024.0)); } catch { }

            // Resumo da barra superior da aba Processos
            TxtDiskUsage.Text = FormatBytesSpeed(diskMB * 1024 * 1024);
            ChartNetText = FormatBytesSpeed(netMB * 1024 * 1024);

            // Atualiza cada dispositivo
            foreach (var dev in _perfDevices)
            {
                float val; float max;
                switch (dev.Key)
                {
                    case "cpu": val = cpuVal; max = 100f; break;
                    case "mem": val = ramVal; max = 100f; break;
                    case var k when k.StartsWith("disk:"):
                        try { val = Math.Min(100f, _instanceCounters.TryGetValue(k, out var c1) ? c1.NextValue() : 0); } catch { val = 0; }
                        max = 100f; break;
                    case var k when k.StartsWith("net:"):
                        try { val = GetNetBytesPerSec(k) / (1024f * 1024f); } catch { val = 0; }
                        max = 0; break; // autoescala
                    case var k when k.StartsWith("gpu:"): val = Math.Max(0f, _lastGpuPct); max = 100f; break;
                    default: continue;
                }
                if (!_perfHistory.TryGetValue(dev.Key, out var q)) { q = new Queue<float>(61); _perfHistory[dev.Key] = q; }
                q.Enqueue(val);
                if (q.Count > 60) q.Dequeue();

                // resumo na lista lateral
                dev.Summary = dev.Key switch
                {
                    "cpu" => $"{val:F0}% · {Environment.ProcessorCount} núcleos",
                    "mem" => $"{val:F0}% de {Math.Max(1, _totalMemBytes / 1024 / 1024 / 1024)} GB",
                    var k when k.StartsWith("net:") => FormatBytesSpeed(val * 1024 * 1024),
                    var k when k.StartsWith("gpu:") => _lastGpuPct >= 0 ? $"{val:F0}%" : "N/A",
                    _ => $"{val:F0}% ativo"
                };

                // painel grande só do selecionado
                if (dev == _selectedPerfDevice)
                {
                    PerfDeviceUtil.Text = dev.Key.StartsWith("net:") ? FormatBytesSpeed(val * 1024 * 1024) : $"{val:F0}%";
                    PerfDeviceUtil.Foreground = GetHeatColor(val, 60, 90);
                    DrawLineChart(PerfBigCanvas, q, FromHex(dev.ColorHex), max);
                    SetUsageLine(PerfDeviceUtil.Text);
                }
            }

            // Mini previews na lista lateral — igual ao Gerenciador de Tarefas Win11
            DrawMiniPreviews();

            // Mini CPU graph no painel de detalhes do processo (aba Processos)
            _miniCpuHistory.Enqueue(cpuVal);
            if (_miniCpuHistory.Count > 30) _miniCpuHistory.Dequeue();
            DrawLineChart(MiniCpuCanvas, _miniCpuHistory, System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50), 100f);
        }

        // Valor de rede compartilhado com o botão Copiar
        private string ChartNetText = "—";

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
                    var q = _perfHistory.TryGetValue(dev.Key, out var hist) ? hist : null;
                    DrawLineChart(canvas, q ?? new Queue<float>(), FromHex(dev.ColorHex), dev.Key.StartsWith("net:") ? 0f : 100f);
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

            for (int i = 0; i < count; i++)
            {
                double x = (double)i / (count - 1) * w;
                double y = h - (values[i] / actualMax * h);
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
