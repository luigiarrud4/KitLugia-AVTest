using System;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Obtém o status do modo MSI (Message Signaled Interrupts) da GPU principal.
        /// </summary>
        /// <returns>Uma tupla contendo (bool isEnabled, string gpuName).</returns>
        public static (bool isEnabled, string gpuName) GetMsiStatus()
        {

            var gpuNames = SystemTweaks.GetAllGpuNames();
            var gpuName = gpuNames.FirstOrDefault(n => !n.Contains("Microsoft Basic"));

            // Se nenhuma GPU for encontrada, retorna um status seguro.
            if (string.IsNullOrEmpty(gpuName))
            {
                return (false, "Nenhuma GPU detectada");
            }

            // Obtém PNPDeviceID de forma segura via registry
            string? regPath = SystemTweaks.FindGpuRegistryPathByDescription(gpuName);
            string pnpDeviceId = string.Empty;
            if (!string.IsNullOrEmpty(regPath))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath.Replace("HKEY_LOCAL_MACHINE\\", ""));
                    pnpDeviceId = key?.GetValue("MatchingDeviceId")?.ToString() ?? string.Empty;
                }
                catch { }
            }

            // Se não conseguirmos o ID do dispositivo, não podemos prosseguir.
            if (string.IsNullOrEmpty(pnpDeviceId))
            {
                return (false, gpuName);
            }

            return (SystemTweaks.IsGpuMsiEnabled(pnpDeviceId), gpuName);
        }

        /// <summary>
        /// Alterna o modo MSI para um dispositivo específico usando seu ID PNP.
        /// </summary>
        /// <param name="pnpDeviceId">O ID Plug and Play do dispositivo (geralmente a GPU).</param>
        /// <returns>Uma tupla com o resultado da operação.</returns>
        public static (bool Success, string Message) ToggleMsi(string pnpDeviceId)
        {
            // A lógica complexa de alterar o registro fica encapsulada no SystemTweaks.
            return SystemTweaks.ToggleGpuMsiMode(pnpDeviceId);
        }

        /// <summary>
        /// Verifica se a Segurança Baseada em Virtualização (VBS) está ativa.
        /// </summary>
        /// <returns>Verdadeiro se o VBS estiver habilitado.</returns>
        public static bool GetVbsStatus()
        {
            return SystemTweaks.IsVbsEnabled();
        }

        /// <summary>
        /// Alterna o estado da Segurança Baseada em Virtualização (VBS).
        /// </summary>
        /// <returns>Uma tupla com o resultado da operação.</returns>
        public static (bool Success, string Message) ToggleVbs()
        {
            // A lógica de ligar/desligar o VBS fica no SystemTweaks.
            return SystemTweaks.ToggleVbs();
        }
    }
}