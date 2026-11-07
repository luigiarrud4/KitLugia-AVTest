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

        // Trava de corrida: 0 = livre, 1 = uma coleta em andamento. O timer dispara
        // a cada 2s e a coleta de hardware pode passar de 2s (WMI/GPU/rede lentos):
        // sem esta trava o proximo tick roda POR CIMA do anterior e os dois disputam
        // a mesma lista da UI (dados piscando / lista trocando sozinha).
        private int _refreshBusy;

        // O timer so' comeca DEPOIS da carga inicial (evita duas respostas ao mesmo tempo).
        private bool _initialLoadDone;
        private bool _pageUnloaded;
        private CancellationTokenSource? _loadCts;

        // Escopos WMI usados pela coleta estatica
        private const string TpmScope = @"\\.\root\CIMV2\Security\MicrosoftTpm";
        private const string WmiScope = @"\\.\root\wmi";

        public SystemInfoPage()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (s, e) => await RefreshRealtimeAsync();
            Loaded += SystemInfoPage_Loaded;
            Unloaded += SystemInfoPage_Unloaded;
        }

        private async void SystemInfoPage_Loaded(object sender, RoutedEventArgs e)
        {
            _pageUnloaded = false;

            if (!_initialLoadDone)
            {
                _initialLoadDone = true;
                SetLoading(true, "Coletando informações do hardware...");
                try
                {
                    // As duas coletas rodam EM PARALELO: a estatica (WMI) e a realtime
                    // (CPU/GPU/rede) nao dependem uma da outra. O overlay so' some
                    // quando as DUAS terminam — assim a pagina nunca aparece pela
                    // metade (o "grafico primeiro, informacoes depois" que o usuario
                    // reclamou).
                    var estatica = LoadStaticAsync();
                    var realtime = RefreshRealtimeAsync();
                    await Task.WhenAll(estatica, realtime);
                }
                catch (Exception ex)
                {
                    Logger.LogError("SystemInfoPage", "Falha na carga inicial: " + ex.Message);
                    ShowError("Falha ao carregar as informações: " + ex.Message);
                }
                finally
                {
                    SetLoading(false);
                }
            }

            if (!_pageUnloaded) _timer.Start();
        }

        private void SystemInfoPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _pageUnloaded = true;
            _timer.Stop();
            try { _loadCts?.Cancel(); } catch { }
        }

        private void SetLoading(bool visible, string message = "")
        {
            if (LoadingOverlay == null) return;
            if (!string.IsNullOrEmpty(message) && TxtLoadingMessage != null)
                TxtLoadingMessage.Text = message;
            LoadingOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            if (TxtLoadError == null) return;
            TxtLoadError.Text = message;
            TxtLoadError.Visibility = Visibility.Visible;
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

        #region CARGA ESTATICA (WMI)

        /// <summary>
        /// Dados estaticos (WMI). A coleta roda FORA DA THREAD DA UI: as queries de
        /// ManagementObjectSearcher travavam a janela por ~5 s e eram a causa do
        /// congelamento desta pagina.
        /// </summary>
        private async Task LoadStaticAsync()
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            StaticInfo info;
            try
            {
                info = await Task.Run(() => CollectStatic(token), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError("SystemInfoPage", "Falha ao coletar dados estaticos: " + ex.Message);
                ShowError("Falha ao ler dados do hardware: " + ex.Message);
                return;
            }

            if (_pageUnloaded) return;

            // Assign to UI
            ListPc.ItemsSource = info.Pc;
            ListMotherboard.ItemsSource = info.Motherboard;
            ListBios.ItemsSource = info.Bios;
            ListOS.ItemsSource = info.Os;
            ListStorage.ItemsSource = info.Storage;
            ListNetwork.ItemsSource = info.Network;
            ListAudio.ItemsSource = info.Audio;
            ListBattery.ItemsSource = info.Battery;
            ListThermal.ItemsSource = info.Thermal;

            // O TPM ainda nao veio (connect() no namespace do TPM leva ~5 s): a lista
            // mostra o aviso e e' preenchida quando a consulta volta.
            ListTpm.ItemsSource = new List<string> { "Lendo TPM (pode levar alguns segundos)..." };

            TxtPc.Text = $"PC: {info.MachineName}";
            TxtArch.Text = $"Arquitetura: {info.Arch}";
            TxtCores.Text = $"Núcleos lógicos: {info.Cores}";

            // O TPM roda solto: ele NAO segura a pagina (so' o cartao dele demora).
            _ = Task.Run(() => CollectTpm(token), token).ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled) return;
                var lista = t.Result;
                try
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_pageUnloaded) return;
                        ListTpm.ItemsSource = lista;
                    });
                }
                catch { }
            });
        }

        /// <summary>
        /// Cada lista tem UM dono: a carga estatica escreve nas listas e o realtime
        /// NUNCA reescreve as mesmas listas (era o que fazia os dados piscarem).
        /// </summary>
        private sealed class StaticInfo
        {
            public List<string> Pc = new();
            public List<string> Motherboard = new();
            public List<string> Bios = new();
            public List<string> Os = new();
            public List<string> Storage = new();
            public List<string> Network = new();
            public List<string> Audio = new();
            public List<string> Battery = new();
            public List<string> Tpm = new();
            public List<string> Thermal = new();
            public string MachineName = "-";
            public string Arch = "-";
            public string Cores = "-";
        }

        /// <summary>
        /// As queries WMI sao independentes: cada uma roda no seu thread. Medido na
        /// maquina: PC/BIOS 63 ms, SO/Disco 91 ms, rede 144 ms, audio 52 ms,
        /// bateria 43 ms, termica 53 ms. O TPM fica de fora (o connect() dele leva
        /// ~5 s) e roda solto, sem segurar a pagina.
        /// </summary>
        private static StaticInfo CollectStatic(CancellationToken token)
        {
            var info = new StaticInfo();

            Task[] grupos =
            {
                Task.Run(() => GrupoSistema(info, token), token),
                Task.Run(() => GrupoSoEDisco(info, token), token),
                Task.Run(() => GrupoRedeEPerifericos(info, token), token)
            };
            try { Task.WaitAll(grupos); }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Falha na coleta WMI paralela: " + ex.Message); }

            // Cancelado propaga para o chamador (a tela nao mostra dados pela metade)
            token.ThrowIfCancellationRequested();

            try
            {
                info.MachineName = Environment.MachineName;
                info.Arch = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
                info.Cores = Environment.ProcessorCount.ToString();
            }
            catch { }

            return info;
        }

        /// <summary>Grupo 1: PC, placa-mae e BIOS. Cada grupo escreve so' nas
        /// SUAS listas — assim nenhum grupo disputa a mesma lista com outro.</summary>
        private static void GrupoSistema(StaticInfo info, CancellationToken token)
        {
            try
            {
                // PC / System
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Model, SystemType, Domain, PartOfDomain, UserName, ChassisSKUNumber, SystemFamily, HypervisorPresent FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Pc.Add($"Fabricante: {o["Manufacturer"]}");
                    info.Pc.Add($"Modelo: {o["Model"]}");
                    info.Pc.Add($"Tipo: {o["SystemType"]}");
                    info.Pc.Add($"Domínio: {o["Domain"]}");
                    info.Pc.Add($"Usuário: {o["UserName"]}");
                    info.Pc.Add($"SKU: {o["ChassisSKUNumber"]}");
                    info.Pc.Add($"Hipervisor: {o["HypervisorPresent"]}");
                }

                token.ThrowIfCancellationRequested();

                // Motherboard
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Motherboard.Add($"Modelo: {o["Manufacturer"]} {o["Product"]}");
                    info.Motherboard.Add($"Versão: {o["Version"]}");
                    info.Motherboard.Add($"S/N: {o["SerialNumber"]}");
                }

                token.ThrowIfCancellationRequested();

                // BIOS
                using (var q = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Bios.Add($"Fabricante: {o["Manufacturer"]}");
                    info.Bios.Add($"Versão: {o["SMBIOSBIOSVersion"]}");
                    info.Bios.Add($"Data: {FmtDate(o["ReleaseDate"])}");
                    info.Bios.Add($"S/N: {o["SerialNumber"]}");
                }
            }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Erro no grupo Sistema: " + ex.Message); }
        }

        /// <summary>Grupo 2: sistema operacional e armazenamento.</summary>
        private static void GrupoSoEDisco(StaticInfo info, CancellationToken token)
        {
            try
            {
                // OS
                using (var q = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, MUILanguages, NumberOfUsers, NumberOfProcesses, TotalVirtualMemorySize, FreePhysicalMemory, FreeVirtualMemory FROM Win32_OperatingSystem"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Os.Add($"SO: {o["Caption"]}");
                    info.Os.Add($"Versão: {o["Version"]} (Build {o["BuildNumber"]})");
                    info.Os.Add($"Arquitetura: {o["OSArchitecture"]}");
                    info.Os.Add($"Instalado em: {FmtDate(o["InstallDate"])}");
                    info.Os.Add($"Último boot: {FmtDate(o["LastBootUpTime"])}");
                    info.Os.Add($"Usuários: {o["NumberOfUsers"]} | Processos: {o["NumberOfProcesses"]}");
                }

                token.ThrowIfCancellationRequested();

                // Storage
                using (var q = new ManagementObjectSearcher("SELECT Model, SerialNumber, Size, InterfaceType, MediaType, Partitions, BytesPerSector, TotalHeads, TotalTracks FROM Win32_DiskDrive"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Storage.Add($"{o["Model"]} ({o["InterfaceType"]}) - {FmtBytes(o["Size"])}");
                }
            }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Erro no grupo SO/Disco: " + ex.Message); }
        }

        /// <summary>Grupo 3: rede, audio, bateria e zona termica — cada query no
        /// seu proprio thread (uma query lenta nao segura as outras).</summary>
        private static void GrupoRedeEPerifericos(StaticInfo info, CancellationToken token)
        {
            Task[] t =
            {
                Task.Run(() => QueryRede(info, token), token),
                Task.Run(() => QueryAudio(info, token), token),
                Task.Run(() => QueryBateria(info, token), token),
                Task.Run(() => QueryTermica(info, token), token)
            };
            try { Task.WaitAll(t); } catch { }
        }

        private static void QueryRede(StaticInfo info, CancellationToken token)
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name, MACAddress, Speed, NetConnectionID, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Network.Add($"{o["Name"]} ({o["NetConnectionID"]}) - {FmtBytes(o["Speed"])}");
                }
            }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Erro na query de rede: " + ex.Message); }
        }

        private static void QueryAudio(StaticInfo info, CancellationToken token)
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status, PNPDeviceID FROM Win32_SoundDevice"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Audio.Add($"{o["Name"]} ({o["Manufacturer"]})");
                }
            }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Erro na query de audio: " + ex.Message); }
        }

        private static void QueryBateria(StaticInfo info, CancellationToken token)
        {
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT * FROM Win32_Battery"))
                foreach (ManagementObject o in q.Get())
                {
                    info.Battery.Add($"Bateria: {o["Name"]} ({o["Chemistry"]})");
                    info.Battery.Add($"Carga: {o["EstimatedChargeRemaining"]}%");
                }
            }
            catch (Exception ex) { Logger.LogError("SystemInfoPage", "Erro na query de bateria: " + ex.Message); }
        }

        private static void QueryTermica(StaticInfo info, CancellationToken token)
        {
            try
            {
                using (var q = new ManagementObjectSearcher(new ManagementScope(WmiScope), new ObjectQuery("SELECT * FROM MSAcpi_ThermalZoneTemperature")))
                foreach (ManagementObject o in q.Get())
                {
                    double t = 0;
                    try { t = (Convert.ToDouble(o["CurrentTemperature"]) - 2732) / 10.0; } catch { }
                    info.Thermal.Add($"Zona: {o["InstanceName"]} = {t:F1} °C");
                }
            }
            catch { }
        }

        /// <summary>
        /// TPM: connect() no namespace do TPM leva ~5 s nesta maquina (medido) — por
        /// isso roda SOLTO, sem segurar a carga da pagina. O cartao do TPM e'
        /// preenchido quando a consulta volta.
        /// </summary>
        private static List<string> CollectTpm(CancellationToken token)
        {
            var lista = new List<string>();
            try
            {
                var ns = new ManagementScope(TpmScope);
                ns.Connect();
                using (var q = new ManagementObjectSearcher(ns, new ObjectQuery("SELECT * FROM Win32_Tpm")))
                foreach (ManagementObject o in q.Get())
                {
                    lista.Add($"Presente: {o["IsPresent_InitialValue"]}");
                    lista.Add($"Versão: {o["SpecVersion"]}");
                }
            }
            catch { lista.Add("TPM não disponível"); }
            return lista;
        }

        #endregion

        #region TEMPO REAL

        private async Task RefreshRealtimeAsync()
        {
            // Trava de corrida: se ja' existe uma coleta em andamento, esta e' descartada.
            // Sem isso, um tick lento (WMI/GPU > 2s) recebia o tick seguinte por cima e
            // os dois disputavam os mesmos controles da UI.
            if (Interlocked.CompareExchange(ref _refreshBusy, 1, 0) != 0) return;

            try
            {
                if (_pageUnloaded) return;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var dm = new DashboardManager();
                var s = await dm.GetSystemSnapshotAsync(cts.Token);
                if (_pageUnloaded) return;

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

                TxtRam.Text = $"{s.RamUsed:F2} / {s.RamTotal:F2} GB";
                double pct = s.RamTotal > 0 ? (s.RamUsed / s.RamTotal * 100.0) : 0;
                TxtRamPct.Text = $"{pct:F0}%";
                ArcRam.Data = CreateArc(pct, 46, 55, 55);
                TxtRamUsed.Text = $"Usado: {pct:F1}%";

                // GPU e rede sao leituras SINCRONAS (nvidia-smi / PerformanceCounter):
                // fora da thread da UI para nao travar a janela a cada 2 segundos.
                var gpu = await Task.Run(() => ReadGpu(s.GpuName, s.GpuTemp), cts.Token);
                if (_pageUnloaded) return;
                TxtGpuName.Text = s.GpuName ?? "N/A";
                TxtGpuPct.Text = $"{gpu.Pct:F0}%";
                ArcGpu.Data = CreateArc(gpu.Pct, 46, 55, 55);
                TxtGpuTemp.Text = gpu.Temp > 0 ? $"Temp: {gpu.Temp:F1} °C" : "Temp: N/A";

                // Storage/rede do realtime vao para LINHAS PROPRIAS: as listas sao da
                // carga estatica e nao podem ser sobrescritas aqui (era a corrida).
                var storage = new List<string>();
                foreach (var d in s.StorageDevices)
                    storage.Add($"{d.Name} ({d.DriveLetter}) - {d.HealthStatus}");
                TxtStorageRealtime.Text = storage.Count > 0 ? string.Join("  ·  ", storage) : "";

                var net = await Task.Run(() => ReadNetwork(), cts.Token);
                if (_pageUnloaded) return;
                TxtNetworkRealtime.Text = net;
            }
            catch (OperationCanceledException)
            {
                // coleta cancelada ou estourou o tempo: o proximo tick tenta de novo
            }
            catch (Exception ex)
            {
                Logger.LogError("SystemInfoPage", "Falha ao ler hardware: " + ex.Message);
                ShowError("Falha ao ler hardware: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshBusy, 0);
            }
        }

        /// <summary>Leitura de GPU (sincrona, chamada em background).</summary>
        private static (double Pct, double Temp) ReadGpu(string? gpuName, double fallbackTemp)
        {
            double pct = 0;
            try { pct = GpuMonitor.GetTotalGpuUtilization(); } catch { }

            double temp = fallbackTemp;
            try
            {
                foreach (var a in GpuMetrics.GetAdapters())
                    if (!string.IsNullOrEmpty(gpuName) && a.Name.Contains(gpuName, StringComparison.OrdinalIgnoreCase) && a.TemperatureC > 0)
                        temp = a.TemperatureC;
            }
            catch { }

            return (pct, temp);
        }

        /// <summary>Leitura de rede (sincrona, chamada em background).</summary>
        private static string ReadNetwork()
        {
            var net = new List<string>();
            try
            {
                var adapters = Toolbox.GetAllNetworkAdapters();
                if (adapters.Count == 0) net.Add("Nenhum adaptador detectado");
                foreach (var a in adapters)
                    net.Add($"{a.AdapterName} ({a.AdapterType}) - {a.Description} - {a.DataUsage}");
            }
            catch (Exception ex) { net.Add("Falha: " + ex.Message); }
            return net.Count > 0 ? string.Join("  ·  ", net) : "";
        }

        #endregion
    }
}
