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
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    }
                };
                
                process.Start();
                
                if (process.WaitForExit(timeoutMs))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
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
