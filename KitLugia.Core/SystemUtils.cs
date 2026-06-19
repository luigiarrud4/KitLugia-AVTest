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
            catch
            {
                return false;
            }
        }

        public static string? GetServiceStartMode(string serviceName)
        {
            try
            {
                using var s = new ManagementObject($"Win32_Service.Name='{serviceName}'");
                s.Get();
                return s["StartMode"]?.ToString();
            }
            catch { return null; }
        }

        public static double GetTotalSystemRamGB()
        {
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
            catch { }

            try
            {
                var memStatus = default(NativeMethods.MEMORYSTATUSEX);
                memStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>();
                if (NativeMethods.GlobalMemoryStatusEx(ref memStatus))
                    return memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
            }
            catch { }

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
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
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
                    var outputTask = process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    var errorTask = process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    
                    string output = await outputTask;
                    string error = await errorTask;
                    
                    // Combinar stdout e stderr para capturar todos os resultados
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

        // --- RETROCOMPATIBILIDADE SÍNCRONA ---
        // Mantém a assinatura antiga para não quebrar centenas de chamadas no projeto 
        // e redireciona para a versão async de forma segura (GetAwaiter().GetResult()).
        public static string RunExternalProcess(string fileName, string arguments, bool hidden = false, bool waitForExit = true, bool runAs = false)
        {
            return RunExternalProcessAsync(fileName, arguments, hidden, waitForExit, runAs).GetAwaiter().GetResult();
        }

        public static string? FindWingetPath()
        {
            string winApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(winApps)) return winApps;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\Paths");
                if (key?.GetValue("Winget") is string saved) return saved;
            }
            catch { }

            return null;
        }

        #endregion

        #region Utilitários de Restauração e Sistema

        // Modelo de dados para a lista de backups
        public record RestorePointModel(int SequenceNumber, string Description, string Date);

        /// <summary>
        /// Obtém a lista de pontos de restauração do sistema via WMI.
        /// </summary>
        public static List<RestorePointModel> GetRestorePoints()
        {

            // Típico: 5-20 pontos de restauração
            var points = new List<RestorePointModel>(20);
            ManagementScope? scope = null;
            try
            {
                // Conecta ao WMI na raiz padrão
                scope = new ManagementScope("\\\\localhost\\root\\default");
                ObjectQuery query = new ObjectQuery("SELECT * FROM SystemRestore");
                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, query);
                using ManagementObjectCollection results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    string desc = obj["Description"]?.ToString() ?? "Ponto Automático";
                    uint seq = (uint)(obj["SequenceNumber"] ?? 0);

                    // A data vem em formato WMI (ex: 20230501120000.000000+000)
                    string rawDate = obj["CreationTime"]?.ToString() ?? "";
                    string prettyDate = rawDate;

                    // Formata para algo legível (DD/MM/AAAA HH:MM)
                    if (rawDate.Length >= 14)
                    {
                        prettyDate = $"{rawDate.Substring(6, 2)}/{rawDate.Substring(4, 2)}/{rawDate.Substring(0, 4)} {rawDate.Substring(8, 2)}:{rawDate.Substring(10, 2)}";
                    }

                    points.Add(new RestorePointModel((int)seq, desc, prettyDate));
                }
            }
            catch
            {
                // Ignora falhas (ex: Restauração desativada no Windows)
            }

            // Retorna ordenado do mais recente para o mais antigo
            return points.OrderByDescending(x => x.Date).ToList();
        }

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

        public static void OpenSystemRestoreWizard()
        {
            // Abre o assistente nativo do Windows (rstrui.exe) para restaurar o sistema
            RunExternalProcess("rstrui.exe", "", hidden: false, waitForExit: false);
        }

        public static bool IsAdmin()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static List<string> RunPreflightCheck()
        {
            var errors = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                if (!searcher.Get().Cast<ManagementObject>().Any()) errors.Add("- WMI não está retornando dados.");
            }
            catch { errors.Add("- Falha crítica ao acessar o WMI."); }

            try
            {
                const string testKey = @"Software\KitLUGIA_Test";
                Registry.CurrentUser.CreateSubKey(testKey)?.Close();
                Registry.CurrentUser.DeleteSubKey(testKey);
            }
            catch { errors.Add("- Falha crítica de acesso ao Registro."); }

            string[] requiredTools = { "sc.exe", "ipconfig.exe", "bcdedit.exe", "powershell.exe", "sfc.exe", "dism.exe", "powercfg.exe", "compact.exe" };
            foreach (var tool in requiredTools)
            {
                if (!CommandExists(tool)) errors.Add($"- Ferramenta essencial '{tool}' não encontrada no PATH.");
            }
            return errors;
        }

        private static bool CommandExists(string command)
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            return pathDirs.Any(dir => File.Exists(Path.Combine(dir.Trim(), command)));
        }

        #region Registro

        public static object? GetRegistryValue(RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using var key = hive.OpenSubKey(subKey, false);
                if (key == null) return null;
                return key.GetValue(valueName);
            }
            catch
            {
                return null;
            }
        }

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