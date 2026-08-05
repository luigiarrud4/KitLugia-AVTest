using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KitLugia.Core
{
    /// <summary>
    /// Camada nativa de acesso a disco via DeviceIoControl (winioctl.h), sem WMI e sem diskpart.
    /// Enumera discos/particoes em milissegundos (IOCTL_DISK_GET_DRIVE_LAYOUT_EX),
    /// limpa disco em segundos (IOCTL_DISK_DELETE_DRIVE_LAYOUT) e associa volumes por
    /// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS. Structs conferidas com winioctl.h e com
    /// MBW.Libraries.DeviceIOControlLib (referencia).
    /// Fonte dos IOCTLs: rpi-imager/src/windows/diskpart_util.h (cleanDiskFast) e Microsoft docs.
    /// </summary>
    internal static class NativeDiskIo
    {
        // --- IOCTL codes (CTL_CODE calculado do winioctl.h / ntdddisk.h) ---
        internal const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
        internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        internal const uint IOCTL_DISK_GROW_PARTITION = 0x0007C0D0;
        internal const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT = 0x0007C100;
        internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        internal const uint FSCTL_QUERY_SHRINK_VOLUME = 0x00090114;
        internal const uint FSCTL_SHRINK_VOLUME = 0x000901DC;
        internal const uint FSCTL_EXTEND_VOLUME = 0x00090118;

        private const uint PARTITION_STYLE_MBR = 0;
        private const uint PARTITION_STYLE_GPT = 1;
        private const uint PARTITION_STYLE_RAW = 2;

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_READ_ATTRIBUTES = 0x80;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        // --- P/Invoke ---
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll")]
        private static extern uint GetLogicalDrives();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(string lpRootPathName,
            StringBuilder lpVolumeNameBuffer, uint nVolumeNameSize, out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller, out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        // --- Structs nativas (winioctl.h) ---

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_NUMBER
        {
            public uint DeviceType;
            public uint DeviceNumber;
            public uint PartitionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_GEOMETRY
        {
            public long Cylinders;
            public uint MediaType;
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_GEOMETRY_EX
        {
            public DISK_GEOMETRY Geometry;
            public long DiskSize;
        }

        // DRIVE_LAYOUT_INFORMATION_EX: cabecalho seguido por PARTITION_INFORMATION_EX
        // de 144 bytes cada (alinhamento natural de 8, confirmado por hexdump do kernel):
        //   style(4) + pad(4) + StartingOffset(8) + PartitionLength(8) + PartitionNumber(4)
        //   + RewritePartition(1) + pad(3) + union(112: MBR 12 bytes | GPT 16+16+8+72)
        // Tamanho do cabecalho NAO e fixo:
        //   - MBR: 16 bytes (style 4 + count 4 + Signature 4 + CheckSum 4)
        //   - GPT: 48 bytes (o kernel ntioapi adiciona ao GUID de 16: StartingUsableOffset 8
        //     + UsableLength 8 + MaxPartitionCount 4 + pad 4). Confirmado: returned=768
        //     = 48 + 5*144, primeira entrada em 0x30 com GUID/len/num corretos.
        private const int PartitionEntrySize = 144;
        private const int PartitionUnionOffset = 32;
        private const int LayoutHeaderMaxSize = 48;

        private static int LayoutHeaderSize(uint style) => style == PARTITION_STYLE_GPT ? 48 : 16;

        [StructLayout(LayoutKind.Sequential)]
        internal struct DISK_EXTENT
        {
            public uint DiskNumber;
            public long StartingOffset;
            public long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;   // StorageDeviceProperty = 0
            public uint QueryType;    // PropertyStandardQuery = 0
            public uint AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            public byte RemovableMedia;
            public byte CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public uint BusType;
            public uint RawPropertiesLength;
        }

        // --- Resultado limpo da leitura do layout ---
        internal struct NativePartition
        {
            public uint Number;
            public long StartingOffset;
            public long Length;
            public string TypeName;
            public string GptName;
            public bool IsBoot;
            public bool IsSystem;
            public bool IsMsr;
            public bool IsRecovery;
            public bool IsProtected;
            public bool IsData;
        }

        internal struct NativeVolume
        {
            public string Letter; // "C"
            public string Label;
            public string FileSystem;
            public ulong FreeBytes;
            public ulong TotalBytes;
            public List<DISK_EXTENT> Extents;
        }

        internal static SafeFileHandle OpenDisk(uint diskNumber, bool write = false)
        {
            uint access = GENERIC_READ | (write ? GENERIC_WRITE : 0);
            return CreateFileW($@"\\.\PhysicalDrive{diskNumber}", access,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }

        internal static SafeFileHandle OpenVolume(char letter, bool write = false)
        {
            // Volumes abrem com FILE_READ_ATTRIBUTES (leitura de extents funciona sem admin).
            // GENERIC_READ em \\.\X: falha com access denied (5) na maioria dos casos.
            uint access = FILE_READ_ATTRIBUTES | (write ? GENERIC_WRITE : 0);
            return CreateFileW($@"\\.\{letter}:", access,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }

        private static bool DeviceIoControlPtr(SafeFileHandle handle, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint bytesReturned)
        {
            return DeviceIoControl(handle, code, inBuf, inSize, outBuf, outSize, out bytesReturned, IntPtr.Zero);
        }

        internal static bool GetDeviceNumber(SafeFileHandle handle, out STORAGE_DEVICE_NUMBER number)
        {
            number = default;
            int size = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControlPtr(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, (uint)size, out _))
                    return false;
                number = Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(buf);
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal static bool GetDiskSize(SafeFileHandle handle, out long size)
        {
            size = 0;
            IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<DISK_GEOMETRY_EX>());
            try
            {
                if (!DeviceIoControlPtr(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, buf,
                        (uint)Marshal.SizeOf<DISK_GEOMETRY_EX>(), out _))
                    return false;
                size = Marshal.PtrToStructure<DISK_GEOMETRY_EX>(buf).DiskSize;
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Le a tabela de particoes via IOCTL_DISK_GET_DRIVE_LAYOUT_EX (MBR/GPT direto, sem WMI).
        /// Parsing manual por ponteiro (DRIVE_LAYOUT_INFORMATION_EX de winioctl.h/ntioapi.h):
        /// cabecalho 16 (MBR) ou 48 (GPT) bytes + N entradas de 144 bytes (PARTITION_INFORMATION_EX).
        /// Evita structs com union contendo string (inválido no CLR para layout explícito).
        /// </summary>
        internal static bool GetDriveLayout(SafeFileHandle handle, out uint style, out List<NativePartition> partitions)
        {
            style = PARTITION_STYLE_RAW;
            partitions = new List<NativePartition>();
            int capacity = 128;
            uint outSize = (uint)(LayoutHeaderMaxSize + PartitionEntrySize * capacity);
            IntPtr buf = Marshal.AllocHGlobal((int)outSize);
            try
            {
                uint bytesReturned = 0;
                if (!DeviceIoControlPtr(handle, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IntPtr.Zero, 0, buf, outSize, out bytesReturned))
                    return false;
                return ParseDriveLayout(buf, bytesReturned, out style, out partitions);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Interpreta o buffer bruto do IOCTL_DISK_GET_DRIVE_LAYOUT_EX (separado do IOCTL
        /// para permitir teste unitário do parsing). Formato winioctl.h/ntioapi.h:
        /// DRIVE_LAYOUT_INFORMATION_EX (16 MBR / 48 GPT) + PARTITION_INFORMATION_EX (144) * N.
        /// </summary>
        internal static bool ParseDriveLayout(IntPtr buf, uint bytesReturned, out uint style, out List<NativePartition> partitions)
        {
            style = PARTITION_STYLE_RAW;
            partitions = new List<NativePartition>();
            if (bytesReturned < 16) return false;

            style = (uint)Marshal.ReadInt32(buf, 0);
            int count = Marshal.ReadInt32(buf, 4);
            int headerSize = LayoutHeaderSize(style);
            if (bytesReturned < headerSize) return false;
            int maxCount = (int)(bytesReturned - headerSize) / PartitionEntrySize;
            if (count < 0 || count > maxCount) return false;

            for (int i = 0; i < count; i++)
            {
                IntPtr p = IntPtr.Add(buf, headerSize + i * PartitionEntrySize);
                uint partStyle = (uint)Marshal.ReadInt32(p, 0);
                var part = new NativePartition
                {
                    Number = (uint)Marshal.ReadInt32(p, 24),
                    StartingOffset = Marshal.ReadInt64(p, 8),
                    Length = Marshal.ReadInt64(p, 16)
                };

                if (partStyle == PARTITION_STYLE_GPT)
                {
                    Guid type = Marshal.PtrToStructure<Guid>(IntPtr.Add(p, PartitionUnionOffset));
                    part.TypeName = GptTypeName(type);
                    // Nome GPT: WCHAR[36] no offset 40 da union (union começa em 32)
                    IntPtr namePtr = IntPtr.Add(p, PartitionUnionOffset + 40);
                    int nameLen = 0;
                    while (nameLen < 36 && Marshal.ReadInt16(namePtr, nameLen * 2) != 0) nameLen++;
                    if (nameLen > 0)
                        part.GptName = Marshal.PtrToStringUni(namePtr, nameLen) ?? "";
                    part.IsSystem = type == KnownGuids.Esp;
                    part.IsMsr = type == KnownGuids.Msr;
                    part.IsRecovery = type == KnownGuids.Recovery;
                    part.IsData = type == KnownGuids.BasicData;
                    part.IsProtected = part.IsSystem || part.IsMsr || part.IsRecovery;
                }
                else if (partStyle == PARTITION_STYLE_MBR)
                {
                    byte partType = Marshal.ReadByte(p, PartitionUnionOffset);
                    byte bootIndicator = Marshal.ReadByte(p, PartitionUnionOffset + 1);
                    part.TypeName = MbrTypeName(partType);
                    part.IsBoot = bootIndicator != 0;
                    part.IsRecovery = partType == 0x27;
                    part.IsData = partType is 0x07 or 0x0B or 0x0C or 0x06 or 0x0E or 0x17;
                    part.IsProtected = part.IsRecovery;
                }
                else
                {
                    part.TypeName = "RAW";
                }
                partitions.Add(part);
            }
            return true;
        }

        /// <summary>
        /// Deleta a tabela de particoes (equivalente ao "clean" do diskpart, versao rapida).
        /// Mesma tecnica do cleanDiskFast() do rpi-imager.
        /// </summary>
        internal static bool DeleteDriveLayout(SafeFileHandle handle)
        {
            return DeviceIoControlPtr(handle, IOCTL_DISK_DELETE_DRIVE_LAYOUT, IntPtr.Zero, 0, IntPtr.Zero, 0, out _);
        }

        /// <summary>
        /// Lê modelo, serial e tipo de barramento via IOCTL_STORAGE_QUERY_PROPERTY (sem WMI).
        /// </summary>
        internal static bool GetStorageProperties(SafeFileHandle handle, out string model, out string serial, out string bus)
        {
            model = "";
            serial = "";
            bus = "Unknown";
            uint qSize = (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            IntPtr inBuf = Marshal.AllocHGlobal((int)qSize);
            IntPtr outBuf = Marshal.AllocHGlobal(512);
            try
            {
                Marshal.StructureToPtr(new STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 }, inBuf, false);
                if (!DeviceIoControlPtr(handle, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, qSize, outBuf, 512, out _))
                    return false;

                var desc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(outBuf);
                if (desc.Size == 0) return false;

                model = ReadAnsiString(outBuf, desc.ProductIdOffset);
                serial = ReadAnsiString(outBuf, desc.SerialNumberOffset);
                bus = BusTypeName(desc.BusType);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
            }
        }

        private static string ReadAnsiString(IntPtr basePtr, uint offset)
        {
            if (offset == 0) return "";
            IntPtr p = IntPtr.Add(basePtr, (int)offset);
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            if (len == 0) return "";
            byte[] data = new byte[len];
            Marshal.Copy(p, data, 0, len);
            return Encoding.ASCII.GetString(data).Trim();
        }

        /// <summary>
        /// Enumera volumes com letra: label, FS, espaco livre e extents (disco/offset) sem WMI.
        /// Layout nativo de IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS: DWORD count (offset 0) + 4 bytes de
        /// alinhamento + DISK_EXTENT[] de 24 bytes cada (8-alinhado). Confirma por returned=32 p/ 1 extent.
        /// </summary>
        internal static List<NativeVolume> EnumerateVolumes()
        {
            var result = new List<NativeVolume>();
            uint mask = GetLogicalDrives();
            int extentSize = 24; // DISK_EXTENT nativo (DWORD + pad4 + LONG64 + LONG64)
            int extentsStart = 8; // count(4) + pad(4)
            for (char c = 'A'; c <= 'Z'; c++)
            {
                if ((mask & (1u << (c - 'A'))) == 0) continue;
                string root = $"{c}:\\";
                var label = new StringBuilder(260);
                var fs = new StringBuilder(64);
                ulong free = 0, total = 0, freeTotal = 0;
                try
                {
                    if (GetVolumeInformationW(root, label, 260, out _, out _, out _, fs, 64))
                    {
                        GetDiskFreeSpaceEx(root, out free, out total, out freeTotal);
                    }
                }
                catch { continue; }

                var extents = new List<DISK_EXTENT>();
                using (var vol = OpenVolume(c))
                {
                    if (!vol.IsInvalid)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(extentsStart + extentSize * 32);
                        try
                        {
                            if (DeviceIoControlPtr(vol, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf,
                                    (uint)(extentsStart + extentSize * 32), out uint returned))
                            {
                                int count = Marshal.ReadInt32(buf, 0);
                                if (count > 0 && returned >= extentsStart)
                                {
                                    for (int i = 0; i < count && i < 32; i++)
                                    {
                                        // Leitura raw: DiskNumber@0, StartOffset@8, Length@16 (dentro do extent)
                                        IntPtr p = IntPtr.Add(buf, extentsStart + i * extentSize);
                                        extents.Add(new DISK_EXTENT
                                        {
                                            DiskNumber = (uint)Marshal.ReadInt32(p, 0),
                                            StartingOffset = Marshal.ReadInt64(p, 8),
                                            ExtentLength = Marshal.ReadInt64(p, 16)
                                        });
                                    }
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }

                result.Add(new NativeVolume
                {
                    Letter = c.ToString(),
                    Label = label.ToString().Trim(),
                    FileSystem = fs.ToString().Trim(),
                    FreeBytes = free,
                    TotalBytes = total,
                    Extents = extents
                });
            }
            return result;
        }

        /// <summary>
        /// Número do disco físico que contém o Windows (usado p/ IsSystemDisk sem WMI).
        /// </summary>
        internal static int? FindBootDiskNumber()
        {
            string root;
            try { root = Path.GetPathRoot(Environment.SystemDirectory) ?? ""; }
            catch { return null; }
            if (root.Length < 3) return null;
            char letter = root[0];

            var volumes = EnumerateVolumes();
            foreach (var v in volumes)
            {
                if (!v.Letter.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var e in v.Extents)
                    return (int)e.DiskNumber;
            }
            return null;
        }

        private static string BusTypeName(uint bus) => bus switch
        {
            1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE-1394", 5 => "SSA",
            6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS",
            11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File Backed",
            16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", _ => "Unknown"
        };

        private static string MbrTypeName(byte type) => type switch
        {
            0x01 => "FAT12", 0x04 => "FAT16 (small)", 0x06 => "FAT16", 0x07 => "NTFS/exFAT (IFS)",
            0x0B => "FAT32", 0x0C => "FAT32 (LBA)", 0x0E => "FAT16 (LBA)", 0x11 => "FAT12 (Hidden)",
            0x17 => "NTFS (Hidden)", 0x1B => "FAT32 (Hidden)", 0x1C => "FAT32 (Hidden LBA)",
            0x27 => "WinRE (Recovery)", 0x42 => "LDM", 0x82 => "Linux Swap", 0x83 => "Linux",
            0x8E => "Linux LVM", 0xA5 => "FreeBSD", 0xAF => "HFS", 0xEE => "GPT Protective",
            0xEF => "EFI (FAT)", _ => $"MBR 0x{type:X2}"
        };

        internal static string GptTypeName(Guid type) => type switch
        {
            _ when type == KnownGuids.Esp => "EFI System",
            _ when type == KnownGuids.Msr => "Microsoft Reserved (MSR)",
            _ when type == KnownGuids.BasicData => "Basic data",
            _ when type == KnownGuids.LdmMetadata => "LDM Metadata",
            _ when type == KnownGuids.LdmData => "LDM Data",
            _ when type == KnownGuids.Recovery => "Windows Recovery",
            _ when type == KnownGuids.Unknown => "Unknown",
            _ when type == KnownGuids.LinuxFilesystem => "Linux Filesystem",
            _ when type == KnownGuids.LinuxSwap => "Linux Swap",
            _ when type == KnownGuids.LinuxLvm => "Linux LVM",
            _ when type == KnownGuids.LinuxRootX64 => "Linux Root x86-64",
            _ when type == KnownGuids.LinuxHome => "Linux Home",
            _ when type == KnownGuids.AppleHfs => "Apple HFS",
            _ when type == KnownGuids.AppleApfs => "Apple APFS",
            _ when type == KnownGuids.LinuxBoot => "Linux Boot",
            _ => "GPT"
        };

        private static class KnownGuids
        {
            internal static readonly Guid Esp = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
            internal static readonly Guid Msr = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
            internal static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
            internal static readonly Guid LdmMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
            internal static readonly Guid LdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");
            internal static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
            internal static readonly Guid Unknown = new("486c8f40-4a65-4a8a-9a2e-8b5c9f5f3e3a");
            internal static readonly Guid LinuxFilesystem = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
            internal static readonly Guid LinuxSwap = new("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");
            internal static readonly Guid LinuxLvm = new("e6d6d379-f507-44c2-a23c-238f2a3df928");
            internal static readonly Guid LinuxRootX64 = new("4f68bce3-e8cd-4db1-96e7-fbcaf984b709");
            internal static readonly Guid LinuxHome = new("933ac7e1-2eb4-4f13-b844-0e14e2aef915");
            internal static readonly Guid LinuxBoot = new("bc13c2ff-59e6-4262-a352-b275fd6f7172");
            internal static readonly Guid AppleHfs = new("48465300-0000-11aa-aa11-00306543ecac");
            internal static readonly Guid AppleApfs = new("7c3457ef-0000-11aa-aa11-00306543ecac");
        }
    }
}
