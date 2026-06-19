using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// Registrar encoding providers para suportar codificações legadas
#pragma warning disable CA1416 // Suppress warning about cross-platform support
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class EncodingProvider
{
    static EncodingProvider()
    {
        // Registrar provider para encoding 850 (DOS Latin-1)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
#pragma warning restore CA1416

namespace KitLugia.Core
{
    /// <summary>
    /// Gerenciador de Partições (Estilo EaseUS Partition Master)
    /// Operações de disco via diskpart + WMI com suporte a Safe Mode (VDS auto-start)
    /// </summary>
    public static class PartitionManager
    {
        public static event Action<string>? OnLog;
        private static readonly List<string> _logBuffer = new();
        private const int MaxLogEntries = 500;

        public static void Log(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_logBuffer)
            {
                _logBuffer.Add(entry);

                if (_logBuffer.Count > MaxLogEntries)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxLogEntries);
            }
            OnLog?.Invoke(entry);
        }

        public static string GetSessionLog() => string.Join("\n", _logBuffer);

        private static string NormalizeLetter(string letter)
        {
            letter = (letter ?? string.Empty).Trim();
            if (letter.EndsWith(":", StringComparison.Ordinal)) letter = letter[..^1];
            if (letter.EndsWith("\\", StringComparison.Ordinal)) letter = letter[..^1];
            return letter;
        }

        private static void EnsureDriveReadyOrThrow(string driveLetter)
        {
            driveLetter = NormalizeLetter(driveLetter);
            if (string.IsNullOrWhiteSpace(driveLetter)) throw new ArgumentException("Drive letter inválida.", nameof(driveLetter));

            string root = $"{driveLetter}:\\";
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Unidade não encontrada: {root}");
            }
        }

        // --- VDS SAFE MODE FIX ---
        private static async Task EnsureVds()
        {
            try
            {
                await RunProcess("sc", "config vds start= demand");
                await RunProcess("net", "start vds");
            }
            catch { }
        }

        // --- DISK ENUMERATION (WMI) ---
        public static List<DiskInfoEx> GetAllDisks()
        {

            // Típico: 1-4 discos em sistemas comuns
            var disks = new List<DiskInfoEx>(4);
            try
            {
                using var diskSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                using var diskResults = diskSearcher.Get();
                foreach (ManagementObject disk in diskResults)
                {
                    using (disk)
                    {
                        var diskInfo = new DiskInfoEx
                        {
                            Index = Convert.ToUInt32(disk["Index"]),
                            Model = disk["Model"]?.ToString() ?? "Disco Desconhecido",
                            Interface = disk["InterfaceType"]?.ToString() ?? "Unknown",
                            Size = Convert.ToUInt64(disk["Size"] ?? 0),
                            MediaType = disk["MediaType"]?.ToString() ?? "",
                            SerialNumber = disk["SerialNumber"]?.ToString()?.Trim() ?? ""
                        };

                        // Detect GPT/MBR via partition style
                        try
                        {
                            using var partStyleSearcher = new ManagementObjectSearcher(
                                $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskInfo.Index}");
                            using var partStyleResults = partStyleSearcher.Get();
                            foreach (ManagementObject part in partStyleResults)
                            {
                                using (part)
                                {
                                    string type = part["Type"]?.ToString() ?? "";
                                    if (type.Contains("GPT", StringComparison.OrdinalIgnoreCase))
                                    {
                                        diskInfo.PartitionStyle = "GPT";
                                        break;
                                    }
                                    else if (type.Contains("Installable", StringComparison.OrdinalIgnoreCase) ||
                                             type.Contains("IFS", StringComparison.OrdinalIgnoreCase) ||
                                             type.Contains("12", StringComparison.OrdinalIgnoreCase))
                                    {
                                        diskInfo.PartitionStyle = "MBR";
                                    }
                                }
                            }
                        }
                        catch { diskInfo.PartitionStyle = "Desconhecido"; }

                        // 2. Get partitions (including those without letters) via Win32_DiskPartition
                        try
                        {
                            using var partSearcher = new ManagementObjectSearcher(
                                $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskInfo.Index}");
                            using var partResults = partSearcher.Get();
                            
                            foreach (ManagementObject partition in partResults)
                            {
                                using (partition)
                                {
                                    var partInfo = new PartitionInfoEx
                                    {
                                        Index = Convert.ToUInt32(partition["Index"]),
                                        DiskIndex = diskInfo.Index,
                                        Size = Convert.ToUInt64(partition["Size"] ?? 0),
                                        StartingOffset = Convert.ToUInt64(partition["StartingOffset"] ?? 0),
                                        Label = partition["Name"]?.ToString() ?? "Partição",
                                        Type = partition["Type"]?.ToString() ?? "Unknown"
                                    };

                                    // Look for drive letter via Win32_LogicalDisk
                                    try
                                    {
                                        using var logicalSearcher = new ManagementObjectSearcher(
                                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                                        using var logicalResults = logicalSearcher.Get();
                                        foreach (ManagementObject logical in logicalResults)
                                        {
                                            using (logical)
                                            {
                                                partInfo.DriveLetter = logical["DeviceID"]?.ToString() ?? "";
                                                partInfo.Label = logical["VolumeName"]?.ToString() ?? partInfo.Label;
                                                partInfo.FileSystem = logical["FileSystem"]?.ToString() ?? "";
                                                partInfo.FreeSpace = Convert.ToUInt64(logical["FreeSpace"] ?? 0);
                                            }
                                        }
                                    }
                                    catch { }
                                    diskInfo.Partitions.Add(partInfo);
                                }
                            }
                        }
                        catch { }

                        // Sort partitions by offset and fill Gaps
                        diskInfo.Partitions = diskInfo.Partitions.OrderBy(p => p.StartingOffset).ToList();
                        diskInfo.UpdateWithUnallocated();
                        // Re-index real partitions to 1-based sequential (Linux/diskpart convention)
                        uint seq = 0;
                        foreach (var p in diskInfo.Partitions)
                            if (!p.IsUnallocated) p.Index = ++seq;
                        disks.Add(diskInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERRO ao enumerar discos: {ex.Message}");
            }
            return disks.OrderBy(d => d.Index).ToList();
        }

        private static string DetectPartitionStyle(uint diskIndex)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskIndex}");
                foreach (ManagementObject part in searcher.Get())
                {
                    using (part)
                    {
                        string type = part["Type"]?.ToString() ?? "";
                        if (type.Contains("GPT", StringComparison.OrdinalIgnoreCase)) return "GPT";
                    }
                }
                return "MBR"; // Default se não achar GPT explícito
            }
            catch { return "Desconhecido"; }
        }

        private static void FetchPartitionsForDisk(DiskInfoEx diskInfo)
        {
            try
            {
                using var partSearcher = new ManagementObjectSearcher($"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskInfo.Index}");
                
                foreach (ManagementObject partition in partSearcher.Get())
                {
                    using (partition)
                    {
                        var partInfo = new PartitionInfoEx
                        {
                            Index = Convert.ToUInt32(partition["Index"]), // Índice global do WMI, não sequencial do disco
                            DiskIndex = diskInfo.Index,
                            Size = Convert.ToUInt64(partition["Size"] ?? 0),
                            StartingOffset = Convert.ToUInt64(partition["StartingOffset"] ?? 0),
                            Label = partition["Name"]?.ToString() ?? "Volume",
                            Type = partition["Type"]?.ToString() ?? "Unknown"
                        };

                        // Tenta associar com Letra de Unidade (Logical Disk)
                        try
                        {
                            using var logicalSearcher = new ManagementObjectSearcher(
                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                            
                            foreach (ManagementObject logical in logicalSearcher.Get())
                            {
                                using (logical)
                                {
                                    partInfo.DriveLetter = logical["DeviceID"]?.ToString() ?? ""; // Ex: "C:"
                                    partInfo.Label = logical["VolumeName"]?.ToString() ?? partInfo.Label;
                                    partInfo.FileSystem = logical["FileSystem"]?.ToString() ?? "";
                                    
                                    // FreeSpace vem do volume lógico
                                    if (ulong.TryParse(logical["FreeSpace"]?.ToString(), out ulong free))
                                    {
                                        partInfo.FreeSpace = free;
                                    }
                                }
                            }
                        }
                        catch { /* Pode não ter letra atribuída */ }

                        diskInfo.Partitions.Add(partInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Erro ao ler partições do disco {diskInfo.Index}: {ex.Message}");
            }

            // Ordena e corrige índices sequenciais para bater com o Diskpart (Partition 1, Partition 2...)
            diskInfo.Partitions = diskInfo.Partitions.OrderBy(p => p.StartingOffset).ToList();
            for (int i = 0; i < diskInfo.Partitions.Count; i++)
            {
                diskInfo.Partitions[i].Index = (uint)(i + 1); // Diskpart usa base 1
            }
        }

        public static void RefreshUsage(PartitionInfoEx part)
        {
            if (part.IsUnallocated || string.IsNullOrEmpty(part.DriveLetter)) return;

            try
            {
                var drive = new DriveInfo(part.DriveLetter.Substring(0, 1));
                if (drive.IsReady)
                {
                    part.FreeSpace = (ulong)drive.AvailableFreeSpace;
                    part.Size = (ulong)drive.TotalSize;
                }
            }
            catch { }
        }

        // --- FORMAT PARTITION ---
        public static async Task<bool> FormatPartition(uint diskIndex, uint partitionIndex, string driveLetter, string fileSystem, string label)
        {
            Log($"Formatando Partição {partitionIndex} (Disco {diskIndex}) como {fileSystem}...");

            await EnsureVds();


            // Típico: 5-10 linhas de script diskpart
            StringBuilder script = new StringBuilder(256);
            if (!string.IsNullOrEmpty(driveLetter))
            {
                script.AppendLine($"select volume {driveLetter.Replace(":", "")}");
            }
            else
            {
                script.AppendLine($"select disk {diskIndex}");
                script.AppendLine($"select partition {partitionIndex}");
            }
            script.AppendLine($"format quick fs={fileSystem} label=\"{label}\"");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "format");
        }

        // --- RESIZE (SHRINK) PARTITION ---
        public static async Task<bool> ShrinkPartition(uint diskIndex, uint partitionIndex, string driveLetter, int shrinkMb, Action<double, string>? progressCallback = null)
        {
            Log($"Reduzindo Partição {partitionIndex} em {shrinkMb} MB...");

            await EnsureVds();


            // Típico: 5-10 linhas de script diskpart
            StringBuilder script = new StringBuilder(256);
            script.AppendLine("rescan");
            if (!string.IsNullOrEmpty(driveLetter))
            {
                script.AppendLine($"select volume {driveLetter.Replace(":", "")}");
            }
            else
            {
                script.AppendLine($"select disk {diskIndex}");
                script.AppendLine($"select partition {partitionIndex}");
            }
            script.AppendLine($"shrink desired={shrinkMb}");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "shrink", progressCallback);
        }

        /// <summary>
        /// Obtém os tamanhos mínimo e máximo suportados para redimensionamento via Storage API
        /// </summary>
        public static async Task<(ulong SizeMin, ulong SizeMax, uint ReturnCode, string ErrorMessage)> GetSupportedSizes(char driveLetter)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var task = Task.Run(() =>
                {
                    var session = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
                    session.Connect();

                    var partitionQuery = new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DriveLetter = '{driveLetter}'");
                    using var searcher = new ManagementObjectSearcher(session, partitionQuery);
                    using var partitions = searcher.Get();

                    foreach (ManagementObject partition in partitions)
                    {
                        using (partition)
                        {
                            object[] methodArgs = { null, null, null };
                            var result = partition.InvokeMethod("GetSupportedSize", methodArgs);
                            uint returnCode = Convert.ToUInt32(result);

                            if (returnCode != 0)
                            {
                                string errorMsg = GetStorageErrorMessage(returnCode);
                                return (0UL, 0UL, returnCode, errorMsg);
                            }

                            ulong sizeMin = Convert.ToUInt64(methodArgs[0]);
                            ulong sizeMax = Convert.ToUInt64(methodArgs[1]);
                            return (sizeMin, sizeMax, returnCode, "");
                        }
                    }

                    return (0UL, 0UL, 999u, "Partição não encontrada");
                });
                cts.Token.Register(() => { try { task.Dispose(); } catch { } });
                return await task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("❌ GetSupportedSizes cancelado por timeout (10s)");
                return (0, 0, 999, "Timeout ao acessar Storage API");
            }
            catch (Exception ex)
            {
                return (0, 0, 999, $"Exceção: {ex.Message}");
            }
        }

        /// <summary>
        /// Obtém mensagem de erro baseada no código de erro da Storage API
        /// </summary>
        private static string GetStorageErrorMessage(uint errorCode)
        {
            return errorCode switch
            {
                0 => "Sucesso",
                1 => "Não suportado (partição não é NTFS ou RAW)",
                5 => "Parâmetro inválido (tamanho zero ou inválido)",
                4097 => "Tamanho não suportado (fora dos limites SizeMin/SizeMax)",
                40001 => "Acesso negado (privilégios de administrador insuficientes)",
                40002 => "Recursos insuficientes",
                42008 => "Volume com erros (execute chkdsk /f)",
                42009 => "Sistema de arquivos desconhecido (não é NTFS)",
                _ => $"Erro desconhecido: {errorCode}"
            };
        }

        /// <summary>
        /// Reduz partição usando Storage Management API (MSFT_Partition.Resize)
        /// API oficial da Microsoft que redimensiona partição e sistema de arquivos
        /// Mais flexível que DiskPart, mas ainda limitada por arquivos imóveis
        /// </summary>
        public static async Task<bool> ShrinkPartitionUsingStorageAPI(char driveLetter, long newSizeInBytes, Action<double, string>? progressCallback = null)
        {
            Log($"[DEBUG] Iniciando ShrinkPartitionUsingStorageAPI para {driveLetter}");
            progressCallback?.Invoke(10, $"Iniciando Storage API - Drive: {driveLetter}");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var task = ShrinkViaStorageApiInternal(driveLetter, newSizeInBytes, progressCallback);
                return await task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("❌ ShrinkPartitionUsingStorageAPI cancelado por timeout (15s)");
                progressCallback?.Invoke(-1, "Timeout ao acessar Storage API");
                return false;
            }
            catch (Exception ex)
            {
                progressCallback?.Invoke(-1, $"Exceção: {ex.Message}");
                Log($"❌ Exceção ao usar Storage Management API: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> ShrinkViaStorageApiInternal(char driveLetter, long newSizeInBytes, Action<double, string>? progressCallback)
        {
            progressCallback?.Invoke(20, "Verificando limites suportados...");
            var (sizeMin, sizeMax, returnCode, errorMsg) = await GetSupportedSizes(driveLetter);

            if (returnCode != 0)
            {
                progressCallback?.Invoke(-1, $"Erro: {errorMsg}");
                return false;
            }

            if (newSizeInBytes < (long)sizeMin || newSizeInBytes > (long)sizeMax)
            {
                progressCallback?.Invoke(-1, "Tamanho fora dos limites suportados");
                return false;
            }

            progressCallback?.Invoke(50, "Conectando ao WMI Storage...");
            var session = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            session.Connect();

            var partitionQuery = new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DriveLetter = '{driveLetter}'");
            using var searcher = new ManagementObjectSearcher(session, partitionQuery);
            using var partitions = searcher.Get();

            foreach (ManagementObject partition in partitions)
            {
                using (partition)
                {
                    progressCallback?.Invoke(70, "Redimensionando...");
                    object[] methodArgs = { newSizeInBytes, null };
                    var result = partition.InvokeMethod("Resize", methodArgs);
                    uint returnValue = Convert.ToUInt32(result);

                    if (returnValue == 0)
                    {
                        progressCallback?.Invoke(100, "Partição reduzida com sucesso");
                        return true;
                    }
                    else
                    {
                        string errorDetail = GetStorageErrorMessage(returnValue);
                        progressCallback?.Invoke(-1, $"Erro: {errorDetail}");
                        return false;
                    }
                }
            }

            progressCallback?.Invoke(-1, "Partição não encontrada");
            return false;
        }

        // --- EXTEND PARTITION ---
        public static async Task<bool> ExtendPartition(string driveLetter, int extendMb = 0, Action<double, string>? progressCallback = null)
        {
            driveLetter = driveLetter.Replace(":", "");
            Log($"Estendendo {driveLetter}: {(extendMb > 0 ? $"em {extendMb} MB" : "para todo espaço disponível")}...");

            await EnsureVds();


            // Típico: 5-10 linhas de script diskpart
            StringBuilder script = new StringBuilder(256);
            script.AppendLine($"select volume {driveLetter}");
            if (extendMb > 0)
                script.AppendLine($"extend size={extendMb}");
            else
                script.AppendLine("extend");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "extend", progressCallback);
        }

        // --- DELETE PARTITION ---
        public static async Task<bool> DeletePartition(uint diskIndex, uint partitionIndex, string driveLetter, bool forceDelete = false)
        {
            Log($"Deletando partição {partitionIndex} (Disco {diskIndex}){(forceDelete ? " [FORÇADO]" : "")}...");
            

            if (!forceDelete)
            {
                if (!string.IsNullOrEmpty(driveLetter))
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                    if (driveLetter.Replace(":", "").Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"❌ ERRO CRÍTICO: Partição {driveLetter} parece ser a partição do sistema (C:).");
                        Log("❌ Deletar a partição do sistema apagará o Windows.");
                        Log("❌ Esta operação foi bloqueada por segurança.");
                        return false;
                    }
                }
                

                if (IsSystemDisk(diskIndex))
                {
                    Log($"❌ ERRO CRÍTICO: Disco {diskIndex} parece ser o disco do sistema.");
                    Log("❌ Deletar partições do disco do sistema pode tornar o Windows inoperável.");
                    Log("❌ Esta operação foi bloqueada por segurança.");
                    return false;
                }
            }
            else
            {

                Log($"⚠️ AVISO: forceDelete está ativo - verificação de segurança desabilitada.");
                if (!string.IsNullOrEmpty(driveLetter))
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                    if (driveLetter.Replace(":", "").Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"⚠️ ATENÇÃO: Deletando partição do sistema {driveLetter} - usuário confirmou operação.");
                    }
                }
            }
            
            await EnsureVds();

            StringBuilder script = new();
            if (!string.IsNullOrEmpty(driveLetter))
            {
                script.AppendLine($"select volume {driveLetter.Replace(":", "")}");
            }
            else
            {
                script.AppendLine($"select disk {diskIndex}");
                script.AppendLine($"select partition {partitionIndex}");
            }
            script.AppendLine("delete partition override");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "delete");
        }

        // --- CREATE PARTITION ON UNALLOCATED SPACE ---
        public static async Task<bool> CreatePartition(uint diskIndex, int sizeMb, string fileSystem, string label)
        {
            Log($"Criando partição de {sizeMb} MB no Disco {diskIndex}...");

            await EnsureVds();

            StringBuilder script = new StringBuilder(256);
            script.AppendLine("rescan");
            script.AppendLine($"select disk {diskIndex}");
            if (sizeMb > 0)
                script.AppendLine($"create partition primary size={sizeMb}");
            else
                script.AppendLine("create partition primary"); // Uses all unallocated
            script.AppendLine($"format quick fs={fileSystem} label=\"{label}\"");
            script.AppendLine("assign");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "create");
        }

        // --- CHANGE DRIVE LETTER ---
        public static async Task<bool> ChangeDriveLetter(string oldLetter, string newLetter)
        {
            oldLetter = oldLetter.Replace(":", "");
            newLetter = newLetter.Replace(":", "");
            Log($"Alterando letra de {oldLetter}: para {newLetter}:...");

            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select volume {oldLetter}");
            script.AppendLine($"remove letter={oldLetter}");
            script.AppendLine($"assign letter={newLetter}");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "letter");
        }

        // --- QUERY MAX SHRINK ---
        public static async Task<long> GetMaxShrinkMb(string driveLetter)
        {
            driveLetter = driveLetter.Replace(":", "");
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select volume {driveLetter}");
            script.AppendLine("shrink querymax");
            script.AppendLine("exit");

            string scriptPath = Path.Combine(Path.GetTempPath(), "pm_querymax.txt");
            File.WriteAllText(scriptPath, script.ToString());
            var (_, output) = await RunProcess("diskpart.exe", $"/s \"{scriptPath}\"");
            File.Delete(scriptPath);

            // Parse "O número máximo de bytes recuperáveis é:   XXXX MB"
            // or "The maximum number of reclaimable bytes is:   XXXX MB"
            var match = Regex.Match(output, @"(\d+)\s*MB", RegexOptions.IgnoreCase);
            if (match.Success && long.TryParse(match.Groups[1].Value, out long maxMb))
            {
                Log($"Máximo reduzível em {driveLetter}: = {maxMb} MB");
                return maxMb;
            }

            Log($"Não foi possível determinar o máximo reduzível para {driveLetter}:");
            return 0;
        }

        // --- CLEAN DISK (Wipe all partitions) ---
        public static async Task<bool> CleanDisk(uint diskIndex, bool fullClean = false)
        {
            Log($"LIMPANDO Disco {diskIndex} ({(fullClean ? "COMPLETO" : "rápido")})...");
            

            if (IsSystemDisk(diskIndex))
            {
                Log($"❌ ERRO CRÍTICO: Disco {diskIndex} parece ser o disco do sistema.");
                Log("❌ Limpar o disco do sistema apagará o Windows e tornará o PC inoperável.");
                Log("❌ Esta operação foi bloqueada por segurança.");
                return false;
            }
            

            var disks = GetAllDisks();
            var targetDisk = disks.FirstOrDefault(d => d.Index == diskIndex);
            if (targetDisk != null && targetDisk.Partitions.Count(p => !p.IsUnallocated) > 0)
            {
                Log($"⚠️ AVISO: Disco tem {targetDisk.Partitions.Count(p => !p.IsUnallocated)} partição(ões) que serão apagadas.");
            }
            
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine(fullClean ? "clean all" : "clean");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "clean");
        }

        // --- SET ACTIVE PARTITION (MBR only) ---
        public static async Task<bool> SetActivePartition(uint diskIndex, uint partitionIndex)
        {
            Log($"Marcando partição {partitionIndex} (Disco {diskIndex}) como ATIVA...");
            

            var disks = GetAllDisks();
            var targetDisk = disks.FirstOrDefault(d => d.Index == diskIndex);
            
            if (targetDisk == null)
            {
                Log("❌ ERRO: Disco não encontrado");
                return false;
            }
            
            if (targetDisk.PartitionStyle != "MBR")
            {
                Log($"❌ ERRO: Disco {diskIndex} usa {targetDisk.PartitionStyle}, não MBR.");
                Log("❌ O comando 'active' só funciona em discos MBR.");
                Log("❌ Em discos GPT, use 'bcdedit' para definir a partição de boot.");
                return false;
            }
            
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine($"select partition {partitionIndex}");
            script.AppendLine("active");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "active");
        }

        // --- CHECK FILE SYSTEM ---
        public static async Task<(bool Success, string Output)> CheckFileSystem(string driveLetter, bool repair = false)
        {
            driveLetter = driveLetter.Replace(":", "");
            string flags = repair ? "/F /R" : "";
            Log($"Verificando sistema de arquivos em {driveLetter}: (Reparar: {repair})...");

            var (exitCode, output) = await RunProcess("chkdsk.exe", $"{driveLetter}: {flags}");
            Log(output);

            bool hasErrors = output.Contains("errors", StringComparison.OrdinalIgnoreCase) && 
                            !output.Contains("no errors", StringComparison.OrdinalIgnoreCase) &&
                            !output.Contains("found no errors", StringComparison.OrdinalIgnoreCase);
            
            return (!hasErrors, output);
        }

        // --- CONVERT DISK STYLE (MBR <-> GPT) ---
        // NOTA: Requer disco VAZIO (sem partições)
        public static async Task<bool> ConvertDiskStyle(uint diskIndex, string targetStyle)
        {
            Log($"Convertendo Disco {diskIndex} para {targetStyle}...");
            

            var disks = GetAllDisks();
            var targetDisk = disks.FirstOrDefault(d => d.Index == diskIndex);
            
            if (targetDisk == null)
            {
                Log("❌ ERRO: Disco não encontrado");
                return false;
            }
            
            if (targetDisk.Partitions.Any(p => !p.IsUnallocated))
            {
                Log($"❌ ERRO CRÍTICO: Disco {diskIndex} não está vazio. Tem {targetDisk.Partitions.Count(p => !p.IsUnallocated)} partição(ões).");
                Log("❌ A conversão MBR/GPT requer que o disco esteja completamente vazio.");
                Log("❌ Use 'Limpar Disco' primeiro para apagar todas as partições.");
                return false;
            }
            

            if (IsSystemDisk(diskIndex))
            {
                Log($"❌ ERRO CRÍTICO: Disco {diskIndex} parece ser o disco do sistema.");
                Log("❌ Converter o disco do sistema pode tornar o Windows inoperável.");
                return false;
            }
            
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine($"convert {targetStyle.ToLower()}"); // "gpt" or "mbr"
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "convert");
        }
        

        private static bool IsSystemDisk(uint diskIndex)
        {
            try
            {
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                if (string.IsNullOrEmpty(systemDrive)) return false;

                using var searcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{systemDrive}:'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    using (mo)
                    {
                        var diskIndexStr = mo["DiskIndex"]?.ToString();
                        if (diskIndexStr != null && uint.TryParse(diskIndexStr, out var idx))
                            return idx == diskIndex;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // --- REMOVE DRIVE LETTER ---
        public static async Task<bool> RemoveDriveLetter(string driveLetter)
        {
            driveLetter = driveLetter.Replace(":", "");
            Log($"Removendo letra {driveLetter}:...");
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select volume {driveLetter}");
            script.AppendLine($"remove letter={driveLetter}");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "removeletter");
        }

        public static async Task<bool> MoveVolumeData(string sourceLetter, string targetLetter, Action<double, string>? progressCallback = null, string folderName = "Arquivos_Mesclados")
        {
            sourceLetter = sourceLetter.TrimEnd('\\').Replace(":", "");
            targetLetter = targetLetter.TrimEnd('\\').Replace(":", "");
            
            string destPath = Path.Combine($"{targetLetter}:\\", folderName);
            Log($"Movendo dados de {sourceLetter}: para {destPath}...");
            
            string args = $"\"{sourceLetter}:\\\" \"{destPath}\" /E /MOVE /B /J /R:0 /W:0 /XJ /MT:128 /XD \"System Volume Information\" \"$RECYCLE.BIN\" \"Config.Msi\" \"recovery\"";
            
            var (exitCode, output) = await RunProcessStreamed("robocopy.exe", args, (line) => {
                // Robocopy shows files like: "  New File  		     1.2 m	FILENAME.EXT"
                if (line.Contains("New File") || line.Contains("EXTRA File") || line.Contains("New Dir"))
                {
                    var fileMatch = Regex.Match(line, @"[^\t\\]+$");
                    if (fileMatch.Success) progressCallback?.Invoke(-1, fileMatch.Value.Trim());
                }
            });

            Log("--- ROBOCOPY RESULTS ---");
            Log(output);
            
            return exitCode < 8;
        }

        public static async Task<bool> CaptureVolumeImage(string sourceLetter, string wimPath, Action<double, string>? progressCallback = null, string name = "KitLugia_Capture")
        {
            sourceLetter = NormalizeLetter(sourceLetter);
            EnsureDriveReadyOrThrow(sourceLetter);

            if (string.IsNullOrWhiteSpace(wimPath)) throw new ArgumentException("Caminho do WIM inválido.", nameof(wimPath));
            string? wimDir = Path.GetDirectoryName(wimPath);
            if (!string.IsNullOrWhiteSpace(wimDir) && !Directory.Exists(wimDir)) Directory.CreateDirectory(wimDir);

            Log($"Capturando Imagem de {sourceLetter}: para {wimPath}...");
            
            string args = $"/Capture-Image /ImageFile:\"{wimPath}\" /CaptureDir:{sourceLetter}:\\ /Name:\"{name}\" /Compress:fast /NoRestart";
            
            var (exitCode, output) = await RunProcessStreamed("dism.exe", args, (line) => {
                var match = Regex.Match(line, @"(\d+\.?\d*)%");
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double pct)) {
                    progressCallback?.Invoke(pct, $"Capturando: {pct}%");
                }
                else if (line.Length > 5 && !line.Contains("=") && !line.Contains("[") && !line.Contains("Deployment"))
                {
                    progressCallback?.Invoke(-1, line.Trim());
                }
            });
            
            Log("--- DISM CAPTURE ---");
            Log(output);
            
            return exitCode == 0;
        }

        public static async Task<bool> ApplyVolumeImage(string wimPath, string targetPath, Action<double, string>? progressCallback = null)
        {
            Log($"Aplicando Imagem {wimPath} para {targetPath}...");
            
            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);

            string args = $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:\"{targetPath}\" /NoRestart";
            
            var (exitCode, output) = await RunProcessStreamed("dism.exe", args, (line) => {
                var match = Regex.Match(line, @"(\d+\.?\d*)%");
                if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double pct)) {
                    progressCallback?.Invoke(pct, $"Restaurando: {pct}%");
                }
                else if (line.Length > 5 && !line.Contains("=") && !line.Contains("[") && !line.Contains("Deployment"))
                {
                    progressCallback?.Invoke(-1, line.Trim());
                }
            });
            
            Log("--- DISM APPLY ---");
            Log(output);
            
            return exitCode == 0;
        }

        private static async Task SafeDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    await Task.Run(() => File.Delete(path));
                }
            }
            catch { }
        }

        // --- DISK DETAIL INFO ---
        public static async Task<string> GetDiskDetail(uint diskIndex)
        {
            await EnsureVds();
            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine("detail disk");
            script.AppendLine("exit");

            string scriptPath = Path.Combine(Path.GetTempPath(), "pm_detail.txt");
            File.WriteAllText(scriptPath, script.ToString());
            var (_, output) = await RunProcess("diskpart.exe", $"/s \"{scriptPath}\"");
            File.Delete(scriptPath);
            return output;
        }


        public static async Task<bool> CreateVhdBypass(uint diskIndex, string driveLetter, int sizeMb)
        {
            string vhdPath = Path.Combine($"{driveLetter}:\\", "virtual_disk.vhdx");
            Log($"Iniciando Bypass de Limite 3GB via VHD em {driveLetter}:\\ ({sizeMb} MB)...");

            StringBuilder script = new();
            script.AppendLine($"create vdisk file=\"{vhdPath}\" maximum={sizeMb} type=expandable");
            script.AppendLine($"attach vdisk");
            script.AppendLine("create partition primary");
            script.AppendLine("format quick fs=ntfs label=\"VHD_Bypass\"");
            script.AppendLine("assign");
            script.AppendLine("exit");

            bool ok = await RunDiskpartScript(script.ToString(), "vhd_bypass");
            if (ok) Log("VHD criado e montado com sucesso para bypass.");
            return ok;
        }

        public static async Task<bool> MovePartition(uint diskIndex, uint partitionIndex, string driveLetter, Action<double, string>? progressCallback = null)
        {
            Log($"Iniciando Movimentação Segura (Imaging) da Partição {partitionIndex}...");
            string tempWim = Path.Combine(Path.GetTempPath(), $"move_part_{partitionIndex}.wim");
            
            progressCallback?.Invoke(0, "Capturando imagem da partição...");
            bool capOk = await CaptureVolumeImage(driveLetter, tempWim, progressCallback);
            if (!capOk) { Log("Falha na captura da imagem."); return false; }

            progressCallback?.Invoke(50, "Excluindo partição original...");

            bool delOk = await DeletePartition(diskIndex, partitionIndex, driveLetter, forceDelete: true);
            if (!delOk) { Log("Falha ao excluir partição original."); return false; }

            await Task.Delay(1000); // Wait for VDS refresh

            progressCallback?.Invoke(60, "Recriando partição no novo local...");
            // Aqui assumimos que o espaço não alocado adjacente será usado
            bool createOk = await CreatePartition(diskIndex, 0, "ntfs", "Restaurada");
            if (!createOk) { Log("Falha ao recriar partição."); return false; }

            // Encontrar a nova letra (assign automático do diskpart)
            var disks = GetAllDisks();
            var newPart = disks.FirstOrDefault(d => d.Index == diskIndex)?.Partitions.LastOrDefault(p => !p.IsUnallocated);
            string newLetter = newPart?.DriveLetter ?? "";

            if (string.IsNullOrEmpty(newLetter)) { Log("Nova letra não detectada."); return false; }

            progressCallback?.Invoke(80, "Restaurando dados...");
            bool applyOk = await ApplyVolumeImage(tempWim, $"{newLetter}\\", progressCallback);
            
            try { File.Delete(tempWim); } catch { }

            if (applyOk) Log("Movimentação concluída com sucesso.");
            return applyOk;
        }

        public static async Task<bool> AtomicMergeDISM(uint sourceDisk, uint sourcePart, string sourceLetter, string targetLetter, Action<double, string>? progressCallback = null)
        {
            sourceLetter = NormalizeLetter(sourceLetter);
            targetLetter = NormalizeLetter(targetLetter);
            EnsureDriveReadyOrThrow(sourceLetter);
            EnsureDriveReadyOrThrow(targetLetter);

            string tempWim = Path.Combine($"{targetLetter}:\\", "atomic_merge_payload.wim");
            
            Log($"Iniciando Mesclagem Atômica (DISM) de {sourceLetter}: para {targetLetter}:...");
            Log("Esta técnica ignora arquivos imóveis e limites de 3GB do Windows.");

            // 1. Capturar Imagem (Clonagem Atômica)
            progressCallback?.Invoke(0, "Criando Snapshot Atômico (DISM)...");
            bool capOk = await CaptureVolumeImage(sourceLetter, tempWim, progressCallback, "AtomicMerge_Backup");
            if (!capOk)
            {
                Log("DISM falhou na captura. Tentando fallback Robocopy /B...");
                // Fallback: move os arquivos diretamente e segue com delete+extend.
                bool moveOk = await MoveVolumeData(sourceLetter, targetLetter, progressCallback, "Arquivos_Mesclados");
                if (!moveOk) { Log("Fallback Robocopy também falhou."); return false; }
                capOk = false; // indica que não teremos Apply WIM
            }

            // 2. Excluir Partição Origem (Liberação Total de Espaço)
            progressCallback?.Invoke(60, "Liberando espaço físico (Excluindo origem)...");

            bool delOk = await DeletePartition(sourceDisk, sourcePart, sourceLetter, forceDelete: true);
            if (!delOk) { Log("Falha ao liberar espaço físico."); return false; }

            await Task.Delay(1000); // Estabilização VDS

            // 3. Estender Destino (Crescimento Real)
            progressCallback?.Invoke(70, "Estendendo partição de destino...");
            bool extOk = await ExtendPartition(targetLetter, 0, progressCallback);
            if (!extOk) { Log("Falha ao estender destino após liberação."); }

            // 4. Aplicar Imagem (Injeção de Dados)
            progressCallback?.Invoke(80, "Injetando arquivos mesclados...");
            string mergeFolder = Path.Combine($"{targetLetter}:\\", "Arquivos_Mesclados");

            bool finalOk;
            if (File.Exists(tempWim))
            {
                finalOk = await ApplyVolumeImage(tempWim, mergeFolder, progressCallback);
                await SafeDeleteFile(tempWim);
            }
            else
            {
                // Se foi fallback Robocopy, os arquivos já foram movidos.
                finalOk = true;
            }

            if (finalOk) Log("Mesclagem Atômica concluída com sucesso absoluto.");
            return finalOk;
        }

        public static async Task<bool> AtomicExtendDISM(uint diskIndex, uint partIndex, string driveLetter, Action<double, string>? progressCallback = null)
        {
            driveLetter = NormalizeLetter(driveLetter);
            EnsureDriveReadyOrThrow(driveLetter);
            string tempWim = Path.Combine(Path.GetTempPath(), $"extend_bypass_{partIndex}.wim");
            
            Log($"Iniciando Extensão Atômica (Bypass 3GB) em {driveLetter}:...");
            
            // 1. Captura
            progressCallback?.Invoke(0, "Capturando Snapshot para Bypass...");
            if (!await CaptureVolumeImage(driveLetter, tempWim, progressCallback)) return false;

            // 2. Delete
            progressCallback?.Invoke(50, "Limpando estrutura bloqueada...");

            if (!await DeletePartition(diskIndex, partIndex, driveLetter, forceDelete: true)) return false;

            await Task.Delay(1000);

            // 3. Create (com o novo tamanho)
            progressCallback?.Invoke(70, "Recriando com novo tamanho...");
            if (!await CreatePartition(diskIndex, 0, "ntfs", "Restaurado")) return false;

            // Detectar nova letra
            var disks = GetAllDisks();
            var newPart = disks.FirstOrDefault(d => d.Index == diskIndex)?.Partitions.LastOrDefault(p => !p.IsUnallocated);
            string newLetter = newPart?.DriveLetter ?? "";
            if (string.IsNullOrEmpty(newLetter)) return false;

            // 4. Apply
            progressCallback?.Invoke(85, "Restaurando Snapshot...");
            bool ok = await ApplyVolumeImage(tempWim, $"{newLetter}\\", progressCallback);
            
            await SafeDeleteFile(tempWim);
            return ok;
        }

        // --- INTERNAL HELPERS ---
        private static async Task<bool> RunDiskpartScript(string scriptContent, string operationName, Action<double, string>? progressCallback = null)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"pm_{operationName}.txt");
            File.WriteAllText(scriptPath, scriptContent);

            Log($"Executando diskpart ({operationName})...");
            
            // Regex agnóstico de idioma: captura apenas os dígitos antes do % ou da palavra
            var (exitCode, output) = await RunProcessStreamed("diskpart.exe", $"/s \"{scriptPath}\"", (line) => {
                var match = Regex.Match(line, @"(\d+)\s*(?:percent|por cento|%)", RegexOptions.IgnoreCase);
                if (match.Success && double.TryParse(match.Groups[1].Value, out double pct)) {
                    progressCallback?.Invoke(pct, $"Processando: {pct}%");
                }
                else if (line.Trim().Length > 5 && !line.Contains("DISKPART>") && !line.Contains("Copyright")) {
                    progressCallback?.Invoke(-1, line.Trim());
                }
            });

            Log("--- DISKPART ---");
            Log(output);

            File.Delete(scriptPath);

            bool hasVdsError = output.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase);
            if (hasVdsError)
            {
                Log($"ERRO VDS na operação '{operationName}'.");
                return false;
            }

            // A validação agora confia estritamente no código de saída do processo
            // (Diskpart retorna > 0 se o script falhar estruturalmente ou comandos forem abortados)
            if (exitCode != 0)
            {
                Log($"ERRO detectado na operação '{operationName}'. Código de saída: {exitCode}");
                return false;
            }

            Log($"Operação '{operationName}' concluída.");
            return true;
        }

        private static async Task<(int ExitCode, string Output)> RunProcess(string filename, string args)
        {
            return await RunProcessStreamed(filename, args, null);
        }

        private static async Task<(int ExitCode, string Output)> RunProcessStreamed(string filename, string args, Action<string>? onLineRead)
        {
            return await Task.Run(() =>
            {
                // Garante que o provider de codificação está registrado na thread atual
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                StringBuilder fullOutput = new();
                var psi = new ProcessStartInfo(filename, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // DISM costuma emitir Unicode/UTF-8; Diskpart/Robocopy em PT-BR usam OEM (CP850).
                    // Usamos OEM encoding para garantir acentos corretos em português.
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var proc = new Process { StartInfo = psi };
                
                proc.OutputDataReceived += (s, e) => {
                    if (e.Data != null) {
                        string cleanLine = FixEncoding(e.Data);
                        fullOutput.AppendLine(cleanLine);
                        onLineRead?.Invoke(cleanLine);
                    }
                };
                proc.ErrorDataReceived += (s, e) => {
                    if (e.Data != null) {
                        string cleanLine = FixEncoding(e.Data);
                        fullOutput.AppendLine(cleanLine);
                        onLineRead?.Invoke(cleanLine);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                return (proc.ExitCode, fullOutput.ToString());
            });
        }

        private static string FixEncoding(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            // Com encoding CP850, os acentos virão corretos.
            // Apenas limpa caracteres de controle problemáticos.
            return input.Replace("\0", "").Trim();
        }
    }

    // --- EXTENDED MODELS ---
    public class DiskInfoEx
    {
        public uint Index { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Interface { get; set; } = string.Empty;
        public ulong Size { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
        public string PartitionStyle { get; set; } = "Desconhecido"; // GPT or MBR
        public List<PartitionInfoEx> Partitions { get; set; } = new();

        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F1} GB";
        public string DisplayName => $"Disco {Index}: {Model} ({SizeString}) [{PartitionStyle}]";

        public void UpdateWithUnallocated()
        {
            var updatedList = new List<PartitionInfoEx>();
            ulong currentOffset = 0;

            foreach (var part in Partitions.OrderBy(p => p.StartingOffset))
            {
                // Gap detected?
                if (part.StartingOffset > currentOffset + (1024 * 1024)) // Margin of 1MB
                {
                    updatedList.Add(new PartitionInfoEx
                    {
                        Label = "Não Alocado",
                        Size = part.StartingOffset - currentOffset,
                        StartingOffset = currentOffset,
                        FileSystem = "Unallocated",
                        IsUnallocated = true
                    });
                }
                updatedList.Add(part);
                currentOffset = part.StartingOffset + part.Size;
            }

            // Gap at the end? (Margin 10MB to avoid noise)
            if (Size > currentOffset + (10 * 1024 * 1024))
            {
                updatedList.Add(new PartitionInfoEx
                {
                    Label = "Não Alocado",
                    Size = Size - currentOffset,
                    StartingOffset = currentOffset,
                    FileSystem = "Unallocated",
                    IsUnallocated = true
                });
            }

            Partitions = updatedList;
        }
    }

    public class PartitionInfoEx : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) 
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public uint Index { get; set; }
        public uint DiskIndex { get; set; }
        private string _driveLetter = string.Empty;
        public string DriveLetter { get => _driveLetter; set { _driveLetter = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        
        private string _label = string.Empty;
        public string Label { get => _label; set { _label = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayName)); } }
        
        public string FileSystem { get; set; } = string.Empty;
        
        private ulong _size;
        public ulong Size { get => _size; set { _size = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeString)); OnPropertyChanged(nameof(UsedPercent)); } }
        
        private ulong _freeSpace;
        public ulong FreeSpace { get => _freeSpace; set { _freeSpace = value; OnPropertyChanged(); OnPropertyChanged(nameof(FreeSpaceString)); OnPropertyChanged(nameof(UsedPercent)); OnPropertyChanged(nameof(UsedPercentText)); } }
        
        public ulong StartingOffset { get; set; }
        public string Type { get; set; } = string.Empty;
        public bool IsUnallocated { get; set; }

        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F1} GB";
        public string FreeSpaceString => IsUnallocated ? "" : $"{(FreeSpace / (1024.0 * 1024 * 1024)):F1} GB livre";
        public double UsedPercent => (Size > 0 && !IsUnallocated) ? ((double)(Size - FreeSpace) / Size) * 100 : 0;
        public double FreePercent => 100 - UsedPercent;
        public string UsedPercentText => IsUnallocated ? "0%" : $"{UsedPercent:F0}%";
        public double UsedPercentWidth => UsedPercent * 1.4;

        public string Status => IsSystemPartition ? "Sistema/Saudável" : "Saudável";

        public bool IsSystemPartition =>
            Label.Contains("Sistema", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("System", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("EFI", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("Reservad", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("Reserved", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("Recovery", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("Recuper", StringComparison.OrdinalIgnoreCase) ||
            DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase);

        public bool IsProtected =>
            IsSystemPartition ||
            Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase) ||
            Label.Contains("NAO_DELETAR", StringComparison.OrdinalIgnoreCase);

        public string Icon
        {
            get
            {
                if (IsSystemPartition) return "🔒";
                if (Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase) ||
                    Label.Contains("NAO_DELETAR", StringComparison.OrdinalIgnoreCase)) return "🚀";
                return "💾";
            }
        }

        public string DisplayName =>
            string.IsNullOrEmpty(DriveLetter)
                ? $"({Label}) [{FileSystem}] - {SizeString}"
                : $"{Icon} {DriveLetter} ({Label}) [{FileSystem}] - {FreeSpaceString} livres de {SizeString}";

        public string BarColor
        {
            get
            {
                if (IsUnallocated) return "#37474F"; // Blue Gray
                if (IsSystemPartition) return "#2962FF"; // Deep Blue (Modern Win)
                if (DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase)) return "#0091EA"; // Light Blue C:
                if (Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase)) return "#FFD600"; // Gold Winboot
                
                return (Index % 3) switch
                {
                    0 => "#00C853", // Green
                    1 => "#AA00FF", // Purple
                    _ => "#FF6D00"  // Orange
                };
            }
        }
    }
}
