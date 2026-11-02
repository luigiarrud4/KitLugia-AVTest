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
            var pcList = new List<string>();
            var mbList = new List<string>();
            var biosList = new List<string>();
            var osList = new List<string>();
            var storageList = new List<string>();
            var networkList = new List<string>();
            var audioList = new List<string>();
            var batteryList = new List<string>();
            var tpmList = new List<string>();
            var thermalList = new List<string>();

            try
            {
                // PC / System
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemType, Domain, PartOfDomain, UserName, ChassisSKUNumber, SystemFamily, HypervisorPresent FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    pcList.Add($"Fabricante: {o["Manufacturer"]}");
                    pcList.Add($"Modelo: {o["Model"]}");
                    pcList.Add($"Tipo: {o["SystemType"]}");
                    pcList.Add($"Domínio: {o["Domain"]}");
                    pcList.Add($"Usuário: {o["UserName"]}");
                    pcList.Add($"SKU: {o["ChassisSKUNumber"]}");
                    pcList.Add($"Hipervisor: {o["HypervisorPresent"]}");
                }

                // Motherboard
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard"))
                foreach (ManagementObject o in q.Get())
                {
                    mbList.Add($"Modelo: {o["Manufacturer"]} {o["Product"]}");
                    mbList.Add($"Versão: {o["Version"]}");
                    mbList.Add($"S/N: {o["SerialNumber"]}");
                }

                // BIOS
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"))
                foreach (ManagementObject o in q.Get())
                {
                    biosList.Add($"Fabricante: {o["Manufacturer"]}");
                    biosList.Add($"Versão: {o["SMBIOSBIOSVersion"]}");
                    biosList.Add($"Data: {FmtDate(o["ReleaseDate"])}");
                    biosList.Add($"S/N: {o["SerialNumber"]}");
                }

                // OS
                using (var q = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, MUILanguages, NumberOfUsers, NumberOfProcesses, TotalVirtualMemorySize, FreePhysicalMemory, FreeVirtualMemory FROM Win32_OperatingSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    osList.Add($"SO: {o["Caption"]}");
                    osList.Add($"Versão: {o["Version"]} (Build {o["BuildNumber"]})");
                    osList.Add($"Arquitetura: {o["OSArchitecture"]}");
                    osList.Add($"Instalado em: {FmtDate(o["InstallDate"])}");
                    osList.Add($"Último boot: {FmtDate(o["LastBootUpTime"])}");
                    osList.Add($"Usuários: {o["NumberOfUsers"]} | Processos: {o["NumberOfProcesses"]}");
                }

                // Storage
                using (var q = new ManagementObjectSearcher("SELECT Model, SerialNumber, Size, InterfaceType, MediaType, Partitions, BytesPerSector, TotalHeads, TotalTracks FROM Win32_DiskDrive"))
                foreach (ManagementObject o in q.Get())
                {
                    storageList.Add($"{o["Model"]} ({o["InterfaceType"]}) - {FmtBytes(o["Size"])}");
                }

                // Network
                using (var q = new ManagementObjectSearcher("SELECT Name, MACAddress, Speed, NetConnectionID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
                foreach (ManagementObject o in q.Get())
                {
                    networkList.Add($"{o["Name"]} ({o["NetConnectionID"]}) - {FmtBytes(o["Speed"])}");
                }

                // Audio
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_SoundDevice"))
                foreach (ManagementObject o in q.Get())
                {
                    audioList.Add($"{o["Name"]} ({o["Manufacturer"]})");
                }

                // Battery
                using (var q = new ManagementObjectSearcher("SELECT * FROM Win32_Battery"))
                foreach (ManagementObject o in q.Get())
                {
                    batteryList.Add($"Bateria: {o["Name"]} ({o["Chemistry"]})");
                    batteryList.Add($"Carga: {o["EstimatedChargeRemaining"]}%");
                }

                // TPM
                try
                {
                    var ns = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
                    ns.Connect();
                    using (var q = new ManagementObjectSearcher(ns, new ObjectQuery("SELECT * FROM Win32_Tpm")))
                    foreach (ManagementObject o in q.Get())
                    {
                        tpmList.Add($"Presente: {o["IsPresent_InitialValue"]}");
                        tpmList.Add($"Versão: {o["SpecVersion"]}");
                    }
                }
                catch { tpmList.Add("TPM não disponível"); }

                // Thermal
                try
                {
                    using (var q = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT * FROM MSAcpi_ThermalZoneTemperature")))
                    foreach (ManagementObject o in q.Get())
                    {
                        double t = 0;
                        try { t = (Convert.ToDouble(o["CurrentTemperature"]) - 2732) / 10.0; } catch { }
                        thermalList.Add($"Zona: {o["InstanceName"]} = {t:F1} °C");
                    }
                }
                catch { }
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Erro ao carregar dados estáticos: " + ex.Message); }

            // Assign to UI
            ListPc.ItemsSource = pcList;
            ListMotherboard.ItemsSource = mbList;
            ListBios.ItemsSource = biosList;
            ListOS.ItemsSource = osList;
            ListStorage.ItemsSource = storageList;
            ListNetwork.ItemsSource = networkList;
            ListAudio.ItemsSource = audioList;
            ListBattery.ItemsSource = batteryList;
            ListTpm.ItemsSource = tpmList;
            ListThermal.ItemsSource = thermalList;

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
