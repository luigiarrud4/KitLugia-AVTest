using System;
using System.Diagnostics;

namespace KitLugia.Core
{
    public static class ProcessRunner
    {
        public static (int ExitCode, string Output, string Error) Run(string fileName, string arguments, int timeoutMs = 5000)
        {
            try
            {
                // Encoding OEM (cp850/cp437/65001): ferramentas nativas emitem OEM;
                // ler como UTF-8 fixo gerava mojibake ("[SC] ChangeServiceConfig �XITO").
                var oem = SystemUtils.GetOemEncoding();
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = oem,
                        StandardErrorEncoding = oem
                    }
                };
                
                process.Start();
                
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                
                if (process.WaitForExit(timeoutMs))
                {
                    return (process.ExitCode, output, error);
                }
                else
                {
                    process.Kill();
                    Logger.Log($"[PROCESS] Timeout ao executar: {fileName} {arguments}");
                    return (-1, "", "Timeout");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[PROCESS] Erro ao executar {fileName}: {ex.Message}");
                return (-1, "", ex.Message);
            }
        }
    }
}
