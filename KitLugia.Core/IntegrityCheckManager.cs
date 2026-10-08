using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class IntegrityCheckManager
    {
        private static bool CheckDiskHealth(string drive)
        {
            try
            {
                // TODO: Implementar consulta WMI para MSStorageDriver_FailurePredictStatus
                return true; 
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return true; }
        }

        private static bool VerifyFileSystem(string drive)
        {
            try
            {
                // Executa chkdsk em modo somente leitura para verificar erros sem travar a thread por horas
                string output = SystemUtils.RunExternalProcess("chkdsk", drive.Substring(0, 2), true);
                return !output.Contains("detected problems") && !output.Contains("erros");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        private static long GetFreeSpaceBytes(string drive)
        {
            try
            {
                var dInfo = new DriveInfo(drive.Substring(0, 1));
                return dInfo.AvailableFreeSpace;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return 0; }
        }
    }
}
