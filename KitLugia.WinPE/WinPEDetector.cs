using System.IO;
using System.Runtime.InteropServices;

namespace KitLugia.WinPE
{
    public static class WinPEDetector
    {
        public static bool IsWinPE()
        {
            try
            {
                if (File.Exists(@"X:\Windows\System32\winpe.jpg"))
                    return true;
                if (File.Exists(@"X:\Windows\System32\startnet.cmd"))
                    return true;
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (pf.StartsWith("X:\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

        public static bool IsValOS()
        {
            try
            {
                if (File.Exists(@"C:\Windows\System32\startnet.valos.cmd"))
                    return true;
                if (Directory.Exists(@"C:\KL_WINPE"))
                    return true;
            }
            catch { }
            return false;
        }

        public static string GetEnvironment()
        {
            if (IsValOS()) return "Validation OS";
            if (IsWinPE()) return "WinPE";
            return "Windows";
        }
    }
}
