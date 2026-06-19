using System;
using System.Collections.Generic;
using System.Linq;
using System.Management; // A única dependência necessária (Nativa do Windows)
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public class DashboardManager : IDisposable
    {
        // Como usamos WMI, não precisamos manter um objeto "Computer" aberto.
        // O Dispose fica apenas para manter a compatibilidade se a GUI chamar.
        public void Dispose() { }

        public async Task<SystemStats> GetSystemSnapshotAsync()
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

                    // Mas com melhor GC do .NET 10, alocações são mais eficientes

                    // 1. CPU (Nome e Carga)
                    using (var searcher = new ManagementObjectSearcher("SELECT Name, LoadPercentage FROM Win32_Processor"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                cpuName = item["Name"]?.ToString() ?? "CPU Genérica";
                                cpuLoad = Convert.ToSingle(item["LoadPercentage"]);
                                break; // Pega só o primeiro processador
                            }
                        }
                    }

                    // 2. RAM (Total e Livre)
                    // Usando Win32_OperatingSystem é mais rápido que Win32_PhysicalMemory para isso
                    using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory, Caption FROM Win32_OperatingSystem"))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            using (item)
                            {
                                // WMI retorna em Kilobytes (KB)
                                ulong totalKb = Convert.ToUInt64(item["TotalVisibleMemorySize"]);
                                ulong freeKb = Convert.ToUInt64(item["FreePhysicalMemory"]);
                                osName = item["Caption"]?.ToString() ?? "Windows";

                                ramTotal = totalKb / 1024.0 / 1024.0; // Converte KB para GB
                                ramFree = freeKb / 1024.0 / 1024.0;
                            }
                        }
                    }

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
    }
}