using System.Collections.Generic;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Obtém a lista de aplicativos de bloatware conhecidos e verifica se estão instalados no sistema.
        /// </summary>
        /// <returns>Uma lista de objetos 'BloatwareApp' com o status de cada um.</returns>
        public static List<BloatwareApp> GetKnownBloatwareApps()
        {
            // A lógica de busca e verificação é delegada para o SystemTweaks, mantendo este arquivo limpo.
            return SystemTweaks.GetBloatwareAppsStatus();
        }

        /// <summary>
        /// Remove um aplicativo de bloatware específico usando seu nome de pacote.
        /// </summary>
        /// <param name="packageName">O nome do pacote do aplicativo a ser removido (ex: "*Microsoft.XboxGamingOverlay*").</param>
        public static void RemoveKnownBloatwareApp(string packageName)
        {
            // Delega a remoção via PowerShell para o SystemTweaks.
            SystemTweaks.RemoveBloatwareApp(packageName);
        }

        /// <summary>
        /// Abre a Microsoft Store na página de um aplicativo para permitir a reinstalação.
        /// Esta função foi adicionada para completar o ciclo de gerenciamento de bloatware.
        /// </summary>
        /// <param name="storeId">O ID do produto na Microsoft Store (ex: "9NZKPSTSNW4P").</param>
        public static void ReinstallKnownBloatwareApp(string storeId)
        {
            // A lógica para abrir a loja é delegada ao SystemTweaks.
            SystemTweaks.ReinstallBloatwareApp(storeId);
        }
    }
}