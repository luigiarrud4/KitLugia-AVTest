using System;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class ToggleManager
    {
        // Salvar estado de TODAS as funções do KitLugia
        public static void SaveAllToggles()
        {
            try
            {
                using (var configKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KitLugia\Toggles", true))
                {
                    // 1. Salvar vulnerabilidades (Guardian)
                    var allTweaks = Guardian.GetAllTweaksDefinition();
                    foreach (var tweak in allTweaks)
                    {
                        string valueKey = $"Guardian_{tweak.Name.GetHashCode()}";
                        configKey.SetValue(valueKey, tweak.Status.ToString(), RegistryValueKind.String);
                    }

                    // 2. Salvar tweaks de sistema (SystemTweaks)
                    SaveSystemTweaks(configKey);

                    // 3. Salvar reparos aplicados (GeneralRepairManager)
                    SaveRepairStates(configKey);

                    // 4. Salvar configurações de privacidade (OOShutUpManager)
                    SavePrivacySettings(configKey);

                    Logger.Log($"Snapshot completo salvo: {allTweaks.Count} vulnerabilidades + tweaks + reparos + privacidade");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar snapshot: {ex.Message}");
            }
        }

        // Restaurar estado de TODAS as funções do KitLugia
        public static void RestoreAllToggles()
        {
            try
            {
                using (RegistryKey? configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\Toggles", true))
                {
                    if (configKey == null) return;
                    int restoredCount = 0;

                    // 1. Restaurar vulnerabilidades (Guardian)
                    var allTweaks = Guardian.GetAllTweaksDefinition();
                    foreach (var tweak in allTweaks)
                    {
                        string valueKey = $"Guardian_{tweak.Name.GetHashCode()}";
                        string? savedStatus = configKey.GetValue(valueKey) as string;

                        if (!string.IsNullOrEmpty(savedStatus) && Enum.TryParse<TweakStatus>(savedStatus, out var status))
                        {
                            if (status == TweakStatus.MODIFIED)
                            {
                                Guardian.ToggleTweak(tweak);
                                restoredCount++;
                            }
                        }
                    }

                    // 2. Restaurar tweaks de sistema
                    if (configKey != null)
                        RestoreSystemTweaks(configKey);

                    // 3. Restaurar reparos
                    if (configKey != null)
                        RestoreRepairStates(configKey);

                    // 4. Restaurar configurações de privacidade
                    if (configKey != null)
                        RestorePrivacySettings(configKey);

                    Logger.Log($"Snapshot restaurado: {restoredCount} configurações revertidas");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao restaurar snapshot: {ex.Message}");
            }
        }

        // Toggle rápido para funções específicas
        public static (bool Success, string Message) QuickToggle(string functionName, bool enable)
        {
            try
            {
                switch (functionName.ToLower())
                {
                    case "gaming":
                        ToggleGamingMode(enable);
                        break;
                    case "powershell":
                        TogglePowerShell(enable);
                        break;
                    case "telemetry":
                        ToggleTelemetry(enable);
                        break;
                    case "defender":
                        ToggleDefender(enable);
                        break;
                    case "updates":
                        ToggleUpdates(enable);
                        break;
                    case "services":
                        ToggleServices(enable);
                        break;
                    case "privacy":
                        TogglePrivacy(enable);
                        break;
                    default:
                        return (false, $"Função '{functionName}' não encontrada para toggle rápido.");
                }

                // Salvar estado do toggle rápido
                using (var configKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KitLugia\QuickToggles", true))
                {
                    configKey.SetValue(functionName, enable, RegistryValueKind.DWord);
                }

                string stateStr = enable ? "ativado" : "desativado";
                return (true, $"{functionName} foi {stateStr} com sucesso.");
            }
            catch (Exception ex)
            {
                string err = $"Erro no toggle rápido '{functionName}': {ex.Message}";
                Logger.Log(err);
                return (false, err);
            }
        }

        // Métodos de Toggle Rápido
        private static void ToggleGamingMode(bool enable)
        {
            if (enable)
            {
                SystemTweaks.ApplyGamingOptimizations();
                SystemTweaks.ToggleGameDvr(false);
                Logger.Log("Modo Gaming ATIVADO");
            }
            else
            {
                SystemTweaks.ToggleGameDvr(true);
                Logger.Log("Modo Gaming DESATIVADO");
            }
        }

        private static void TogglePowerShell(bool enable)
        {
            string policyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\PowerShell";
            if (enable)
            {
                Registry.SetValue(policyPath, "EnableScripts", 1, RegistryValueKind.DWord);
                Logger.Log("PowerShell Scripts ATIVADOS");
            }
            else
            {
                Registry.SetValue(policyPath, "EnableScripts", 0, RegistryValueKind.DWord);
                Logger.Log("PowerShell Scripts DESATIVADOS");
            }
        }

        private static void ToggleTelemetry(bool enable)
        {
            string telemetryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection";
            if (enable)
            {
                Registry.SetValue(telemetryPath, "AllowTelemetry", 3, RegistryValueKind.DWord);
                Logger.Log("Telemetry ATIVADA");
            }
            else
            {
                Registry.SetValue(telemetryPath, "AllowTelemetry", 0, RegistryValueKind.DWord);
                Logger.Log("Telemetry DESATIVADA");
            }
        }

        private static void ToggleDefender(bool enable)
        {
            if (enable)
            {
                SystemUtils.RunExternalProcess("sc.exe", "config WinDefend start= auto", true);
                SystemUtils.RunExternalProcess("sc.exe", "start WinDefend", true);
                Logger.Log("Windows Defender ATIVADO");
            }
            else
            {
                SystemUtils.RunExternalProcess("sc.exe", "config WinDefend start= disabled", true);
                SystemUtils.RunExternalProcess("sc.exe", "stop WinDefend", true);
                Logger.Log("Windows Defender DESATIVADO");
            }
        }

        private static void ToggleUpdates(bool enable)
        {
            string updatePath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
            if (enable)
            {
                Registry.SetValue(updatePath, "NoAutoUpdate", 0, RegistryValueKind.DWord);
                Logger.Log("Windows Updates ATIVADOS");
            }
            else
            {
                Registry.SetValue(updatePath, "NoAutoUpdate", 1, RegistryValueKind.DWord);
                Logger.Log("Windows Updates DESATIVADOS");
            }
        }

        private static void ToggleServices(bool enable)
        {
            var services = new[] { "SysMain", "Themes", "TabletInputService", "WSearch" };
            foreach (var service in services)
            {
                string mode = enable ? "auto" : "disabled";
                SystemUtils.RunExternalProcess("sc.exe", $"config {service} start= {mode}", true);
            }
            Logger.Log($"Serviços {(enable ? "ATIVADOS" : "DESATIVADOS")}");
        }

        // Métodos auxiliares para salvar/restaurar estados específicos
        private static void SaveSystemTweaks(RegistryKey configKey)
        {
            try
            {
                configKey.SetValue("SystemTweaks_IsGamingOptimized", SystemTweaks.IsGamingOptimized(), RegistryValueKind.DWord);
                configKey.SetValue("SystemTweaks_IsMpoDisabled", SystemTweaks.IsMpoDisabled(), RegistryValueKind.DWord);
                configKey.SetValue("SystemTweaks_IsVbsEnabled", SystemTweaks.IsVbsEnabled(), RegistryValueKind.DWord);
                configKey.SetValue("SystemTweaks_IsFastShutdownEnabled", SystemTweaks.IsFastShutdownEnabled(), RegistryValueKind.DWord);
                configKey.SetValue("SystemTweaks_IsPageFileDisabled", SystemTweaks.IsPageFileDisabled(), RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar SystemTweaks: {ex.Message}");
            }
        }

        private static void RestoreSystemTweaks(RegistryKey configKey)
        {
            try
            {
                if (Convert.ToBoolean(configKey.GetValue("SystemTweaks_IsGamingOptimized", false)))
                    SystemTweaks.ApplyGamingOptimizations();

                if (Convert.ToBoolean(configKey.GetValue("SystemTweaks_IsMpoDisabled", false)))
                    SystemTweaks.ToggleMpo();

                if (Convert.ToBoolean(configKey.GetValue("SystemTweaks_IsVbsEnabled", false)))
                    SystemTweaks.ToggleVbs();

                if (Convert.ToBoolean(configKey.GetValue("SystemTweaks_IsFastShutdownEnabled", false)))
                    SystemTweaks.ToggleFastShutdown();

                if (Convert.ToBoolean(configKey.GetValue("SystemTweaks_IsPageFileDisabled", false)))
                    SystemTweaks.DisableMemoryPagination();
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao restaurar SystemTweaks: {ex.Message}");
            }
        }

        private static void SaveRepairStates(RegistryKey configKey)
        {
            try
            {
                var repairs = GeneralRepairManager.GetAllRepairs();
                int appliedCount = 0;
                
                foreach (var repair in repairs)
                {
                    if (repair.IsSelected)
                    {
                        configKey.SetValue($"Repair_{repair.Name.GetHashCode()}", true, RegistryValueKind.DWord);
                        appliedCount++;
                    }
                }
                
                configKey.SetValue("TotalRepairsApplied", appliedCount, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar RepairStates: {ex.Message}");
            }
        }

        private static void RestoreRepairStates(RegistryKey configKey)
        {
            try
            {
                var repairs = GeneralRepairManager.GetAllRepairs();
                int restoredCount = 0;
                
                foreach (var repair in repairs)
                {
                    string valueKey = $"Repair_{repair.Name.GetHashCode()}";
                    if (Convert.ToBoolean(configKey.GetValue(valueKey, false)))
                    {
                        // Marcar como aplicado (simulação da restauração)
                        repair.IsSelected = true;
                        restoredCount++;
                    }
                }
                
                Logger.Log($"Reparos restaurados: {restoredCount}");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao restaurar RepairStates: {ex.Message}");
            }
        }

        // Limpar todos os snapshots
        public static void ClearAllSnapshots()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\KitLugia\Toggles");
                Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\KitLugia\QuickToggles");
                Logger.Log("Todos os snapshots foram limpos");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao limpar snapshots: {ex.Message}");
            }
        }

        // Obter lista de toggles rápidos aplicados
        private static void TogglePrivacy(bool enable)
        {
            try
            {
                if (enable)
                {
                    OOShutUpManager.ApplyPreset(OOShutUpManager.PrivacyLevel.Recommended);
                    Logger.Log("Modo privacidade recomendado ativado");
                }
                else
                {
                    OOShutUpManager.RestoreDefaults();
                    Logger.Log("Configurações de privacidade restauradas para padrão");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao toggle privacidade: {ex.Message}");
            }
        }

        private static void SavePrivacySettings(RegistryKey configKey)
        {
            try
            {
                var privacySettings = OOShutUpManager.GetPrivacySettings();
                foreach (var setting in privacySettings)
                {
                    string valueKey = $"Privacy_{setting.Name.GetHashCode()}";
                    bool isApplied = OOShutUpManager.IsPrivacySettingApplied(setting);
                    configKey.SetValue(valueKey, isApplied, RegistryValueKind.DWord);
                }
                Logger.Log($"Configurações de privacidade salvas: {privacySettings.Count} itens");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar configurações de privacidade: {ex.Message}");
            }
        }

        private static void RestorePrivacySettings(RegistryKey configKey)
        {
            try
            {
                var privacySettings = OOShutUpManager.GetPrivacySettings();
                int restoredCount = 0;

                foreach (var setting in privacySettings)
                {
                    string valueKey = $"Privacy_{setting.Name.GetHashCode()}";
                    object? savedValue = configKey.GetValue(valueKey);

                    if (savedValue != null && Convert.ToBoolean(savedValue))
                    {
                        if (OOShutUpManager.ApplyPrivacySetting(setting))
                        {
                            restoredCount++;
                        }
                    }
                }

                Logger.Log($"Configurações de privacidade restauradas: {restoredCount} itens");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao restaurar configurações de privacidade: {ex.Message}");
            }
        }

        public static Dictionary<string, bool> GetQuickToggleStates()
        {

            // Típico: 5-10 quick toggles
            var states = new Dictionary<string, bool>(10, StringComparer.OrdinalIgnoreCase);
            
            try
            {
                using (var configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\QuickToggles", true))
                {
                    if (configKey == null) return states;
                    foreach (var valueName in configKey.GetValueNames())
                    {
                        object? value = configKey.GetValue(valueName);
                        if (value != null)
                        {
                            states[valueName] = Convert.ToBoolean(value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao carregar quick toggles: {ex.Message}");
            }
            
            return states;
        }
    }
}
