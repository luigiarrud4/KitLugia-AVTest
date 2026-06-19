using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess; // Necessário para ServiceController
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        // =========================================================
        // 1. REPARO DE COMPONENTES DO SISTEMA (UPDATE, SFC, DISM)
        // =========================================================

        /// <summary>
        /// Reseta os componentes do Windows Update para corrigir problemas de atualização (Erro 0x800...).
        /// </summary>
        public static (bool Success, List<string> Log) ResetWindowsUpdateComponents()
        {
            // ðŸ”¥ OTIMIZAÃ‡ÃƒO .NET 10: Capacidade pré-definida para List
            // Típico: 10-20 entradas de log
            var log = new List<string>(20);
            bool overallSuccess = true;

            string[] services = { "wuauserv", "cryptSvc", "bits", "msiserver" };

            log.Add("Parando serviços do Windows Update...");
            foreach (var serviceName in services)
            {
                var result = ManageService(serviceName, "stop");
                log.Add(result.Message);
                if (!result.Success) overallSuccess = false;
            }

            log.Add("Renomeando pastas de cache...");
            try
            {
                string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string systemdir = Environment.GetFolderPath(Environment.SpecialFolder.System);

                string sd = Path.Combine(windir, "SoftwareDistribution");
                string oldSd = sd + ".old";
                if (Directory.Exists(oldSd)) try { Directory.Delete(oldSd, true); } catch { }
                if (Directory.Exists(sd)) try { Directory.Move(sd, oldSd); } catch { }
                log.Add("  - 'SoftwareDistribution' limpa.");

                string cr = Path.Combine(systemdir, "catroot2");
                string oldCr = cr + ".old";
                if (Directory.Exists(oldCr)) try { Directory.Delete(oldCr, true); } catch { }
                if (Directory.Exists(cr)) try { Directory.Move(cr, oldCr); } catch { }
                log.Add("  - 'Catroot2' limpa.");
            }
            catch (Exception ex)
            {
                log.Add($"ERRO ao limpar cache: {ex.Message}");
                overallSuccess = false;
            }

            log.Add("Reiniciando serviços...");
            foreach (var serviceName in services.Reverse())
            {
                var result = ManageService(serviceName, "start");
                log.Add(result.Message);
            }

            return (overallSuccess, log);
        }

        /// <summary>
        /// Helper interno para controlar serviços.
        /// </summary>
        internal static (bool Success, string Message) ManageService(string serviceName, string action)
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (action == "stop" && service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                else if (action == "start" && service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                return (true, $"  - Serviço '{serviceName}': {action} OK.");
            }
            catch (Exception ex)
            {
                return (false, $"  - Serviço '{serviceName}': FALHA ({ex.Message})");
            }
        }

        public static void RepairSystemComponentsSFC()
        {
            SystemUtils.RunExternalProcess("cmd.exe", "/c sfc /scannow & pause", hidden: false, waitForExit: false);
        }

        public static void RepairSystemComponentsDISM()
        {
            SystemUtils.RunExternalProcess("cmd.exe", "/c DISM /Online /Cleanup-Image /RestoreHealth & pause", hidden: false, waitForExit: false);
        }

        // =========================================================
        // 2. DIAGNÃ“STICO DE DRIVERS (DRIVER VERIFIER - RESTAURADO)
        // =========================================================

        /// <summary>
        /// Verifica se o Driver Verifier está ativo no momento.
        /// </summary>
        public static (bool IsActive, string StatusMessage) GetDriverVerifierStatus()
        {
            try
            {
                string output = SystemUtils.RunExternalProcess("verifier", "/querysettings", hidden: true);
                if (string.IsNullOrWhiteSpace(output) || output.Contains("No active settings") || output.Contains("Nenhuma configuração ativa"))
                {
                    return (false, "INATIVO");
                }
                return (true, "ATIVO (Em execução)");
            }
            catch
            {
                return (false, "Erro ao ler status");
            }
        }

        /// <summary>
        /// Ativa o Verifier para forçar estresse em todos os drivers. Causa BSOD se houver driver ruim.
        /// Requer reinicialização.
        /// </summary>
        public static (bool Success, string Message) EnableDriverVerifier()
        {
            try
            {
                // /standard = flags padrão
                // /all = monitorar todos os drivers do sistema
                SystemUtils.RunExternalProcess("verifier", "/standard /all", hidden: true);
                return (true, "Driver Verifier ativado.\nREINICIE O PC para iniciar o teste de estresse.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao ativar: {ex.Message}");
            }
        }

        /// <summary>
        /// Desativa o Driver Verifier e remove todas as configurações. Use para sair de boot loops (em Modo de Segurança).
        /// </summary>
        public static (bool Success, string Message) ResetDriverVerifier()
        {
            try
            {
                SystemUtils.RunExternalProcess("verifier", "/reset", hidden: true);
                return (true, "Driver Verifier desativado e configurações resetadas.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao resetar: {ex.Message}");
            }
        }

        // =========================================================
        // 3. REPARO DE DISCO E REDE
        // =========================================================

        /// <summary>
        /// Agenda uma verificação de disco (CHKDSK) na próxima reinicialização.
        /// </summary>
        public static (bool Success, string Message) ScheduleDiskCheck(string driveLetter = "C:")
        {
            try
            {
                // Envia "y" (sim) para o pipe do comando para confirmar o agendamento
                string args = $"/c echo y | chkdsk {driveLetter} /f /r";
                SystemUtils.RunExternalProcess("cmd.exe", args, hidden: true);
                return (true, $"Verificação de disco (CHKDSK) agendada para {driveLetter} na próxima reinicialização.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao agendar CHKDSK: {ex.Message}");
            }
        }

        public static (bool Success, string Message) ResetNetworkStack()
        {
            try
            {
                SystemUtils.RunExternalProcess("netsh", "winsock reset", hidden: true);
                SystemUtils.RunExternalProcess("netsh", "int ip reset", hidden: true);
                return (true, "Pilha de rede (Winsock/TCP) resetada. Reinicie o PC.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro: {ex.Message}");
            }
        }

        // =========================================================
        // 4. APPS PADRÃƒO
        // =========================================================

        public static void ReinstallDefaultApps()
        {
            const string command = "Get-AppxPackage -AllUsers | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\"}";
            SystemUtils.RunExternalProcess("cmd.exe", $"/c powershell -ExecutionPolicy Bypass -Command \"{command}\" & pause", hidden: false, waitForExit: false);
        }
    }
}