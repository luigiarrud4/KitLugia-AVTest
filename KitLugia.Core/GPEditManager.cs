using System;
using System.IO;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Aplica um conjunto de tweaks de registro focados em privacidade e redução de telemetria.
        /// </summary>
        public static void ApplyPrivacyCombo()
        {
            // Desativa recursos "sugeridos" pela Microsoft (propagandas, apps, etc.).
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, 0, true, "Apps Sugeridos");
            // Desativa os widgets de Notícias e Interesses.
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, 1, true, "Widgets");
            // Define o nível de telemetria para o mínimo (0 = Segurança).
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, 1, true, "Telemetria Principal");
            // Desativa a telemetria de compatibilidade de aplicativos.
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AIT_DisableAppTelemetry", 1, 0, true, "Telemetria de Aplicativos");
            // Impede que aplicativos da Microsoft Store rodem em segundo plano.
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, 1, true, "Apps em Segundo Plano");
            // Desativa a Linha do Tempo do Windows.
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, 1, true, "Linha do Tempo");
            // Desativa a Cortana.
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, 1, true, "Cortana");
            // Desativa o SmartScreen (filtro de arquivos e aplicativos).
            SystemTweaks.ToggleRegistryTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", 0, 1, true, "SmartScreen");
            // Desativa o SmartScreen do Explorer (Win11)
            Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                "SmartScreenEnabled", "Off", Microsoft.Win32.RegistryValueKind.String);
        }

        /// <summary>
        /// Reverte todas as políticas de registro alteradas pelo KitLugia para o estado padrão do Windows.
        /// </summary>
        public static void RevertAllPolicies()
        {
            // O método RevertPolicyTweak simplesmente remove a chave do registro,
            // fazendo com que o Windows volte a usar seu comportamento padrão.
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", true);
            // Ocultar o ícone do OneDrive não é uma política, então o revert é diferente.
            // Usamos Toggle para garantir que o valor volte a 1 (visível).
            SystemTweaks.ToggleRegistryTweak(@"HKEY_CLASSES_ROOT\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}", "System.IsPinnedToNameSpaceTree", 0, 1, false, "Atalho do OneDrive");
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat", "AIT_DisableAppTelemetry", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen", true);
            SystemTweaks.RevertPolicyTweak(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer", "SmartScreenEnabled", true);
        }

        /// <summary>
        /// Verifica se o Editor de Política de Grupo (gpedit.msc) está presente no sistema.
        /// </summary>
        /// <returns>Verdadeiro se o arquivo existir.</returns>
        public static bool IsGPEditAvailable()
        {
            // A forma mais simples e direta de verificar é ver se o executável existe na pasta do sistema.
            return File.Exists(Path.Combine(Environment.SystemDirectory, "gpedit.msc"));
        }

        /// <summary>
        /// Abre o Editor de Política de Grupo.
        /// </summary>
        public static void OpenGPEdit()
        {
            try
            {
                // Chama o console de gerenciamento (mmc) para abrir o snap-in do gpedit.
                SystemUtils.RunExternalProcess("mmc", "gpedit.msc", hidden: false, waitForExit: false);
            }
            catch
            {
                // A UI pode opcionalmente mostrar um erro se a execução falhar.
            }
        }
    }
}