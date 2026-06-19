using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    /// <summary>
    /// Microsoft Activation Scripts (MAS) - Lightweight Integration
    /// Lança o MAS_AIO.cmd externo e lê status via slmgr.
    /// </summary>
    public static class ActivationManager
    {
        [DllImport("wininet.dll")]
        private static extern bool InternetGetConnectedState(out int desc, int reserved);

        public class ActivationStatus
        {
            public bool IsActivated { get; set; }
            public string ProductName { get; set; } = "Desconhecido";
            public string LicenseStatus { get; set; } = "Desconhecido";
            public string PartialProductKey { get; set; } = "";
            public string ActivationMethod { get; set; } = "";
        }

        /// <summary>
        /// Lê o status de ativação do Windows usando slmgr /dli (rápido, sem WMI).
        /// </summary>
        public static async Task<ActivationStatus> GetWindowsActivationStatusAsync()
        {
            var status = new ActivationStatus();

            try
            {
                string output = await RunCommandAsync("cscript", "//nologo C:\\Windows\\System32\\slmgr.vbs /dli");

                if (string.IsNullOrWhiteSpace(output))
                {
                    status.LicenseStatus = "Erro ao ler slmgr";
                    return status;
                }

                // Parse output
                foreach (var rawLine in output.Split('\n'))
                {
                    var line = rawLine.Trim();

                    if (line.StartsWith("Nome:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                        status.ProductName = line.Substring(line.IndexOf(':') + 1).Trim();

                    if (line.Contains("License Status:", StringComparison.OrdinalIgnoreCase) || line.Contains("Status da Licença:", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = line.Substring(line.IndexOf(':') + 1).Trim();
                        status.LicenseStatus = val;
                        status.IsActivated = val.Contains("Licensed", StringComparison.OrdinalIgnoreCase) ||
                                             val.Contains("Licenciado", StringComparison.OrdinalIgnoreCase);
                    }

                    if (line.Contains("Partial Product Key:", StringComparison.OrdinalIgnoreCase) || line.Contains("Chave de Produto Parcial:", StringComparison.OrdinalIgnoreCase))
                        status.PartialProductKey = line.Substring(line.IndexOf(':') + 1).Trim();
                }

                // Detect activation method
                string dliAll = await RunCommandAsync("cscript", "//nologo C:\\Windows\\System32\\slmgr.vbs /dlv");
                if (dliAll.Contains("VOLUME_KMSCLIENT", StringComparison.OrdinalIgnoreCase))
                    status.ActivationMethod = "KMS";
                else if (dliAll.Contains("RETAIL", StringComparison.OrdinalIgnoreCase))
                    status.ActivationMethod = "Retail";
                else if (dliAll.Contains("OEM", StringComparison.OrdinalIgnoreCase))
                    status.ActivationMethod = "OEM";
                else
                    status.ActivationMethod = "Digital (HWID)";
            }
            catch (Exception ex)
            {
                Logger.Log($"[ActivationManager] Erro: {ex.Message}");
                status.LicenseStatus = "Erro";
            }

            return status;
        }

        /// <summary>
        /// Verifica se há conexão com a internet.
        /// </summary>
        public static bool IsInternetConnected()
        {
            try { return InternetGetConnectedState(out _, 0); }
            catch { return false; }
        }

        /// <summary>
        /// Executa um comando e retorna a saída.
        /// </summary>
        private static async Task<string> RunCommandAsync(string fileName, string arguments)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    using var process = Process.Start(psi);
                    if (process == null) return "";

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000); // max 10s
                    return output;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ActivationManager] RunCommand Error: {ex.Message}");
                    return "";
                }
            });
        }
    }
}
