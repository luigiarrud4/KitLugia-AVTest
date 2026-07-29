using System.Runtime.InteropServices;
using System.Text;

namespace KitLugia.WinPE
{
    public static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder? fileSystemNameBuffer,
            int fileSystemNameSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetLogicalDrives();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern uint GetDriveType(string lpRootPathName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        public static string GetDriveLabel(string drive)
        {
            try
            {
                var sb = new StringBuilder(261);
                if (GetVolumeInformation(drive, sb, sb.Capacity,
                    out _, out _, out _, null, 0))
                {
                    string label = sb.ToString().Trim();
                    return string.IsNullOrEmpty(label) ? "Sem Rótulo" : label;
                }
            }
            catch { }
            return "Sem Rótulo";
        }

        public static ulong GetDriveFreeSpace(string drive)
        {
            if (GetDiskFreeSpaceEx(drive, out ulong free, out ulong total, out _))
                return free;
            return 0;
        }

        public static ulong GetDriveTotalSize(string drive)
        {
            if (GetDiskFreeSpaceEx(drive, out _, out ulong total, out _))
                return total;
            return 0;
        }
    }
}
