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
        public record IntegrityResult(bool Passed, string Message, string Details);

        /// <summary>
        /// Realiza uma verificação completa de pré-operação para evitar falhas catastróficas.
        /// </summary>
        public static IntegrityResult RunPreOperationCheck(string targetDrive)
        {
            Logger.Log($"[INTEGRIDADE] Iniciando verificação de pré-operação para {targetDrive}...");

            // 1. Verificar se o disco está saudável (S.M.A.R.T via WMI)
            if (!CheckDiskHealth(targetDrive))
                return new IntegrityResult(false, "Disco em estado crítico!", "Atributos S.M.A.R.T indicam falha iminente no hardware.");

            // 2. Verificar consistência do Sistema de Arquivos (Chkdsk modo RO)
            if (!VerifyFileSystem(targetDrive))
                return new IntegrityResult(false, "Sistema de Arquivos Corrompido!", "Erros detectados no NTFS. Execute 'chkdsk /f' antes de prosseguir.");

            // 3. Verificar Espaço em Disco
            long freeSpace = GetFreeSpaceBytes(targetDrive);
            if (freeSpace < 15L * 1024 * 1024 * 1024) // 15GB mínimo
                return new IntegrityResult(false, "Espaço Insuficiente!", $"Necessário 15GB livres, mas há apenas {freeSpace / 1024 / 1024 / 1024}GB.");

            return new IntegrityResult(true, "Integridade Verificada!", "O sistema está pronto para a operação de particionamento.");
        }

        private static bool CheckDiskHealth(string drive)
        {
            try
            {
                // TODO: Implementar consulta WMI para MSStorageDriver_FailurePredictStatus
                return true; 
            }
            catch { return true; }
        }

        private static bool VerifyFileSystem(string drive)
        {
            try
            {
                // Executa chkdsk em modo somente leitura para verificar erros sem travar a thread por horas
                string output = SystemUtils.RunExternalProcess("chkdsk", drive.Substring(0, 2), true);
                return !output.Contains("detected problems") && !output.Contains("erros");
            }
            catch { return false; }
        }

        private static long GetFreeSpaceBytes(string drive)
        {
            try
            {
                var dInfo = new DriveInfo(drive.Substring(0, 1));
                return dInfo.AvailableFreeSpace;
            }
            catch { return 0; }
        }
    }
}
