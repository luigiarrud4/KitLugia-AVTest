using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KitLugia.Core
{
    public class InstalledUpdate
    {
        public string? HotFixId { get; set; }
        public string? Description { get; set; }
        public string? InstalledOn { get; set; }
    }

    /// <summary>
    /// Controle de atualizacoes NAO-Insider: lista updates instalados (Get-HotFix),
    /// remove/desinstala um KB (wusa /uninstall — downgrade/rollback do patch) e
    /// instala pacotes .msu/.cab manualmente.
    /// </summary>
    public static class UpdateControlManager
    {
        public static List<InstalledUpdate> ListInstalledUpdates()
        {
            var list = new List<InstalledUpdate>();
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"Get-HotFix | Select-Object HotFixID,Description,@{N='InstalledOn';E={$_.InstalledOn.ToString('yyyy-MM-dd')}} | ConvertTo-Json -Compress\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var proc = Process.Start(psi);
                if (proc == null) return list;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(60000);

                if (string.IsNullOrWhiteSpace(output)) return list;
                using var doc = JsonDocument.Parse(output);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(new InstalledUpdate
                    {
                        HotFixId = el.TryGetProperty("HotFixID", out var id) ? id.GetString() : null,
                        Description = el.TryGetProperty("Description", out var d) ? d.GetString() : null,
                        InstalledOn = el.TryGetProperty("InstalledOn", out var dt) ? dt.GetString() : null
                    });
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Desinstala um KB instalado. ExitCode: 0 = sucesso, 3010 = sucesso (reinicio pendente),
        /// 2359302 = KB nao encontrado / desinstalacao nao suportada, 87 = argumento invalido.
        /// </summary>
        public static (int ExitCode, string Output) UninstallUpdate(string kbNumber)
        {
            EnsureElevated();
            var kb = kbNumber.Trim().ToUpperInvariant();
            if (!kb.StartsWith("KB", StringComparison.Ordinal)) kb = "KB" + kb;
            return RunProcess("wusa.exe", $"/uninstall /kb:{kb} /quiet /norestart", 10 * 60 * 1000);
        }

        /// <summary>
        /// Instala um pacote de update manual (.msu via wusa, .cab via DISM).
        /// ExitCode: 0 = sucesso, 3010 = sucesso (reinicio pendente).
        /// </summary>
        public static (int ExitCode, string Output) InstallUpdatePackage(string path)
        {
            EnsureElevated();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Arquivo de update nao encontrado.", path);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".msu")
                return RunProcess("wusa.exe", $"\"{path}\" /quiet /norestart", 10 * 60 * 1000);
            if (ext == ".cab")
                return RunProcess("dism.exe", $"/English /Online /NoRestart /Add-Package /PackagePath:\"{path}\"", 10 * 60 * 1000);
            throw new ArgumentException("Formato nao suportado. Use .msu ou .cab.");
        }

        public static string DescribeExitCode(int code)
        {
            switch (code)
            {
                case 0: return "Sucesso.";
                case 3010: return "Sucesso — reinicie o PC para concluir.";
                case 2359302: return "KB nao encontrado ou desinstalacao nao suportada (pode exigir a remocao da LCU anterior primeiro).";
                case 87: return "Parametro invalido.";
                case -1: return "Operacao excedeu o tempo limite.";
                // HRESULT 0x800F081F: source files not found (DISM)
                case unchecked((int)0x800F081F): return "Arquivos fonte nao encontrados. Verifique se o pacote e compativel com a versao do Windows.";
                // HRESULT 0x800F0954: DISM does not support servicing Windows PE
                case unchecked((int)0x800F0954): return "DISM nao suporta instalacao em Windows PE.";
                // HRESULT 0x800B0109: certificate chain not trusted (CRYPT_E_CHAIN_TRUST)
                case unchecked((int)0x800B0109): return "Falha na verificacao de assinatura digital do pacote. A cadeia de certificados nao e confiavel — o arquivo pode estar corrompido, alterado ou nao e um pacote Microsoft genuino.";
                // HRESULT 0x80070005: access denied
                case unchecked((int)0x80070005): return "Acesso negado. Execute o KitLugia como administrador.";
                // HRESULT 0x80070002: file not found
                case unchecked((int)0x80070002): return "Arquivo nao encontrado. Verifique se o caminho do pacote esta correto.";
                // HRESULT 0x80073712: component store corrupted
                case unchecked((int)0x80073712): return "O repositorio de componentes do Windows esta corrompido. Execute: DISM /Online /Cleanup-Image /RestoreHealth";
                // HRESULT 0x8024001E: WU_E_UNEXPECTED (Windows Update general failure)
                case unchecked((int)0x8024001E): return "Erro inesperado do Windows Update. Reinicie o PC e tente novamente.";
                // Win32 error 1168: element not found
                case 1168: return "Elemento nao encontrado. O pacote pode nao ser compativel com este sistema.";
                default:
                {
                    var hex = $"0x{((uint)code):X8}";
                    return $"Falha (codigo {code}, {hex}). Consulte C:\\Windows\\Logs\\CBS\\cbs.log.";
                }
            }
        }

        private static (int ExitCode, string Output) RunProcess(string file, string args, int timeoutMs)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(file, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (proc == null) return (-1, "Falha ao iniciar o processo.");
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return (-1, "Operacao excedeu o tempo limite.");
                }
                return (proc.ExitCode, (outTask.IsCompleted ? outTask.Result : "") + (errTask.IsCompleted ? errTask.Result : ""));
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        private static void EnsureElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Esta operacao requer privilegios de administrador.");
        }
    }
}
