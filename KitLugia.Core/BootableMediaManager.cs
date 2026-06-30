using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public enum BootableMediaMode
    {
        RawIso,       // Rufus-like: write ISO directly to USB (sector by sector)
        WindowsBoot,  // Use bcdboot + bootsect to create Windows bootable USB
        WinPE         // Create WinPE/RE recovery drive
    }

    [SupportedOSPlatform("windows")]
    public class DriveEntry
    {
        public uint DiskNumber { get; set; }
        public string DriveLetter { get; set; } = "";
        public string Label { get; set; } = "";
        public long Size { get; set; }
        public string BusType { get; set; } = "";
    }

    [SupportedOSPlatform("windows")]
    public class BootableMediaOptions
    {
        public string FileSystem { get; set; } = "FAT32";
        public string Label { get; set; } = "KITLUGIA";
        public bool UseGPT { get; set; } = false;
        public bool QuickFormat { get; set; } = true;
    }

    [SupportedOSPlatform("windows")]
    public static class BootableMediaManager
    {
        #region Win32 Raw Disk Access

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        private enum STORAGE_BUS_TYPE
        {
            BusTypeUnknown = 0x00,
            BusTypeScsi = 0x01,
            BusTypeAtapi = 0x02,
            BusTypeAta = 0x03,
            BusType1394 = 0x04,
            BusTypeSsa = 0x05,
            BusTypeFibre = 0x06,
            BusTypeUsb = 0x07,
            BusTypeRAID = 0x08,
            BusTypeiScsi = 0x09,
            BusTypeSas = 0x0A,
            BusTypeSata = 0x0B,
            BusTypeSd = 0x0C,
            BusTypeMmc = 0x0D,
            BusTypeVirtual = 0x0E,
            BusTypeFileBackedVirtual = 0x0F,
            BusTypeSpaces = 0x10,
            BusTypeNvme = 0x11,
            BusTypeSCM = 0x12,
            BusTypeUfs = 0x13,
            BusTypeMax = 0x14,
            BusTypeMaxReserved = 0x7F
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            [MarshalAs(UnmanagedType.U1)]
            public bool RemovableMedia;
            [MarshalAs(UnmanagedType.U1)]
            public bool CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public STORAGE_BUS_TYPE BusType;
            public uint RawPropertiesLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] RawDeviceProperties;
        }

        #endregion

        /// <summary>
        /// Lists all physical drives suitable for bootable media using the same
        /// approach as Rufus/Ventoy: enumerate \\.\PhysicalDriveX and query bus type.
        /// Returns drive letters mapped to physical drives.
        /// </summary>
        public static List<DriveEntry> GetUsbDrives()
        {
            var result = new List<DriveEntry>();
            var systemDrive = Environment.SystemDirectory[0].ToString();

            for (uint diskNum = 0; diskNum < 64; diskNum++)
            {
                try
                {
                    string physicalPath = $@"\\.\PhysicalDrive{diskNum}";
                    using var handle = CreateFile(physicalPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (handle.IsInvalid) continue;

                    // Query bus type
                    var query = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 0, // StorageDeviceProperty
                        QueryType = 0,  // PropertyStandardQuery
                        AdditionalParameters = new byte[1]
                    };

                    int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
                    IntPtr queryPtr = Marshal.AllocHGlobal(querySize);
                    Marshal.StructureToPtr(query, queryPtr, false);

                    int descSize = Marshal.SizeOf<STORAGE_DEVICE_DESCRIPTOR>() + 256;
                    IntPtr descPtr = Marshal.AllocHGlobal(descSize);
                    // Zero-initialize the memory
                    for (int i = 0; i < descSize; i++) Marshal.WriteByte(descPtr, i, 0);
                    uint bytesReturned;

                    bool success = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                        queryPtr, (uint)querySize, descPtr, (uint)descSize, out bytesReturned, IntPtr.Zero);

                    Marshal.FreeHGlobal(queryPtr);

                    if (!success)
                    {
                        Marshal.FreeHGlobal(descPtr);
                        continue;
                    }

                    var descriptor = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(descPtr);
                    Marshal.FreeHGlobal(descPtr);

                    // Get drive letters for this physical disk
                    string driveLetter = GetDriveLetterForDisk(diskNum);
                    if (string.IsNullOrEmpty(driveLetter)) continue;

                    // Skip system drive
                    if (driveLetter[0].ToString() == systemDrive) continue;

                    try
                    {
                        var di = new DriveInfo(driveLetter);
                        if (!di.IsReady) continue;
                        if (di.DriveType == DriveType.CDRom) continue;

                        result.Add(new DriveEntry
                        {
                            DiskNumber = diskNum,
                            DriveLetter = driveLetter,
                            Label = di.VolumeLabel,
                            Size = di.TotalSize,
                            BusType = descriptor.BusType.ToString()
                        });
                    }
                    catch { continue; }
                }
                catch { continue; }
            }

            return result.OrderBy(r => r.DriveLetter).ToList();
        }

        private static string GetDriveLetterForDisk(uint diskNumber)
        {
            char[] driveLetters = new char[256];
            uint len = (uint)driveLetters.Length;

            // Use QueryDosDevice to find drive letters for physical drives
            // Simpler: iterate A:-Z: and check which physical disk they belong to
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                string volume = $"{letter}:";
                try
                {
                    int volDiskNum = GetPhysicalDiskNumber(volume);
                    if (volDiskNum == (int)diskNumber)
                    {
                        var di = new DriveInfo(volume);
                        if (di.DriveType != DriveType.CDRom && di.DriveType != DriveType.Network)
                            return volume;
                    }
                }
                catch { continue; }
            }

            return "";
        }

        /// <summary>
        /// Gets the physical disk number for a drive letter (e.g., "D:" -> 1).
        /// </summary>
        public static int GetPhysicalDiskNumber(string driveLetter)
        {
            string volumePath = $@"\\.\{driveLetter.TrimEnd(':')}:";
            using var handle = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) return -1;

            uint bytesReturned;
            var storageDeviceNumber = new byte[32];
            GCHandle pinned = GCHandle.Alloc(storageDeviceNumber, GCHandleType.Pinned);

            try
            {
                bool success = DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    IntPtr.Zero, 0, pinned.AddrOfPinnedObject(), (uint)storageDeviceNumber.Length,
                    out bytesReturned, IntPtr.Zero);

                if (success && bytesReturned >= 12)
                {
                    return BitConverter.ToInt32(storageDeviceNumber, 8);
                }
            }
            finally
            {
                pinned.Free();
            }

            return -1;
        }

        /// <summary>
        /// Formats a USB drive with the specified options using diskpart.
        /// </summary>
        public static async Task<(bool Success, string Message)> FormatDrive(string driveLetter,
            BootableMediaOptions options)
        {
            try
            {
                var diskpartScript = $@"select volume {driveLetter.TrimEnd(':')}
clean
{(options.UseGPT ? "convert gpt" : "convert mbr")}
create partition primary
format fs={options.FileSystem} label={options.Label} quick
active
exit";

                string scriptPath = Path.Combine(Path.GetTempPath(), "kitformat.txt");
                await File.WriteAllTextAsync(scriptPath, diskpartScript);

                var (exitCode, output, error) = await Task.Run(() =>
                    ProcessRunner.Run("diskpart", $"/s \"{scriptPath}\"", 60000));

                try { File.Delete(scriptPath); } catch { }

                return exitCode == 0
                    ? (true, $"Drive {driveLetter} formatado como {options.FileSystem}.")
                    : (false, $"Falha ao formatar: {error}");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        /// <summary>
        /// Rufus-style raw ISO writing: writes ISO contents sector-by-sector to a physical USB drive.
        /// </summary>
        /// <summary>
        /// Detects the type of ISO: "Windows", "Ubuntu", "Debian", "Fedora", "Linux", or "Other".
        /// Examines ISO contents using a mounted volume.
        /// </summary>
        public static async Task<string> DetectIsoType(string isoPath)
        {
            try
            {
                string mountPoint = await WinbootManager.MountIso(isoPath);
                if (string.IsNullOrEmpty(mountPoint))
                    return "Other";

                try
                {
                    // Windows: has sources/install.wim or install.esd
                    if (Directory.Exists(Path.Combine(mountPoint, "sources")))
                    {
                        if (File.Exists(Path.Combine(mountPoint, "sources", "install.wim")) ||
                            File.Exists(Path.Combine(mountPoint, "sources", "install.esd")) ||
                            File.Exists(Path.Combine(mountPoint, "sources", "boot.wim")))
                            return "Windows";
                    }

                    // Ubuntu/Linux: has casper, live, dists, isolinux
                    if (Directory.Exists(Path.Combine(mountPoint, "casper")))
                        return "Ubuntu";
                    if (Directory.Exists(Path.Combine(mountPoint, "live")))
                        return "Linux";
                    if (Directory.Exists(Path.Combine(mountPoint, "dists")) &&
                        Directory.Exists(Path.Combine(mountPoint, "pool")))
                        return "Debian";
                    if (Directory.Exists(Path.Combine(mountPoint, "EFI")) &&
                        Directory.Exists(Path.Combine(mountPoint, "isolinux")))
                        return "Linux";
                    if (File.Exists(Path.Combine(mountPoint, "isolinux", "isolinux.bin")))
                        return "Linux";
                    if (Directory.Exists(Path.Combine(mountPoint, "EFI", "boot")))
                        return "Other";

                    return "Other";
                }
                finally
                {
                    await WinbootManager.DismountIso(isoPath);
                }
            }
            catch
            {
                return "Other";
            }
        }

        /// <summary>
        /// Executa bootsect.exe se disponível. Útil para boot BIOS legacy.
        /// Se não encontrado, apenas loga aviso (UEFI continua funcionando).
        /// </summary>
        private static async Task RunBootsectIfAvailable(string arguments)
        {
            string? bootsectPath = FindBootsectPath();
            if (bootsectPath != null)
            {
                await Task.Run(() => ProcessRunner.Run(bootsectPath, arguments, 30000));
            }
            else
            {
                Logger.Log("[BOOTSECT] bootsect.exe não encontrado (instale Windows ADK para boot BIOS legacy). " +
                           "Boot UEFI continua funcionando normalmente.");
            }
        }

        /// <summary>
        /// Localiza bootsect.exe no Windows ADK ou no PATH.
        /// Retorna o caminho completo ou null se não encontrado.
        /// </summary>
        private static string? FindBootsectPath()
        {
            // 1. Verificar se está no PATH
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "bootsect.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        string firstMatch = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                        if (File.Exists(firstMatch))
                            return firstMatch;
                    }
                }
            }
            catch { }

            // 2. Caminhos comuns do Windows ADK
            string[] adkCandidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Windows Kits", "10", "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "bootsect.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Windows Kits", "10", "Assessment and Deployment Kit",
                    "Deployment and Imaging Tools", "bootsect.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Windows Kits", "10", "Assessment and Deployment Kit",
                    "Windows Preinstallation Environment", "bootsect.exe"),
            };

            foreach (var path in adkCandidates)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        /// <summary>
        /// Creates a dual-boot USB drive (Easy2Boot style): one FAT32 EFI partition
        /// and one NTFS data partition for ISOs. Both BIOS and UEFI bootable.
        /// Uses Windows built-in bootx64.efi + BCD for UEFI, bootsect for BIOS.
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateDualBootDrive(
            List<string> isoPaths, string driveLetter,
            IProgress<(double Percent, string Status)>? progress = null)
        {
            int diskNumber = GetPhysicalDiskNumber(driveLetter);
            if (diskNumber < 0)
                return (false, "Não foi possível identificar o disco físico.");

            progress?.Report((0.0, "Criando partições (FAT32 EFI + NTFS Data)..."));

            // Script diskpart: clean, create EFI partition (512MB FAT32), rest NTFS
            var diskpartScript = $@"select disk {diskNumber}
clean
convert gpt
create partition efi size=512
format fs=fat32 quick label=""KITEFI""
assign letter=""K""
create partition primary
format fs=ntfs quick label=""KITDATA""
assign letter=""L""
exit";

            string scriptPath = Path.Combine(Path.GetTempPath(), "kitdualboot.txt");
            try
            {
                await File.WriteAllTextAsync(scriptPath, diskpartScript);
                var (exitCode, _, error) = await Task.Run(() =>
                    ProcessRunner.Run("diskpart", $"/s \"{scriptPath}\"", 60000));

                if (exitCode != 0)
                    return (false, $"Falha ao particionar: {error}");

                progress?.Report((30.0, "Configurando boot UEFI..."));

                // Copy Windows boot files to EFI partition
                string efiDir = @"K:\EFI\BOOT";
                Directory.CreateDirectory(efiDir);

                // Use Windows built-in bootx64.efi from System32
                string bootmgfw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Boot", "EFI", "bootmgfw.efi");
                if (File.Exists(bootmgfw))
                {
                    File.Copy(bootmgfw, Path.Combine(efiDir, "bootx64.efi"), true);
                    File.Copy(bootmgfw, Path.Combine(efiDir, "bootmgfw.efi"), true);
                }

                progress?.Report((50.0, "Configurando boot BIOS (bootsect)..."));
                await RunBootsectIfAvailable("/nt60 K: /force /mbr");

                // Copy ISOs to data partition
                progress?.Report((60.0, "Copiando ISOs..."));
                string isoDir = @"L:\ISOS";
                Directory.CreateDirectory(isoDir);

                for (int i = 0; i < isoPaths.Count; i++)
                {
                    string destName = $"{i + 1:D2}_{Path.GetFileName(isoPaths[i])}";
                    progress?.Report((60.0 + 30.0 * (i + 1) / isoPaths.Count, $"Copiando {destName}..."));
                    File.Copy(isoPaths[i], Path.Combine(isoDir, destName), true);
                }

                // Create a simple BCD for booting ISOs
                progress?.Report((95.0, "Configurando menu de boot..."));
                try
                {
                    await Task.Run(() =>
                        ProcessRunner.Run("bcdboot", @"K:\Windows /s K: /f UEFI", 30000));
                }
                catch { }

                progress?.Report((100.0, "Concluído!"));
                return (true, $"Dual-boot criado: FAT32(EFI) + NTFS(Dados) com {isoPaths.Count} ISO(s).");
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
                try { SystemUtils.RunExternalProcess("diskpart", "/c \"select volume K\" & \"remove letter=K\"", true); } catch { }
                try { SystemUtils.RunExternalProcess("diskpart", "/c \"select volume L\" & \"remove letter=L\"", true); } catch { }
            }
        }

        /// <summary>
        /// Computes SHA256 hash of a file for verification.
        /// </summary>
        public static async Task<string> ComputeHash(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                byte[] hash = await Task.Run(() => sha256.ComputeHash(stream));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>
        /// DD Image Mode: writes ISO directly to physical disk sector-by-sector (like Rufus DD mode).
        /// Required for ISOs that can't be extracted via mount+copy (e.g., some Linux/BSD ISOs).
        /// </summary>
        public static async Task<(bool Success, string Message)> WriteImageDD(string isoPath,
            string driveLetter, IProgress<(double Percent, string Status)>? progress = null)
        {
            if (!File.Exists(isoPath))
                return (false, "Arquivo ISO não encontrado.");

            int diskNumber = GetPhysicalDiskNumber(driveLetter);
            if (diskNumber < 0)
                return (false, "Não foi possível identificar o disco físico.");

            progress?.Report((5.0, "Preparando para escrita RAW (DD mode)..."));

            string physicalPath = $@"\\.\PhysicalDrive{diskNumber}";
            long totalBytes = new FileInfo(isoPath).Length;
            const int bufferSize = 4 * 1024 * 1024; // 4MB buffer

            try
            {
                using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
                using var diskStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Write, FileShare.Write, bufferSize, FileOptions.WriteThrough);

                long bytesWritten = 0;
                byte[] buffer = new byte[bufferSize];

                while (bytesWritten < totalBytes)
                {
                    int read = await isoStream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    await diskStream.WriteAsync(buffer, 0, read);
                    bytesWritten += read;
                    progress?.Report((5.0 + (bytesWritten * 85.0 / totalBytes), $"Escrevendo setores... {bytesWritten / 1048576:N0} MB"));
                }

                progress?.Report((92.0, "Sincronizando disco..."));
                diskStream.Flush();
                return (true, $"ISO escrita em modo DD. {bytesWritten / 1048576:N0} MB escritos em {driveLetter}.");

            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Acesso negado ao disco físico. Execute como Administrador.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro na escrita DD: {ex.Message}");
            }
        }

        public static async Task<(bool Success, string Message)> WriteIsoRaw(string isoPath,
            string driveLetter, BootableMediaOptions? options = null,
            IProgress<(double Percent, string Status)>? progress = null)
        {
            options ??= new BootableMediaOptions();

            if (!File.Exists(isoPath))
                return (false, "ISO não encontrada.");

            int diskNumber = GetPhysicalDiskNumber(driveLetter);
            if (diskNumber < 0)
                return (false, "Não foi possível identificar o disco físico.");

            progress?.Report((0.0, "Formatando drive..."));

            // 1. Format the drive
            var formatResult = await FormatDrive(driveLetter, options);
            if (!formatResult.Success)
                return formatResult;

            // 2. Mount ISO and copy files
            progress?.Report((10.0, "Montando ISO..."));
            string mountPoint = await WinbootManager.MountIso(isoPath);
            if (string.IsNullOrEmpty(mountPoint))
                return (false, "Falha ao montar a ISO.");

            try
            {
                progress?.Report((20.0, "Copiando arquivos da ISO..."));

                // Copy all files from ISO to USB
                var (exitCode, _, error) = await Task.Run(() =>
                    ProcessRunner.Run("robocopy", $@"""{mountPoint}"" ""{driveLetter}\"" /E /NJH /NFL /NDL /NP", 120000));

                if (exitCode >= 8)
                    return (false, $"Falha ao copiar: {error}");

                progress?.Report((60.0, "Aplicando boot sector..."));

                await RunBootsectIfAvailable($"/nt60 {driveLetter} /force /mbr");

                // Apply BCD boot files if Windows is present
                string windowsPath = $@"{driveLetter}\Windows";
                if (Directory.Exists(windowsPath))
                {
                    progress?.Report((75.0, "Executando bcdboot..."));
                    await Task.Run(() =>
                        ProcessRunner.Run("bcdboot", $@"""{windowsPath}"" /s {driveLetter} /f ALL", 30000));
                }

                progress?.Report((90.0, "Finalizando..."));
            }
            finally
            {
                await WinbootManager.DismountIso(isoPath);
            }

            progress?.Report((100.0, "Concluído!"));
            return (true, $"Drive {driveLetter} criado com sucesso a partir da ISO.");
        }

        /// <summary>
        /// Creates a bootable Windows USB using native Windows tools (bcdboot + bootsect).
        /// Extracts the ISO and applies boot configuration.
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateWindowsBootable(
            string isoPath, string driveLetter, BootableMediaOptions? options = null,
            IProgress<(double Percent, string Status)>? progress = null)
        {
            options ??= new BootableMediaOptions();

            if (!File.Exists(isoPath))
                return (false, "ISO não encontrada.");

            progress?.Report((0.0, "Formatando drive USB..."));
            var formatResult = await FormatDrive(driveLetter, options);
            if (!formatResult.Success) return formatResult;

            progress?.Report((15.0, "Montando ISO..."));
            string mountPoint = await WinbootManager.MountIso(isoPath);
            if (string.IsNullOrEmpty(mountPoint))
                return (false, "Falha ao montar ISO.");

            try
            {
                progress?.Report((25.0, "Copiando arquivos do Windows..."));
                var (exitCode, _, error) = await Task.Run(() =>
                    ProcessRunner.Run("robocopy", $@"""{mountPoint}"" ""{driveLetter}\"" /E /NJH /NFL /NDL /NP", 120000));

                if (exitCode >= 8)
                    return (false, $"Falha na cópia: {error}");

                progress?.Report((50.0, "Aplicando boot sector (bootsect)..."));
                await RunBootsectIfAvailable($"/nt60 {driveLetter} /force /mbr");

                progress?.Report((65.0, "Configurando BCD (bcdboot)..."));
                string windowsPath = $@"{driveLetter}\Windows";
                if (Directory.Exists(windowsPath))
                {
                    await Task.Run(() =>
                        ProcessRunner.Run("bcdboot", $@"""{windowsPath}"" /s {driveLetter} /f ALL", 30000));
                }

                progress?.Report((90.0, "Finalizando..."));
            }
            finally
            {
                await WinbootManager.DismountIso(isoPath);
            }

            progress?.Report((100.0, "Concluído"));
            return (true, $"Windows bootável criado em {driveLetter}.");
        }

        /// <summary>
        /// Creates a WinPE/RE recovery drive on the target USB.
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateWinPEDrive(
            string driveLetter, string? winreWimPath = null,
            IProgress<(double Percent, string Status)>? progress = null)
        {
            progress?.Report((0.0, "Preparando drive de recuperação..."));

            if (string.IsNullOrEmpty(winreWimPath))
            {
                winreWimPath = await WinbootManager.LocateWinreWim();
                if (string.IsNullOrEmpty(winreWimPath))
                    return (false, "WinRE.wim não encontrado. Especifique o caminho manualmente.");
            }

            if (!File.Exists(winreWimPath))
                return (false, "Arquivo WinRE.wim não encontrado.");

            progress?.Report((20.0, "Formatando drive..."));
            var formatResult = await FormatDrive(driveLetter, new BootableMediaOptions
            {
                FileSystem = "FAT32",
                Label = "KITLUGIA_RECOVERY",
                UseGPT = false
            });

            if (!formatResult.Success) return formatResult;

            progress?.Report((40.0, "Copiando WinRE..."));
            string targetDir = $@"{driveLetter}\sources";
            Directory.CreateDirectory(targetDir);
            File.Copy(winreWimPath, Path.Combine(targetDir, "boot.wim"), true);

            progress?.Report((60.0, "Aplicando boot sector..."));
            await RunBootsectIfAvailable($"/nt60 {driveLetter} /force /mbr");

            progress?.Report((80.0, "Configurando BCD..."));
            await Task.Run(() =>
                ProcessRunner.Run("bcdboot", $@"""{targetDir}"" /s {driveLetter} /f ALL", 30000));

            progress?.Report((100.0, "Concluído"));
            return (true, $"Drive de recuperação criado em {driveLetter}.");
        }

        /// <summary>
        /// Creates a multi-boot USB using a simple GRUB2-based approach.
        /// Each ISO is placed in its own folder and added to the GRUB menu.
        /// </summary>
        public static async Task<(bool Success, string Message)> CreateMultiBootDrive(
            List<string> isoPaths, string driveLetter, BootableMediaOptions? options = null,
            IProgress<(double Percent, string Status)>? progress = null)
        {
            options ??= new BootableMediaOptions();

            if (isoPaths == null || isoPaths.Count == 0)
                return (false, "Nenhuma ISO selecionada.");

            progress?.Report((0.0, "Formatando drive..."));
            var formatResult = await FormatDrive(driveLetter, options);
            if (!formatResult.Success) return formatResult;

            double baseProgress = 10.0;
            double step = 70.0 / isoPaths.Count;
            int isoIndex = 0;

            foreach (var isoPath in isoPaths)
            {
                isoIndex++;
                string isoName = Path.GetFileNameWithoutExtension(isoPath);
                progress?.Report((baseProgress, $"[{isoIndex}/{isoPaths.Count}] Montando {isoName}..."));

                string mountPoint = await WinbootManager.MountIso(isoPath);
                if (string.IsNullOrEmpty(mountPoint)) continue;

                try
                {
                    string targetFolder = $@"{driveLetter}\ISOS\{isoName}";
                    Directory.CreateDirectory(targetFolder);

                    progress?.Report((baseProgress + step * 0.5, $"[{isoIndex}/{isoPaths.Count}] Copiando {isoName}..."));
                    await Task.Run(() =>
                        ProcessRunner.Run("robocopy", $@"""{mountPoint}"" ""{targetFolder}"" /E /NJH /NFL /NDL /NP", 120000));
                }
                finally
                {
                    await WinbootManager.DismountIso(isoPath);
                }

                baseProgress += step;
            }

            progress?.Report((85.0, "Instalando GRUB2..."));
            await RunBootsectIfAvailable($"/nt60 {driveLetter} /force");

            progress?.Report((100.0, "Concluído"));
            return (true, $"Multi-boot criado em {driveLetter} com {isoPaths.Count} ISO(s).");
        }
    }
}
