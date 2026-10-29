using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.Core.TaskManager;
using KitLugia.GUI.Services;

namespace KitLugia.GUI.Pages
{
    public partial class SystemInfoPage : Page
    {
        private readonly DispatcherTimer _timer;
        public SystemInfoPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => { await LoadStaticAsync(); await RefreshRealtimeAsync(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await RefreshRealtimeAsync();
            Loaded += (s, e) => _timer.Start();
            Unloaded += (s, e) => _timer.Stop();
        }

        private static string FmtBytes(object o)
        {
            if (o == null) return "N/A";
            try
            {
                double b = Convert.ToDouble(o);
                string[] u = { "B", "KB", "MB", "GB", "TB" };
                int i = 0;
                while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
                return $"{b:F2} {u[i]}";
            }
            catch { return o.ToString(); }
        }
        private static string FmtDate(object o)
        {
            if (o == null) return "N/A";
            try
            {
                var s = o.ToString();
                if (string.IsNullOrWhiteSpace(s)) return "N/A";
                var dt = ManagementDateTimeConverter.ToDateTime(s);
                return dt.ToString("dd/MM/yyyy HH:mm");
            }
            catch { return o.ToString(); }
        }

        private Geometry CreateArc(double pct, double radius, double cx, double cy)
        {
            pct = Math.Min(100, Math.Max(0, pct));
            double angle = pct / 100.0 * 360.0;
            if (angle >= 360) angle = 359.99;
            if (angle <= 0) return Geometry.Empty;
            double rad = angle * Math.PI / 180.0;
            double x = cx + radius * Math.Sin(rad);
            double y = cy - radius * Math.Cos(rad);
            bool largeArc = angle > 180;
            var fig = new PathFigure { StartPoint = new System.Windows.Point(cx, cy - radius), IsClosed = false };
            fig.Segments.Add(new ArcSegment(new System.Windows.Point(x, y), new System.Windows.Size(radius, radius), 0, largeArc, SweepDirection.Clockwise, true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            return geo;
        }

        private async System.Threading.Tasks.Task LoadStaticAsync()
        {
            var detail = new List<string>();
            try
            {
                detail.Add("── PC / Sistema ──");
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemType, Domain, PartOfDomain, UserName, ChassisSKUNumber, SystemFamily, HypervisorPresent FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"Fabricante do PC: {o["Manufacturer"]}");
                    detail.Add($"Modelo do PC: {o["Model"]}");
                    detail.Add($"Tipo do sistema: {o["SystemType"]}");
                    detail.Add($"Domínio: {o["Domain"]} (PartOfDomain={o["PartOfDomain"]})");
                    detail.Add($"Usuário logado: {o["UserName"]}");
                    detail.Add($"Família/SKU: {o["SystemFamily"]} / {o["ChassisSKUNumber"]}");
                    detail.Add($"Hipervisor presente: {o["HypervisorPresent"]}");
                }
                detail.Add("── Placa-mãe ──");
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"Placa-mãe: {o["Manufacturer"]} {o["Product"]}");
                    detail.Add($"Versão da placa: {o["Version"]}");
                    detail.Add($"S/N da placa: {o["SerialNumber"]}");
                }
                detail.Add("── BIOS ──");
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"BIOS: {o["Manufacturer"]} {o["SMBIOSBIOSVersion"]}");
                    detail.Add($"Data da BIOS: {FmtDate(o["ReleaseDate"])}");
                    detail.Add($"S/N BIOS: {o["SerialNumber"]}");
                }
                detail.Add("── Sistema Operacional ──");
                using (var q = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, MUILanguages, NumberOfUsers, NumberOfProcesses, TotalVirtualMemorySize, FreePhysicalMemory, FreeVirtualMemory FROM Win32_OperatingSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"SO: {o["Caption"]}");
                    detail.Add($"Versão do SO: {o["Version"]} (Build {o["BuildNumber"]})");
                    detail.Add($"Arquitetura: {o["OSArchitecture"]}");
                    detail.Add($"Instalado em: {FmtDate(o["InstallDate"])}");
                    detail.Add($"Último boot: {FmtDate(o["LastBootUpTime"])}");
                    detail.Add($"Idiomas MUI: {o["MUILanguages"]}");
                    detail.Add($"Usuários: {o["NumberOfUsers"]} | Processos: {o["NumberOfProcesses"]}");
                    detail.Add($"  Memória virtual total: {FmtBytes(Convert.ToDouble(o["TotalVirtualMemorySize"]) * 1024)}");
                    detail.Add($"  Memória física livre: {FmtBytes(Convert.ToDouble(o["FreePhysicalMemory"]) * 1024)}");
                    detail.Add($"  Memória virtual livre: {FmtBytes(Convert.ToDouble(o["FreeVirtualMemory"]) * 1024)}");
                }
                detail.Add("── CPU ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, MaxClockSpeed, CurrentClockSpeed, NumberOfCores, NumberOfLogicalProcessors, L2CacheSize, L3CacheSize, SocketDesignation, Stepping, Revision, ProcessorId, VirtualizationFirmwareEnabled, VMMonitorModeExtensions, SecondLevelAddressTranslationExtensions FROM Win32_Processor"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"CPU: {o["Name"]}");
                    detail.Add($"Fabricante CPU: {o["Manufacturer"]}");
                    detail.Add($"Clock máx: {o["MaxClockSpeed"]} MHz | Atual: {o["CurrentClockSpeed"]} MHz");
                    detail.Add($"Núcleos: {o["NumberOfCores"]} ({o["NumberOfLogicalProcessors"]} lógicos)");
                    detail.Add($"Cache L2: {o["L2CacheSize"]} KB | L3: {o["L3CacheSize"]} KB");
                    detail.Add($"Socket: {o["SocketDesignation"]}");
                    detail.Add($"Stepping/Revisão: {o["Stepping"]} / {o["Revision"]}");
                    detail.Add($"ProcessorId: {o["ProcessorId"]}");
                    detail.Add($"Virtualização firmware: {o["VirtualizationFirmwareEnabled"]} | VMMonitor: {o["VMMonitorModeExtensions"]} | SLAT: {o["SecondLevelAddressTranslationExtensions"]}");
                }
                detail.Add("── RAM (módulos) ──");
                using (var q = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, FormFactor, BankLabel, ConfiguredClockSpeed, ConfiguredVoltage FROM Win32_PhysicalMemory"))
                foreach (ManagementObject o in q.Get())
                {
                    ulong cap = 0; ulong.TryParse(o["Capacity"]?.ToString(), out cap);
                    detail.Add($"RAM: {cap / (1024 * 1024 * 1024)} GB @ {o["Speed"]} MHz (config {o["ConfiguredClockSpeed"]} MHz) ({o["Manufacturer"]})");
                    detail.Add($"  PartNumber: {o["PartNumber"]} | S/N: {o["SerialNumber"]}");
                    detail.Add($"  Tipo SMBIOS: {o["SMBIOSMemoryType"]} | FormFactor: {o["FormFactor"]} | Bank: {o["BankLabel"]}");
                    detail.Add($"  Voltagem configurada: {o["ConfiguredVoltage"]} mV");
                }
                detail.Add("── GPU ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, VideoProcessor, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, CurrentBitsPerPixel, VideoArchitecture, VideoMemoryType, InfFilename FROM Win32_VideoController"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"GPU: {o["Name"]} ({o["AdapterCompatibility"]})");
                    detail.Add($"  Processador de vídeo: {o["VideoProcessor"]}");
                    detail.Add($"  VRAM: {FmtBytes(o["AdapterRAM"])}");
                    detail.Add($"  Driver: {o["DriverVersion"]} ({FmtDate(o["DriverDate"])})");
                    detail.Add($"  Resolução: {o["CurrentHorizontalResolution"]}x{o["CurrentVerticalResolution"]} @ {o["CurrentRefreshRate"]}Hz ({o["CurrentBitsPerPixel"]}bpp)");
                    detail.Add($"  Arquitetura de vídeo: {o["VideoArchitecture"]} | Tipo memória: {o["VideoMemoryType"]}");
                    detail.Add($"  INF: {o["InfFilename"]}");
                }
                detail.Add("── Disco ──");
                using (var q = new ManagementObjectSearcher("SELECT Model, SerialNumber, Size, InterfaceType, MediaType, Partitions, BytesPerSector, TotalHeads, TotalTracks FROM Win32_DiskDrive"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"Disco: {o["Model"]} ({o["InterfaceType"]}, {o["MediaType"]})");
                    detail.Add($"  Serial: {o["SerialNumber"]} | Tamanho: {FmtBytes(o["Size"])}");
                    detail.Add($"  Partições: {o["Partitions"]} | Bytes/setor: {o["BytesPerSector"]}");
                }
                detail.Add("── Rede ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, MACAddress, Speed, NetConnectionID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"Adaptador: {o["Name"]} (MAC {o["MACAddress"]})");
                    detail.Add($"  Velocidade: {o["Speed"]} | Conexão: {o["NetConnectionID"]} | PNP: {o["PNPDeviceID"]}");
                }
                detail.Add("── Áudio ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_SoundDevice"))
                foreach (ManagementObject o in q.Get())
                    detail.Add($"Áudio: {o["Name"]} ({o["Manufacturer"]}) - {o["Status"]}");
                detail.Add("── Impressoras ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_Printer"))
                foreach (ManagementObject o in q.Get())
                    detail.Add($"Impressora: {o["Name"]} ({o["Manufacturer"]}) - {o["Status"]}");
                detail.Add("── CD/DVD ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, Drive, MediaLoaded FROM Win32_CDROMDrive"))
                foreach (ManagementObject o in q.Get())
                    detail.Add($"CD/DVD: {o["Name"]} (Drive {o["Drive"]}, Mídia carregada: {o["MediaLoaded"]})");
                detail.Add("── Dispositivos com problema ──");
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                foreach (ManagementObject o in q.Get())
                    detail.Add($"⚠️ Dispositivo com problema: {o["Name"]} ({o["Manufacturer"]}) - {o["Status"]} PNP={o["PNPDeviceID"]}");
                detail.Add("── Bateria ──");
                using (var q = new ManagementObjectSearcher("SELECT * FROM Win32_Battery"))
                foreach (ManagementObject o in q.Get())
                {
                    detail.Add($"Bateria: {o["Name"]} ({o["Chemistry"]})");
                    detail.Add($"  Carga: {o["EstimatedChargeRemaining"]}% | Tempo restante: {o["EstimatedRunTime"]} min");
                    detail.Add($"  Capacidade design: {o["DesignCapacity"]} | Carga completa: {o["FullChargeCapacity"]}");
                }
                detail.Add("── TPM ──");
                try
                {
                    var ns = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
                    ns.Connect();
                    using (var q = new ManagementObjectSearcher(ns, new ObjectQuery("SELECT * FROM Win32_Tpm")))
                    foreach (ManagementObject o in q.Get())
                    {
                        detail.Add($"TPM: presente={o["IsPresent_InitialValue"]}, habilitado={o["IsEnabled_InitialValue"]}, ativado={o["IsActivated_InitialValue"]}, pronto={o["IsOwned_InitialValue"]}");
                        detail.Add($"  Fabricante: {o["ManufacturerId"]} | Spec: {o["SpecVersion"]}");
                    }
                }
                catch { detail.Add("TPM: não disponível/não suportado"); }
                detail.Add("── Zona térmica (ACPI) ──");
                try
                {
                    using (var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT * FROM MSAcpi_ThermalZoneTemperature")))
                    foreach (ManagementObject o in q.Get())
                    {
                        double t = 0;
                        try { t = (Convert.ToDouble(o["CurrentTemperature"]) - 2732) / 10.0; } catch { }
                        detail.Add($"Zona térmica ACPI: {o["InstanceName"]} = {t:F1} °C");
                    }
                }
                catch { }
            }
            catch (Exception ex) { detail.Add("Falha WMI: " + ex.Message); }
            ListSystemDetail.ItemsSource = detail;

            try
            {
                TxtPc.Text = $"PC: {Environment.MachineName}";
                TxtArch.Text = $"Arquitetura: {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}";
                TxtCores.Text = $"Núcleos lógicos: {Environment.ProcessorCount}";
            }
            catch { }
        }

        private async System.Threading.Tasks.Task RefreshRealtimeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var dm = new DashboardManager();
                var s = await dm.GetSystemSnapshotAsync(cts.Token);

                TxtOs.Text = $"SO: {s.OsName} (Build {Environment.OSVersion.Version})";
                var up = s.Uptime;
                string upStr = up.TotalDays >= 1
                    ? $"{(int)up.TotalDays}d {up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}"
                    : $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
                TxtUptime.Text = $"Uptime: {upStr}";

                TxtCpuName.Text = s.CpuName ?? "N/A";
                double cpuTemp = s.CpuTemp;
                try { var t = NativeMetricsHelper.GetCpuTemperatureC(); if (t.HasValue) cpuTemp = t.Value; } catch { }
                TxtCpuPct.Text = $"{s.CpuLoad:F0}%";
                ArcCpu.Data = CreateArc(s.CpuLoad, 46, 55, 55);
                TxtCpuTemp.Text = cpuTemp > 0 ? $"Temp: {cpuTemp:F1} °C" : "Temp: N/A";

                double gpuPct = 0;
                try { gpuPct = GpuMonitor.GetTotalGpuUtilization(); } catch { }
                double gpuTemp = s.GpuTemp;
                try
                {
                    var adapters = GpuMetrics.GetAdapters();
                    foreach (var a in adapters)
                        if (!string.IsNullOrEmpty(s.GpuName) && a.Name.Contains(s.GpuName, StringComparison.OrdinalIgnoreCase) && a.TemperatureC > 0)
                            gpuTemp = a.TemperatureC;
                }
                catch { }
                TxtGpuName.Text = s.GpuName ?? "N/A";
                TxtGpuPct.Text = $"{gpuPct:F0}%";
                ArcGpu.Data = CreateArc(gpuPct, 46, 55, 55);
                TxtGpuTemp.Text = gpuTemp > 0 ? $"Temp: {gpuTemp:F1} °C" : "Temp: N/A";

                TxtRam.Text = $"{s.RamUsed:F2} / {s.RamTotal:F2} GB";
                double pct = s.RamTotal > 0 ? (s.RamUsed / s.RamTotal * 100.0) : 0;
                TxtRamPct.Text = $"{pct:F0}%";
                ArcRam.Data = CreateArc(pct, 46, 55, 55);
                TxtRamUsed.Text = $"Usado: {pct:F1}%";

                var storage = new List<string>();
                foreach (var d in s.StorageDevices)
                    storage.Add($"{d.Name} ({d.DriveLetter}) - {d.HealthStatus}");
                ListStorage.ItemsSource = storage;

                var net = new List<string>();
                try
                {
                    var adapters = Toolbox.GetAllNetworkAdapters();
                    if (adapters.Count == 0) net.Add("Nenhum adaptador detectado");
                    foreach (var a in adapters)
                        net.Add($"{a.AdapterName} ({a.AdapterType}) - {a.Description} - {a.DataUsage}");
                }
                catch (Exception ex) { net.Add("Falha: " + ex.Message); }
                ListNetwork.ItemsSource = net;
            }
            catch (Exception ex)
            {
                TxtPc.Text = "Falha ao ler hardware: " + ex.Message;
            }
            }

}
}
