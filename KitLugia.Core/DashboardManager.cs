using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public class DashboardManager : IDisposable
    {
        // Caminho NATIVO primeiro (registry + GetSystemTimes + GlobalMemoryStatusEx + IOCTL via
        // PartitionManager.GetAllDisks) — milissegundos, sem dependencia do servico WMI.
        // Fallbacks mantidos: WMI (Win32_*) e por ultimo registry/PerformanceCounter.
        public void Dispose() { }

        public async Task<SystemStats> GetSystemSnapshotAsync(CancellationToken ct = default)
        {
            // 1. Nativo: rapido, sem WMI, sem rede
            try
            {
                var native = await GetSystemSnapshotNativeAsync(ct);
                if (native != null) return native;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Log($"[DASHBOARD] Snapshot nativo falhou, tentando WMI: {ex.Message}");
            }

            // 2. WMI (legado, mais lento e dependente do servico Winmgmt)
            try
            {
                return await GetSystemSnapshotWmiAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Logger.Log("[DASHBOARD] WMI excedeu o tempo limite, usando fallback.");
                return GetSystemSnapshotFallback();
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException or TypeLoadException)
            {
                Logger.Log($"[DASHBOARD] WMI não disponível, usando fallback: {ex.Message}");
                return GetSystemSnapshotFallback();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  CAMINHO NATIVO (TMOG-style: P/Invoke + registry, sem WMI)
        // ════════════════════════════════════════════════════════════════════

        private static async Task<SystemStats?> GetSystemSnapshotNativeAsync(CancellationToken ct)
        {
            return await Task.Run(async () =>
            {
                // CPU: nome do registry (mesma fonte de NativeHardware.GetCpuNameNative)
                string cpuName = "Desconhecido";
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    cpuName = k?.GetValue("ProcessorNameString")?.ToString()?.Trim() is { Length: > 0 } n
                        ? n : "Desconhecido";
                }
                catch { Logger.LogWarning("Dashboard", "CPU name registry falhou"); }

                // CPU: carga via 2 amostras de GetSystemTimes (mesma tecnica de NativeMetricsHelper)
                float cpuLoad = await ReadCpuLoadAsync(ct);

                // RAM: GlobalMemoryStatusEx (1 syscall)
                double ramTotal = 0, ramUsed = 0;
                try
                {
                    var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
                    {
                        ramTotal = ms.ullTotalPhys / (1024.0 * 1024 * 1024);
                        ramUsed = (ms.ullTotalPhys - ms.ullAvailPhys) / (1024.0 * 1024 * 1024);
                    }
                }
                catch { Logger.LogWarning("Dashboard", "GlobalMemoryStatusEx falhou"); }
                if (ramTotal <= 0) return null; // RAM e obrigatoria para o snapshot

                // GPU: enumeracao por registry (Control\Class\{4d36e968...}) — sem WMI
                string gpuName = "N/A";
                double gpuVram = 0;
                try
                {
                    var gpu = ReadGpuFromRegistry();
                    gpuName = gpu.name;
                    gpuVram = gpu.vramGb;
                }
                catch { Logger.LogWarning("Dashboard", "GPU registry falhou"); }

                // SO: ProductName + DisplayVersion + build do registry
                string osName = ReadOsDisplayName();

                // Armazenamento: PartitionManager (nativo IOCTL → Storage API → legado)
                var storageList = new List<StorageInfo>(4);
                try
                {
                    foreach (var disk in PartitionManager.GetAllDisks())
                    {
                        var letters = string.Join(", ", disk.Partitions
                            .Select(p => p.DriveLetter).Where(l => !string.IsNullOrWhiteSpace(l)));
                        storageList.Add(new StorageInfo(
                            string.IsNullOrWhiteSpace(disk.Model) ? $"Disco {disk.Index}" : disk.Model,
                            "Saudável",
                            0,
                            letters));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[DASHBOARD] Lista de discos indisponivel (sem WMI): {ex.Message}");
                }

                return new SystemStats(
                    CpuName: cpuName,
                    CpuLoad: cpuLoad,
                    CpuTemp: 0,
                    GpuName: gpuName,
                    GpuTemp: 0,
                    GpuVramUsed: gpuVram,
                    RamUsed: ramUsed,
                    RamTotal: ramTotal,
                    OsName: osName,
                    Uptime: SystemUtils.GetSystemUptime(),
                    StorageDevices: storageList);
            }, ct);
        }

        /// <summary>Carga de CPU 0-100 via GetSystemTimes com 2 amostras (400 ms entre elas).</summary>
        private static async Task<float> ReadCpuLoadAsync(CancellationToken ct)
        {
            try
            {
                if (!GetSystemTimes(out long idle1, out long kernel1, out long user1)) return 0;
                await Task.Delay(400, ct).ConfigureAwait(false);
                if (!GetSystemTimes(out long idle2, out long kernel2, out long user2)) return 0;

                long dIdle = idle2 - idle1;
                long dKernel = kernel2 - kernel1; // kernel inclui idle no Windows
                long dUser = user2 - user1;
                double busy = (dKernel - dIdle) + dUser;
                if (busy <= 0) return 0;
                return (float)Math.Clamp(busy / (busy + dIdle) * 100.0, 0, 100);
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }

        /// <summary>GPU real via subchaves do class installer de display (ignora "Basic Display").</summary>
        private static (string name, double vramGb) ReadGpuFromRegistry()
        {
            const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(displayClass);
            if (classKey == null) return ("N/A", 0);

            foreach (var sub in classKey.GetSubKeyNames())
            {
                // Subchaves de instancia sao "0000", "0001", ... — ignora propriedades/filters
                if (sub.Length != 4 || !sub.StartsWith("00", StringComparison.Ordinal)) continue;

                try
                {
                    using var key = classKey.OpenSubKey(sub);
                    string? desc = key?.GetValue("DriverDesc")?.ToString();
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    if (desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        desc.StartsWith("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                        continue;

                    double vramGb = 0;
                    foreach (var valueName in new[] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
                    {
                        var raw = key?.GetValue(valueName);
                        ulong bytes = raw switch
                        {
                            long l => unchecked((ulong)l),
                            int i => unchecked((ulong)i),
                            byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                            _ => 0
                        };
                        if (bytes > 0)
                        {
                            vramGb = bytes / (1024.0 * 1024 * 1024);
                            break;
                        }
                    }
                    return (desc, vramGb);
                }
                catch { /* subchave protegida — proxima */ }
            }
            return ("N/A", 0);
        }

        /// <summary>Nome amigavel do SO: ProductName + DisplayVersion + build (corrige quirk Win11 do registry).</summary>
        private static string ReadOsDisplayName()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string product = k?.GetValue("ProductName")?.ToString() ?? "Windows";
                string display = k?.GetValue("DisplayVersion")?.ToString() ?? "";
                string build = k?.GetValue("CurrentBuildNumber")?.ToString() ?? "";
                int ubr = (k?.GetValue("UBR") as int?) ?? 0;

                // O registry diz "Windows 10 ..." mesmo no Windows 11 (quirk oficial) — corrige pelo build
                int buildNum = int.TryParse(build, out var b) ? b : 0;
                if (buildNum >= 22000 && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);

                var parts = new List<string> { product };
                if (!string.IsNullOrEmpty(display)) parts.Add(display);
                if (buildNum > 0) parts.Add(ubr > 0 ? $"build {buildNum}.{ubr}" : $"build {buildNum}");
                return string.Join(" · ", parts);
            }
            catch { return "Windows"; }
        }

        // ════════════════════════════════════════════════════════════════════
        //  FALLBACK FINAL: registry/PerformanceCounter (sem WMI nativo)
        // ════════════════════════════════════════════════════════════════════

        private SystemStats GetSystemSnapshotFallback()
        {
            string cpuName = "Desconhecido";
            string osName = "Windows";
            double ramTotal = 0;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpuName = key?.GetValue("ProcessorNameString")?.ToString() ?? "Desconhecido";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            try
            {
                osName = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            try
            {
                using var mem = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
                ramTotal = mem.NextValue() / 1024.0;
            }
            catch
            {
                ramTotal = SystemUtils.GetTotalSystemRamGB();
            }

            return new SystemStats(cpuName, 0, 0, "N/A", 0, 0, 0, ramTotal, osName, SystemUtils.GetSystemUptime(), new List<StorageInfo>());
        }

        // ════════════════════════════════════════════════════════════════════
        //  FALLBACK 1: WMI (legado) — mantido para maquinas onde o nativo falha
        // ════════════════════════════════════════════════════════════════════

        private async Task<SystemStats> GetSystemSnapshotWmiAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                string cpuName = "Desconhecido";
                float cpuLoad = 0;
                double ramTotal = 0;
                double ramFree = 0;
                string gpuName = "N/A";
                double gpuVram = 0;
                string osName = "Windows";

                try
                {
                    ct.ThrowIfCancellationRequested();

                    // 1. CPU (Nome e Carga)
                    using (var searcher = new ManagementObjectSearcher("SELECT Name, LoadPercentage FROM Win32_Processor"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            ct.ThrowIfCancellationRequested();
                            using (item)
                            {
                                cpuName = item["Name"]?.ToString() ?? "CPU Genérica";
                                cpuLoad = Convert.ToSingle(item["LoadPercentage"]);
                                break; // Pega só o primeiro processador
                            }
                        }
                    }

                    ct.ThrowIfCancellationRequested();

                    // 2. RAM (Total e Livre)
                    using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory, Caption FROM Win32_OperatingSystem"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            ct.ThrowIfCancellationRequested();
                            using (item)
                            {
                                ulong totalKb = Convert.ToUInt64(item["TotalVisibleMemorySize"]);
                                ulong freeKb = Convert.ToUInt64(item["FreePhysicalMemory"]);
                                osName = item["Caption"]?.ToString() ?? "Windows";

                                ramTotal = totalKb / 1024.0 / 1024.0;
                                ramFree = freeKb / 1024.0 / 1024.0;
                            }
                        }
                    }

                    ct.ThrowIfCancellationRequested();

                    // 3. GPU (Nome e VRAM Estimada)
                    using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                string name = item["Name"]?.ToString() ?? "";
                                // Filtra o driver básico do Windows para tentar achar a GPU real
                                if (!string.IsNullOrEmpty(name) && !name.Contains("Basic Display"))
                                {
                                    gpuName = name;
                                    try
                                    {
                                        // AdapterRAM vem em Bytes. Convertendo para GB.
                                        ulong vramBytes = Convert.ToUInt64(item["AdapterRAM"]);
                                        gpuVram = vramBytes / 1024.0 / 1024.0 / 1024.0;
                                    }
                                    catch { gpuVram = 0; }
                                    break;
                                }
                            }
                        }
                    }

                    // 4. Armazenamento (Lista de Discos)
                    var storageList = new List<StorageInfo>(4); // Típico: 1-4 discos
                    try
                    {
                        using (var searcher = new ManagementObjectSearcher("SELECT Model, Size, Status FROM Win32_DiskDrive"))
                        using (var results = searcher.Get())
                        {
                            foreach (ManagementObject item in results)
                            {
                                using (item)
                                {
                                    string model = item["Model"]?.ToString() ?? "Disco";
                                    string status = item["Status"]?.ToString() ?? "OK";

                                    // Formata saúde simples baseada no status do driver
                                    string health = status.ToUpper() == "OK" ? "Saudável" : "Verificar";

                                    storageList.Add(new StorageInfo(
                                        model,
                                        health,
                                        0, // WMI padrão não lê temperatura de disco
                                        "" // Letra da unidade é complexo de mapear no WMI simples, deixamos vazio
                                    ));
                                }
                            }
                        }
                    }
                    catch { /* Ignora erro de disco */ }

                    double ramUsed = ramTotal - ramFree;

                    // Retorna o objeto (Nota: Temps ficam 0 pois WMI nativo não lê sensores térmicos com precisão)
                    return new SystemStats(
                        CpuName: cpuName,
                        CpuLoad: cpuLoad,
                        CpuTemp: 0,
                        GpuName: gpuName,
                        GpuTemp: 0,
                        GpuVramUsed: gpuVram, // Mostramos o total da placa aqui na verdade, ou 0
                        RamUsed: ramUsed,
                        RamTotal: ramTotal,
                        OsName: osName,
                        Uptime: SystemUtils.GetSystemUptime(),
                        StorageDevices: storageList
                    );
                }
                catch (Exception)
                {
                    // Em caso de erro crítico no WMI, retorna dados vazios para não crashar o app
                    return new SystemStats("Erro WMI", 0, 0, "Erro WMI", 0, 0, 0, 0, "Erro", TimeSpan.Zero, new List<StorageInfo>());
                }
            });
        }

        // ════════════════════════════════════════════════════════════════════
        //  P/Invoke self-contained (convenção do Core: 1 bloco por arquivo)
        // ════════════════════════════════════════════════════════════════════

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}
