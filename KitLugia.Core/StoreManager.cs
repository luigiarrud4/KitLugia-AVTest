using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Limpa o cache da Microsoft Store executando o utilitário 'wsreset.exe'.
        /// </summary>
        /// <returns>Uma tupla com o status da operação e uma mensagem.</returns>
        public static (bool Success, string Message) ResetStoreCache()
        {
            try
            {
                // O 'wsreset.exe' é uma ferramenta nativa do Windows projetada para limpar o cache da Store.
                // Ele abre uma janela de console temporária e, ao concluir, geralmente abre a própria Store.
                // Não esperamos o processo terminar (waitForExit = false) para não bloquear a UI.
                SystemUtils.RunExternalProcess("wsreset.exe", "", hidden: false, waitForExit: false);
                return (true, "O processo de limpeza de cache da Microsoft Store (wsreset.exe) foi iniciado.");
            }
            catch (Exception ex)
            {
                return (false, $"ERRO ao iniciar o 'wsreset.exe': {ex.Message}");
            }
        }

        /// <summary>
        /// Tenta reparar os 'Serviços de Jogos' da Microsoft, que são essenciais para jogos do Game Pass.
        /// </summary>
        /// <returns>Uma tupla com o status da operação e uma mensagem.</returns>
        public static (bool Success, string Message) RepairGamingServices()
        {
            try
            {
                // Passo 1: Tenta desinstalar forçadamente qualquer versão corrompida dos Serviços de Jogos.
                SystemUtils.RunExternalProcess(
                    "powershell",
                    "-Command \"Get-AppxPackage *gamingservices* -AllUsers | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue\"",
                    hidden: true);

                // Passo 2: Abre a página da Microsoft Store diretamente no produto 'Serviços de Jogos'
                // para que o usuário possa reinstalar a versão mais recente e funcional.
                // O ID '9MWPM2CQNLHN' é o identificador oficial do produto na Store.
                Process.Start(new ProcessStartInfo("cmd", $"/c start ms-windows-store://pdp/?ProductId=9MWPM2CQNLHN") { CreateNoWindow = true });

                return (true, "A página da Microsoft Store para os Serviços de Jogos foi aberta. Por favor, clique em 'Instalar' ou 'Obter' para concluir o reparo.");
            }
            catch (Exception ex)
            {
                return (false, $"ERRO ao tentar reparar os Serviços de Jogos: {ex.Message}");
            }
        }

        // Nota: A função para reinstalar todos os aplicativos padrão (ReinstallDefaultApps),
        // que também afeta a Store, está corretamente localizada no DiagnosticsManager.cs.
    }
}