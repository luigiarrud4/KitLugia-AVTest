using System;
using System.ServiceProcess;

namespace KitLugia.Core
{
    /// <summary>
    /// Helper para serviços do Windows sem dependência de WMI.
    /// Usa System.ServiceProcess (mais leve e estável que WMI).
    /// </summary>
    public static class ServiceHelper
    {
        /// <summary>
        /// Obtém o modo de inicialização de um serviço usando ServiceController.
        /// Retorna: "Auto", "Manual", "Disabled", "Delayed-Auto" ou null se não encontrado/inacessível.
        /// </summary>
        public static string? GetServiceStartMode(string serviceName)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    // ServiceController.StartType pode lançar exceção se não tiver acesso
                    var startType = sc.StartType;
                    return startType switch
                    {
                        ServiceStartMode.Automatic => "Auto",
                        ServiceStartMode.Manual => "Manual",
                        ServiceStartMode.Disabled => "Disabled",
                        ServiceStartMode.Boot => "Boot",
                        ServiceStartMode.System => "System",
                        _ => startType.ToString()
                    };
                }
            }
            catch (InvalidOperationException)
            {
                // Serviço não encontrado (não existe no sistema)
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Acesso negado ou serviço não pode ser acessado
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                // Não está no Windows (segurança)
                return null;
            }
            catch (Exception ex)
            {
                // Log apenas em caso de erro inesperado, não para serviços que não existem
                if (!ex.Message.Contains("não existe", StringComparison.OrdinalIgnoreCase) &&
                    !ex.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"[SERVICEHELPER] Erro em '{serviceName}': {ex.GetType().Name}: {ex.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Verifica se um serviço está rodando.
        /// </summary>
        public static bool IsServiceRunning(string serviceName)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tenta iniciar um serviço.
        /// </summary>
        public static bool TryStartService(string serviceName, int timeoutMs = 10000)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(timeoutMs));
                        return sc.Status == ServiceControllerStatus.Running;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tenta parar um serviço.
        /// </summary>
        public static bool TryStopService(string serviceName, int timeoutMs = 10000)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    if (sc.Status != ServiceControllerStatus.Stopped)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(timeoutMs));
                        return sc.Status == ServiceControllerStatus.Stopped;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}