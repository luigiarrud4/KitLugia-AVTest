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
        /// Inicia o serviço e devolve mensagem amigável em português (usado pela ServicesPage).
        /// </summary>
        public static (bool Success, string Message) TryStartServiceWithMessage(string serviceName, int timeoutMs = 15000)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running)
                    return (true, $"'{serviceName}' já está em execução.");

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(timeoutMs));
                return (true, $"'{serviceName}' iniciado com sucesso.");
            }
            catch (System.ComponentModel.Win32Exception w32)
            {
                // 5 = Access Denied
                if (w32.NativeErrorCode == 5)
                    return (false, $"Acesso negado ao iniciar '{serviceName}'. Execute o Kit como Administrador.");
                return (false, $"Erro do Windows ao iniciar '{serviceName}': {w32.Message}");
            }
            catch (InvalidOperationException)
            {
                return (false, $"Serviço '{serviceName}' não encontrado ou desativado (há serviços desativados que não podem ser iniciados).");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return (false, $"'{serviceName}' não respondeu em {timeoutMs / 1000}s (pode ainda ter iniciado — recarregue a lista).");
            }
            catch (Exception ex)
            {
                Logger.LogError("TryStartServiceWithMessage", $"{serviceName}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Para o serviço e devolve mensagem amigável em português (usado pela ServicesPage).
        /// </summary>
        public static (bool Success, string Message) TryStopServiceWithMessage(string serviceName, int timeoutMs = 15000)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                    return (true, $"'{serviceName}' já está parado.");

                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMilliseconds(timeoutMs));
                return (true, $"'{serviceName}' parado com sucesso.");
            }
            catch (System.ComponentModel.Win32Exception w32)
            {
                if (w32.NativeErrorCode == 5)
                    return (false, $"Acesso negado ao parar '{serviceName}'. Execute o Kit como Administrador.");
                return (false, $"Erro do Windows ao parar '{serviceName}': {w32.Message}");
            }
            catch (InvalidOperationException)
            {
                return (false, $"'{serviceName}' não pode ser parado (serviço inexistente, desativado ou com dependências em execução).");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return (false, $"'{serviceName}' não respondeu em {timeoutMs / 1000}s (pode ainda estar parando — recarregue a lista).");
            }
            catch (Exception ex)
            {
                Logger.LogError("TryStopServiceWithMessage", $"{serviceName}: {ex.Message}");
                return (false, ex.Message);
            }
        }
    }
}