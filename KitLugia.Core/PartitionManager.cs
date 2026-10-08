using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Gerenciador de Partições (Estilo EaseUS Partition Master)
    /// Operações de disco via DeviceIoControl nativo (winioctl.h) + Storage Management API (MSFT_*) + diskpart.
    /// Enumeracao usa IOCTL nativo (mais rapido, sem WMI/provider), fallback Storage API (Windows 8+), fallback legado Win32_*.
    /// </summary>
    public static class PartitionManager
    {
        /// <summary>Namespace WMI da Storage Management API moderna (Windows 8+)</summary>
        private const string StorageScopePath = @"\\.\ROOT\Microsoft\Windows\Storage";

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

        /// <summary>
        /// Motivo REAL da ultima operacao que falhou. A UI mostra este texto em vez do generico
        /// "Nao foi possivel estender" — sem isso o usuario nao descobre que era BitLocker,
        /// espaco nao contiguo, limite real de reducao ou o diskpart devolvendo OK sem fazer nada.
        /// </summary>
        public static string LastError { get; private set; } = "";

        private static string SetError(string? message)
        {
            LastError = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
            return LastError;
        }

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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        // --- DISK ENUMERATION (IOCTL NATIVO - sem WMI, sem diskpart) ---
        public static List<DiskInfoEx> GetAllDisks()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var disks = GetAllDisksNative();
                sw.Stop();
                Logger.Log($"[DISK] GetAllDisks: caminho NATIVO (IOCTL) - {disks.Count} disco(s) em {sw.ElapsedMilliseconds} ms");
                return disks;
            }
            catch (Exception ex)
            {
                Log($"IOCTL nativo indisponível, usando Storage API: {ex.Message}");
            }

            try
            {
                var disks = GetAllDisksStorageApi();
                sw.Stop();
                Logger.Log($"[DISK] GetAllDisks: caminho STORAGE API (MSFT_*) - {disks.Count} disco(s) em {sw.ElapsedMilliseconds} ms");
                // A Storage API pode voltar "com sucesso" mas SEM partições (ex.: MSFT_Partition
                // negado via DCOM para token não elevado, enquanto o MSFT_Disk passa). Disco sem
                // partição real é inútil para as páginas — cai no legado em vez de mostrar
                // "Não Alocado" do tamanho do disco inteiro.
                if (disks.Count > 0 && disks.All(d => d.Partitions.All(p => p.IsUnallocated)))
                {
                    Log("Storage API voltou sem partições reais — usando fallback legado (Win32_*).");
                    var legacy = GetAllDisksLegacy();
                    sw.Stop();
                    Logger.Log($"[DISK] GetAllDisks: caminho LEGADO (Win32_*) - {legacy.Count} disco(s) em {sw.ElapsedMilliseconds} ms");
                    return legacy;
                }
                return disks;
            }
            catch (Exception ex)
            {
                Log($"Storage API indisponível, usando fallback legado: {ex.Message}");
                var disks = GetAllDisksLegacy();
                sw.Stop();
                Logger.Log($"[DISK] GetAllDisks: caminho LEGADO (Win32_*) - {disks.Count} disco(s) em {sw.ElapsedMilliseconds} ms");
                return disks;
            }
        }

        /// <summary>
        /// Enumera discos via DeviceIoControl direto (winioctl.h): IOCTL_DISK_GET_DRIVE_LAYOUT_EX
        /// devolve a tabela MBR/GPT inteira em milissegundos, sem WMI nem spawn de processo.
        /// Volumes (letra/FS/espaço) via GetLogicalDrives + IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS.
        /// Mesma abordagem do cleanDiskFast() do rpi-imager. Se nada for detectado, lanca excecao
        /// para o chamador cair no fallback (Storage API).
        /// </summary>
        private static List<DiskInfoEx> GetAllDisksNative()
        {
            var swTotal = Stopwatch.StartNew();
            var volumes = NativeDiskIo.EnumerateVolumes();
            int? bootDisk = NativeDiskIo.FindBootDiskNumber();
            var disks = new List<DiskInfoEx>();

            for (uint i = 0; i < 32; i++)
            {
                var swDisk = Stopwatch.StartNew();
                using var handle = NativeDiskIo.OpenDisk(i);
                if (handle.IsInvalid) continue;

                // Confirma que o handle corresponde a um disco físico real
                if (!NativeDiskIo.GetDeviceNumber(handle, out var devNum)) continue;
                if (devNum.DeviceNumber != i) continue;

                if (!NativeDiskIo.GetDiskSize(handle, out long diskSize)) continue;

                var diskInfo = new DiskInfoEx
                {
                    Index = i,
                    Model = "Disco Físico",
                    Interface = "Unknown",
                    Size = (ulong)diskSize,
                    IsSystemDisk = bootDisk.HasValue && bootDisk.Value == (int)i
                };

                if (NativeDiskIo.GetStorageProperties(handle, out var model, out var serial, out var bus))
                {
                    if (!string.IsNullOrWhiteSpace(model)) diskInfo.Model = model;
                    diskInfo.SerialNumber = serial;
                    diskInfo.Interface = bus;
                }

                if (NativeDiskIo.GetDriveLayout(handle, out uint style, out var nativeParts))
                {
                    diskInfo.PartitionStyle = style switch
                    {
                        0 => "MBR",
                        1 => "GPT",
                        _ => "RAW"
                    };

                    foreach (var p in nativeParts)
                    {
                        var partInfo = new PartitionInfoEx
                        {
                            Index = p.Number,
                            DiskIndex = i,
                            Size = (ulong)Math.Max(0, p.Length),
                            StartingOffset = (ulong)Math.Max(0, p.StartingOffset),
                            Label = string.IsNullOrWhiteSpace(p.GptName) ? "Partição" : p.GptName,
                            Type = p.TypeName,
                            IsBootFlag = p.IsBoot,
                            IsSystemFlag = p.IsSystem
                        };

                        // Associa volume com letra que cai nesta partição (offset exato no disco)
                        foreach (var v in volumes)
                        {
                            if (v.Extents.Count == 0) continue;
                            foreach (var e in v.Extents)
                            {
                                if (e.DiskNumber == i && e.StartingOffset == p.StartingOffset)
                                {
                                    partInfo.DriveLetter = v.Letter + ":";
                                    if (!string.IsNullOrWhiteSpace(v.Label)) partInfo.Label = v.Label;
                                    partInfo.FileSystem = v.FileSystem;
                                    partInfo.FreeSpace = v.FreeBytes;
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(partInfo.DriveLetter)) break;
                        }

                        diskInfo.Partitions.Add(partInfo);
                    }

                    diskInfo.IsBootDisk = diskInfo.Partitions.Any(p => p.IsBootFlag || p.IsSystemFlag);
                }

                diskInfo.Partitions = diskInfo.Partitions.OrderBy(p => p.StartingOffset).ToList();
                diskInfo.UpdateWithUnallocated(diskInfo.Index);
                uint seq = 0;
                foreach (var p in diskInfo.Partitions)
                    if (!p.IsUnallocated) p.Index = ++seq;
                disks.Add(diskInfo);
                swDisk.Stop();
                string parts = string.Join(" | ", diskInfo.Partitions
                    .Where(p => !p.IsUnallocated)
                    .Select(p => $"{p.DriveLetter}{p.Label}({p.Type},{p.SizeString})"));
                Logger.Log($"[DISK]  Disco {i}: {diskInfo.Model} | {diskInfo.Interface} | {diskInfo.PartitionStyle} | {diskInfo.SizeString} | Sys={diskInfo.IsSystemDisk} ({swDisk.ElapsedMilliseconds} ms)");
                Logger.Log($"[DISK]    {parts}");
            }

            if (disks.Count == 0)
                throw new InvalidOperationException("Nenhum disco físico detectado via IOCTL nativo");
            if (disks.All(d => d.Partitions.All(p => p.IsUnallocated)))
                throw new InvalidOperationException("IOCTL nativo sem permissão de leitura de layout (sem admin?): nenhum disco trouxe partições reais — caindo no fallback");
            swTotal.Stop();
            Logger.Log($"[DISK]  Enumeracao nativa total (volumes+boot+layout): {swTotal.ElapsedMilliseconds} ms");
            return disks.OrderBy(d => d.Index).ToList();
        }

        /// <summary>
        /// Enumera discos via MSFT_Disk/MSFT_Partition/MSFT_Volume (Storage Management API).
        /// Mais rapida (uma query por classe em vez de N+1), PartitionStyle/IsSystem/IsBoot
        /// sao propriedades nativas (sem heuristica de string).
        /// </summary>
        private static List<DiskInfoEx> GetAllDisksStorageApi()
        {
            var scope = new ManagementScope(StorageScopePath);
            scope.Connect();

            // 1. Todos os discos fisicos (MSFT_Disk)
            var diskResults = new List<ManagementObject>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Disk")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo) diskResults.Add((ManagementObject)mo.Clone());
                }
            }

            if (diskResults.Count == 0) return GetAllDisksLegacy();

            // 2. Todas as particoes (MSFT_Partition) — uma query, agrupadas por DiskNumber
            var partitionsByDisk = new Dictionary<uint, List<ManagementObject>>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Partition")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (!uint.TryParse(mo["DiskNumber"]?.ToString(), out uint diskNumber)) continue;
                        if (!partitionsByDisk.TryGetValue(diskNumber, out var list)) partitionsByDisk[diskNumber] = list = new List<ManagementObject>();
                        list.Add((ManagementObject)mo.Clone());
                    }
                }
            }

            // 3. Todos os volumes (MSFT_Volume) — para letra/FS/espaço livre, por DriveLetter
            var volumesByLetter = new Dictionary<string, ManagementObject>();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_Volume")))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        string? letter = mo["DriveLetter"]?.ToString();
                        if (!string.IsNullOrEmpty(letter)) volumesByLetter[letter] = (ManagementObject)mo.Clone();
                    }
                }
            }

            try
            {
                var disks = new List<DiskInfoEx>(diskResults.Count);
                foreach (var disk in diskResults)
                {
                    uint index = Convert.ToUInt32(disk["Number"]);
                    var diskInfo = new DiskInfoEx
                    {
                        Index = index,
                        Model = disk["FriendlyName"]?.ToString() ?? disk["Model"]?.ToString() ?? "Disco Desconhecido",
                        Interface = BusTypeToString(disk["BusType"]),
                        Size = Convert.ToUInt64(disk["Size"] ?? 0),
                        MediaType = "",
                        SerialNumber = disk["SerialNumber"]?.ToString()?.Trim() ?? "",
                        PartitionStyle = PartitionStyleToString(disk["PartitionStyle"]),
                        IsSystemDisk = disk["IsSystem"] is bool b && b,
                        IsBootDisk = disk["IsBoot"] is bool b2 && b2
                    };

                    if (partitionsByDisk.TryGetValue(index, out var partitions))
                    {
                        foreach (var part in partitions)
                        {
                            try
                            {
                                var partInfo = new PartitionInfoEx
                                {
                                    Index = Convert.ToUInt32(part["PartitionNumber"]),
                                    DiskIndex = index,
                                    Size = Convert.ToUInt64(part["Size"] ?? 0),
                                    StartingOffset = Convert.ToUInt64(part["Offset"] ?? 0),
                                    Label = "Partição",
                                    Type = part["Type"]?.ToString() ?? "Unknown",
                                    IsSystemFlag = part["IsSystem"] is bool sb && sb,
                                    IsBootFlag = part["IsBoot"] is bool bb && bb
                                };

                                string? letter = part["DriveLetter"]?.ToString();
                                if (!string.IsNullOrEmpty(letter))
                                {
                                    partInfo.DriveLetter = letter;
                                    if (volumesByLetter.TryGetValue(letter, out var volume))
                                    {
                                        partInfo.Label = volume["VolumeLabel"]?.ToString() ?? partInfo.Label;
                                        partInfo.FileSystem = volume["FileSystem"]?.ToString() ?? "";
                                        partInfo.FreeSpace = Convert.ToUInt64(volume["SizeRemaining"] ?? 0);
                                    }
                                }
                                diskInfo.Partitions.Add(partInfo);
                            }
                            catch (Exception ex)
                            {
                                Log($"Erro ao ler partição do disco {index}: {ex.Message}");
                            }
                            finally
                            {
                                part.Dispose();
                            }
                        }
                    }

                    diskInfo.Partitions = diskInfo.Partitions.OrderBy(p => p.StartingOffset).ToList();
                    diskInfo.UpdateWithUnallocated(diskInfo.Index);
                    uint seq = 0;
                    foreach (var p in diskInfo.Partitions)
                        if (!p.IsUnallocated) p.Index = ++seq;
                    disks.Add(diskInfo);
                }
                return disks.OrderBy(d => d.Index).ToList();
            }
            finally
            {
                foreach (var list in partitionsByDisk.Values)
                    foreach (var mo in list) mo.Dispose();
                foreach (var mo in volumesByLetter.Values) mo.Dispose();
                foreach (var mo in diskResults) mo.Dispose();
            }
        }

        private static string BusTypeToString(object? busType)
        {
            if (busType == null) return "Unknown";
            try
            {
                return (Convert.ToUInt16(busType)) switch
                {
                    1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE-1394", 5 => "SSA",
                    6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
                    11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File Backed",
                    16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", _ => $"Bus({busType})"
                };
            }
            catch { return "Unknown"; }
        }

        private static string PartitionStyleToString(object? style)
        {
            if (style == null) return "Desconhecido";
            try
            {
                return Convert.ToUInt16(style) switch
                {
                    1 => "MBR",
                    2 => "GPT",
                    3 => "RAW",
                    _ => "Desconhecido"
                };
            }
            catch { return "Desconhecido"; }
        }

        /// <summary>
        /// Fallback legado (Win32_DiskDrive + Win32_DiskPartition + Win32_LogicalDisk)
        /// para sistemas sem a Storage Management API.
        /// </summary>
        private static List<DiskInfoEx> GetAllDisksLegacy()
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
                                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                                    diskInfo.Partitions.Add(partInfo);
                                }
                            }
                        }
                        catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                        // Sort partitions by offset and fill Gaps
                        diskInfo.Partitions = diskInfo.Partitions.OrderBy(p => p.StartingOffset).ToList();
                        diskInfo.UpdateWithUnallocated(diskInfo.Index);
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
        /// <summary>
        /// Reduz a particao. Escada: (1) Storage Management API (MSFT_Partition.Resize — oficial do
        /// Windows, sem diskpart), (2) diskpart. Antes valida o limite REAL (GetSupportedSize),
        /// que e o unico numero confiavel de "quanto da para tirar" (arquivos imoveis, hiberfil,
        /// pagefile e shadow copies no fim do volume cortam a reducao).
        /// </summary>
        public static async Task<bool> ShrinkPartition(uint diskIndex, uint partitionIndex, string driveLetter, int shrinkMb, Action<double, string>? progressCallback = null)
        {
            string letter = NormalizeLetter(driveLetter).ToUpperInvariant();
            SetError("");
            Log($"Reduzindo Partição {partitionIndex} em {shrinkMb} MB...");

            long before = GetVolumeSizeBytes(letter);
            var reasons = new List<string>();

            // (1) Storage Management API
            progressCallback?.Invoke(5, "Consultando limite real de redução...");
            var limits = GetPartitionSizeLimits(string.IsNullOrEmpty(letter) ? '?' : letter[0]);

            if (limits.ReturnCode == 0 && limits.Size > 0)
            {
                long maxShrinkMb = (long)((limits.Size - limits.SizeMin) / (1024UL * 1024UL));
                ulong wantBytes = before > 0
                    ? (ulong)Math.Max(0L, before - (long)shrinkMb * 1024 * 1024)
                    : (ulong)Math.Max(0L, (long)limits.Size - (long)shrinkMb * 1024 * 1024);

                if (wantBytes < limits.SizeMin)
                {
                    SetError($"Limite real de redução em {(string.IsNullOrEmpty(letter) ? "?" : letter + ":")}: {maxShrinkMb} MB (você pediu {shrinkMb} MB). " +
                             "O que ocupa o fim do volume não sai do lugar: hiberfil.sys, pagefile, shadow copies ou arquivos imóveis. " +
                             "Soluções: desative a hibernação (powercfg /h off), mova o pagefile, ou use 'Emergency Pre-Boot' (WinPE).");
                    Log($"[SHRINK] Abortado: {shrinkMb} MB > maximo real {maxShrinkMb} MB (Size={limits.Size} SizeMin={limits.SizeMin})");
                    progressCallback?.Invoke(-1, SetError(""));
                    return false;
                }

                progressCallback?.Invoke(30, "Reduzindo via Storage API...");
                uint rc = ResizeStoragePartition(letter[0], wantBytes);
                long after = GetVolumeSizeBytes(letter);

                if (rc == 0 && after > 0 && before > 0 && after < before)
                {
                    Log($"✅ Reduzido via Storage API em {before / 1048576} MB -> {after / 1048576} MB");
                    progressCallback?.Invoke(100, $"Reduzido para {after / 1048576} MB");
                    return true;
                }

                reasons.Add(rc == 0
                    ? "a Storage API respondeu OK, mas o volume não diminuiu"
                    : $"Storage API: {GetStorageErrorMessage(rc)}");
                progressCallback?.Invoke(45, $"Storage API recusou. Tentando diskpart...");
            }
            else
            {
                reasons.Add($"Storage API indisponível (rc={limits.ReturnCode}: {limits.ErrorMessage})");
            }

            // (2) diskpart
            await EnsureVds();

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

            // Retry estilo EaseUS (CAsynLockVolume): repete 1x após 3 s somente quando a
            // medição prova que nada mudou (sem efeito parcial — shrink parcial + repetição
            // encolheria 2x, por isso o gate é pela medição).
            bool ok;
            {
                var (ok2, _) = await RunDiskpartWithRetryAsync(
                    script.ToString(), "shrink", () => GetVolumeSizeBytes(letter), before, progressCallback);
                ok = ok2;
            }

            // VERIFICAÇÃO PÓS-OPERAÇÃO: o diskpart devolve exit 0 em vários cenários de falha.
            long afterDp = GetVolumeSizeBytes(letter);
            if (ok && before > 0 && afterDp > 0 && afterDp >= before)
            {
                SetError($"diskpart retornou OK, mas o volume NÃO diminuiu (antes {before / 1048576} MB, depois {afterDp / 1048576} MB). Motivos comuns: " +
                         "arquivos imóveis no fim do volume, pagefile/hiberfil.sys ou shadow copies. Use 'Emergency Pre-Boot' (WinPE).");
                Log($"[SHRINK] diskpart exit=0 sem efeito real: {before} -> {afterDp}");
                return false;
            }

            if (!ok)
            {
                var extra = string.Join("; ", reasons);
                if (!string.IsNullOrEmpty(extra) && string.IsNullOrEmpty(LastError))
                    SetError($"Falha na redução ({extra}).");
            }

            return ok;
        }

        /// <summary>
        /// Obtém os tamanhos mínimo e máximo suportados para redimensionamento via Storage API
        /// </summary>
        public static async Task<(ulong SizeMin, ulong SizeMax, uint ReturnCode, string ErrorMessage)> GetSupportedSizes(char driveLetter)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var task = Task.Run(() => GetPartitionSizeLimits(driveLetter));
                var limits = await task.WaitAsync(cts.Token);
                return (limits.SizeMin, limits.SizeMax, limits.ReturnCode, limits.ErrorMessage);
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

        // --- HELPERS: tamanho real, limites via Storage API, diagnóstico ---

        /// <summary>Tamanho real do volume em bytes (0 se inacessível/sem letra).</summary>
        private static long GetVolumeSizeBytes(string letter)
        {
            string l = NormalizeLetter(letter);
            if (string.IsNullOrWhiteSpace(l)) return 0;
            try
            {
                var d = new DriveInfo($"{l[0]}:\\");
                return d.IsReady ? d.TotalSize : 0;
            }
            catch { return 0; }
        }

        /// <summary>Tamanho atual + limites de redimensionamento reportedos pela Storage API.</summary>
        public sealed class PartitionSizeLimits
        {
            public ulong Size { get; init; }      // tamanho atual da partição (bytes)
            public ulong SizeMin { get; init; }   // menor tamanho possível (limite de redução)
            public ulong SizeMax { get; init; }   // maior tamanho possível (limite de extensão)
            public uint ReturnCode { get; init; } // 0 = ok
            public string ErrorMessage { get; init; } = "";
        }

        /// <summary>
        /// MSFT_Partition.GetSupportedSize: numero EXATO e instantaneo de quanto da para
        /// estender/reduzir (e o mesmo que o Gerenciador de Discos mostra). Sem spawn de diskpart.
        /// </summary>
        public static PartitionSizeLimits GetPartitionSizeLimits(char driveLetter)
        {
            if (!char.IsLetter(driveLetter))
                return new PartitionSizeLimits { ReturnCode = 5, ErrorMessage = "Letra de unidade inválida" };

            try
            {
                var session = new ManagementScope(StorageScopePath);
                session.Connect();

                var query = new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DriveLetter = '{char.ToUpperInvariant(driveLetter)}'");
                using var searcher = new ManagementObjectSearcher(session, query);
                using var parts = searcher.Get();

                foreach (ManagementObject p in parts)
                {
                    using (p)
                    {
                        ulong size = Convert.ToUInt64(p["Size"] ?? 0UL);
                        object[] methodArgs = { null!, null!, null! };
                        uint rc = Convert.ToUInt32(p.InvokeMethod("GetSupportedSize", methodArgs));
                        if (rc != 0)
                            return new PartitionSizeLimits { Size = size, ReturnCode = rc, ErrorMessage = GetStorageErrorMessage(rc) };

                        return new PartitionSizeLimits
                        {
                            Size = size,
                            SizeMin = Convert.ToUInt64(methodArgs[0]),
                            SizeMax = Convert.ToUInt64(methodArgs[1]),
                            ReturnCode = 0
                        };
                    }
                }

                return new PartitionSizeLimits { ReturnCode = 404, ErrorMessage = "Partição não encontrada" };
            }
            catch (Exception ex)
            {
                Log($"[STORAGE] GetSupportedSize falhou: {ex.Message}");
                return new PartitionSizeLimits { ReturnCode = 999, ErrorMessage = ex.Message };
            }
        }

        /// <summary>MSFT_Partition.Resize — redimensiona partição E sistema de arquivos de uma vez.</summary>
        private static uint ResizeStoragePartition(char driveLetter, ulong newSizeBytes)
        {
            try
            {
                var session = new ManagementScope(StorageScopePath);
                session.Connect();

                var query = new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DriveLetter = '{char.ToUpperInvariant(driveLetter)}'");
                using var searcher = new ManagementObjectSearcher(session, query);
                using var parts = searcher.Get();

                foreach (ManagementObject p in parts)
                {
                    using (p)
                    {
                        object[] methodArgs = { newSizeBytes, null! };
                        return Convert.ToUInt32(p.InvokeMethod("Resize", methodArgs));
                    }
                }
                return 404;
            }
            catch (Exception ex)
            {
                Log($"[STORAGE] Resize falhou: {ex.Message}");
                return 999;
            }
        }

        /// <summary>Espaço não alocado contíguo (encostado) à direita da partição.</summary>
        private static ulong ContiguousFreeRight(DiskInfoEx disk, PartitionInfoEx part)
        {
            var ordered = disk.Partitions.OrderBy(p => p.StartingOffset).ToList();
            int idx = ordered.FindIndex(p => p.StartingOffset == part.StartingOffset && p.Index == part.Index);
            if (idx < 0) return 0;

            ulong free = 0;
            for (int i = idx + 1; i < ordered.Count; i++)
            {
                if (!ordered[i].IsUnallocated) break;   // partição real no meio = espaço não contíguo
                free += ordered[i].Size;
            }
            return free;
        }

        /// <summary>
        /// Estende 100% nativo (sem diskpart): IOCTL_DISK_GROW_PARTITION (amplia a partição no disco)
        /// + FSCTL_EXTEND_VOLUME (NTFS/ReFS cresce online, sem lock/dismount do volume).
        /// </summary>
        private static (bool Ok, string Error) TryExtendNativeIoctl(string letter, uint diskIndex, uint partIndex, ulong targetBytes, long volumeBytes)
        {
            try
            {
                var disk = GetAllDisks().FirstOrDefault(d => d.Index == diskIndex);
                var part = disk?.Partitions.FirstOrDefault(p => p.Index == partIndex && !p.IsUnallocated);
                if (disk == null || part == null) return (false, "partição não encontrada na tabela do disco");

                ulong freeRight = ContiguousFreeRight(disk, part);
                ulong delta = targetBytes > 0 && volumeBytes > 0
                    ? (ulong)Math.Max(0L, (long)targetBytes - volumeBytes)
                    : freeRight;

                if (delta == 0) return (false, "sem espaço contíguo à direita");

                ulong newPartSize = part.Size + delta;
                if (newPartSize > disk.Size) newPartSize = disk.Size;   // segurança contra estouro do disco

                using var hDisk = NativeDiskIo.OpenDisk(diskIndex, write: true);
                if (hDisk.IsInvalid) return (false, $"sem acesso a \\\\.\\PhysicalDrive{diskIndex} (err={Marshal.GetLastWin32Error()})");

                if (!NativeDiskIo.GrowPartition(hDisk, part.StartingOffset, newPartSize - part.Size))
                    return (false, $"IOCTL_DISK_GROW_PARTITION falhou (err={Marshal.GetLastWin32Error()})");

                using var hVol = NativeDiskIo.OpenVolume(letter[0], write: true);
                if (hVol.IsInvalid) return (false, $"sem acesso a \\\\.\\{letter}: (err={Marshal.GetLastWin32Error()})");

                if (!NativeDiskIo.ExtendVolume(hVol, (long)(newPartSize - (ulong)Math.Max(0L, volumeBytes))))
                    return (false, $"FSCTL_EXTEND_VOLUME falhou (err={Marshal.GetLastWin32Error()})");

                return (true, "");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>Quantos MB dá para ESTENDER (limite real, via Storage API; 0 se não souber).</summary>
        public static async Task<long> GetMaxExtendMb(string driveLetter)
        {
            string letter = NormalizeLetter(driveLetter).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(letter)) return 0;

            await Task.CompletedTask;
            var limits = GetPartitionSizeLimits(letter[0]);
            if (limits.ReturnCode == 0 && limits.SizeMax > limits.Size)
                return (long)((limits.SizeMax - limits.Size) / (1024UL * 1024UL));

            // Sem Storage API: soma o não alocado contíguo pela tabela do disco.
            try
            {
                var part = GetAllDisks()
                    .SelectMany(d => d.Partitions)
                    .FirstOrDefault(p => !p.IsUnallocated && p.DriveLetter.Equals(letter, StringComparison.OrdinalIgnoreCase));
                if (part == null) return 0;

                var disk = GetAllDisks().FirstOrDefault(d => d.Index == part.DiskIndex);
                return disk == null ? 0 : (long)(ContiguousFreeRight(disk, part) / (1024UL * 1024UL));
            }
            catch { return 0; }
        }

        private static bool? _bitLockerSupported;

        /// <summary>True se o volume está protegido por BitLocker (consulta somente-leitura, sem desabilitar nada).</summary>
        private static bool IsBitLockerProtected(string letter)
        {
            string l = NormalizeLetter(letter).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(l)) return false;
            if (_bitLockerSupported == false) return false;

            try
            {
                var scope = new ManagementScope(@"\\.\root\CIMV2\Security\Microsoft\VolumeEncryption");
                scope.Connect();
                var query = new ObjectQuery($"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='{l}:'");
                using var searcher = new ManagementObjectSearcher(scope, query);
                using var vols = searcher.Get();

                _bitLockerSupported = true;

                foreach (ManagementObject v in vols)
                {
                    using (v)
                    {
                        object? ps = v["ProtectionStatus"];
                        return ps != null && Convert.ToInt32(ps) == 1;   // 0=desligado 1=ligado 2=desconhecido
                    }
                }
            }
            catch (Exception ex)
            {
                // Sem o provedor (BitLocker não instalado) a consulta sempre falha — testa 1x e desliga.
                if (_bitLockerSupported == null)
                {
                    _bitLockerSupported = false;
                    Log($"[BITLOCKER] provedor indisponível (BitLocker não instalado?): {ex.Message}");
                }
            }
            return false;
        }

        /// <summary>True se existe pagefile configurado no volume.</summary>
        private static bool HasPageFileOn(string letter)
        {
            string l = NormalizeLetter(letter).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(l)) return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                if (key?.GetValue("PagingFiles") is string[] files)
                    return files.Any(f => f.Contains($"{l}:", StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            return false;
        }

        private static bool HasHiberFileOn(string letter)
        {
            string l = NormalizeLetter(letter).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(l)) return false;
            try { return File.Exists($"{l}:\\hiberfil.sys"); }
            catch { return false; }
        }

        /// <summary>
        /// Diagnóstico ANTES de estender/reduzir: devolve o que BLOQUEIA a operação e o que apenas
        /// AVISA (pagefile, BitLocker, hiberfil). É isso que a página mostra em vez de um
        /// "não foi possível" depois de facturar erro.
        /// </summary>
        public static (List<string> Blockers, List<string> Warnings) GetResizeDiagnostics(PartitionInfoEx part, bool forExtend)
        {
            var blockers = new List<string>();
            var warnings = new List<string>();
            if (part == null) return (blockers, warnings);

            string letter = NormalizeLetter(part.DriveLetter).ToUpperInvariant();
            string alvo = string.IsNullOrEmpty(letter) ? "a partição selecionada" : $"{letter}:";

            if (string.IsNullOrEmpty(letter))
                blockers.Add($"{alvo} está sem letra de unidade — atribua uma letra primeiro.");

            if (forExtend)
            {
                string fs = (part.FileSystem ?? string.Empty).ToUpperInvariant();
                if (fs.Length > 0 && fs != "NTFS" && fs != "REFS" && fs != "RAW")
                    blockers.Add($"{alvo} está em {fs}. Só NTFS/ReFS podem ser estendidos online (FAT32/exFAT não).");

                var disk = GetAllDisks().FirstOrDefault(d => d.Index == part.DiskIndex);
                ulong freeRight = disk == null ? 0 : ContiguousFreeRight(disk, part);
                if (disk != null && freeRight == 0)
                    blockers.Add($"{alvo} não tem espaço não alocado encostado à direita. O Windows só estende para o lado direito — " +
                                 "se o espaço estiver no meio ou à esquerda, reduza/exclua a partição que está entre as duas.");

                if (disk != null && disk.PartitionStyle == "MBR" && part.StartingOffset + part.Size + freeRight > 2UL * 1024 * 1024 * 1024)
                    blockers.Add("Limite de 2 TB da tabela MBR: a extensão passaria de 2 TB. Converta o disco para GPT (disco vazio) ou reduza o alvo.");
            }
            else
            {
                if (!string.IsNullOrEmpty(letter))
                {
                    if (HasHiberFileOn(letter))
                        warnings.Add($"Há hiberfil.sys no fim de {letter}: — ele trava a redução. Rode 'powercfg /h off' para liberar esse espaço.");
                    if (HasPageFileOn(letter))
                        warnings.Add($"Existe pagefile em {letter}: — mova-o para outro disco em 'Configurações advanced > Memória virtual' para liberar espaço no fim do volume.");
                }
            }

            if (!string.IsNullOrEmpty(letter) && IsBitLockerProtected(letter))
                warnings.Add($"{letter}: está protegido por BitLocker. Se o redimensionamento falhar, desbloqueie/suspenda a proteção antes.");

            if (part.IsProtected)
                warnings.Add($"{alvo} é partição de sistema/boot (proteção ativa) — operações podem exigir WinPE.");

            return (blockers, warnings);
        }

        /// <summary>Offset de início da partição (necessário p/ achar a partição recriada depois de delete).</summary>
        private static long GetPartitionOffset(uint diskIndex, uint partitionIndex)
        {
            try
            {
                var part = GetAllDisks()
                    .FirstOrDefault(d => d.Index == diskIndex)?
                    .Partitions.FirstOrDefault(p => p.Index == partitionIndex && !p.IsUnallocated);
                return part == null ? -1 : (long)part.StartingOffset;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Acha a partição que RECRIOU a original, pelo offset mais próximo. O código antigo usava
        /// Partitions.LastOrDefault(...) — que devolve a última partição do DISCO, não a nova:
        /// depois de um delete+create o WIM podia ser aplicado na partição errada.
        /// </summary>
        private static PartitionInfoEx? DetectRecreatedPartition(uint diskIndex, long oldStartingOffset)
        {
            try
            {
                var parts = GetAllDisks()
                    .FirstOrDefault(d => d.Index == diskIndex)?
                    .Partitions.Where(p => !p.IsUnallocated).ToList();
                if (parts == null || parts.Count == 0) return null;

                if (oldStartingOffset < 0) return parts.LastOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter));

                return parts
                    .OrderBy(p => Math.Abs((long)p.StartingOffset - oldStartingOffset))
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter));
            }
            catch { return null; }
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
                    object[] methodArgs = { newSizeInBytes, null! };
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
        /// <summary>
        /// Estende a particao. Escada de 3 degraus, todas VERIFICADAS medindo o volume antes/depois:
        ///   (1) Storage Management API (MSFT_Partition.Resize) — o mesmo caminho do Gerenciador de Discos;
        ///   (2) IOCTL nativo (IOCTL_DISK_GROW_PARTITION + FSCTL_EXTEND_VOLUME) — volume online, sem lock;
        ///   (3) diskpart (ultimo recurso, mantem compatibilidade).
        /// O motivo do "estender falhou" na maioria dos casos e espaco nao alocado NAO contiguo a
        /// direita — e isso e detectado antes de qualquer chamada (SizeMax == tamanho atual).
        /// </summary>
        public static async Task<bool> ExtendPartition(string driveLetter, int extendMb = 0, Action<double, string>? progressCallback = null, uint? diskIndex = null, uint? partitionIndex = null)
        {
            string letter = NormalizeLetter(driveLetter).ToUpperInvariant();
            SetError("");

            if (string.IsNullOrWhiteSpace(letter))
            {
                SetError("Esta partição não tem letra de unidade. Atribua uma letra antes de estender.");
                return false;
            }
            if (!Directory.Exists(letter + ":\\"))
            {
                SetError($"A unidade {letter}: não está acessível (sem letra, RAW ou volume desmontado).");
                return false;
            }

            var sw = Stopwatch.StartNew();
            long before = GetVolumeSizeBytes(letter);
            Log($"Estendendo {letter}: {(extendMb > 0 ? $"em {extendMb} MB" : "para todo espaço contíguo")} (antes: {before / 1048576} MB)");
            var reasons = new List<string>();

            ulong targetBytes = extendMb > 0 ? (ulong)Math.Max(0L, before + (long)extendMb * 1024 * 1024) : 0;

            // (1) Storage Management API
            progressCallback?.Invoke(10, "Consultando espaço disponível...");
            var limits = GetPartitionSizeLimits(letter[0]);

            if (limits.ReturnCode == 0 && limits.SizeMax > 0)
            {
                ulong newSize = targetBytes > 0 ? Math.Min(targetBytes, limits.SizeMax) : limits.SizeMax;

                if (limits.SizeMax <= limits.Size || limits.SizeMax <= (ulong)Math.Max(0L, before))
                {
                    SetError($"Não há espaço não alocado CONTÍGUO à direita de {letter}: para estender, o espaço precisa " +
                             "estar imediatamente depois da partição (o Windows não move partições). " +
                             "Opções: reduza a partição vizinha à direita (Estender → Vizinha) ou exclua a partição que está no meio.");
                    Log($"[EXTEND] Sem espaço contiguo: Size={limits.Size} SizeMax={limits.SizeMax}");
                    progressCallback?.Invoke(-1, SetError(""));
                    return false;
                }

                if (newSize > limits.SizeMax) newSize = limits.SizeMax;

                progressCallback?.Invoke(25, "Estendendo via Storage API...");
                uint rc = ResizeStoragePartition(letter[0], newSize);
                long after = GetVolumeSizeBytes(letter);

                if (rc == 0 && after > before)
                {
                    Log($"✅ Estendido via Storage API em {sw.ElapsedMilliseconds} ms: {before / 1048576} MB -> {after / 1048576} MB (+{(after - before) / 1048576} MB)");
                    progressCallback?.Invoke(100, $"Estendido (+{(after - before) / 1048576} MB)");
                    return true;
                }

                reasons.Add(rc == 0 ? "a Storage API respondeu OK, mas o volume não cresceu" : $"Storage API: {GetStorageErrorMessage(rc)}");
                progressCallback?.Invoke(40, "Storage API recusou. Tentando IOCTL nativo...");
            }
            else
            {
                reasons.Add($"Storage API indisponível (rc={limits.ReturnCode}: {limits.ErrorMessage})");
            }

            // (2) IOCTL nativo (partição ao vivo, volume online)
            if (diskIndex.HasValue && partitionIndex.HasValue)
            {
                progressCallback?.Invoke(50, "IOCTL nativo (GROW_PARTITION + EXTEND_VOLUME)...");
                var (ioctlOk, ioctlErr) = TryExtendNativeIoctl(letter, diskIndex.Value, partitionIndex.Value, targetBytes, before);
                if (ioctlOk)
                {
                    long after = GetVolumeSizeBytes(letter);
                    Log($"✅ Estendido via IOCTL nativo em {sw.ElapsedMilliseconds} ms: {before / 1048576} MB -> {after / 1048576} MB");
                    progressCallback?.Invoke(100, $"Estendido (+{(after - before) / 1048576} MB)");
                    return after > before;
                }
                reasons.Add($"IOCTL nativo: {ioctlErr}");
                progressCallback?.Invoke(65, "IOCTL nativo recusou. Usando diskpart...");
            }

            // (3) diskpart
            await EnsureVds();

            StringBuilder script = new StringBuilder(256);
            script.AppendLine($"select volume {letter}");
            if (extendMb > 0)
                script.AppendLine($"extend size={extendMb}");
            else
                script.AppendLine("extend");
            script.AppendLine("exit");

            bool ok;
            {
                // Retry estilo EaseUS (CAsynLockVolume): 1 repetição após 3 s quando a 1ª
                // tentativa não mudou nada (handle aberto libera em segundos). Só repete
                // com medição provando "nada mudou" — nunca após efeito parcial.
                var (ok2, _) = await RunDiskpartWithRetryAsync(
                    script.ToString(), "extend", () => GetVolumeSizeBytes(letter), before, progressCallback);
                ok = ok2;
            }

            // VERIFICAÇÃO PÓS-OPERAÇÃO: o diskpart frequentemente devolve exit 0 sem aplicar nada.
            long afterDp = GetVolumeSizeBytes(letter);
            if (ok && afterDp <= before)
            {
                SetError($"diskpart retornou OK, mas {letter}: NÃO aumentou (antes {before / 1048576} MB, depois {afterDp / 1048576} MB). " +
                         "Causas: espaço não contíguo à direita, sistema de arquivos não-NTFS ou partição de sistema/boot bloqueada. Motivos da API: " +
                         string.Join("; ", reasons));
                Log($"[EXTEND] diskpart exit=0 sem efeito real: {before} -> {afterDp}");
                return false;
            }

            if (!ok)
            {
                var extra = string.Join("; ", reasons);
                if (!string.IsNullOrEmpty(extra) && string.IsNullOrEmpty(LastError))
                    SetError($"Falha ao estender {letter}: ({extra}).");
            }

            return ok;
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

            // Validação defensiva: o disco alvo precisa ter espaço não alocado
            var targetDisk = GetAllDisks().FirstOrDefault(d => d.Index == diskIndex);
            if (targetDisk != null)
            {
                ulong freeBytes = (ulong)targetDisk.Partitions
                    .Where(p => p.IsUnallocated)
                    .Sum(p => (decimal)p.Size);
                if (freeBytes < (10 * 1024 * 1024))
                {
                    Log($"❌ Disco {diskIndex} não tem espaço não alocado disponível ({freeBytes / (1024.0 * 1024 * 1024):F1} GB).");
                    Logger.Log($"[DISKPART] CreatePartition abortado: Disco {diskIndex} sem espaço não alocado ({(freeBytes / (1024.0 * 1024 * 1024)):F1} GB).");
                    return false;
                }
                if (sizeMb > 0 && (ulong)sizeMb * 1024 * 1024 > freeBytes)
                {
                    Log($"❌ Tamanho {sizeMb} MB excede o espaço não alocado de {(freeBytes / (1024.0 * 1024 * 1024)):F1} GB no Disco {diskIndex}.");
                    Logger.Log($"[DISKPART] CreatePartition abortado: {sizeMb} MB > {(freeBytes / (1024.0 * 1024 * 1024)):F1} GB livres no Disco {diskIndex}.");
                    return false;
                }
            }

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
        public static async Task<bool> ChangeDriveLetter(string oldLetter, string newLetter, uint? diskIndex = null, uint? partitionIndex = null)
        {
            oldLetter = NormalizeLetter(oldLetter);
            newLetter = NormalizeLetter(newLetter);
            if (string.IsNullOrWhiteSpace(newLetter)) { Log("❌ Nova letra inválida."); return false; }
            Log($"Alterando letra de {oldLetter}: para {newLetter}:...");

            await EnsureVds();

            StringBuilder script = new();
            if (!string.IsNullOrEmpty(oldLetter))
            {
                script.AppendLine($"select volume {oldLetter}");
                script.AppendLine($"remove letter={oldLetter}");
            }
            else if (diskIndex.HasValue && partitionIndex.HasValue)
            {
                // Partição sem letra: seleciona via disco+partição e atribui direto
                script.AppendLine($"select disk {diskIndex.Value}");
                script.AppendLine($"select partition {partitionIndex.Value}");
            }
            else
            {
                Log("❌ Partição sem letra e sem disco/partição para atribuição.");
                return false;
            }
            script.AppendLine($"assign letter={newLetter}");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "letter");
        }

        // --- QUERY MAX SHRINK ---
        /// <summary>
        /// Maximo real reduzivel em MB. Caminho primario: Storage API (SizeMax - Size) — exato e
        /// instantaneo, sem spawn de diskpart e sem parsing de texto localizado. Fallback: diskpart
        /// "shrink querymax" (necessario em RAW/onde a Storage API nao responde).
        /// </summary>
        public static async Task<long> GetMaxShrinkMb(string driveLetter)
        {
            driveLetter = NormalizeLetter(driveLetter).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(driveLetter)) return 0;

            char letter = driveLetter[0];
            var limits = GetPartitionSizeLimits(letter);
            if (limits.ReturnCode == 0 && limits.Size > 0)
            {
                long mb = (long)((limits.Size - limits.SizeMin) / (1024UL * 1024UL));
                Log($"Máximo reduzível em {driveLetter}: = {mb} MB (Storage API: Size={limits.Size} SizeMin={limits.SizeMin})");
                Logger.Log($"[DISK] MaxShrink {driveLetter}:={mb} MB via Storage API (sem diskpart)");
                return mb;
            }

            Log($"[DISK] Storage API indisponível para {driveLetter} (rc={limits.ReturnCode}), usando diskpart querymax...");
            await EnsureVds();

            StringBuilder script = new();
            script.AppendLine($"select volume {driveLetter}");
            script.AppendLine("shrink querymax");
            script.AppendLine("exit");

            string scriptPath = Path.Combine(Path.GetTempPath(), "pm_querymax.txt");
            File.WriteAllText(scriptPath, script.ToString());
            var (_, output) = await RunProcess("diskpart.exe", $"/s \"{scriptPath}\"");
            try { File.Delete(scriptPath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Parse agnóstico de idioma: pt-BR "O número máximo de bytes recuperáveis é: X MB"
            // en-US "The maximum number of reclaimable bytes is: X MB" (captura o valor antes de "MB")
            var match = Regex.Match(output, @"([\d.,]+)\s*MB", RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value.Replace(".", "").Replace(",", "."), System.Globalization.CultureInfo.InvariantCulture, out double maxMb))
            {
                long mb = (long)maxMb;
                Log($"Máximo reduzível em {driveLetter}: = {mb} MB");
                return mb;
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

            // FAST PATH nativo: IOCTL_DISK_DELETE_DRIVE_LAYOUT (segundos, sem diskpart).
            // Mesma tecnica do cleanDiskFast() do rpi-imager. "clean all" (fullClean) continua
            // no diskpart porque zera setor a setor.
            if (!fullClean)
            {
                var swIoctl = Stopwatch.StartNew();
                using var handle = NativeDiskIo.OpenDisk(diskIndex, write: true);
                if (!handle.IsInvalid && NativeDiskIo.DeleteDriveLayout(handle))
                {
                    swIoctl.Stop();
                    Logger.Log($"[DISK] CleanDisk disco {diskIndex}: IOCTL_DISK_DELETE_DRIVE_LAYOUT em {swIoctl.ElapsedMilliseconds} ms (sem diskpart)");
                    Log($"✅ Disco {diskIndex} limpo via IOCTL nativo (sem diskpart).");
                    return true;
                }
                swIoctl.Stop();
                Logger.Log($"[DISK] CleanDisk disco {diskIndex}: IOCTL falhou em {swIoctl.ElapsedMilliseconds} ms (err={Marshal.GetLastWin32Error()}), caindo no diskpart...");
                Log($"⚠️ IOCTL nativo falhou para disco {diskIndex}, caindo no diskpart...");
            }

            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine(fullClean ? "clean all" : "clean");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "clean");
        }

        // --- CLEAR READ-ONLY (estilo EaseUS: limpa antes de operar, nao so reporta) ---
        // O EaseUS roda "attributes disk clear readonly" ANTES de qualquer operacao
        // (ver docs/EASEUS_EPM_MAP.md §3.2). O Kit so REPORTAVA read-only como bloqueio
        // e parava — o usuario tinha que descobrir sozinho como tirar.
        public static async Task<bool> ClearDiskReadOnlyAsync(uint diskIndex)
        {
            Log($"Disco {diskIndex}: limpando flag somente-leitura (attributes disk clear readonly)...");
            StringBuilder script = new();
            script.AppendLine($"select disk {diskIndex}");
            script.AppendLine("attributes disk clear readonly");
            script.AppendLine("exit");

            return await RunDiskpartScript(script.ToString(), "readonly");
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

            // Deteccao de erros agnostica de idioma (chkdsk localizado: "errors" / "erros" / "fehler"...)
            bool hasErrors = Regex.IsMatch(output, @"\b(?:error|erro|fehler)\b", RegexOptions.IgnoreCase) &&
                            !Regex.IsMatch(output, @"\b(?:no errors|nenhum erro|keine fehler|0 (?:errors|erros)|não foram encontrados erros|not found any errors)\b", RegexOptions.IgnoreCase) &&
                            exitCode != 0;

            return (!hasErrors, output);
        }// --- CONVERT DISK STYLE (MBR <-> GPT) ---
        // Agora delega ao DiskConverterManager (MSFT_Disk.ConvertStyle): converte a tabela NO
        // LUGAR, preservando as partições, e valida antes/depois (o antigo caminho exigia disco
        // vazio e recusava o disco do sistema — restrição que o EaseUS não tem).
        public static async Task<bool> ConvertDiskStyle(uint diskIndex, string targetStyle)
        {
            var target = DiskConverterManager.ParseStyle(targetStyle);
            Log($"Convertendo Disco {diskIndex} para {targetStyle}...");

            var (ok, message) = await DiskConverterManager.ConvertAsync(
                diskIndex, target, force: true, progress: null, logLine: Log);

            foreach (var line in message.Split('\n')) Log(line);
            return ok;
        }

        /// <summary>diskpart "select disk N" + "convert gpt|mbr" (fallback do DiskConverterManager).</summary>
        internal static async Task<bool> RunDiskpartAsync(uint diskIndex, string targetStyle, Action<string>? logLine = null)
        {
            await EnsureVds();

            var sb = new StringBuilder();
            sb.AppendLine($"select disk {diskIndex}");
            sb.AppendLine($"convert {targetStyle.ToLower()}");   // "gpt" ou "mbr"
            sb.AppendLine("exit");

            logLine?.Invoke($"[DISKPART] convert {targetStyle.ToLower()} no disco {diskIndex}");
            return await RunDiskpartScript(sb.ToString(), "convert");
        }
        

        private static bool IsSystemDisk(uint diskIndex)
        {
            try
            {
                int? bootDisk = NativeDiskIo.FindBootDiskNumber();
                if (bootDisk.HasValue) return bootDisk.Value == (int)diskIndex;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            try
            {
                var scope = new ManagementScope(StorageScopePath);
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT IsSystem FROM MSFT_Disk WHERE Number = {diskIndex}"));
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        if (mo["IsSystem"] is bool b) return b;
                    }
                }
                return false;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return IsSystemDiskLegacy(diskIndex); }
        }

        private static bool IsSystemDiskLegacy(uint diskIndex)
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        // --- REMOVE DRIVE LETTER ---

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

        /// <summary>
        /// Motor de imagem: wimlib-imagex (primário) em vez de DISM (fallback).
        ///
        /// POR QUE (03/10/2026, relato do usuario): "o processo de aplicar imagem via dism é lento".
        ///
        /// **MEDIÇÃO REAL (03/10/2026) — e o resultado NÃO confirma o speedup esperado.**
        /// 1,2 GB / 300 arquivos incompressíveis, no mesmo host:
        ///   capture: wimlib 5.063 ms  vs  DISM /Compress:fast 4.819 ms
        ///   apply  : wimlib 1.734 ms  vs  DISM 2.168 ms
        ///   ciclo  : wimlib 6.797 ms  vs  DISM 6.987 ms  (2,7% = ruido)
        /// Conteudo restaurado IDÊNTICO nos dois (diff recursivo sem diferencas).
        /// Motivo: neste fluxo (`AtomicExtendDISM` / `AtomicMergeDISM` / `MovePartition`) NUNCA
        /// houve `/Mount-Image` + `/Unmount /Commit` — o DISM so faz Capture/Apply direto, e
        /// quem paga o mount/commit caro é o `EmergencyWinREManager` e o `WinpeBuilder`.
        ///
        /// ENTÃO O MOTIVO REAL DA TROCA É ROBUSTEZ, não velocidade:
        ///   1. o wimlib é à prova do bug de quoting do DISM (`/ApplyDir:"E:\"` -> exit 123,
        ///      que em 02/08 fez a partição ser recriada SEM restaurar os dados);
        ///   2. não depende do serviço TrustedInstaller/CBS, que falha quando o Windows Update
        ///      está em andamento — comum em maquina real, justamente no pior momento.
        ///
        /// Os WIMs sao interoperaveis nos dois sentidos, entao o fallback cruzado vale.
        /// Se um dia medir-mos em maquina real e o DISM ganhar, e so trocar a ordem.
        /// </summary>
        internal static string? WimlibExe => WinpeBuilder.FindBundledWimlib();

        /// <summary>
        /// Escapa um caminho para a linha de comando SEM o bug de aspas + barra final.
        /// Raiz de volume ("C:\") e tokens especiais (@, #, %) vao SEM aspas: no
        /// CommandLineToArgvW/CRT a barra final escaparia a aspa e truncaria o caminho
        /// (é exatamente o bug que fazia o DISM /ApplyDir:"E:\" devolver exit 123).
        /// </summary>
        private static string CmdArg(string path)
        {
            if (string.IsNullOrEmpty(path)) return "\"\"";
            if (path.Length <= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return path;
            if (path == "@" || path == "#" || path == "%") return "\"" + path + "\"";
            if (path.IndexOf(' ') < 0 && path.IndexOf('\t') < 0) return path;
            return "\"" + path + "\"";
        }

        private static void ReportImageProgress(Action<double, string>? cb, string label, string line)
        {
            if (cb == null) return;
            var m = Regex.Match(line, @"(\d+\.?\d*)%");
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double pct))
                cb(pct, $"{label}: {pct}%");
        }

        /// <summary>
        /// Loga a SAIDA de um processo, truncando.
        /// O wimlib-imagex escreve o progresso com '\r' e SEM '\n', entao uma unica "linha"
        /// pode ter centenas de KB ("412 GiB scanned (679676 files...)" repetido). Medido em
        /// 03/10/2026 no capture de um volume de 400+ GB. Logar isso inteiro estoura o arquivo
        /// de log e trava a UI do console. Aqui ficam so as ultimas linhas UTEIS (o wimlib
        /// escreve o resumo e o erro no fim).
        /// </summary>
        private static void LogTail(string output, string titulo, int maxChars = 3000)
        {
            if (string.IsNullOrWhiteSpace(output)) { Log($"--- {titulo} ---"); Log("(sem saida)"); return; }

            // Normaliza \r em \n para as linhas de progresso virarem linhas separadas.
            string norm = output.Replace('\r', '\n');
            var meaningful = norm.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                .Where(l => !Regex.IsMatch(l, @"(scanned|\(\d+ bytes of \d+ bytes).*%.*done", RegexOptions.IgnoreCase)
                                         && !Regex.IsMatch(l, @"^\s*\d+(\.\d+)?% (scanned|done)", RegexOptions.IgnoreCase))
                                .ToList();

            string body = string.Join(Environment.NewLine, meaningful);
            if (body.Length > maxChars)
                body = "…(saida truncada, " + body.Length + " chars)…\n" + body[^maxChars..];

            Log($"--- {titulo} ---");
            Log(body);
        }

        public static async Task<bool> CaptureVolumeImage(string sourceLetter, string wimPath, Action<double, string>? progressCallback = null, string name = "KitLugia_Capture")
        {
            sourceLetter = NormalizeLetter(sourceLetter);
            EnsureDriveReadyOrThrow(sourceLetter);

            if (string.IsNullOrWhiteSpace(wimPath)) throw new ArgumentException("Caminho do WIM inválido.", nameof(wimPath));
            string? wimDir = Path.GetDirectoryName(wimPath);
            if (!string.IsNullOrWhiteSpace(wimDir) && !Directory.Exists(wimDir)) Directory.CreateDirectory(wimDir);

            // --- MOTOR 1: wimlib-imagex (sem mount, sem commit) ---
            string? wimlib = WimlibExe;
            if (wimlib != null)
            {
                progressCallback?.Invoke(0, "Capturando snapshot (wimlib-imagex, sem DISM)...");
                var sw = Stopwatch.StartNew();
                // wimlib capture DIRECTORY WIMFILE [IMAGE_NAME] — o nome da imagem e POSICIONAL
                // (--name= nao existe nessa versao e faz o binario imprimir o usage inteiro).
                string wlArgs = $"capture {CmdArg($"{sourceLetter}:\\")} {CmdArg(wimPath)} {CmdArg(name)}";
                var (wlExit, wlOut) = await RunProcessStreamed(wimlib, wlArgs, line => ReportImageProgress(progressCallback, "Capturando", line));

                LogTail(wlOut, "WIMLIB CAPTURE");
                Logger.Log($"[WIMLIB] Capture exit={wlExit} em {sw.ElapsedMilliseconds} ms ({sourceLetter}: -> {Path.GetFileName(wimPath)})");

                if (wlExit == 0 && File.Exists(wimPath) && new FileInfo(wimPath).Length > 0)
                    return true;

                if (wlExit != 0)
                {
                    LogErrorsFrom(wlOut, "WIMLIB-CAPTURE");
                    if (File.Exists(wimPath))
                        Log($"Aviso: o wimlib costuma recusar capturar para um WIM que ja existe ({Path.GetFileName(wimPath)}). " +
                            "Apague o arquivo antes de repetir a operacao.");
                }
                Log("wimlib-imagex nao concluiu a captura; voltando para o DISM...");
            }

            // --- MOTOR 2: DISM (fallback) ---
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
            Logger.Log($"[DISM] Capture exit={exitCode} ({sourceLetter}: -> {Path.GetFileName(wimPath)})");
            if (exitCode != 0) LogErrorsFrom(output, "CAPTURE");
            return exitCode == 0;
        }

        public static async Task<bool> ApplyVolumeImage(string wimPath, string targetPath, Action<double, string>? progressCallback = null)
        {
            Log($"Aplicando Imagem {wimPath} para {targetPath}...");

            if (!Directory.Exists(targetPath)) Directory.CreateDirectory(targetPath);
            string applyRoot = targetPath.TrimEnd('\\');

            // --- MOTOR 1: wimlib-imagex (apply direto no arquivo, sem depender do CBS) ---
            string? wimlibExe = WimlibExe;
            if (wimlibExe != null && File.Exists(wimPath))
            {
                progressCallback?.Invoke(0, "Restaurando snapshot (wimlib-imagex, sem DISM)...");
                var sw = Stopwatch.StartNew();
                string wimlibArgs = $"apply {CmdArg(wimPath)} 1 {CmdArg(applyRoot)}";
                var (wExit, wOut) = await RunProcessStreamed(wimlibExe, wimlibArgs, line => ReportImageProgress(progressCallback, "Restaurando (wimlib)", line));

                LogTail(wOut, "WIMLIB APPLY");
                Logger.Log($"[WIMLIB] Apply exit={wExit} em {sw.ElapsedMilliseconds} ms ({Path.GetFileName(wimPath)} -> {applyRoot})");

                if (wExit == 0)
                {
                    Logger.Log("[WIMLIB] Imagem aplicada via wimlib-imagex (à prova do bug de quoting do DISM, exit 123).");
                    return true;
                }

                LogErrorsFrom(wOut, "WIMLIB-APPLY");
                Log("wimlib-imagex nao concluiu a aplicacao; voltando para o DISM...");
            }

            // --- MOTOR 2: DISM (fallback) ---

            // IMPORTANTE (bug 123): raiz de volume NÃO pode ir entre aspas com barra final.
            // "/ApplyDir:\"E:\"" quebra no parsing da linha de comando (\" vira aspa literal)
            // e o DISM retorna 123 (ERROR_INVALID_NAME). Espelha o padrão do CaptureDir.
            string trimmed = targetPath.TrimEnd('\\');
            bool isDriveRoot = trimmed.EndsWith(":", StringComparison.Ordinal);
            string args = isDriveRoot
                ? $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:{trimmed}\\"
                : $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:\"{trimmed}\"";
            args += " /NoRestart";

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
            Logger.Log($"[DISM] Apply exit={exitCode} ({Path.GetFileName(wimPath)} -> {targetPath})");
            if (exitCode != 0)
            {
                // O wimlib (motor 1) JA foi tentado acima e falhou — repetir aqui seria
                // so perder tempo. Fica so o log do erro do DISM.
                LogErrorsFrom(output, "APPLY");
            }
            return exitCode == 0;
        }

        private static void LogErrorsFrom(string output, string etapa)
        {
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0 && (t.Contains("erro", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("falhou", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                     t.StartsWith("[", StringComparison.Ordinal)))
                    Logger.Log($"[DISM {etapa}] {t}");
            }
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
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
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
            try { File.Delete(scriptPath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return output;
        }


        public static async Task<bool> MovePartition(uint diskIndex, uint partitionIndex, string driveLetter, Action<double, string>? progressCallback = null)
        {
            Log($"Iniciando Movimentação Segura (Imaging) da Partição {partitionIndex}...");
            string tempWim = Path.Combine(Path.GetTempPath(), $"move_part_{partitionIndex}.wim");
            long oldOffset = GetPartitionOffset(diskIndex, partitionIndex);   // p/ localizar a recriada depois
            
            progressCallback?.Invoke(0, "Capturando imagem da partição...");
            bool capOk = await CaptureVolumeImage(driveLetter, tempWim, progressCallback);
            if (!capOk) { Logger.Log($"[MOVE] 1.Capture FALHOU ({driveLetter}:)"); Log("Falha na captura da imagem."); return false; }
            Logger.Log($"[MOVE] 1.Capture OK ({driveLetter}:)");

            progressCallback?.Invoke(50, "Excluindo partição original...");

            bool delOk = await DeletePartition(diskIndex, partitionIndex, driveLetter, forceDelete: true);
            if (!delOk) { Logger.Log($"[MOVE] 2.Delete FALHOU (part {partitionIndex} disco {diskIndex})"); Log("Falha ao excluir partição original."); return false; }
            Logger.Log($"[MOVE] 2.Delete OK");

            await Task.Delay(1000); // Wait for VDS refresh

            progressCallback?.Invoke(60, "Recriando partição no novo local...");
            // Aqui assumimos que o espaço não alocado adjacente será usado
            bool createOk = await CreatePartition(diskIndex, 0, "ntfs", "Restaurada");
            if (!createOk) { Logger.Log($"[MOVE] 3.Create FALHOU (disco {diskIndex})"); Log("Falha ao recriar partição."); return false; }
            Logger.Log($"[MOVE] 3.Create OK");

            // Encontrar a nova letra: partição mais próxima do offset original (NÃO a última do disco)
            var newPart = DetectRecreatedPartition(diskIndex, oldOffset);
            string newLetter = newPart?.DriveLetter ?? "";
            Logger.Log($"[MOVE] Nova letra: '{newLetter}' label='{newPart?.Label}'");

            if (string.IsNullOrEmpty(newLetter)) { Logger.Log($"[MOVE] 4.Letra nova não detectada - ABORTADO"); Log("Nova letra não detectada."); return false; }

            progressCallback?.Invoke(80, "Restaurando dados...");
            bool applyOk = await ApplyVolumeImage(tempWim, $"{newLetter}\\", progressCallback);
            Logger.Log($"[MOVE] 4.Apply {(applyOk ? "OK" : "FALHOU")}");

            // Só apaga o snapshot se restaurou — em falha mantém o WIM para recuperação manual.
            if (applyOk) { try { File.Delete(tempWim); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } }
            else Logger.Log($"[MOVE] Snapshot mantido em {tempWim} para recuperação manual (Apply falhou).");

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
                Logger.Log($"[MERGE] 1.Capture FALHOU ({sourceLetter}:) - tentando fallback Robocopy");
                Log("DISM falhou na captura. Tentando fallback Robocopy /B...");
                // Fallback: move os arquivos diretamente e segue com delete+extend.
                bool moveOk = await MoveVolumeData(sourceLetter, targetLetter, progressCallback, "Arquivos_Mesclados");
                if (!moveOk) { Log("Fallback Robocopy também falhou."); return false; }
                capOk = false; // indica que não teremos Apply WIM
            }
            else Logger.Log($"[MERGE] 1.Capture OK ({sourceLetter}:)");

            // 2. Excluir Partição Origem (Liberação Total de Espaço)
            progressCallback?.Invoke(60, "Liberando espaço físico (Excluindo origem)...");

            bool delOk = await DeletePartition(sourceDisk, sourcePart, sourceLetter, forceDelete: true);
            if (!delOk) { Logger.Log($"[MERGE] 2.Delete FALHOU (part {sourcePart} disco {sourceDisk})"); Log("Falha ao liberar espaço físico."); return false; }
            Logger.Log($"[MERGE] 2.Delete OK");

            await Task.Delay(1000); // Estabilização VDS

            // 3. Estender Destino (Crescimento Real)
            progressCallback?.Invoke(70, "Estendendo partição de destino...");
            bool extOk = await ExtendPartition(targetLetter, 0, progressCallback);
            Logger.Log($"[MERGE] 3.Extend {targetLetter}: {(extOk ? "OK" : "FALHOU (continuando)")}");
            if (!extOk) { Log("Falha ao estender destino após liberação."); }

            // 4. Aplicar Imagem (Injeção de Dados)
            progressCallback?.Invoke(80, "Injetando arquivos mesclados...");
            string mergeFolder = Path.Combine($"{targetLetter}:\\", "Arquivos_Mesclados");

            bool finalOk;
            if (File.Exists(tempWim))
            {
                finalOk = await ApplyVolumeImage(tempWim, mergeFolder, progressCallback);
                Logger.Log($"[MERGE] 4.Apply {(finalOk ? "OK" : "FALHOU")}");
                if (finalOk) await SafeDeleteFile(tempWim);
                else Logger.Log($"[MERGE] Snapshot mantido em {tempWim} para recuperação manual (Apply falhou).");
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
            Logger.Log($"[ATOMIC] Extend {driveLetter}: -> 1.Capture (WIM={Path.GetFileName(tempWim)})");
            long oldOffset = GetPartitionOffset(diskIndex, partIndex);   // p/ localizar a recriada depois
            
            // 1. Captura
            progressCallback?.Invoke(0, "Capturando Snapshot para Bypass...");
            if (!await CaptureVolumeImage(driveLetter, tempWim, progressCallback))
            {
                Logger.Log($"[ATOMIC] 1.Capture FALHOU para {driveLetter}:");
                return false;
            }
            Logger.Log($"[ATOMIC] 1.Capture OK ({driveLetter}:)");

            // 2. Delete
            progressCallback?.Invoke(50, "Limpando estrutura bloqueada...");

            if (!await DeletePartition(diskIndex, partIndex, driveLetter, forceDelete: true))
            {
                Logger.Log($"[ATOMIC] 2.Delete FALHOU (disco {diskIndex}, part {partIndex}, {driveLetter}:)");
                return false;
            }
            Logger.Log($"[ATOMIC] 2.Delete OK (part {partIndex} do disco {diskIndex})");

            await Task.Delay(1000);

            // 3. Create (com o novo tamanho)
            progressCallback?.Invoke(70, "Recriando com novo tamanho...");
            if (!await CreatePartition(diskIndex, 0, "ntfs", "Restaurado"))
            {
                Logger.Log($"[ATOMIC] 3.Create FALHOU (disco {diskIndex})");
                return false;
            }
            Logger.Log($"[ATOMIC] 3.Create OK (disco {diskIndex}, tudo não alocado)");

            // Detectar nova letra: partição mais próxima do offset original (NÃO a última do disco —
            // se o disco tem partições depois, o WIM seria aplicado na partição ERRADA)
            var newPart = DetectRecreatedPartition(diskIndex, oldOffset);
            string newLetter = newPart?.DriveLetter ?? "";
            Logger.Log($"[ATOMIC] Nova partição detectada: letra='{newLetter}' label='{newPart?.Label}' type='{newPart?.Type}' size={newPart?.SizeString}");
            if (string.IsNullOrEmpty(newLetter))
            {
                Logger.Log($"[ATOMIC] 4.Letra nova não encontrada - ABORTADO (partição criada sem letra?)");
                return false;
            }

            // 4. Apply
            progressCallback?.Invoke(85, "Restaurando Snapshot...");
            bool ok = await ApplyVolumeImage(tempWim, $"{newLetter}\\", progressCallback);
            Logger.Log($"[ATOMIC] 4.Apply {(ok ? "OK" : "FALHOU")} ({Path.GetFileName(tempWim)} -> {newLetter}\\)");

            // Só apaga o snapshot se restaurou com sucesso — em falha mantém o WIM
            // para recuperação manual (nunca deixar a partição recriada sem os dados).
            if (ok) await SafeDeleteFile(tempWim);
            else Logger.Log($"[ATOMIC] Snapshot mantido em {tempWim} para recuperação manual (Apply falhou).");
            return ok;
        }

        // --- INTERNAL HELPERS ---
        internal static async Task<bool> RunDiskpartScript(string scriptContent, string operationName, Action<double, string>? progressCallback = null)
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

            try { File.Delete(scriptPath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Loga no terminal as linhas de erro do diskpart (antes só iam ao buffer interno)
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 0 && (t.Contains("erro", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("falhou", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("não há espaço", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("no space", StringComparison.OrdinalIgnoreCase) ||
                                     t.Contains("insuficiente", StringComparison.OrdinalIgnoreCase) ||
                                     t.StartsWith("[", StringComparison.Ordinal)))
                    Logger.Log($"[DISKPART] {t}");
            }

            bool hasVdsError = output.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("Erro do Serviço de Disco Virtual", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("serviço de disco virtual", StringComparison.OrdinalIgnoreCase);
            if (hasVdsError)
            {
                string vdsLine = output.Split('\n')
                    .FirstOrDefault(l => l.Contains("Virtual Disk Service error", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("Disco Virtual", StringComparison.OrdinalIgnoreCase)
                                      || l.Contains("Virtual Disk", StringComparison.OrdinalIgnoreCase));
                Log($"ERRO VDS na operação '{operationName}'.");
                SetError($"diskpart/VDS na operação '{operationName}': {(vdsLine ?? "").Trim()}");
                return false;
            }

            // A validação agora confia estritamente no código de saída do processo
            // (Diskpart retorna > 0 se o script falhar estruturalmente ou comandos forem abortados)
            if (exitCode != 0)
            {
                Log($"ERRO detectado na operação '{operationName}'. Código de saída: {exitCode}");
                if (string.IsNullOrEmpty(LastError))
                    SetError($"diskpart terminou com código {exitCode} na operação '{operationName}' (veja o log do terminal para a linha de erro).");
                return false;
            }

            Log($"Operação '{operationName}' concluída.");
            return true;
        }

        /// <summary>
        /// Roda o script diskpart com 1 repetição após 3 s quando a 1ª tentativa não mudou nada.
        /// Estilo EaseUS CAsynLockVolume (MAX_RETRY_TIMES): file handle aberto (antivírus,
        /// indexer, shadow copy em andamento) é a causa nº 1 de "extend/shrink falhou" e
        /// libera em segundos. A repetição SÓ acontece quando a medição prova que nada mudou
        /// (sizeAfter == sizeBefore) — repetir um shrink que já mexeu encolheria 2x, por isso
        /// o gate é pela medição, nunca pela mensagem de erro.
        /// </summary>
        internal static async Task<(bool Ok, bool Retried)> RunDiskpartWithRetryAsync(
            string scriptContent, string operationName,
            Func<long> measureSize, long sizeBefore,
            Action<double, string>? progressCallback = null)
        {
            bool ok = await RunDiskpartScript(scriptContent, operationName, progressCallback);
            if (ok) return (true, false);

            long after = 0;
            try { after = measureSize(); } catch { }
            if (after != sizeBefore)
            {
                Log($"[DISK] '{operationName}' falhou mas o volume mudou ({sizeBefore} -> {after}): sem repetição (efeito parcial).");
                return (false, false);
            }

            Log($"[DISK] '{operationName}' sem efeito — aguardando 3 s (handle aberto?) e tentando de novo...");
            progressCallback?.Invoke(-1, "Sem efeito na 1ª tentativa — aguardando 3 s e tentando de novo...");
            await Task.Delay(3000).ConfigureAwait(false);
            SetError("");
            bool ok2 = await RunDiskpartScript(scriptContent, operationName, progressCallback);
            Log(ok2 ? $"[DISK] '{operationName}' OK na 2ª tentativa." : $"[DISK] '{operationName}' falhou nas 2 tentativas.");
            return (ok2, true);
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
                    StandardOutputEncoding = Encoding.GetEncoding(850),
                    StandardErrorEncoding = Encoding.GetEncoding(850)
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
                if (!proc.WaitForExit(300000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    fullOutput.AppendLine("[TIMEOUT] Processo excedeu 5 minutos e foi encerrado.");
                    proc.WaitForExit(5000); // aguarda o exit code ficar disponível após o Kill
                }

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
        public bool IsSystemDisk { get; set; }
        public bool IsBootDisk { get; set; }
        public List<PartitionInfoEx> Partitions { get; set; } = new();

        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F1} GB";
        public string DisplayName => $"Disco {Index}: {Model} ({SizeString}) [{PartitionStyle}]";

        public void UpdateWithUnallocated(uint diskIndex)
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
                        IsUnallocated = true,
                        DiskIndex = diskIndex
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
                    IsUnallocated = true,
                    DiskIndex = diskIndex
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
        public bool IsSystemFlag { get; set; }
        public bool IsBootFlag { get; set; }

        public string SizeString => $"{(Size / (1024.0 * 1024 * 1024)):F1} GB";
        public string FreeSpaceString => IsUnallocated ? "" : $"{(FreeSpace / (1024.0 * 1024 * 1024)):F1} GB livre";
        public double UsedPercent => (Size > 0 && !IsUnallocated) ? ((double)(Size - FreeSpace) / Size) * 100 : 0;
        public double FreePercent => 100 - UsedPercent;
        public string UsedPercentText => IsUnallocated ? "0%" : $"{UsedPercent:F0}%";
        public double UsedPercentWidth => UsedPercent * 1.4;

        public string Status => IsSystemPartition ? "Sistema/Saudável" : "Saudável";

        public bool IsSystemPartition =>
            IsSystemFlag ||
            IsBootFlag ||
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
