using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    public static class SystemRepair
    {
        public static async Task<(bool Success, string Output)> RunDismRestoreHealthAsync(CancellationToken ct = default)
        {
            return await RunRepairToolAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", ct);
        }

        public static async Task<(bool Success, string Output)> RunDismStartComponentCleanupAsync(CancellationToken ct = default)
        {
            return await RunRepairToolAsync("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup", ct);
        }

        public static async Task<(bool Success, string Output)> RunSfcScanNowAsync(CancellationToken ct = default)
        {
            return await RunRepairToolAsync("sfc.exe", "/scannow", ct);
        }

        private static async Task<(bool Success, string Output)> RunRepairToolAsync(string fileName, string arguments, CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    Verb = "runas"
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return (false, "Falha ao iniciar o processo.");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(3));

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cts.Token);

                var fullOutput = (output + error).Trim();
                return (process.ExitCode == 0, string.IsNullOrEmpty(fullOutput) ? "Concluído." : fullOutput);
            }
            catch (OperationCanceledException)
            {
                return (false, "Operação cancelada ou excedeu o tempo limite de 3 minutos.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }
    }
}
