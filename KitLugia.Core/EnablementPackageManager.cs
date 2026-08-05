using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace KitLugia.Core
{
    /// <summary>
    /// Gerencia aplicacao de Enablement Packages (EKB) no sistema online,
    /// replicando o fluxo do W10UI (opcao 0): localizar o .cab, verificar
    /// se ja esta instalado e aplicar via DISM /online /Add-Package.
    /// </summary>
    public static class EnablementPackageManager
    {
        public static bool IsElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        private static void EnsureElevated()
        {
            if (!IsElevated())
                throw new UnauthorizedAccessException("Esta operacao requer privilegios de administrador.");
        }

        public static int GetCurrentBuild()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuildNumber") is string buildStr && int.TryParse(buildStr, out var bld))
                return bld;
            return 0;
        }

        /// <summary>
        /// Procura um Enablement Package (.cab KB) em locais comuns:
        /// diretorio do app, diretorio atual, Desktop, Downloads.
        /// </summary>
        public static string? FindEnablementCab()
        {
            var candidates = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads"
            };

            foreach (var dir in candidates.Distinct())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var cab = Directory.GetFiles(dir, "*.cab")
                        .FirstOrDefault(f =>
                        {
                            var name = Path.GetFileName(f);
                            return name.StartsWith("Windows1", StringComparison.OrdinalIgnoreCase) &&
                                   name.Contains("-KB", StringComparison.OrdinalIgnoreCase);
                        });
                    if (cab != null) return cab;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Verifica se o pacote (por KB number) ja esta instalado no sistema.
        /// </summary>
        public static bool IsPackageInstalled(string kbNumber)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages");
                if (key == null) return false;
                return key.GetSubKeyNames().Any(n =>
                    n.Contains($"Package_for_{kbNumber}", StringComparison.OrdinalIgnoreCase) ||
                    n.Contains($"~{kbNumber}~", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public static string ExtractKbNumber(string cabPath)
        {
            var name = Path.GetFileNameWithoutExtension(cabPath);
            var idx = name.IndexOf("-KB", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            var rest = name.Substring(idx + 1);
            var parts = rest.Split('-', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : "";
        }

        /// <summary>
        /// Aplica um Enablement Package (.cab) no sistema online via DISM.
        /// Retorna (sucesso, mensagem de log).
        /// </summary>
        public static async Task<(bool success, string log)> ApplyEnablementPackageAsync(string cabPath)
        {
            EnsureElevated();

            var sb = new StringBuilder();
            try
            {
                if (string.IsNullOrEmpty(cabPath) || !File.Exists(cabPath))
                    return (false, "Arquivo .cab nao encontrado.");

                var fileName = Path.GetFileName(cabPath);
                var kb = ExtractKbNumber(cabPath);
                sb.AppendLine($"Pacote: {fileName}");
                sb.AppendLine($"Build atual: {GetCurrentBuild()}");
                if (!string.IsNullOrEmpty(kb))
                    sb.AppendLine($"KB detectado: {kb}");

                if (IsPackageInstalled(kb))
                {
                    sb.AppendLine("Status: pacote ja esta instalado. Nada a fazer.");
                    return (true, sb.ToString());
                }

                sb.AppendLine("Aplicando via DISM (Add-Package)...");

                var psi = new ProcessStartInfo("dism.exe",
                    $"/English /Online /NoRestart /Add-Package /PackagePath:\"{cabPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return (false, "Falha ao iniciar DISM.");

                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(600000))
                {
                    try { proc.Kill(); } catch { }
                    return (false, "DISM excedeu o tempo limite (10 min).");
                }
                var output = await outputTask;
                var error = await errorTask;

                var code = proc.ExitCode;
                sb.AppendLine($"Codigo de saida: {code}");

                var relevantLines = (output + "\n" + error)
                    .Split('\n')
                    .Where(l => l.Trim().Length > 0)
                    .Take(40);
                foreach (var line in relevantLines)
                    sb.AppendLine("  " + line.Trim());

                if (code != 0)
                    return (false, sb.ToString());

                sb.AppendLine("Pacote aplicado com sucesso. Reinicie o computador para ativar os recursos.");
                return (true, sb.ToString());
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Erro: {ex.Message}");
                return (false, sb.ToString());
            }
        }
    }
}
