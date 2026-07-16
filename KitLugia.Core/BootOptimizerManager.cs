using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class BootOptimizerManager
    {
        // =========================================================
        // ANÁLISE DE INICIALIZAÇÃO (BOOT)
        // =========================================================

        /// <summary>
        /// Realiza uma análise completa da última inicialização do sistema.
        /// </summary>
        /// <returns>Objeto contendo o tempo total e listas de apps problemáticos.</returns>
        public static BootAnalysisResult AnalyzeBootPerformance()
        {
            var result = new BootAnalysisResult();

            // 1. Verifica se o serviço de monitoramento do Windows está ativo
            if (SystemUtils.GetServiceStartMode("pla") == "Disabled")
            {
                result.ServiceStatusMessage = "AVISO: O serviço 'Logs e Alertas de Desempenho' (pla) está desativado. O Windows não registrou detalhes do boot.";
                result.HasWarning = true;
            }

            // 2. Busca os eventos brutos (ID 100 = Boot Total, 101-199 = Degradação por Apps)
            var allEvents = SystemTweaks.GetPerformanceEvents(100, 101, 199);

            // [NB] allEvents pode vir vazio se PLA estiver desligado — tratamos isso na UI

            // Pega os apps de inicialização atuais para cruzar dados
            var startupApps = StartupManager.GetStartupAppsWithDetails(true);

            // Extrai nomes de processo (sem caminho, sem extensão) para matching robusto
            var startupAppNames = startupApps
                .Select(s =>
                {
                    try { return System.IO.Path.GetFileNameWithoutExtension(s.FullCommand?.Trim('"') ?? s.Name); }
                    catch { return s.Name; }
                })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Define o evento principal (Tempo Total)
            result.TotalTimeEvent = allEvents.FirstOrDefault(e => e.EventId == 100);

            // 3. Filtra itens lentos (> 1 segundo) do último mês
            var recentSlowItems = allEvents
                .Where(e => e.EventId != 100 && e.TimeTaken > 1000 && e.TimeOfEvent >= DateTime.Now.AddMonths(-1))
                .GroupBy(e => e.ItemName)
                .Select(g => g.OrderByDescending(e => e.TimeOfEvent).First())
                .ToList();

            // 4. Separação Inteligente:

            // LISTA A: Itens que estão na sua inicialização automática
            result.SlowStartupItems = recentSlowItems
                .Where(e => startupAppNames.Any(appName => e.ItemName.Contains(appName, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.TimeTaken)
                .ToList();

            // LISTA B: Itens que causaram lentidão mas não são de inicialização automática
            result.HighImpactApps = recentSlowItems
                .Where(e => !result.SlowStartupItems.Any(s => s.ItemName == e.ItemName))
                .OrderByDescending(e => e.TimeTaken)
                .ToList();

            return result;
        }

        // =========================================================
        // ANÁLISE DE DESLIGAMENTO (SHUTDOWN)
        // =========================================================

        /// <summary>
        /// Retorna o evento de tempo total do último desligamento.
        /// </summary>
        public static PerformanceEvent? AnalyzeShutdownPerformance()
        {
            // ID 200 = Shutdown Total
            var shutdownEvents = SystemTweaks.GetPerformanceEvents(200, 200, 299);
            return shutdownEvents.FirstOrDefault(e => e.EventId == 200);
        }

        // =========================================================
        // UTILITÁRIOS
        // =========================================================

        /// <summary>
        /// Abre uma pesquisa no Google para ajudar o usuário a entender o que é um processo desconhecido.
        /// Útil para o menu de contexto da lista de boot.
        /// </summary>
        public static void SearchBootItemOnline(string itemName)
        {
            try
            {
                // Remove extensão .exe para melhorar a busca
                string queryName = itemName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                string query = Uri.EscapeDataString($"what is {queryName} process windows");
                string url = $"https://www.google.com/search?q={query}";

                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch { }
        }
    }
}