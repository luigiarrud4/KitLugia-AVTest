using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        private const string BALANCED_GUID = "381b4222-f694-41f0-9685-ff5bb260df2e";
        private const string HIGH_PERF_GUID = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private const string POWER_SAVER_GUID = "a1841308-3541-4fab-bc81-f71556f20b4a";
        private const string ULTIMATE_PERF_TEMPLATE_GUID = "e9a42b02-d5df-448d-aa00-03f14749eb61";

        public static (string Guid, string Name) GetActivePowerPlan()
        {
            string output = SystemUtils.RunExternalProcess("powercfg", "/getactivescheme", hidden: true);
            var guidMatch = GuidRegex().Match(output);
            var nameMatch = PowerPlanNameRegex().Match(output);
            if (guidMatch.Success && nameMatch.Success)
            {
                return (guidMatch.Value, nameMatch.Value.Trim('(', ')'));
            }
            return ("-1", "Não identificado");
        }

        public static List<(string Guid, string Name)> GetAllPowerPlans()
        {
            string output = SystemUtils.RunExternalProcess("powercfg", "/list", hidden: true);

            // Típico: 3-6 planos de energia (Balanced, High Performance, Power Saver, Ultimate Performance, etc)
            var plans = new List<(string Guid, string Name)>(6);
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var guidMatch = GuidRegex().Match(line);
                var nameMatch = PowerPlanNameRegex().Match(line);
                if (guidMatch.Success && nameMatch.Success)
                {
                    plans.Add((guidMatch.Value, nameMatch.Value.Trim('(', ')')));
                }
            }
            return plans;
        }

        public static (bool Success, string Message) SetActivePowerPlan(string guid)
        {
            try
            {
                SystemUtils.RunExternalProcess("powercfg", $"/setactive {guid}", hidden: true);
                return (true, "Plano de energia alterado com sucesso!");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao ativar plano de energia: {ex.Message}");
            }
        }

        public static (bool Success, string Message, string? NewGuid) UnlockAndActivateUltimatePerformance()
        {
            var allPlans = GetAllPowerPlans();
            var ultimatePlan = allPlans.FirstOrDefault(p => p.Name.Contains("Desempenho máximo", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Ultimate Performance", StringComparison.OrdinalIgnoreCase));
            if (ultimatePlan.Guid != null && !string.IsNullOrEmpty(ultimatePlan.Guid))
            {
                var result = SetActivePowerPlan(ultimatePlan.Guid);
                return (result.Success, "Plano 'Desempenho Máximo' já existia e foi ativado.", ultimatePlan.Guid);
            }
            string duplicateOutput = SystemUtils.RunExternalProcess("powercfg", $"-duplicatescheme {ULTIMATE_PERF_TEMPLATE_GUID}", hidden: true);
            var newGuidMatch = GuidRegex().Match(duplicateOutput);
            if (newGuidMatch.Success)
            {
                string newGuid = newGuidMatch.Value;
                SetActivePowerPlan(newGuid);
                return (true, "Plano 'Desempenho Máximo' desbloqueado e ativado com sucesso!", newGuid);
            }
            return (false, "Não foi possível desbloquear o plano 'Desempenho Máximo'.", null);
        }

        public static (bool Success, string Message, string? NewGuid) ImportAndActivateBitsumPlan()
        {
            var allPlans = GetAllPowerPlans();
            var bitsumPlan = allPlans.FirstOrDefault(p => p.Name.Contains("Bitsum Highest Performance", StringComparison.OrdinalIgnoreCase));
            if (bitsumPlan.Guid != null && !string.IsNullOrEmpty(bitsumPlan.Guid))
            {
                SetActivePowerPlan(bitsumPlan.Guid);
                return (true, "'Bitsum Highest Performance' já existia e foi ativado.", bitsumPlan.Guid);
            }
            string resourceName = "KitLugia.Core.Resources.BitsumHighestPerformance.pow";
            return SystemTweaks.ImportAndActivatePowerPlan(resourceName);
        }

        // --- NOVO MÉTODO ADICIONADO ---
        /// <summary>
        /// Deleta um plano de energia customizado.
        /// </summary>
        public static (bool Success, string Message) DeletePowerPlan(string guid)
        {
            var defaultGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { BALANCED_GUID, HIGH_PERF_GUID, POWER_SAVER_GUID, ULTIMATE_PERF_TEMPLATE_GUID };

            // SAFETY CHECK: Previne a exclusão de planos padrão do Windows.
            if (defaultGuids.Contains(guid))
            {
                return (false, "Não é permitido remover os planos de energia padrão do Windows.");
            }

            // Não permite deletar o plano ativo
            var activePlan = GetActivePowerPlan();
            if (activePlan.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Não é possível excluir o plano de energia que está ativo no momento.");
            }

            try
            {
                SystemUtils.RunExternalProcess("powercfg", $"/delete {guid}", hidden: true);
                return (true, "Plano de energia personalizado removido com sucesso.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao remover o plano: {ex.Message}");
            }
        }
    }
}