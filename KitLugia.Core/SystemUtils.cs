using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management; // Necessário adicionar referência ao System.Management
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class SystemUtils
    {
        #region Informações do Sistema

        /// <summary>
        /// Verifica se o aplicativo está rodando como administrador.
        /// </summary>
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static string? GetServiceStartMode(string serviceName)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2", new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
                using var s = new ManagementObject(scope, new ManagementPath($"Win32_Service.Name='{serviceName.Replace("'", "''")}'"), null);
                s.Get();
                return s["StartMode"]?.ToString();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return null; }
        }

        public static double GetTotalSystemRamGB()
        {
            // GlobalMemoryStatusEx primeiro: 1 syscall, sem servico WMI (as consultas Win32_OperatingSystem
            // custam 100-300 ms e este metodo e chamado em varios fluxos de UI).
            try
            {
                var memStatus = default(NativeMethods.MEMORYSTATUSEX);
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
                if (NativeMethods.GlobalMemoryStatusEx(ref memStatus) && memStatus.ullTotalPhys > 0)
                    return memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // Fallback: WMI (caso o P/Invoke falhe)
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
                using var results = searcher.Get();
                var mem = results.Cast<ManagementObject>().FirstOrDefault()?["TotalVisibleMemorySize"];
                if (mem != null)
                {
                    ulong totalRamKB = Convert.ToUInt64(mem);
                    return totalRamKB / 1048576.0;
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return 0;
        }

        /// <summary>
        /// Obtém o tempo que o sistema está ligado (uptime).
        /// </summary>
        public static TimeSpan GetSystemUptime()
        {
            return TimeSpan.FromMilliseconds(Environment.TickCount64);
        }

        #endregion

        #region Execução de Processos

        /// <summary>
        /// Encoding OEM do sistema (cp850 pt-BR / cp437 en-US / cp65001 UTF-8).
        /// Ferramentas nativas (sc.exe, bcdedit, powercfg) emitem texto OEM; ler como
        /// UTF-8 fixo gerava mojibake no log (ex: "[SC] ChangeServiceConfig �XITO").
        /// </summary>
        public static Encoding GetOemEncoding()
        {
            try
            {
                int cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                if (cp > 0) return Encoding.GetEncoding(cp);
            }
            catch { /* code page não instalada - usa fallback */ }

            try { return Encoding.GetEncoding(850); } catch { /* fallback final */ }
            return Encoding.UTF8;
        }

        public static async Task<string> RunExternalProcessAsync(string fileName, string arguments, bool hidden = false, bool waitForExit = true, bool runAs = false)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };

            // Apenas usar Verb = "runas" quando explicitamente solicitado
            if (runAs)
            {
                psi.Verb = "runas";
            }

            if (waitForExit)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                var oem = GetOemEncoding();
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            else
            {
                psi.UseShellExecute = true;
            }

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return string.Empty;
                if (waitForExit)
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    
                    var exitTask = process.WaitForExitAsync();
                    if (await Task.WhenAny(exitTask, Task.Delay(120000)).ConfigureAwait(false) != exitTask)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                        return "[TIMEOUT] Processo excedeu 120 segundos.";
                    }
                    
                    string output = await outputTask.ConfigureAwait(false);
                    string error = await errorTask.ConfigureAwait(false);
                    
                    if (!string.IsNullOrEmpty(error))
                    {
                        return string.IsNullOrEmpty(output) ? error : $"{output}\n{error}";
                    }
                    return output;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Processo cancelado pelo usuário.";
            }
            catch (Exception ex)
            {
                return $"Erro ao executar processo: {ex.Message}";
            }
            return string.Empty;
        }

        // --- API SÍNCRONA (bloqueante por design) ---
        // Mantem a assinatura antiga usada por centenas de chamadas. Executa o processo
        // de verdade de forma sincrona (threads dedicadas por stream evitam deadlock de
        // pipe) - sem Task/GetAwaiter, eliminando o sync-over-async.
        public static string RunExternalProcess(string fileName, string arguments, bool hidden = false, bool waitForExit = true, bool runAs = false)
        {
            ProcessStartInfo psi = new(fileName, arguments)
            {
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };

            if (runAs) psi.Verb = "runas";

            if (waitForExit)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                var oem = GetOemEncoding();
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            else
            {
                psi.UseShellExecute = true;
            }

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return string.Empty;
                if (waitForExit)
                {
                    var (exitCode, output) = ReadProcessOutputSync(process, 120000);
                    if (exitCode < 0) return "[TIMEOUT] Processo excedeu 120 segundos.";
                    return output;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "Processo cancelado pelo usuario.";
            }
            catch (Exception ex)
            {
                return $"Erro ao executar processo: {ex.Message}";
            }
            return string.Empty;
        }

        /// <summary>
        /// Le stdout+stderr de um processo de forma sincrona, com uma thread dedicada por
        /// stream (evita deadlock de pipe quando o filho enche um buffer) e timeout opcional.
        /// ExitCode -1 = timeout. Sem Task/GetAwaiter em nenhum ponto.
        /// </summary>
        private static (int ExitCode, string Output) ReadProcessOutputSync(Process process, int timeoutMs)
        {
            string output = string.Empty, error = string.Empty;
            var readOut = new System.Threading.Thread(() => { try { output = process.StandardOutput.ReadToEnd(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } }) { IsBackground = true };
            var readErr = new System.Threading.Thread(() => { try { error = process.StandardError.ReadToEnd(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } }) { IsBackground = true };
            readOut.Start();
            readErr.Start();

            bool exited;
            if (timeoutMs > 0) exited = process.WaitForExit(timeoutMs);
            else { process.WaitForExit(); exited = true; }

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                readOut.Join(5000);
                readErr.Join(5000);
                return (-1, string.Empty);
            }

            readOut.Join(10000);
            readErr.Join(10000);

            if (!string.IsNullOrEmpty(error))
                return (process.ExitCode, string.IsNullOrEmpty(output) ? error : $"{output}\n{error}");
            return (process.ExitCode, output);
        }

        /// <summary>
        /// Executa um processo e retorna (ExitCode, Saída) - útil para sc.exe/bcdedit onde
        /// o exit code é a fonte de verdade e o texto é OEM (encoding correto aplicado).
        /// </summary>
        public static async Task<(int ExitCode, string Output)> RunExternalProcessWithCodeAsync(string fileName, string arguments, bool hidden = false)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var oem = GetOemEncoding();
            psi.StandardOutputEncoding = oem;
            psi.StandardErrorEncoding = oem;

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return (1, "Falha ao iniciar o processo.");

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                var exitTask = process.WaitForExitAsync();
                if (await Task.WhenAny(exitTask, Task.Delay(120000)).ConfigureAwait(false) != exitTask)
                {
                    try { process.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    return (1, "[TIMEOUT] Processo excedeu 120 segundos.");
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                return (process.ExitCode, string.IsNullOrEmpty(error) ? output : $"{output}\n{error}");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (1, "Processo cancelado pelo usuário.");
            }
            catch (Exception ex)
            {
                return (1, $"Erro ao executar processo: {ex.Message}");
            }
        }

        public static (int ExitCode, string Output) RunExternalProcessWithCode(string fileName, string arguments, bool hidden = false)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = hidden,
                WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var oem = GetOemEncoding();
            psi.StandardOutputEncoding = oem;
            psi.StandardErrorEncoding = oem;

            try
            {
                using var process = Process.Start(psi);
                if (process == null) return (1, "Falha ao iniciar o processo.");
                var (exitCode, output) = ReadProcessOutputSync(process, 120000);
                if (exitCode < 0) return (1, "[TIMEOUT] Processo excedeu 120 segundos.");
                return (exitCode, output);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (1, "Processo cancelado pelo usuario.");
            }
            catch (Exception ex)
            {
                return (1, $"Erro ao executar processo: {ex.Message}");
            }
        }

        public static string? FindWingetPath()
        {
            // 1) Cache do Kit
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\Paths");
                if (key?.GetValue("Winget") is string saved && !string.IsNullOrWhiteSpace(saved) && File.Exists(saved)) return saved;
            }
            catch { }
            // 2) LocalAppData WindowsApps (local user)
            string winApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(winApps)) return winApps;
            // 3) where winget
            try
            {
                var wh = KitStore.StoreEngine.RunCapture("where", "winget", 3000);
                var first = wh.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim().Trim('"'))
                              .FirstOrDefault(s => s.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s));
                if (!string.IsNullOrEmpty(first)) return first;
            }
            catch { }
            // 4) Program Files WindowsApps (Store)
            try
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var wa = Path.Combine(pf, "WindowsApps");
                if (Directory.Exists(wa))
                {
                    var cand = Directory.GetDirectories(wa, "Microsoft.DesktopAppInstaller_*")
                                        .Select(d => Path.Combine(d, "winget.exe"))
                                        .FirstOrDefault(File.Exists);
                    if (cand != null) return cand;
                }
            }
            catch { }
            return null;
        }

        #endregion

        #region Utilitários de Restauração e Sistema

        public static (bool Success, string Message) CreateRestorePoint()
        {
            // Cria um ponto de restauração via PowerShell
            string cmd = "try { Checkpoint-Computer -Description 'KitLUGIA_RestorePoint' -RestorePointType 'MODIFY_SETTINGS' } catch { Write-Host $_.Exception.Message }";
            string result = RunExternalProcess("powershell", $"-ExecutionPolicy Bypass -Command \"{cmd}\"", hidden: true);

            if (string.IsNullOrWhiteSpace(result) || !result.Contains("Exception"))
            {
                return (true, "Ponto de restauração criado com sucesso.");
            }
            else
            {
                return (false, $"Falha ao criar ponto de restauração: {result.Trim()}");
            }
        }

        public static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool CommandExists(string command)
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            return pathDirs.Any(dir => File.Exists(Path.Combine(dir.Trim(), command)));
        }

        #region Registro

        public static void SetRegistryValue(RegistryKey hive, string subKey, string valueName, object value, RegistryValueKind kind)
        {
            using var key = hive.CreateSubKey(subKey, true);
            key.SetValue(valueName, value, kind);
        }

        public static void DeleteRegistryKey(RegistryKey hive, string subKey)
        {
            hive.DeleteSubKeyTree(subKey, false);
        }

        #endregion

        #endregion
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }
}