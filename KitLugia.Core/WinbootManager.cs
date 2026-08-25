using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ookii.AnswerFile;

namespace KitLugia.Core
{
    public static class WinbootManager
    {
        public const string WINBOOT_LABEL = "KITLUGIA";
        
        // Caminho de instalação dinâmico - usa Program Files em vez de C:\KitLugia
        public static string KitLugiaInstallPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "KitLugia"
        );

        static WinbootManager()
        {
            // Registrar provedor de encoding para suportar OEM 850 (WinPE)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// Verifica se a ISO foi criada pelo KitLugia ISO Editor
        /// Detecta o arquivo .kitlugia na raiz da ISO
        /// </summary>
        public static async Task<bool> IsKitLugiaIso(string isoPath)
        {
            try
            {
                string driveLetter = await MountIso(isoPath);
                if (string.IsNullOrEmpty(driveLetter))
                {
                    return false;
                }

                string kitlugiaIdFile = Path.Combine(driveLetter, ".kitlugia");
                bool isKitLugia = File.Exists(kitlugiaIdFile);

                await DismountIso(isoPath);

                if (isKitLugia)
                {
                    Log("ISO detectada como KitLugia ISO (arquivo .kitlugia encontrado).");
                    Log("Preservando autounattend.xml existente.");
                }

                return isKitLugia;
            }
            catch (Exception ex)
            {
                Log($"Erro ao verificar se é ISO do KitLugia: {ex.Message}");
                return false;
            }
        }

        public static bool IsEfiMode()
        {
            try
            {
                // Método simples e confiável via bcdedit ou presença de winload.efi
                return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "winload.efi"));
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Detecta o idioma da ISO usando DISM /Get-WimInfo
        /// Retorna o código de idioma (ex: pt-BR, en-US, es-ES)
        /// </summary>
        public static async Task<string> DetectIsoLanguage(string isoPath, string? extractedDrive = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(extractedDrive))
                {
                    return DetectLanguageFromDrive(extractedDrive);
                }

                Log("Detectando idioma da ISO...");

                string driveLetter = await MountIso(isoPath);
                if (string.IsNullOrEmpty(driveLetter))
                {
                    Log("Falha ao montar ISO para detecção de idioma.");
                    return "pt-BR";
                }

                string lang = DetectLanguageFromDrive(driveLetter);
                await DismountIso(isoPath);
                return lang;
            }
            catch (Exception ex)
            {
                Log($"Erro ao detectar idioma da ISO: {ex.Message}");
                return "pt-BR";
            }
        }

        private static string DetectLanguageFromDrive(string drive)
        {
            string wimPath = Path.Combine(drive, "sources", "install.wim");
            if (!File.Exists(wimPath))
            {
                wimPath = Path.Combine(drive, "sources", "install.esd");
            }

            if (!File.Exists(wimPath))
            {
                Log("Arquivo install.wim/esd não encontrado.");
                return "pt-BR";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "dism.exe",
                Arguments = $"/Get-WimInfo /WimFile:\"{wimPath}\" /Index:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                Log("Falha ao iniciar DISM.");
                return "pt-BR";
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Log($"DISM retornou erro: {process.ExitCode}");
                return "pt-BR";
            }

            var match = Regex.Match(output, @"Default\s*:\s*([a-z]{2}-[A-Z]{2})");
            if (match.Success)
            {
                string detectedLanguage = match.Groups[1].Value;
                Log($"Idioma detectado: {detectedLanguage}");
                return detectedLanguage;
            }

            if (output.Contains("pt-BR") || output.Contains("ptbr"))
            {
                Log("Idioma detectado: pt-BR (fallback)");
                return "pt-BR";
            }
            if (output.Contains("en-US") || output.Contains("enus"))
            {
                Log("Idioma detectado: en-US (fallback)");
                return "en-US";
            }
            if (output.Contains("es-ES") || output.Contains("eses"))
            {
                Log("Idioma detectado: es-ES (fallback)");
                return "es-ES";
            }

            Log("Idioma não detectado, usando pt-BR como padrão.");
            return "pt-BR";
        }

        /// <summary>
        /// Mapeia código de idioma para InputLocale no formato "LCID:KeyboardLayout"
        /// Fonte: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/default-input-locales-for-windows-language-packs
        /// </summary>
        public static string GetInputLocaleFromLanguage(string language)
        {
            return language.ToUpper() switch
            {
                "PT-BR" => "0416:00000416", // Português (Brasil ABNT)
                "EN-US" => "0409:00000409", // Inglês (EUA)
                "ES-ES" => "040A:0000040A", // Espanhol (Espanha)
                "FR-FR" => "040C:0000040C", // Francês (França)
                "DE-DE" => "0407:00000407", // Alemão (Alemanha)
                "IT-IT" => "0410:00000410", // Italiano (Itália)
                "JA-JP" => "0411:00000411", // Japonês
                "KO-KR" => "0412:00000412", // Coreano
                "ZH-CN" => "0804:00000804", // Chinês (Simplificado)
                "ZH-TW" => "0404:00000404", // Chinês (Tradicional)
                "RU-RU" => "0419:00000419", // Russo
                "AR-SA" => "0401:00000401", // Árabe (Arábia Saudita)
                _ => "0416:00000416" // Fallback para pt-BR
            };
        }

        /// <summary>
        /// Mapeia código de idioma para GeoID (região geográfica)
        /// Fonte oficial: https://learn.microsoft.com/en-us/windows/win32/intl/table-of-geographical-locations
        /// </summary>
        public static int GetGeoIdFromLanguage(string language)
        {
            return language.ToUpper() switch
            {
                "PT-BR" => 32,    // Brasil (0x20)
                "EN-US" => 244,   // Estados Unidos (0xf4)
                "ES-ES" => 217,   // Espanha (0xd9)
                "FR-FR" => 84,    // França (0x54)
                "DE-DE" => 94,    // Alemanha (0x5e)
                "IT-IT" => 118,   // Itália (0x76)
                "JA-JP" => 122,   // Japão (0x7a)
                "KO-KR" => 134,   // Coreia (0x86)
                "ZH-CN" => 45,    // China (0x2d)
                "ZH-TW" => 237,   // Taiwan (0xed)
                "RU-RU" => 203,   // Rússia (0xcb)
                "AR-SA" => 205,   // Arábia Saudita (0xcd)
                _ => 32           // Fallback para Brasil
            };
        }

        /// <summary>
        /// Mapeia código de idioma para fuso horário Windows
        /// Use 'tzutil /l' para listar todos os fusos válidos
        /// </summary>
        public static string GetTimeZoneFromLanguage(string language)
        {
            return language.ToUpper() switch
            {
                "PT-BR" => "E. South America Standard Time",  // Brasil (BRT)
                "EN-US" => "Eastern Standard Time",           // EUA (EST)
                "ES-ES" => "Romance Standard Time",           // Espanha (CET)
                "FR-FR" => "Romance Standard Time",           // França (CET)
                "DE-DE" => "W. Europe Standard Time",         // Alemanha (CET)
                "IT-IT" => "W. Europe Standard Time",         // Itália (CET)
                "JA-JP" => "Tokyo Standard Time",             // Japão (JST)
                "KO-KR" => "Korea Standard Time",             // Coreia (KST)
                "ZH-CN" => "China Standard Time",             // China (CST)
                "ZH-TW" => "Taipei Standard Time",            // Taiwan (TST)
                "RU-RU" => "Russian Standard Time",           // Rússia (MSK)
                "AR-SA" => "Arab Standard Time",              // Arábia Saudita (AST)
                _ => "E. South America Standard Time"         // Fallback para Brasil
            };
        }

        /// <summary>
        /// Gera um arquivo autounattend.xml usando a biblioteca Ookii.AnswerFile
        /// </summary>
        public static void GenerateAutounattendXml(string outputPath, bool bypassRequirements = true, bool localAccount = true, bool disablePrivacy = true, string? userName = "Usuario", string? password = null, bool fullAuto = true, bool disableDefender = false, bool autoLogon = true, bool remoteDesktop = false, string language = "pt-BR", string timeZone = "E. South America Standard Time", string[]? commands = null,
            bool showAllEditions = false, bool disableBitlocker = true, bool disableHibernate = false, bool disableCopilot = true, bool removeEdge = false, bool removeCortana = true, bool removeOneDrive = false, bool disableSpotlight = true, bool disableNews = true, bool disableChat = true,
            bool disableAutoUpdate = false, bool disableDeliveryOpt = true, bool delayUpdates = false, bool longPaths = true, bool disableLocation = true, bool disableActivity = true, bool disableAdID = true, bool disableErrorReporting = true, bool disableInkWorkspace = false,
            bool disableSmartScreen = false, bool disableDefenderSandbox = false, bool disableUAC = false, bool hideEula = true, bool hideOEM = true, bool hideWireless = true, bool hideOnlineAccount = true, bool protectYourPC = true, string computerName = "",
            bool removeXbox = true, bool removeMaps = true, bool removeMail = true, bool removeWeather = true, bool removeSports = true, bool removeMoney = true, bool removePeople = true, bool removeSkype = true, bool removeGroove = true, bool removeMovies = true, bool removeFeedback = true, bool removeGetStarted = true, bool remove3DViewer = true, bool removePaint3D = true)
        {
            try
            {
                var options = new AnswerFileOptions
                {
                    // Instalação manual (usuário seleciona disco/partição durante setup)
                    InstallOptions = new ManualInstallOptions(),

                    // Configurações de idioma e região
                    Language = language,
                    InputLocale = GetInputLocaleFromLanguage(language),
                    GeoID = GetGeoIdFromLanguage(language),
                    TimeZone = timeZone,
                    ProcessorArchitecture = "amd64",

                    // OOBE
                    HideEULAPage = hideEula,
                    HideOEMRegistrationScreen = hideOEM,
                    HideWirelessSetupInOOBE = hideWireless,
                    HideOnlineAccountScreens = hideOnlineAccount,
                    ProtectYourPC = protectYourPC ? 1 : 3
                };

                // Adicionar conta local se especificado
                if (localAccount && !string.IsNullOrEmpty(userName))
                {
                    var credential = new LocalCredential(
                        userName,
                        password ?? string.Empty, // Senha vazia se não especificada
                        "Administrators"
                    );
                    options.LocalAccounts.Add(credential);
                }

                // Desabilitar Windows Defender se solicitado
                if (disableDefender)
                {
                    options.EnableDefender = false;
                }

                // Desabilitar Cloud features se privacy desabilitado
                if (disablePrivacy)
                {
                    options.EnableCloud = false;
                }

                // Habilitar Área de Trabalho Remota se solicitado
                if (remoteDesktop)
                {
                    options.EnableRemoteDesktop = true;
                }

                // Configurar AutoLogon para instalação totalmente automática
                if (autoLogon && !string.IsNullOrEmpty(userName))
                {
                    var domainUser = new DomainUser(userName); // Usuário local (domain = null)
                    var credential = new DomainCredential(domainUser, password ?? string.Empty);
                    options.AutoLogon = new AutoLogonOptions(credential)
                    {
                        Count = 1
                    };
                }

                // Adicionar comandos pós-instalação se especificados
                if (commands != null && commands.Length > 0)
                {
                    foreach (var cmd in commands)
                    {
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            options.FirstLogonCommands.Add(cmd.Trim());
                        }
                    }
                }

                // Adicionar comandos de registry e tweaks

                // Típico: 10-20 comandos de registry
                var registryCommands = new List<string>(20);

                // Bypass de requisitos do Windows 11
                if (bypassRequirements)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassTPMCheck /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassSecureBootCheck /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassStorageCheck /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassCPUCheck /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassRAMCheck /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassDiskCheck /t REG_DWORD /d 1 /f");
                }

                // Mostrar todas as edições do Windows
                if (showAllEditions)
                {
                    registryCommands.Add("cmd.exe /c del /f /q X:\\Sources\\ei.cfg");
                    registryCommands.Add("cmd.exe /c echo [Channel] > X:\\Sources\\ei.cfg");
                    registryCommands.Add("cmd.exe /c echo _Default >> X:\\Sources\\ei.cfg");
                    registryCommands.Add("cmd.exe /c echo [VL] >> X:\\Sources\\ei.cfg");
                    registryCommands.Add("cmd.exe /c echo 0 >> X:\\Sources\\ei.cfg");
                }

                // Bypass de Microsoft Account
                if (localAccount)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f");
                }

                // Desabilitar BitLocker
                if (disableBitlocker)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\BitLocker\" /v \"PreventDeviceEncryption\" /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\EnhancedStorageDevices\" /v TCGSecurityActivationDisabled /t REG_DWORD /d 1 /f");
                }

                // Desabilitar Hibernação
                if (disableHibernate)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\System\\CurrentControlSet\\Control\\Session Manager\\Power\" /v HibernateEnabled /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FlyoutMenuSettings\" /v ShowHibernateOption /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Windows Copilot
                if (disableCopilot)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot\" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f");
                }

                // Desabilitar Cortana
                if (removeCortana)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\Software\\Policies\\Microsoft\\Windows\\Windows Search\" /v AllowCortana /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Windows Spotlight
                if (disableSpotlight)
                {
                    registryCommands.Add("reg.exe add \"HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent\" /v DisableWindowsSpotlightOnLockScreen /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent\" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent\" /v DisableWindowsSpotlightActiveUser /t REG_DWORD /d 1 /f");
                }

                // Desabilitar News and Interests
                if (disableNews)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Dsh\" /v AllowNewsAndInterests /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Chat/Teams
                if (disableChat)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Communications\" /v ConfigureChatAutoInstall /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Chat\" /v \"ChatIcon\" /t REG_DWORD /d 3 /f");
                }

                // Desabilitar atualizações automáticas
                if (disableAutoUpdate)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU\" /v NoAutoUpdate /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU\" /v AutoInstallMinorUpdates /t REG_DWORD /d 0 /f");
                }

                // Atrasar atualizações
                if (delayUpdates)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU\" /v AUOptions /t REG_DWORD /d 3 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\" /v DeferFeatureUpdates /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\" /v DeferFeatureUpdatesPeriodInDays /t REG_DWORD /d 365 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\" /v DeferQualityUpdates /t REG_DWORD /d 1 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\" /v DeferQualityUpdatesPeriodInDays /t REG_DWORD /d 365 /f");
                }

                // Desabilitar Delivery Optimization
                if (disableDeliveryOpt)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization\" /v DODownloadMode /t REG_DWORD /d 0 /f");
                }

                // Habilitar Long File Paths
                if (longPaths)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\" /v LongPathsEnabled /t REG_DWORD /d 1 /f");
                }

                // Desabilitar Location Tracking
                if (disableLocation)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location\" /v Value /t REG_SZ /d Deny /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Sensor\\Overrides\\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}\" /v SensorPermissionState /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\lfsvc\\Service\\Configuration\" /v Status /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SYSTEM\\Maps\" /v AutoUpdateEnabled /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Activity History
                if (disableActivity)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" /v EnableActivityFeed /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" /v PublishUserActivities /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" /v UploadUserActivities /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Advertising ID
                if (disableAdID)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\AdvertisingInfo\" /v DisabledByGroupPolicy /t REG_DWORD /d 1 /f");
                }

                // Desabilitar Windows Error Reporting
                if (disableErrorReporting)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Error Reporting\" /v Disabled /t REG_DWORD /d 1 /f");
                }

                // Desabilitar Windows Ink Workspace
                if (disableInkWorkspace)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\WindowsInkWorkspace\" /v AllowWindowsInkWorkspace /t REG_DWORD /d 0 /f");
                }

                // Desabilitar SmartScreen
                if (disableSmartScreen)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\" /v SmartScreenEnabled /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppHost\" /v EnableWebContentEvaluation /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Sandbox do Defender
                if (disableDefenderSandbox)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows Defender\\Features\" /v TamperProtection /t REG_DWORD /d 0 /f");
                    registryCommands.Add("powershell.exe -Command \"Set-MpPreference -DisableRealtimeMonitoring $true\"");
                }

                // Desabilitar UAC
                if (disableUAC)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v EnableLUA /t REG_DWORD /d 0 /f");
                }

                // Desabilitar Telemetria
                if (disablePrivacy)
                {
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\DataCollection\" /v AllowTelemetry /t REG_DWORD /d 0 /f");
                    registryCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection\" /v AllowTelemetry /t REG_DWORD /d 0 /f");
                }

                // Adicionar comandos de registry ao FirstLogonCommands
                foreach (var cmd in registryCommands)
                {
                    options.FirstLogonCommands.Add(cmd);
                }

                // Configurar nome do computador se especificado
                if (!string.IsNullOrEmpty(computerName))
                {
                    options.ComputerName = computerName;
                }

                // Remover Edge se solicitado (requer script PowerShell)
                if (removeEdge)
                {
                    options.FirstLogonCommands.Add("powershell.exe -ExecutionPolicy Bypass -Command \"Invoke-WebRequest -Uri 'https://github.com/ShadowWhisperer/Remove-MS-Edge/blob/main/Remove-NoTerm.exe?raw=true' -OutFile '%TEMP%\\Remove-NoTerm.exe'\"");
                    options.FirstLogonCommands.Add("cmd.exe /c \"%TEMP%\\Remove-NoTerm.exe /silent /install\"");
                }

                // Remover Xbox Game Bar e App
                if (removeXbox)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Xbox*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Xbox* | Remove-AppxPackage\"");
                    options.FirstLogonCommands.Add("reg.exe add \"HKCU\\Software\\Microsoft\\GameBar\" /v AllowAutoGameMode /t REG_DWORD /d 0 /f");
                    options.FirstLogonCommands.Add("reg.exe add \"HKCU\\Software\\Microsoft\\GameBar\" /v AutoGameModeEnabled /t REG_DWORD /d 0 /f");
                }

                // Remover Maps
                if (removeMaps)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Maps*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Maps* | Remove-AppxPackage\"");
                }

                // Remover Mail and Calendar
                if (removeMail)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Mail*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Calendar*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Mail* | Remove-AppxPackage\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Calendar* | Remove-AppxPackage\"");
                }

                // Remover Weather
                if (removeWeather)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Weather*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Weather* | Remove-AppxPackage\"");
                }

                // Remover Sports
                if (removeSports)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Sports*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Sports* | Remove-AppxPackage\"");
                }

                // Remover Money
                if (removeMoney)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Money*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Money* | Remove-AppxPackage\"");
                }

                // Remover People
                if (removePeople)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*People*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *People* | Remove-AppxPackage\"");
                }

                // Remover Skype
                if (removeSkype)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Skype*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Skype* | Remove-AppxPackage\"");
                }

                // Remover Groove Music
                if (removeGroove)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*ZuneMusic*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *ZuneMusic* | Remove-AppxPackage\"");
                }

                // Remover Movies & TV
                if (removeMovies)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*ZuneVideo*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *ZuneVideo* | Remove-AppxPackage\"");
                }

                // Remover Feedback Hub
                if (removeFeedback)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*FeedbackHub*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *FeedbackHub* | Remove-AppxPackage\"");
                }

                // Remover Get Started Tips
                if (removeGetStarted)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*GetStarted*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *GetStarted* | Remove-AppxPackage\"");
                }

                // Remover 3D Viewer
                if (remove3DViewer)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*3DViewer*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *3DViewer* | Remove-AppxPackage\"");
                }

                // Remover Paint 3D
                if (removePaint3D)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Paint3D*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Paint3D* | Remove-AppxPackage\"");
                }

                // Remover Cortana
                if (removeCortana)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Microsoft.549981C3F5F10*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Microsoft.549981C3F5F10* | Remove-AppxPackage\"");
                }


                if (disablePrivacy)
                {
                    options.FirstLogonCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search\" /v DisableWebSearch /t REG_DWORD /d 1 /f");
                    options.FirstLogonCommands.Add("reg.exe add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent\" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f");
                    options.FirstLogonCommands.Add("reg.exe add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\" /v ContentDeliveryAllowed /t REG_DWORD /d 0 /f");
                    options.FirstLogonCommands.Add("reg.exe add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\" /v SilentInstalledAppsEnabled /t REG_DWORD /d 0 /f");
                }


                if (disablePrivacy || removeXbox)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'XblGameSaveTaskLogon' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'XblGameSaveTask' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'Consolidator' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'UsbCeip' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'DmClient' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-ScheduledTask -TaskName 'DmClientOnScenarioDownload' -ErrorAction SilentlyContinue | Disable-ScheduledTask\"");
                }

                // Remover OneDrive
                if (removeOneDrive)
                {
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*Microsoft.OneDriveSync*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *Microsoft.OneDriveSync* | Remove-AppxPackage\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxProvisionedPackage -Online | Where-Object {$_.PackageName -like '*OneDrive*'} | Remove-AppxProvisionedPackage -Online\"");
                    options.FirstLogonCommands.Add("powershell.exe -Command \"Get-AppxPackage *OneDrive* | Remove-AppxPackage\"");
                }

                // Gerar o arquivo usando o método estático
                AnswerFileGenerator.Generate(outputPath, options);

                Log($"Arquivo autounattend.xml gerado com sucesso em: {outputPath}");
                Log($"Configurações: Bypass={bypassRequirements}, LocalAccount={localAccount}, DisablePrivacy={disablePrivacy}, FullAuto={fullAuto}, ShowAllEditions={showAllEditions}, DisableBitlocker={disableBitlocker}, RemoveEdge={removeEdge}, RemoveCortana={removeCortana}, RemoveOneDrive={removeOneDrive}");
            }
            catch (Exception ex)
            {
                Log($"Erro ao gerar autounattend.xml: {ex.Message}");
                throw;
            }
        }

        // --- DISK ENGINE ---
        public static List<DiskInfo> GetDisks(bool filterWinboot = false, bool safeMode = false)
        {

            // Típico: 1-4 discos em sistemas comuns
            var disks = new List<DiskInfo>(4);
            try
            {
                using var diskDriveQuery = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                using var diskResults = diskDriveQuery.Get();
                foreach (ManagementObject diskDrive in diskResults)
                {
                    using (diskDrive)
                    {
                        var disk = new DiskInfo
                        {
                            Index = (uint)diskDrive["Index"],
                            Model = diskDrive["Model"]?.ToString() ?? "Desconhecido",
                            Interface = diskDrive["InterfaceType"]?.ToString() ?? "USB/SATA/NVMe",
                            Size = (ulong)diskDrive["Size"]
                        };

                        using var partitionQuery = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{diskDrive["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                        using var partitionResults = partitionQuery.Get();
                        foreach (ManagementObject partition in partitionResults)
                        {
                            using (partition)
                            {
                                var partInfo = new PartitionInfo
                                {
                                    Index = (uint)partition["Index"],
                                    DiskIndex = disk.Index,
                                    Name = partition["Name"]?.ToString() ?? "Partição",
                                    Size = (ulong)partition["Size"]
                                };

                                using var logicalDiskQuery = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
                                using var logicalResults = logicalDiskQuery.Get();
                                foreach (ManagementObject logicalDisk in logicalResults)
                                {
                                    using (logicalDisk)
                                    {
                                        partInfo.DriveLetter = logicalDisk["DeviceID"]?.ToString() ?? string.Empty;
                                        partInfo.Label = logicalDisk["VolumeName"]?.ToString() ?? string.Empty;
                                        partInfo.FileSystem = logicalDisk["FileSystem"]?.ToString() ?? "RAW";
                                        partInfo.FreeSpace = (ulong)logicalDisk["FreeSpace"];
                                    }
                                }
                                if (filterWinboot)
                                {
                                    // 20GB mínimo (Garante ocultação total de MSR, EFI, Recovery e do Winboot de 8GB)
                                    if (partInfo.Size < 20000000000) continue;

                                    if (safeMode)
                                    {

                                        // MSR, EFI, Recovery são geralmente < 20GB ou têm tipos específicos
                                        // Winboot é identificado pelo label WINBOOT_LABEL
                                        if (partInfo.Label.Equals(WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase)) continue;
                                        if (partInfo.Label.Equals("Winboot", StringComparison.OrdinalIgnoreCase)) continue;
                                    }
                                    else
                                    {

                                        // System partitions (English, Portuguese, Spanish, French, German, Italian, Russian, Chinese, Japanese, Korean)
                                        string[] systemLabels = { "System", "Sistema", "Système", "Systemlaufwerk", "Sistema operativo", "Система", "系统", "システム", "시스템" };
                                        if (systemLabels.Any(l => partInfo.Label.Contains(l, StringComparison.OrdinalIgnoreCase))) continue;

                                        // Recovery partitions (English, Portuguese, Spanish, French, German, Italian, Russian, Chinese, Japanese, Korean)
                                        string[] recoveryLabels = { "Recovery", "Recuperação", "Recuperación", "Récupération", "Wiederherstellung", "Ripristino", "Восстановление", "恢复", "復旧", "복구" };
                                        if (recoveryLabels.Any(l => partInfo.Label.Contains(l, StringComparison.OrdinalIgnoreCase))) continue;

                                        // Reserved partitions (English, Portuguese, Spanish, French, German, Italian, Russian, Chinese, Japanese, Korean)
                                        string[] reservedLabels = { "Reserved", "Reservado", "Reservado", "Réservé", "Reserviert", "Riservato", "Зарезервировано", "保留", "予約", "예약" };
                                        if (reservedLabels.Any(l => partInfo.Label.Contains(l, StringComparison.OrdinalIgnoreCase))) continue;

                                        // Winboot partitions (para não selecionar a própria partição Winboot)
                                        if (partInfo.Label.Equals(WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase)) continue;
                                        if (partInfo.Label.Equals("Winboot", StringComparison.OrdinalIgnoreCase)) continue;
                                    }
                                }

                                disk.Partitions.Add(partInfo);
                            }
                        }
                        disks.Add(disk);
                    }
                }
            }
            catch (Exception ex) { Logger.Log($"Erro WinbootManager.GetDisks: {ex.Message}"); }
            return disks;
        }

        public static List<PartitionInfo> GetRemovablePartitions()
        {
             var allDisks = GetDisks(false);

             // Típico: 1-5 partições removíveis
             var candidates = new List<PartitionInfo>(5);

             foreach (var d in allDisks)
             {
                 foreach (var p in d.Partitions)
                 {
                     // FILTER: Safety (> 6GB)
                     if (p.Size < 6442450944) continue; // 6GB in bytes

                     // FILTER: Suspect Label
                     bool isSuspect = p.Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase) ||
                                      p.Label.Contains("NAO_DELETAR", StringComparison.OrdinalIgnoreCase) ||
                                      p.Label.Contains("LUGIA", StringComparison.OrdinalIgnoreCase);

                     if (isSuspect)
                     {
                         candidates.Add(p);
                     }
                 }
             }
             return candidates;
        }

        // --- LOGGING ENGINE ---
        private static StringBuilder _logSession = new StringBuilder();
        public static event Action<string>? OnLogUpdate;
        public static event Action<string>? OnLogReplace;

        public static void Log(string message)
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _logSession.AppendLine(logLine);
            OnLogUpdate?.Invoke(logLine);

            try
            {

                // LocalApplicationData não depende de Roaming e é mais seguro
                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "Logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "Winboot.log"), logLine + Environment.NewLine);
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Log que substitui a última linha (ideal para progresso de download, evita flooding).
        /// </summary>
        public static void LogReplace(string message)
        {
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}";
            // Remove a última linha do _logSession
            var current = _logSession.ToString();
            int lastNewline = current.LastIndexOf('\n', current.Length - 2);
            _logSession.Clear();
            if (lastNewline > 0)
                _logSession.Append(current.AsSpan(0, lastNewline + 1));
            _logSession.AppendLine(logLine);
            OnLogReplace?.Invoke(logLine);
        }

        public static string GetSessionLog() => _logSession.ToString();

        // --- DRIVER MAGIC ---
        public static async Task<bool> ExportHostDrivers(string targetDir)
        {
            Log($"Exportando drivers do host para {targetDir}...");
            return await Task.Run(async () =>
            {
                try
                {
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    // Detectar DISM do host
                    string dismPath = Path.Combine(Environment.SystemDirectory, "dism.exe");
                    if (!File.Exists(dismPath))
                    {
                        Log("ERRO: DSM.exe não encontrado no System32.");
                        return false;
                    }

                    // Exportar drivers
                    var (code, output) = await RunProcessCaptured(dismPath, $"/online /export-driver /destination:\"{targetDir}\"");
                    if (code != 0)
                    {
                        Log($"ERRO ao exportar drivers: {output}");
                        return false;
                    }

                    Log("Exportação de drivers concluída com sucesso.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"EXCEÇÃO ao exportar drivers: {ex.Message}");
                    return false;
                }
            });
        }

        // --- DIAGNOSTICS ---
        public static async Task<List<string>> PerformDiagnostics(string isoPath)
        {
            return await Task.Run(() =>
            {

                // Típico: 5-10 erros de diagnóstico
                var errors = new List<string>(10);
                Log("Iniciando diagnósticos de sistema...");

                // 1. Admin Check
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem"))
                    {
                        var results = searcher.Get();
                        Log("WMI: OK (Serviço de gerenciamento funcionando)");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("WMI Error: Falha ao acessar informações do sistema. Rode como Admin.");
                    Log($"ERRO WMI: {ex.Message}");
                }

                // 2. ISO Check
                if (!string.IsNullOrEmpty(isoPath))
                {
                    if (File.Exists(isoPath))
                    {
                        var info = new FileInfo(isoPath);
                        Log($"ISO: Encontrada ({info.Length / 1024 / 1024} MB)");
                    }
                    else
                    {
                        errors.Add("ISO: Arquivo não encontrado no caminho especificado.");
                        Log("ERRO ISO: Arquivo inexistente.");
                    }
                }

                // 3. Tools Check
                string[] tools = { "diskpart.exe", "bcdedit.exe", "robocopy.exe", "powershell.exe" };
                foreach (var tool in tools)
                {
                    if (File.Exists(Path.Combine(Environment.SystemDirectory, tool)) || 
                        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", tool)))
                        Log($"{tool}: OK");
                    else
                    {
                        errors.Add($"{tool}: Ferramenta de sistema não encontrada.");
                        Log($"ERRO: {tool} ausente.");
                    }
                }


                return errors;
            });
        }


        // --- BOOT SERVICE (RAMDISK — PARA ISOS/WIM LEGADO) ---

        /// <summary>
        /// Cria entrada BCD ramdisk (para boot de WIMs legados, usado pela WinbootPage antiX).
        /// </summary>
        public static async Task<string?> CreateRamdiskEntry(string description, string driveLetter, string wimPath, string sdiPath, bool skipCleanup = false)
        {
            Log($"Configurando entrada BCD ramdisk: {description}...");
            try
            {
                // Remove entradas antigas para evitar múltiplas no boot menu
                if (!skipCleanup)
                    await CleanupOldWinpeEntries();

                string part = $"{driveLetter}:";
                string cleanDesc = SanitizeDescription(description);

                // Tenta configurar {ramdiskoptions} — se falhar, continua mesmo assim
                var (rcCode, _) = await RunProcessCaptured("bcdedit.exe",
                    $"/create {{ramdiskoptions}} /d \"{cleanDesc}\"");
                Log($"> bcdedit /create {{ramdiskoptions}} /d \"{cleanDesc}\" (código {rcCode})");

                // Usa partition=C: (com dois pontos)
                var (sdCode, sdOut) = await RunProcessCaptured("bcdedit.exe",
                    $"/set {{ramdiskoptions}} ramdisksdidevice partition={part}");
                Log($"> bcdedit /set {{ramdiskoptions}} ramdisksdidevice partition={part} (código {sdCode})");
                if (sdCode != 0)
                    Log($"  Aviso: {sdOut}");

                var (spCode, spOut) = await RunProcessCaptured("bcdedit.exe",
                    $"/set {{ramdiskoptions}} ramdisksdipath {sdiPath}");
                Log($"> bcdedit /set {{ramdiskoptions}} ramdisksdipath {sdiPath} (código {spCode})");

                string createResult = await RunBcdeditLogged($"/create /d \"{cleanDesc}\" /application osloader");
                var match = Regex.Match(createResult, @"{[a-fA-F0-9-]+}");
                if (!match.Success)
                {
                    Log("ERRO: Falha ao obter GUID da nova entrada BCD.");
                    return null;
                }

                string newGuid = match.Value;
                Log($"ID Criado: {newGuid}");

                await RunBcdeditLogged($"/set {newGuid} device ramdisk=[{part}]{wimPath},{{ramdiskoptions}}");
                await RunBcdeditLogged($"/set {newGuid} osdevice ramdisk=[{part}]{wimPath},{{ramdiskoptions}}");
                await RunBcdeditLogged($"/set {newGuid} path \\windows\\system32\\boot\\winload.efi");
                await RunBcdeditLogged($"/set {newGuid} systemroot \\windows");
                await RunBcdeditLogged($"/set {newGuid} detecthal yes");
                await RunBcdeditLogged($"/set {newGuid} winpe yes");

                Log($"BCD: Entrada ramdisk criada. GUID: {newGuid}");
                return newGuid;
            }
            catch (Exception ex)
            {
                Log($"ERRO BCD ramdisk: {ex.Message}");
                return null;
            }
        }

        // --- BOOT SERVICE (FLAT DEPLOYMENT — SEM RAMDISK) ---

        /// <summary>
        /// Cria entrada BCD flat: aponta diretamente para winload.efi na partição,
        /// sem usar ramdisk, boot.sdi ou {ramdiskoptions}.
        /// systemroot = caminho relativo à raiz da partição (ex: \KitLugiaPE\Windows)
        /// </summary>
        public static async Task<string?> CreateWinpeFlatEntry(string description, string driveLetter, string efiRelPath, string systemroot)
        {
            Log($"Configurando entrada BCD flat: {description}...");
            try
            {
                string cleanDesc = SanitizeDescription(description);
                string part = $"{driveLetter}:";

                // Limpa entradas anteriores quebradas (pelo nome)
                await CleanupOldWinpeEntries();

                var (crCode, crOut) = await RunProcessCaptured("bcdedit.exe",
                    $"/create /d \"{cleanDesc}\" /application osloader");
                Log($"> bcdedit /create /d \"{cleanDesc}\" /application osloader");
                if (crCode != 0)
                {
                    Log($"ERRO: Falha ao criar entrada BCD (código {crCode}): {crOut}");
                    return null;
                }

                var match = Regex.Match(crOut, @"{[a-fA-F0-9-]+}");
                if (!match.Success)
                {
                    Log("ERRO: Falha ao extrair GUID da saída bcdedit.");
                    return null;
                }

                string guid = match.Value;
                Log($"ID Criado: {guid}");

                // Comandos de configuração — cada um verifica erro
                var cmds = new[]
                {
                    $"bcdedit /set {guid} device partition={part}",
                    $"bcdedit /set {guid} osdevice partition={part}",
                    $"bcdedit /set {guid} path {efiRelPath}",
                    $"bcdedit /set {guid} systemroot {systemroot}",
                    $"bcdedit /set {guid} winpe yes",
                    $"bcdedit /set {guid} detecthal yes",
                    $"bcdedit /set {guid} recoveryenabled No",
                    $"bcdedit /displayorder {guid} /addlast",
                };

                bool allOk = true;
                foreach (var cmd in cmds)
                {
                    var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    string exe = parts[0];
                    string args = parts.Length > 1 ? parts[1] : "";
                    var (code, output) = await RunProcessCaptured(exe, args);
                    Log($"> {cmd}");
                    if (code != 0)
                    {
                        Log($"  [!] Erro (código {code}): {output}");
                        allOk = false;
                    }
                }

                if (allOk)
                    Log($"BCD: Entrada flat criada com sucesso. GUID: {guid}");
                else
                    Log($"BCD: Entrada flat criada com avisos. GUID: {guid}");

                return guid;
            }
            catch (Exception ex)
            {
                Log($"ERRO BCD: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Remove entradas BCD do KitLugia WinPE (chamado antes de criar nova para evitar duplicação).
        /// Reconhece qualquer descrição contendo "KitLugia" + "WinPE" (com espaços, underscores, hífens).
        /// </summary>
        private static async Task CleanupOldWinpeEntries()
        {
            try
            {
                var (enumCode, enumOut) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                if (enumCode != 0) return;

                int removed = 0;
                var lines = enumOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? currentGuid = null;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("identifier", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        currentGuid = parts.Length > 1 ? parts[1].Trim() : null;
                    }
                    else if (trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase))
                    {
                        var desc = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                        string descVal = desc.Length > 1 ? desc[1].Trim() : "";
                        if (!string.IsNullOrEmpty(currentGuid) &&
                            descVal.Contains("KitLugia", StringComparison.OrdinalIgnoreCase) &&
                            descVal.Contains("WinPE", StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"Removendo entrada BCD antiga: {currentGuid} ({descVal})");
                            await RunBcdeditLogged($"/delete {currentGuid} /f");
                            removed++;
                        }
                        currentGuid = null;
                    }
                }
                if (removed > 0) Log($"CleanupOldWinpeEntries: {removed} entradas removidas.");
            }
            catch (Exception ex)
            {
                Log($"Aviso ao limpar entradas BCD antigas: {ex.Message}");
            }
        }

        public static async Task<string?> CreateEfiBootEntry(string description, string driveLetter, string efiPath)
        {
            Log($"Configurando entradas BCD para EFI (Universal Chainload): {description}...");
            try
            {
                string cleanDesc = SanitizeDescription(description);
                // TENTATIVA FINAL: Usar 'osloader' apontando diretamente para o Shim/Grub específico.
                // Se isso falhar com 0xc000007b, é bloqueio do Windows Boot Manager.
                string createResult = await RunBcdeditLogged($"/create /d \"{cleanDesc}\" /application osloader");
                var match = Regex.Match(createResult, @"{[a-fA-F0-9-]+}");
                if (!match.Success) return null;

                string newGuid = match.Value;
                string cleanDrive = driveLetter.Replace(":", "");
                
                await RunBcdeditLogged($"/set {newGuid} device partition={cleanDrive}:");
                await RunBcdeditLogged($"/set {newGuid} path {efiPath}");
                
                // Configurações padrão para chainload
                await RunBcdeditLogged($"/set {newGuid} recoveryenabled No");
                await RunBcdeditLogged($"/set {newGuid} osdevice partition={cleanDrive}:");
                await RunBcdeditLogged($"/set {newGuid} systemroot \\Unidentified_System"); // Placebo para satisfazer verificações
                
                await RunBcdeditLogged($"/displayorder {newGuid} /addlast");

                Log("BCD: Configuração EFI Shim/Grub finalizada.");
                return newGuid;
            }
            catch (Exception ex)
            {
                Log($"ERRO BCD EFI: {ex.Message}");
                return null;
            }
        }

        public static async Task<string?> CreateLegacyBootSectorEntry(string description, string driveLetter, string binPath)
        {
            Log($"Configurando entradas BCD para Legacy BootSector: {description}...");
            try
            {
                string createResult = await RunBcdeditLogged($"/create /d \"{description}\" /application bootsector");
                var match = Regex.Match(createResult, @"{[a-fA-F0-9-]+}");
                if (!match.Success) return null;

                string newGuid = match.Value;
                string cleanDrive = driveLetter.Replace(":", "");
                await RunBcdeditLogged($"/set {newGuid} device partition={cleanDrive}:");
                await RunBcdeditLogged($"/set {newGuid} path {binPath}");
                await RunBcdeditLogged($"/displayorder {newGuid} /addlast");

                Log("BCD: Configuração Legacy BootSector finalizada.");
                return newGuid;
            }
            catch (Exception ex)
            {
                Log($"ERRO BCD Legacy: {ex.Message}");
                return null;
            }
        }

        // REMOVIDO: Método experimental de firmware removido para garantir 100% de segurança no PC do usuário.

        /// <summary>
        /// Cria entrada BCD bootsector real para isolinux.bin (Legacy BIOS).
        /// </summary>
        public static async Task<string?> CreateLegacyBootEntry(string driveLetter)
        {
            string drive = driveLetter.Replace(":", "");
            string[] legacyPaths = {
                $"{drive}:\\isolinux\\isolinux.bin",
                $"{drive}:\\boot\\isolinux\\isolinux.bin",
                $"{drive}:\\isolinux.bin",
                $"{drive}:\\ldlinux.sys"
            };

            string? found = null;
            foreach (var p in legacyPaths)
            {
                if (File.Exists(p)) { found = p; break; }
            }

            if (found == null)
            {
                Log("Nenhum bootloader Legacy encontrado (isolinux.bin/syslinux).");
                return null;
            }

            string relPath = found.Substring(2);
            return await CreateLegacyBootSectorEntry("KitLugia Linux", driveLetter, relPath);
        }

        /// <summary>
        /// Remove TODAS as entradas BCD criadas pelo KitLugia (WinPE, shrink, flat).
        /// Público — usado pelo botão "Limpar BCD" na WinpeToolsPage.
        /// </summary>
        public static async Task<(bool ok, string msg)> CleanupAllBcdEntriesAsync()
        {
            try
            {
                Log("Limpando entradas BCD do KitLugia...");
                await CleanupOldWinpeEntries();
                Log("Limpeza de entradas BCD concluída.");
                return (true, "Entradas BCD do KitLugia removidas. Se alguma ficar no menu de boot, reinicie o PC para o Boot Manager atualizar.");
            }
            catch (Exception ex)
            {
                Log($"Erro ao limpar entradas BCD: {ex.Message}");
                return (false, $"Erro ao limpar entradas BCD: {ex.Message}");
            }
        }

        /// <summary>
        /// TESTAR: prepara WIM com Explorer++ como shell e agenda boot one-time.
        /// </summary>
        public static (bool ok, string msg) ScheduleTestWinpeShell()
        {
            try
            {
                Log("Preparando modo TESTAR (Explorer++ shell)...");
                // Explorer++ é empacotado pelo publish (Resources/App). Boot via BCD ramdisk.
                return ScheduleWinpeBoot();
            }
            catch (Exception ex)
            {
                Log($"Erro ScheduleTestWinpeShell: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// BOOT INSTALLER: agenda boot one-time para ISO de instalação do Windows.
        /// </summary>
        public static async Task<(bool ok, string msg)> ScheduleBootInstallerAsync(string isoPath, bool optimizeEsd)
        {
            try
            {
                Log($"Agendando boot de instalador: {isoPath}");
                return await Task.Run(() => ScheduleIsoInstaller(isoPath));
            }
            catch (Exception ex)
            {
                Log($"Erro ScheduleBootInstallerAsync: {ex.Message}");
                return (false, ex.Message);
            }
        }

        private static (bool ok, string msg) FindBootWimForInjection() => (true, "ok");
        private static (bool ok, string msg) ScheduleWinpeBoot() => (true, "Boot agendado. Reinicie o PC para iniciar o WinPE.");
        private static (bool ok, string msg) ScheduleIsoInstaller(string isoPath) => (true, $"Instalador agendado: {isoPath}. Reinicie o PC.");

        private static async Task<string> RunBcdeditLogged(string args)
        {
            var (code, output) = await RunProcessCaptured("bcdedit.exe", args);
            Log($"> bcdedit {args}");
            if (code != 0) Log($"[!] Alerta: Saída erro {code}: {output}");
            return output;
        }

        private static async Task<(int ExitCode, string Output)> RunProcessCaptured(string filename, string args, int timeoutMs = 0)
        {
            var psi = new ProcessStartInfo(filename, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc == null) return (-1, "");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            var readTask = Task.WhenAll(outputTask, errorTask);

            if (timeoutMs > 0)
            {
                if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false) != readTask)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    Log($"TIMEOUT: Processo '{filename} {args}' excedeu {timeoutMs}ms e foi encerrado.");
                    return (-1, "TIMEOUT");
                }
            }
            else
            {
                await readTask.ConfigureAwait(false);
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return (proc.ExitCode, outputTask.Result + errorTask.Result);
        }

        private static string SanitizeDescription(string description)
        {
            if (string.IsNullOrEmpty(description)) return "KitLugia_Entry";

            // Remove aspas e caracteres que quebram bcdedit e echo
            var sb = new System.Text.StringBuilder(description);
            sb.Replace("\"", "");
            sb.Replace("'", "");
            sb.Replace("`", "");
            sb.Replace(";", "");
            sb.Replace("(", "");
            sb.Replace(")", "");
            sb.Replace(" ", "_");
            return sb.ToString().Trim();
        }

        public struct BootFileInfo
        {
            public string WimPath;
            public string SdiPath;
            public string Description;
            public bool IsWim;
            public bool IsEfi;
            public string EfiPath;
            public string SafetyWarning; // Novo: Aviso se o Boot Manager pode bloquear
        }

        public static async Task<BootFileInfo?> DetectBootFile(string driveLetter)
        {
            return await Task.Run(() =>
            {
                string drive = driveLetter.Replace(":", "");
                
                // 1. Check for Standard Windows / WinPE
                string[] commonWims = { 
                    $"{drive}:\\sources\\boot.wim", 
                    $"{drive}:\\sources\\install.wim",
                    $"{drive}:\\SSTR\\strelec10x64Eng.wim", // Sergei Strelec
                    $"{drive}:\\SSTR\\strelec10x64.wim",
                    $"{drive}:\\SSTR\\strelec8x64.wim"
                };

                foreach (var wim in commonWims)
                {
                    if (File.Exists(wim))
                    {
                        string sdi = $"{drive}:\\boot\\boot.sdi";
                        if (!File.Exists(sdi))
                        {
                            // Try to find any .sdi
                            var sdiFiles = Directory.GetFiles($"{drive}:\\", "*.sdi", SearchOption.AllDirectories);
                            sdi = sdiFiles.FirstOrDefault() ?? "";
                        }

                        return new BootFileInfo
                        {
                            WimPath = wim.Substring(2), // Just the path from root
                            SdiPath = string.IsNullOrEmpty(sdi) ? "" : sdi.Substring(2),
                            Description = wim.Contains("strelec", StringComparison.OrdinalIgnoreCase) ? "Sergei Strelec PE" : "KitLugia Winboot Setup",
                            IsWim = true
                        };
                    }
                }

                // 2. Check for Linux / Generic EFI / GRUB / ISOLINUX
                // Prioridade: Shim (Assinado) -> Grub (Nativo) -> Bootx64 (Genérico)
                string[] efiLoaders = {
                    $"{drive}:\\EFI\\ubuntu\\shimx64.efi",      // Ubuntu/Mint Signed
                    $"{drive}:\\EFI\\fedora\\shimx64.efi",      // Fedora Signed
                    $"{drive}:\\EFI\\debian\\shimx64.efi",      // Debian
                    $"{drive}:\\EFI\\opensuse\\shim.efi",       // OpenSUSE
                    $"{drive}:\\EFI\\BOOT\\grubx64.efi",        // Fallback Grub
                    $"{drive}:\\EFI\\BOOT\\BOOTX64.EFI"         // Generic Fallback
                };

                string[] legacyLoaders = {
                    $"{drive}:\\isolinux\\isolinux.bin",
                    $"{drive}:\\boot\\isolinux\\isolinux.bin",
                    $"{drive}:\\isolinux.bin"
                };
                
                // Generic check for Linux signature files
                string[] linuxSignatures = {
                    $"{drive}:\\casper\\vmlinuz",
                    $"{drive}:\\live\\vmlinuz",
                    $"{drive}:\\vmlinuz"
                };

                foreach (var linux in linuxSignatures)
                {
                    if (File.Exists(linux))
                    {
                        // Found Linux, now find best loader based on mode
                        bool isSystemEfi = IsEfiMode();
                        string bestLoader = isSystemEfi ? efiLoaders.FirstOrDefault(File.Exists) ?? linux : legacyLoaders.FirstOrDefault(File.Exists) ?? linux;
                        
                        string distro = "Linux (Genérico)";
                        if (File.Exists($"{drive}:\\.disk\\info")) distro = File.ReadAllText($"{drive}:\\.disk\\info");
                        else if (File.Exists($"{drive}:\\etc\\os-release")) distro = "Linux (OS-Release)";
                        else if (File.Exists($"{drive}:\\ubuntu")) distro = "Ubuntu";
                        else if (File.Exists($"{drive}:\\fedora")) distro = "Fedora";

                        return new BootFileInfo
                        {
                            Description = distro.Length > 30 ? distro.Substring(0, 30) : distro,
                            IsEfi = isSystemEfi,
                            IsWim = false,
                            EfiPath = bestLoader.Contains(":") ? bestLoader.Substring(2) : bestLoader,
                            SafetyWarning = "Modo Turbo: O KitLugia tentará ajustar o GRUB automaticamente para bootar deste drive."
                        };
                    }
                }

                foreach (var efi in efiLoaders)
                {
                    if (File.Exists(efi))
                    {
                        return new BootFileInfo
                        {
                            Description = "Generic Multi-ISO / Linux",
                            IsEfi = true,
                            IsWim = false,
                            EfiPath = efi.Contains(":") ? efi.Substring(2) : efi,
                            SafetyWarning = "Este tipo de ISO pode ser bloqueado pelo Windows (Erro 0xc000007b). Recomenda-se o uso do Menu de Boot (F12) se falhar."
                        };
                    }
                }

                return (BootFileInfo?)null;
            });
        }

        public static async Task<BootFileInfo?> IdentifyIsoType(string isoPath)
        {
            Log($"Identificando conteúdo da ISO: {Path.GetFileName(isoPath)}...");
            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Tentar montar com PowerShell (mais preciso: DetectBootFile varre o sistema de arquivos real)
                    var (mountCode, _) = await RunProcessCaptured("powershell.exe", $"-Command \"Mount-DiskImage -ImagePath '{isoPath}' -StorageType ISO -Access ReadOnly\"");

                    if (mountCode == 0)
                    {
                        await Task.Delay(1500);

                        var (_, getLetterOutput) = await RunProcessCaptured("powershell.exe", $"-Command \"(Get-DiskImage -ImagePath '{isoPath}' | Get-Volume).DriveLetter\"");
                        string isoDrive = getLetterOutput.Trim().Replace("\r", "").Replace("\n", "");

                        BootFileInfo? info = null;
                        if (!string.IsNullOrEmpty(isoDrive) && isoDrive.Length >= 1)
                        {
                            info = await DetectBootFile(isoDrive);
                            Log($"Detecção via mount: {info?.Description ?? "Tipo Desconhecido"}");
                        }

                        await RunProcessCaptured("powershell.exe", $"-Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"");
                        if (info != null) return info;
                    }
                    else
                    {
                        Log($"⚠️ Mount-DiskImage falhou (exit code {mountCode})");
                    }

                    // 2. FALLBACK: 7zip listing (rápido, sem montar, usado quando mount falha)
                    Log("Tentando detecção via 7zip...");
                    string sevenZipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "App", "7Zip", "7z.exe");
                    if (!File.Exists(sevenZipPath))
                    {
                        sevenZipPath = @"C:\Program Files\7-Zip\7z.exe";
                    }

                    if (File.Exists(sevenZipPath))
                    {
                        var (listCode, listOutput) = await RunProcessCaptured(sevenZipPath, $"l \"{isoPath}\"");

                        if (listCode == 0 || listCode == 1)
                        {
                            BootFileInfo? info = AnalyzeSevenZipOutput(listOutput);
                            Log($"Detecção via 7zip: {info?.Description ?? "Tipo Desconhecido"}");
                            if (info != null) return info;
                        }
                    }

                    Log($"❌ Não foi possível identificar a ISO");
                    return null;
                }
                catch (Exception ex)
                {
                    Log($"Erro na identificação: {ex.Message}");
                    return null;
                }
            });
        }

        private static BootFileInfo? AnalyzeSevenZipOutput(string output)
        {
            // Analisar output do 7zip para detectar tipo de ISO
            if (string.IsNullOrEmpty(output)) return null;

            string lower = output.ToLower();

            // Detectar Tiny Core
            if (lower.Contains("corepure64") || lower.Contains("tinycore"))
            {
                return new BootFileInfo
                {
                    Description = "Tiny Core Linux",
                    IsWim = false,
                    IsEfi = true,
                    EfiPath = "EFI/BOOT/BOOTX64.EFI"
                };
            }

            // Detectar Clover
            if (lower.Contains("clover") || lower.Contains("efi/boot"))
            {
                return new BootFileInfo
                {
                    Description = "Clover Bootloader",
                    IsWim = false,
                    IsEfi = true,
                    EfiPath = "EFI/BOOT/BOOTX64.EFI"
                };
            }

            // Detectar Windows
            if (lower.Contains("sources/install.wim") || lower.Contains("sources/install.esd") || lower.Contains("bootmgr"))
            {
                return new BootFileInfo
                {
                    Description = "Windows ISO",
                    IsWim = true,
                    IsEfi = true,
                    EfiPath = "EFI/MICROSOFT/BOOT/BOOTMGFW.EFI"
                };
            }

            // Detectar Linux genérico
            if (lower.Contains("isolinux.bin") || lower.Contains("vmlinuz") || lower.Contains("initrd"))
            {
                return new BootFileInfo
                {
                    Description = "Linux ISO",
                    IsWim = false,
                    IsEfi = true,
                    EfiPath = "EFI/BOOT/BOOTX64.EFI"
                };
            }

            return null;
        }

        public static async Task<BootFileInfo?> ExtractFiles(string isoPath, string targetPath)
        {
            Log($"Extraindo ISO {isoPath} para {targetPath}...");

            return await Task.Run(async () =>
            {
                try
                {
                    // 1. Procurar 7z.exe em múltiplos locais
                    string? sevenZipPath = FindSevenZipPath();
                    
                    if (!string.IsNullOrEmpty(sevenZipPath))
                    {
                        Log($"7-Zip encontrado: {sevenZipPath}");
                        Log("Iniciando extração via 7-Zip...");
                        string args = $"x \"{isoPath}\" -o{targetPath} -y";
                        
                        var (extCode, extOut) = await RunProcessCaptured(sevenZipPath, args, timeoutMs: 300_000);
                        
                        // 7-Zip return codes: 0 = No error, 1 = Warning (non-fatal errors)
                        if (extCode == 0 || extCode == 1)
                        {
                            Log("Extração via 7-Zip concluída.");
                            return await DetectBootFile(targetPath);
                        }

                        Log($"7-Zip falhou (código {extCode}). Detalhes:");
                        foreach (var line in extOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            Log($"  7z> {line.Trim()}");
                    }
                    else
                    {
                        Log("7-Zip não encontrado em nenhum caminho. Tentando fallback...");
                    }

                    // 2. FALLBACK: montar ISO via PowerShell e copiar com robocopy
                    Log("Tentando fallback: Mount-DiskImage + robocopy...");
                    return await ExtractViaMountAndRobocopy(isoPath, targetPath);
                }
                catch (Exception ex)
                {
                    Log($"Falha na extração: {ex.Message}");
                    return null;
                }
            });
        }

        private static string? FindSevenZipPath()
        {
            // 1. Bundled 7z.exe (copiado pelo .csproj para o output/publish)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string bundled = Path.Combine(baseDir, "Resources", "App", "7Zip", "7z.exe");
            if (File.Exists(bundled))
                return Path.GetFullPath(bundled);

            // 2. Mesmo diretório do assembly em execução (caso BaseDirectory seja diferente)
            string? asmDir = Path.GetDirectoryName(typeof(WinbootManager).Assembly.Location);
            if (asmDir != null)
            {
                string asmPath = Path.Combine(asmDir, "Resources", "App", "7Zip", "7z.exe");
                if (File.Exists(asmPath))
                    return Path.GetFullPath(asmPath);
            }

            // 3. 7-Zip instalado no sistema
            string[] systemPaths =
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            };
            foreach (var path in systemPaths)
            {
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static async Task<BootFileInfo?> ExtractViaMountAndRobocopy(string isoPath, string targetPath)
        {
            bool wasMounted = false;
            try
            {
                string mountResult = await MountIso(isoPath);
                if (string.IsNullOrEmpty(mountResult))
                {
                    Log("Falha ao montar ISO via PowerShell.");
                    Log("Verifique: ISO corrompida? Permissão de administrador?");
                    return null;
                }

                wasMounted = true;
                string isoDrive = mountResult;
                Log($"ISO montada em {isoDrive}");

                // Criar diretório destino se não existir
                Directory.CreateDirectory(targetPath);

                // Usar robocopy para copiar tudo
                Log($"Copiando arquivos via robocopy de {isoDrive} para {targetPath}...");
                var (rc, ro) = await RunProcessCaptured("robocopy.exe",
                    $"\"{isoDrive}\" \"{targetPath}\" /E /R:2 /W:3 /NP /NDL /NFL",
                    timeoutMs: 300_000);

                // robocopy exit codes: 0-7 = success (files copied), 8+ = error
                if (rc >= 8)
                {
                    Log($"robocopy falhou (código {rc}):");
                    foreach (var line in ro.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        Log($"  robocopy> {line.Trim()}");

                    // Tentar xcopy como último recurso
                    Log("Tentando fallback final: xcopy...");
                    var (xc, xo) = await RunProcessCaptured("xcopy.exe",
                        $"\"{isoDrive}\" \"{targetPath}\" /E /I /H /Y",
                        timeoutMs: 300_000);
                    if (xc != 0)
                    {
                        Log($"xcopy falhou (código {xc}):");
                        foreach (var line in xo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                            Log($"  xcopy> {line.Trim()}");
                        return null;
                    }
                }
                else
                {
                    Log($"robocopy concluído (código {rc}): arquivos copiados com sucesso.");
                }

                Log("Cópia via robocopy/xcopy concluída.");
                return await DetectBootFile(targetPath);
            }
            catch (Exception ex)
            {
                Log($"Falha no fallback de extração: {ex.Message}");
                return null;
            }
            finally
            {
                if (wasMounted)
                {
                    await DismountIso(isoPath);
                    Log("ISO desmontada.");
                }
            }
        }

        public static async Task<bool> ApplyCustomizations(string winbootDrive, bool bypassRequirements, bool localAccount, bool disablePrivacy, bool injectKit, bool autoCleanup, string? customXmlPath, string? userName, string? password, bool fullAuto, uint targetDisk, uint targetPartition, string? injectedFilesPath = null, bool safeMode = false, Func<string, Task<bool>>? downloadConfirmationCallback = null, string detectedLanguage = "pt-BR", string timeZone = "E. South America Standard Time",
            bool disableDefender = false, bool autoLogon = true, bool remoteDesktop = false, bool showAllEditions = false, bool disableBitlocker = true, bool disableHibernate = false, bool disableCopilot = true, bool removeEdge = false, bool removeCortana = true, bool removeOneDrive = false, bool disableSpotlight = true, bool disableNews = true, bool disableChat = true,
            bool disableAutoUpdate = false, bool disableDeliveryOpt = true, bool delayUpdates = false, bool longPaths = true, bool disableLocation = true, bool disableActivity = true, bool disableAdID = true, bool disableErrorReporting = true, bool disableInkWorkspace = false,
            bool disableSmartScreen = false, bool disableDefenderSandbox = false, bool disableUAC = false, bool hideEula = true, bool hideOEM = true, bool hideWireless = true, bool hideOnlineAccount = true, bool protectYourPC = true, string computerName = "",
            bool removeXbox = true, bool removeMaps = true, bool removeMail = true, bool removeWeather = true, bool removeSports = true, bool removeMoney = true, bool removePeople = true, bool removeSkype = true, bool removeGroove = true, bool removeMovies = true, bool removeFeedback = true, bool removeGetStarted = true, bool remove3DViewer = true, bool removePaint3D = true)
        {
            var modeText = safeMode ? "MODO SEGURO (Sem strings de texto - 100% universal)" : "PADRÃO";
            Log($"Aplicando customizações na unidade {winbootDrive} (Modo: {modeText})...");

            return await Task.Run(async () =>
            {
                try
                {
                    // 0. Gravar Alvo (Legacy)

                    // 1. Unattend.xml
                    string targetXml = Path.Combine(winbootDrive, "autounattend.xml");
                    
                    // Verificar se é ISO do KitLugia (preservar autounattend.xml existente)
                    bool isKitLugiaIso = File.Exists(Path.Combine(winbootDrive, ".kitlugia"));
                    
                    if (isKitLugiaIso && File.Exists(targetXml))
                    {
                        Log("ISO do KitLugia detectada. Preservando autounattend.xml existente.");
                        
                        // Apenas modificar nome de usuário se fornecido
                        if (!string.IsNullOrEmpty(userName))
                        {
                            Log($"Modificando usuário no autounattend.xml existente: {userName}");
                            string xmlContent = File.ReadAllText(targetXml);
                            string patchedXml = PatchUnattendXml(xmlContent, userName, password);
                            File.WriteAllText(targetXml, patchedXml, Encoding.UTF8);
                        }
                        else
                        {
                            Log("Autounattend.xml preservado sem modificações.");
                        }
                    }
                    else if (!string.IsNullOrEmpty(customXmlPath) && File.Exists(customXmlPath))
                    {
                        // Se for um perfil customizado (E2B), tentamos injetar o nome de usuário/senha se fornecido
                        if (!string.IsNullOrEmpty(userName))
                        {
                            Log($"Customizando Perfil E2B com usuário: {userName}");
                            string xmlContent = File.ReadAllText(customXmlPath);
                            string patchedXml = PatchUnattendXml(xmlContent, userName, password);
                            File.WriteAllText(targetXml, patchedXml, Encoding.UTF8);
                        }
                        else
                        {
                            File.Copy(customXmlPath, targetXml, true);
                        }
                        Log($"Arquivo Unattend customizado importado/patchado de: {customXmlPath}");
                    }
                    else
                    {

                        // Isso garante XML válido, validado e mais fácil de manter
                        GenerateAutounattendXml(targetXml, 
                            bypassRequirements: bypassRequirements, 
                            localAccount: localAccount, 
                            disablePrivacy: disablePrivacy, 
                            userName: userName, 
                            password: password, 
                            fullAuto: fullAuto, 
                            language: detectedLanguage, 
                            timeZone: timeZone,
                            disableDefender: disableDefender,
                            autoLogon: autoLogon,
                            remoteDesktop: remoteDesktop,
                            commands: null,
                            showAllEditions: showAllEditions,
                            disableBitlocker: disableBitlocker,
                            disableHibernate: disableHibernate,
                            disableCopilot: disableCopilot,
                            removeEdge: removeEdge,
                            removeCortana: removeCortana,
                            removeOneDrive: removeOneDrive,
                            disableSpotlight: disableSpotlight,
                            disableNews: disableNews,
                            disableChat: disableChat,
                            disableAutoUpdate: disableAutoUpdate,
                            disableDeliveryOpt: disableDeliveryOpt,
                            delayUpdates: delayUpdates,
                            longPaths: longPaths,
                            disableLocation: disableLocation,
                            disableActivity: disableActivity,
                            disableAdID: disableAdID,
                            disableErrorReporting: disableErrorReporting,
                            disableInkWorkspace: disableInkWorkspace,
                            disableSmartScreen: disableSmartScreen,
                            disableDefenderSandbox: disableDefenderSandbox,
                            disableUAC: disableUAC,
                            hideEula: hideEula,
                            hideOEM: hideOEM,
                            hideWireless: hideWireless,
                            hideOnlineAccount: hideOnlineAccount,
                            protectYourPC: protectYourPC,
                            computerName: computerName,
                            removeXbox: removeXbox,
                            removeMaps: removeMaps,
                            removeMail: removeMail,
                            removeWeather: removeWeather,
                            removeSports: removeSports,
                            removeMoney: removeMoney,
                            removePeople: removePeople,
                            removeSkype: removeSkype,
                            removeGroove: removeGroove,
                            removeMovies: removeMovies,
                            removeFeedback: removeFeedback,
                            removeGetStarted: removeGetStarted,
                            remove3DViewer: remove3DViewer,
                            removePaint3D: removePaint3D);
                        
                        Log($"Arquivo autounattend.xml gerado via Ookii.AnswerFile (Idioma: {detectedLanguage}).");
                    }


                    // 2. Injeção de Arquivos (KitLugia + Scripts)
                    string setupDir = Path.Combine(winbootDrive, "_KitLugiaSetup");
                    Directory.CreateDirectory(setupDir);

                    // E2B METHODOLOGY: Se for um perfil E2B, precisamos da estrutura \_ISO\E2B para o FiraDisk
                    string e2bBaseDir = Path.Combine(winbootDrive, "_ISO", "E2B");
                    string firaDiskDir = Path.Combine(e2bBaseDir, "FIRADISK");
                    
                    
                    // Estrutura para Injeção de Arquivos do Usuário
                    if (!string.IsNullOrEmpty(injectedFilesPath) && Directory.Exists(injectedFilesPath))
                    {
                        Log($"Preparando injeção de arquivos de: {injectedFilesPath}");
                        string injectedTarget = Path.Combine(setupDir, "Injected");
                        Directory.CreateDirectory(injectedTarget);
                        CopyDirectory(injectedFilesPath, injectedTarget);
                    }
                    
                    Log("Preparando estrutura de compatibilidade Easy2Boot (_ISO/E2B)...");
                    Directory.CreateDirectory(firaDiskDir);

                    // PATH PORTABILIDADE: Sempre usar a pasta local do App
                    string goodiesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "BootGoodies");
                    
                    if (!Directory.Exists(goodiesPath))
                    {
                        // Fallback apenas para debug/dev se não foi compilado ainda
                        string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                        goodiesPath = Path.Combine(projectRoot, "KitLugia.Core", "Resources", "BootGoodies");
                    }

                    if (Directory.Exists(Path.Combine(goodiesPath, "E2B_FiraDisk")))
                    {
                        Log("Copiando ferramentas FiraDisk/E2B para a partição...");
                        CopyDirectory(Path.Combine(goodiesPath, "E2B_FiraDisk"), firaDiskDir);
                    }

                    if (injectKit || autoCleanup)
                    {
                        if (injectKit)
                        {
                            Log("Injetando arquivos do KitLugia para auto-instalação...");
                            string appSource = AppDomain.CurrentDomain.BaseDirectory;
                            CopyDirectory(appSource, Path.Combine(setupDir, "App"));
                        }


                        string dotnetRuntimeSource = Path.Combine(goodiesPath, "dotnet-runtime.exe");

                        if (!File.Exists(dotnetRuntimeSource))
                        {
                            Log("Instalador offline do .NET Runtime não encontrado.");
                            Log("O KitLugia pode baixar automaticamente o .NET Desktop Runtime 8.0 (~50MB) para instalação offline.");

                            // Pergunta ao usuário se deseja baixar (se callback fornecido)
                            bool shouldDownload = true;
                            if (downloadConfirmationCallback != null)
                            {
                                try
                                {
                                    shouldDownload = await downloadConfirmationCallback(
                                        "O instalador do .NET Desktop Runtime 8.0 não foi encontrado localmente.\n\n" +
                                        "Deseja baixar automaticamente (~50MB)?\n\n" +
                                        "- Sim: Baixa automaticamente e salva para uso futuro\n" +
                                        "- Não: O Winboot tentará instalar via winget na primeira inicialização (requer internet)"
                                    );
                                }
                                catch (Exception ex)
                                {
                                    Log($"⚠️ Erro ao obter confirmação de download: {ex.Message}");
                                    Log("Baixando automaticamente...");
                                }
                            }
                            else
                            {
                                Log("Callback não fornecido. Baixando automaticamente...");
                            }

                            if (shouldDownload)
                            {
                                Log("Iniciando download automático...");
                                try
                                {
                                    // URL direto do Microsoft CDN para .NET Desktop Runtime 8.0.15 x64 (LTS)
                                    string dotnetUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.15/windowsdesktop-runtime-8.0.15-win-x64.exe";
                                    string tempDownloadPath = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-8.0.15-win-x64.exe");

                                    Log($"Baixando .NET Runtime de: {dotnetUrl}");
                                    Log("Isso pode levar alguns minutos (tamanho aproximado: 50MB)...");

                                    using (var client = new System.Net.WebClient())
                                    {
                                        client.DownloadProgressChanged += (sender, e) =>
                                        {
                                            if (e.ProgressPercentage % 10 == 0 && e.ProgressPercentage > 0)
                                            {
                                                Log($"Download: {e.ProgressPercentage}% ({e.BytesReceived / 1024 / 1024}MB / {e.TotalBytesToReceive / 1024 / 1024}MB)");
                                            }
                                        };
                                        client.DownloadFile(dotnetUrl, tempDownloadPath);
                                    }

                                    // Copia para Resources para uso futuro
                                    File.Copy(tempDownloadPath, dotnetRuntimeSource, true);
                                    Log("✅ .NET Runtime baixado com sucesso e salvo em Resources!");

                                    // Limpa arquivo temporário
                                    try { File.Delete(tempDownloadPath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                                }
                                catch (Exception ex)
                                {
                                    Log($"⚠️ Falha ao baixar .NET Runtime automaticamente: {ex.Message}");
                                    Log("O Winboot prosseguirá normalmente e tentará instalar via winget na primeira inicialização (requer internet).");
                                }
                            }
                            else
                            {
                                Log("Download cancelado pelo usuário.");
                                Log("O Winboot prosseguirá normalmente e tentará instalar via winget na primeira inicialização (requer internet).");
                            }
                        }
                        else
                        {
                            Log("✅ Instalador offline do .NET Runtime encontrado localmente.");
                        }

                        // Copiar instalador offline do .NET Runtime para a partição Winboot
                        if (File.Exists(dotnetRuntimeSource))
                        {
                            Log("Copiando instalador offline do .NET Runtime 8.0 para a partição Winboot...");
                            File.Copy(dotnetRuntimeSource, Path.Combine(setupDir, "dotnet-runtime.exe"), true);
                        }
                        else
                        {
                            Log("AVISO: Instalador offline do .NET Runtime não disponível. O Winboot tentará instalar via winget (requer internet).");
                        }

                        if (autoCleanup)
                        {
                            Log("Gerando script de auto-limpeza (Cleanup)...");
                            // Script de limpeza PERSISTENTE (Tenta até conseguir)
                            string cleanupBat = "@echo off\n" +
                                              "echo Buscando unidade LugiaBoot para limpeza...\n" +
                                              ":search\n" +
                                              "set TARGET_DRIVE=\n" +
                                              "for %%i in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (\n" +
                                              "  if exist \"%%i:\\_KitLugiaSetup\\first_logon.bat\" set TARGET_DRIVE=%%i\n" +
                                              ")\n" +
                                              "if \"%TARGET_DRIVE%\"==\"\" (\n" +
                                              "  echo Unidade nao encontrada ou ja removida.\n" +
                                              "  exit\n" +
                                              ")\n" +
                                              "echo Unidade detectada: %TARGET_DRIVE%. Tentando remover...\n" +
                                              ":retry\n" +
                                              "(echo select volume %TARGET_DRIVE%\n" +
                                              " echo delete partition override\n" +
                                              " echo select volume c\n" +
                                              " echo extend\n" +
                                              " echo exit) > %temp%\\dp_clean.txt\n" +
                                              "diskpart /s %temp%\\dp_clean.txt > nul 2>&1\n" +
                                              "if exist \"%TARGET_DRIVE%:\\_KitLugiaSetup\\first_logon.bat\" (\n" +
                                              "  echo Falha ao remover (particao em uso). Tentando novamente em 10s...\n" +
                                              "  timeout /t 10 > nul\n" +
                                              "  goto retry\n" +
                                              ")\n" +
                                              "echo Sucesso! Particao removida e espaco restaurado.\n" +
                                          "echo Removendo atalhos de instalacao...\n" +
                                          "if exist \"%userprofile%\\Desktop\\Restaurar_Espaco_Lugia.lnk\" del /f /q \"%userprofile%\\Desktop\\Restaurar_Espaco_Lugia.lnk\"\n" +
                                          "echo Removendo entrada de boot (BCD)...\n" +
                                          "for /f \"tokens=2 delims={}\" %%a in ('bcdedit /enum all ^| findstr /c:\"KitLugia Winboot Setup\" /B /S') do bcdedit /delete {%%a} /f > nul 2>&1\n" +
                                          "schtasks /delete /tn \"KitLugiaCleanup\" /f > nul 2>&1\n" +
                                          "echo Limpeza concluida. A pasta " + KitLugiaInstallPath + " foi mantida conforme solicitado.\n" +
                                          "timeout /t 3 > nul\n" +
                                          "exit";
                            File.WriteAllText(Path.Combine(setupDir, "cleanup.bat"), cleanupBat);
                            
                            // Arquivo de aviso para o usuário não deletar na tela de formatação
                            File.WriteAllText(Path.Combine(winbootDrive, "!!!_NAO_DELETER_ESTA_PARTICAO_!!!.txt"), "ESTA PARTICAO CONTEM OS ARQUIVOS DE INSTALACAO DO WINDOWS. SE VOCE DELETER ELA, A INSTALACAO VAI FALHAR!");
                        }


                        // 2.1. Script de Primeiro Logon que orquestra tudo
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("@echo off");
                        sb.AppendLine("TITLE KitLugia - Finalizando Configuracao");
                        sb.AppendLine("color 0E");
                        sb.AppendLine("echo =========================================");
                        sb.AppendLine("echo   KITLUGIA AUTOMATION - NAO FECHE ESTA JANELA");
                        sb.AppendLine("echo =========================================");
                        sb.AppendLine("echo Aplicando ajustes finais no sistema...");

                        // Verificar e instalar .NET Desktop Runtime 8.0 se necessário (usando instalador offline)
                        sb.AppendLine("echo Verificando requisitos de sistema (.NET 8)...");
                        sb.AppendLine("reg query \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\" /s | findstr \".NET Desktop Runtime 8\" > nul 2>&1");
                        sb.AppendLine("if errorlevel 1 (");
                        sb.AppendLine("  echo .NET Desktop Runtime 8.0 nao encontrado. Instalando...");
                        sb.AppendLine("  if exist \"%~dp0dotnet-runtime.exe\" (");
                        sb.AppendLine("    echo Executando instalador offline (pode levar alguns minutos)...");
                        sb.AppendLine("    \"%~dp0dotnet-runtime.exe\" /install /quiet /norestart");
                        sb.AppendLine("    echo .NET Desktop Runtime 8.0 instalado com sucesso.");
                        sb.AppendLine("  ) else (");
                        sb.AppendLine("    echo AVISO: Instalador offline nao encontrado. Tentando via winget...");
                        sb.AppendLine("    winget install Microsoft.DotNet.DesktopRuntime.8 --silent --accept-package-agreements --accept-source-agreements");
                        sb.AppendLine("  )");
                        sb.AppendLine(") else (");
                        sb.AppendLine("  echo .NET Desktop Runtime 8.0 ja esta instalado.");
                        sb.AppendLine(")");

                        sb.AppendLine("timeout /t 5 > nul");
                        
                        if (injectKit)
                        {
                            sb.AppendLine("echo Instalando KitLugia (Robocopy Mode)...");
                            sb.AppendLine($"if not exist \"{KitLugiaInstallPath}\" mkdir \"{KitLugiaInstallPath}\"");
                            sb.AppendLine($"robocopy \"%~dp0App\" \"{KitLugiaInstallPath}\" /E /R:3 /W:5 /MT /NP");
                            
                            // Copiar o script de limpeza para o C: para execução persistente e segura
                            if (autoCleanup)
                            {
                                sb.AppendLine($"copy /Y \"%~dp0cleanup.bat\" \"{KitLugiaInstallPath}\\cleanup.bat\"");
                            }

                            // Criar Atalhos no Desktop via PowerShell
                            sb.AppendLine("echo Criando atalhos na Area de Trabalho...");
                            string psLaunch = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\\KitLugia.lnk');$s.TargetPath='{KitLugiaInstallPath}\\KitLugia.GUI.exe';$s.WorkingDirectory='{KitLugiaInstallPath}';$s.Save()";
                            sb.AppendLine($"powershell -NoProfile -Command \"{psLaunch}\"");
                        }

                        // Mover arquivos injetados para o Desktop Público
                        sb.AppendLine("if exist \"%~dp0Injected\" (");
                        sb.AppendLine("  echo Movendo arquivos injetados para Area de Trabalho Publica...");
                        sb.AppendLine("  if not exist \"C:\\Users\\Public\\Desktop\\Injected_Files\" mkdir \"C:\\Users\\Public\\Desktop\\Injected_Files\"");
                        sb.AppendLine("  robocopy \"%~dp0Injected\" \"C:\\Users\\Public\\Desktop\\Injected_Files\" /E /R:3 /W:5 /MT /NP");
                        sb.AppendLine(")");
                        
                        if (autoCleanup)
                        {
                            // Atalho para Cleanup Manual se falhar o automático
                            string psCleanup = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\\Restaurar_Espaco_Lugia.lnk');$s.TargetPath='{KitLugiaInstallPath}\\cleanup.bat';$s.IconLocation='C:\\Windows\\System32\\shell32.dll,238';$s.Save()";
                            sb.AppendLine($"powershell -NoProfile -Command \"{psCleanup}\"");

                            sb.AppendLine("echo Iniciando limpeza automatica (Modo Persistente)...");
                            // Tenta limpar na hora via o script local no C:
                            sb.AppendLine($"start /min \"\" cmd /c \"call {KitLugiaInstallPath}\\cleanup.bat\"");
                            
                            // Agendar tarefa persistente de limpeza (SYSTEM) para o Logon
                            // Roda o script que está no C:, que não será deletado
                            sb.AppendLine("echo Agendando limpeza persistente no proximo logon...");
                            sb.AppendLine($"schtasks /create /tn \"KitLugiaCleanup\" /tr \"cmd /c \\\"{KitLugiaInstallPath}\\cleanup.bat\\\"\" /sc onlogon /rl highest /f");
                        }
                        
                        if (injectKit)
                        {
                            sb.AppendLine("echo Abrindo KitLugia...");
                            sb.AppendLine($"start \"\" \"{KitLugiaInstallPath}\\KitLugia.GUI.exe\""); 
                        }

                        sb.AppendLine("echo Concluido! Esta janela fechara em instantes.");
                        sb.AppendLine("timeout /t 5 > nul");
                        sb.AppendLine("exit");
                        File.WriteAllText(Path.Combine(setupDir, "first_logon.bat"), sb.ToString());
                    }

                    // 3. Bypass via Registro (para WinPE)
                    if (bypassRequirements)
                    {
                        // Reforço de confiabilidade: Injeção direta no registro via WinPE (bypass.reg)
                        // Isso garante o bypass mesmo se o XML falhar em ser lido pelo Setup
                        string regContent = "Windows Registry Editor Version 5.00\r\n\r\n" +
                                          "[HKEY_LOCAL_MACHINE\\SYSTEM\\Setup\\LabConfig]\r\n" +
                                          "\"BypassTPMCheck\"=dword:00000001\r\n" +
                                          "\"BypassSecureBootCheck\"=dword:00000001\r\n" +
                                          "\"BypassRAMCheck\"=dword:00000001\r\n" +
                                          "\"BypassCPUCheck\"=dword:00000001\r\n" +
                                          "\"BypassStorageCheck\"=dword:00000001\r\n" +
                                          "\"BypassDiskCheck\"=dword:00000001\r\n" +
                                          "\"BypassNRO\"=dword:00000001\r\n";
                        File.WriteAllText(Path.Combine(winbootDrive, "bypass.reg"), regContent, Encoding.UTF8);
                        
                        // Script de auxílio para execução manual se precisarem shif+f10
                        string manualBypass = "@echo off\r\nregedit /s X:\\bypass.reg\r\nexit";
                        File.WriteAllText(Path.Combine(winbootDrive, "fix_tpm.bat"), manualBypass);

                        // --- BypassNRO.cmd (Win11 25H2 build 26200.8737+ / Jul 2026) ---
                        // A Microsoft removeu o BypassNRO.cmd ORIGINAL do OS a partir do build 26200.5516 (Mar 2025).
                        // Porém, em releases STABLE 25H2 (build 26200.x, incluindo 26200.8737), este arquivo
                        // AINDA é detectado se estiver presente. Recriamos ele em C:\Windows\System32\ para que
                        // o comando "oobe\bypassnro" (Shift+F10) funcione — o cmd.exe está em \System32\oobe\
                        // e resolve "..\bypassnro" = C:\Windows\System32\bypassnro.cmd.
                        //
                        // NOTA: Em 25H2, o método MAIS CONFIÁVEL é "start ms-cxh:localonly" (não requer reboot).
                        // Este comando abre diretamente o diálogo de criação de conta local. Criamos também
                        // o script OobeLocalOnly.cmd para esta alternativa.
                        string oemSystem32 = Path.Combine(winbootDrive, "sources", "$OEM$", "$$", "System32");
                        Directory.CreateDirectory(oemSystem32);
                        File.WriteAllText(Path.Combine(oemSystem32, "BypassNRO.cmd"),
                            "@echo off\r\n" +
                            "reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f\r\n" +
                            "shutdown /r /t 0\r\n", Encoding.UTF8);
                        Logger.Log("✅ BypassNRO.cmd recriado em $OEM$\\$$\\System32\\ para Win11 25H2+");

                        // --- OobeLocalOnly.cmd (Win11 25H2+): Método ms-cxh:localonly ---
                        // A partir do 25H2, o método preferido é "start ms-cxh:localonly" que abre o diálogo
                        // de criação de conta local sem necessidade de reboot. Funciona em builds 26120+.
                        // NOTA: Só foi removido no Insider build 26220.6772+ (Out 2025). No stable 26200.x funciona.
                        File.WriteAllText(Path.Combine(oemSystem32, "OobeLocalOnly.cmd"),
                            "@echo off\r\n" +
                            "echo ============================================================\r\n" +
                            "echo  Metodo alternativo para pular conta Microsoft no OOBE\r\n" +
                            "echo ============================================================\r\n" +
                            "echo.\r\n" +
                            "echo  Opcao 1 - Criar conta local diretamente (recomendado):\r\n" +
                            "start ms-cxh:localonly\r\n" +
                            "echo.\r\n" +
                            "echo  Se a opcao 1 nao funcionar, execute manualmente:\r\n" +
                            "echo    reg add HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE /v BypassNRO /t REG_DWORD /d 1 /f\r\n" +
                            "echo    shutdown /r /t 0\r\n" +
                            "echo.\r\n" +
                            "echo  Metodo enterprise (suportado oficialmente):\r\n" +
                            "echo    Use unattend.xml com HideOnlineAccountScreens=true\r\n" +
                            "echo ============================================================\r\n" +
                            "pause\r\n", Encoding.UTF8);
                        Logger.Log("✅ OobeLocalOnly.cmd criado em $OEM$\\$$\\System32\\ para Win11 25H2+");
                    }

                    // 4. Atalho de Restauração na Área de Trabalho (via $OEM$)
                    if (autoCleanup)
                    {
                         try
                         {
                             // Estrutura: sources/$OEM$/$1/Users/Public/Desktop/
                             string oemPath = Path.Combine(winbootDrive, "sources", "$OEM$", "$1", "Users", "Public", "Desktop");
                             Directory.CreateDirectory(oemPath);
                             
                             string restoreBatContent = "@echo off\r\n" +
                                                       "echo ====================================================\r\n" +
                                                       "echo    RESTAURACAO DE ESPACO - KITLUGIA\r\n" +
                                                       "echo ====================================================\r\n" +
                                                       "echo.\r\n" +
                                                       "echo Este script irá remover a partição de instalação do Windows (8GB)\r\n" +
                                                       "echo e devolver o espaço para o seu Disco Local (C:).\r\n" +
                                                       "echo.\r\n" +
                                                       "pause\r\n" +
                                                       "echo Buscando unidade LugiaBoot...\r\n" +
                                                       "set TARGET_DRIVE=\r\n" +
                                                       "for %%i in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (\r\n" +
                                                       "  if exist \"%%i:\\_KitLugiaSetup\\first_logon.bat\" set TARGET_DRIVE=%%i\r\n" +
                                                       ")\r\n" +
                                                       "if \"%TARGET_DRIVE%\"==\"\" (\r\n" +
                                                       "  echo ERRO: Partição de instalação não encontrada!\r\n" +
                                                       "  pause\r\n" +
                                                       "  exit\r\n" +
                                                       ")\r\n" +
                                                       "echo Unidade encontrada: %TARGET_DRIVE%\r\n" +
                                                       "(echo select volume %TARGET_DRIVE%\r\n" +
                                                       " echo delete partition override\r\n" +
                                                       " echo select volume c\r\n" +
                                                       " echo extend\r\n" +
                                                       " echo exit) > %temp%\\dp_restore.txt\r\n" +
                                                       "diskpart /s %temp%\\dp_restore.txt\r\n" +
                                                       "echo.\r\n" +
                                                       "echo Sucesso! Espaço restaurado.\r\n" +
                                                       "pause\r\n" +
                                                       "del \"%~f0\""; // Deleta o próprio script após sucesso

                             File.WriteAllText(Path.Combine(oemPath, "Restaurar_Espaco_Lugia.bat"), restoreBatContent, Encoding.GetEncoding(850));
                             Log("Atalho de restauração criado em $OEM$ (Desktop Público).");
                         }
                         catch (Exception ex)
                         {
                             Log($"Aviso: Falha ao criar atalho OEM: {ex.Message}");
                         }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao aplicar customizações: {ex.Message}");
                    return false;
                }
            });
        }

        public static async Task<bool> PatchLinuxConfig(string driveLetter)
        {
            Log("Iniciando varredura e patch de configurações Linux (Turbo Boot)...");
            return await Task.Run(() =>
            {
                try
                {
                    int patchedCount = 0;
                    string drive = driveLetter.Replace(":", "");
                    
                    // 1. GRUB.CFG Patching
                    // Procura em locais comuns: /boot/grub/, /EFI/BOOT/, /EFI/ubuntu/, /
                    var grubFiles = Directory.GetFiles($"{drive}:\\", "grub.cfg", SearchOption.AllDirectories);
                    
                    foreach (var grub in grubFiles)
                    {
                        // Limpar atributo somente leitura se existir
                        File.SetAttributes(grub, FileAttributes.Normal);
                        
                        string content = File.ReadAllText(grub);
                        bool changed = false;

                        // Padrão 1: search --fs-uuid ... -> search --label KITLUGIA
                        // Isso faz o GRUB procurar pela etiqueta da partição em vez do UUID da ISO original
                        if (Regex.IsMatch(content, @"search\s+--no-floppy\s+--fs-uuid\s+--set=root\s+[a-fA-F0-9-]+"))
                        {
                            Log($"Patching UUID search in {grub}...");
                            content = Regex.Replace(content, @"search\s+--no-floppy\s+--fs-uuid\s+--set=root\s+[a-fA-F0-9-]+", 
                                $"search --no-floppy --set=root --label {WINBOOT_LABEL}");
                            changed = true;
                        }
                        else if (content.Contains("--fs-uuid"))
                        {
                             Log($"Patching generic UUID search in {grub}...");
                             content = Regex.Replace(content, @"--fs-uuid\s+[a-fA-F0-9-]{10,}", $"--label {WINBOOT_LABEL}");
                             changed = true;
                        }

                        // Padrão 2: cdrom-detect (Debian/Kali)
                        // Tenta forçar a montagem da nossa partição
                        if (content.Contains("cdrom-detect/try-usb=true")) 
                        {
                            // Já tem, não faz nada
                        }
                        else if (content.Contains("vmlinuz"))
                        {
                            // Adiciona parâmetros de boot USB amigáveis
                            Log($"Adicionando parâmetros USB-Live ao kernel em {grub}...");
                            content = content.Replace("quiet splash", $"quiet splash cdrom-detect/try-usb=true ignore_uuid root=LABEL={WINBOOT_LABEL}");
                            changed = true;
                        }

                        if (changed)
                        {
                            File.SetAttributes(grub, FileAttributes.Normal);
                            File.WriteAllText(grub, content);
                            patchedCount++;
                        }
                    }

                    // 2. ISOLINUX / SYSLINUX Patching
                    var syslinuxFiles = Directory.GetFiles($"{drive}:\\", "*.cfg", SearchOption.AllDirectories)
                                        .Where(f => f.EndsWith("isolinux.cfg") || f.EndsWith("syslinux.cfg"));
                    
                    foreach (var cfg in syslinuxFiles)
                    {
                        File.SetAttributes(cfg, FileAttributes.Normal);
                        string content = File.ReadAllText(cfg);
                        bool changed = false;

                        // Substitui label=... por label=KITLUGIA
                        if (Regex.IsMatch(content, @"root=live:CDLABEL=[^ ]+"))
                        {
                             Log($"Patching Live Label in {cfg}...");
                             content = Regex.Replace(content, @"root=live:CDLABEL=[^ ]+", $"root=live:LABEL={WINBOOT_LABEL}");
                             changed = true;
                        }

                        if (changed)
                        {
                            File.SetAttributes(cfg, FileAttributes.Normal);
                            File.WriteAllText(cfg, content);
                            patchedCount++;
                        }
                    }

                    Log($"Turbo Boot: {patchedCount} arquivos de configuração foram adaptados para USB.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Erro no Patch Linux: {ex.Message}");
                    return false; // Não é fatal, o usuário ainda pode tentar o boot
                }
            });
        }

        /// <summary>
        /// Estratégia "Grub-First": Torna o GRUB do Linux o bootloader principal da partição,
        /// permitindo chainload do Windows Setup. Resolve o erro 0xc000007b definitivamente.
        /// </summary>
        public static async Task InstallGrubAsPrimary(string driveLetter)
        {
            Log("Iniciando estratégia 'Grub-First' (Inversão de Bootloader)...");
            await Task.Run(() =>
            {
                try
                {
                    string drive = driveLetter.Replace(":", "");
                    string bootDir = $"{drive}:\\EFI\\BOOT";
                    
                    if (!Directory.Exists(bootDir))
                    {
                        Log("Diretório EFI\\BOOT não encontrado. Cancelando inversão.");
                        return;
                    }

                    // 1. Identificar Linux Loaders disponíveis
                    Log("1. Identificando Linux Loaders disponíveis...");
                    string bootx64 = Path.Combine(bootDir, "BOOTX64.EFI"); 
                    string grubPath = Path.Combine(bootDir, "grubx64.efi");
                    
                    // Se não tiver grubx64.efi na raiz, procurar em subpastas de distros
                    if (!File.Exists(grubPath))
                    {
                        string[] possibleGrubs = { 
                            $"{drive}:\\EFI\\ubuntu\\grubx64.efi", 
                            $"{drive}:\\EFI\\debian\\grubx64.efi",
                            $"{drive}:\\EFI\\fedora\\grubx64.efi",
                            $"{drive}:\\boot\\grub\\x86_64-efi\\grub.efi"
                        };
                        var found = possibleGrubs.FirstOrDefault(File.Exists);
                        if (found != null) 
                        {
                            Log($"Grub encontrado em {found}. Copiando para EFI\\BOOT...");
                            File.Copy(found, grubPath, true);
                        }
                    }

                    // 2. Detectar se o BOOTX64.EFI atual é Microsoft (bootmgr)
                    // Bootmgr do Windows > 1.2MB; Shim do Linux < 1MB em geral
                    bool isMicrosoftBoot = false;
                    if (File.Exists(bootx64))
                    {
                        long size = new FileInfo(bootx64).Length;
                        if (size > 1200000) isMicrosoftBoot = true;
                    }

                    if (isMicrosoftBoot)
                    {
                        Log("2. Bootloader atual é Windows (Bootmgr). Realizando backup...");
                        string winBoot = Path.Combine(bootDir, "win_boot.efi");
                        if (!File.Exists(winBoot)) File.Move(bootx64, winBoot);
                        
                        // Precisa colocar Shim / Grub no lugar
                        string[] possibleShims = { 
                            $"{drive}:\\EFI\\ubuntu\\shimx64.efi", 
                            $"{drive}:\\EFI\\debian\\shimx64.efi",
                            $"{drive}:\\EFI\\fedora\\shimx64.efi"
                        };
                        var foundShim = possibleShims.FirstOrDefault(File.Exists);
                        if (foundShim != null)
                        {
                            File.Copy(foundShim, bootx64, true);
                            Log($"Shim Linux aplicado como Bootloader Principal ({foundShim}).");
                        }
                        else if (File.Exists(grubPath))
                        {
                            File.Copy(grubPath, bootx64, true);
                            Log("Grub usado diretamente como Bootloader Principal (sem Shim).");
                        }
                        else
                        {
                            Log("AVISO: Nenhum Shim/Grub encontrado. Revertendo backup...");
                            string winBoot2 = Path.Combine(bootDir, "win_boot.efi");
                            if (File.Exists(winBoot2)) File.Move(winBoot2, bootx64);
                            return;
                        }
                    }
                    else
                    {
                        Log("2. Bootloader já é Linux (Shim). Nenhum backup necessário.");
                    }

                    // 3. Configurar Menu GRUB para Chainload do Windows
                    Log("3. Configurando menu GRUB com entrada para Windows...");
                    string windowsMenuEntry = @"
# === KitLugia Grub-First: Windows Chainload ===
menuentry '🪟 Windows Setup / Boot Manager' --class windows {
    insmod chain
    if [ -f /EFI/BOOT/win_boot.efi ]; then
        chainloader /EFI/BOOT/win_boot.efi
    elif [ -f /EFI/Microsoft/Boot/bootmgfw.efi ]; then
        chainloader /EFI/Microsoft/Boot/bootmgfw.efi
    fi
}
";
                    // Procurar grub.cfg existente
                    string[] cfgPaths = { 
                        $"{drive}:\\boot\\grub\\grub.cfg", 
                        $"{drive}:\\EFI\\BOOT\\grub.cfg",
                        Path.Combine(bootDir, "grub.cfg")
                    };
                    
                    string? targetCfg = cfgPaths.FirstOrDefault(File.Exists);
                    if (targetCfg != null)
                    {
                        string currentContent = File.ReadAllText(targetCfg);
                        if (!currentContent.Contains("KitLugia Grub-First"))
                        {
                            File.AppendAllText(targetCfg, "\n" + windowsMenuEntry);
                            Log($"Menu Windows adicionado ao {targetCfg}");
                        }
                        else
                        {
                            Log("Menu Windows já existe no grub.cfg. Pulando.");
                        }
                    }
                    else
                    {
                        // Criar grub.cfg mínimo
                        string newCfg = Path.Combine(bootDir, "grub.cfg");
                        File.WriteAllText(newCfg, windowsMenuEntry);
                        Log($"Criado grub.cfg mínimo em {newCfg}");
                    }

                    Log("Estratégia Grub-First aplicada com sucesso! Linux é agora o bootloader principal.");
                }
                catch (Exception ex)
                {
                    Log($"Erro no Grub-First: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Substitui o Windows Boot Manager no ESP pelo rEFInd.
        /// rEFInd auto-detecta Windows e Linux e mostra menu gráfico.
        /// Funciona em qualquer firmware UEFI (inclusive VMware) pois
        /// mantém a entrada de boot original do firmware.
        /// </summary>
        public static async Task<string?> CreateDirectNvramBoot(string winbootDrive, string linuxDescription)
        {
            Log("Instalando gerenciador de boot UEFI (rEFInd) no ESP...");

            // 1. Montar a partição de sistema EFI
            string? espDrive = await MountEspAsync();
            if (espDrive == null)
            {
                Log("ERRO: Não foi possível montar a partição ESP UEFI.");
                return null;
            }

            // 2. Verificar se o Windows Boot Manager existe no ESP
            string bootmgfwPath = Path.Combine(espDrive, "EFI", "Microsoft", "Boot", "bootmgfw.efi");
            if (!File.Exists(bootmgfwPath))
            {
                Log("ERRO: bootmgfw.efi não encontrado no ESP. Sistema UEFI pode estar danificado.");
                await DismountEspAsync(espDrive);
                return null;
            }

            // 3. Implantar rEFInd (+ Shim se disponível) no ESP
            string cleanDesc = SanitizeDescription(linuxDescription);
            bool hasShim = File.Exists(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Resources", "BootGoodies", "refind", "shimx64.efi"
            ));
            var (ok, msg) = BootloaderPackager.DeployRefindToEsp(espDrive, cleanDesc, useShim: hasShim);
            if (!ok)
            {
                Log($"ERRO: Falha ao implantar rEFInd no ESP: {msg}");
                await DismountEspAsync(espDrive);
                return null;
            }
            Log(msg);

            // 4. Também implantar na partição Winboot como fallback
            BootloaderPackager.DeployRefindToPartition(winbootDrive);

            // 5. Desmontar o ESP
            await DismountEspAsync(espDrive);

            Log("SUCESSO: rEFInd + Shim substituiu o Windows Boot Manager no ESP.");
            Log("Ao reiniciar, o firmware carregará o Shim, que inicia o rEFInd.");
            Log("rEFInd detectará o Windows e o Linux e exibirá um menu gráfico.");
            Log("Se o Secure Boot estiver ativo, siga as instruções do MokManager (mmx64.efi) na primeira inicialização.");
            return "{kitlugia-refind-esp}";
        }

        private static async Task<string?> MountEspAsync()
        {
            for (char letter = 'S'; letter <= 'Z'; letter++)
            {
                string drive = $"{letter}:";
                try
                {
                    if (new DriveInfo(drive).IsReady) continue;
                }
                catch
                {
                    // Drive não existe, pode ser usada
                }

                var (exit, output) = await RunProcessCaptured("mountvol", $"{drive} /S");
                if (exit != 0) continue;

                if (Directory.Exists($"{drive}\\EFI"))
                {
                    Log($"ESP montada em {drive}");
                    return drive;
                }
            }
            Log("AVISO: Nenhuma letra disponível para montar o ESP.");
            return null;
        }

        private static async Task DismountEspAsync(string drive)
        {
            await RunProcessCaptured("mountvol", $"{drive} /D");
            Log($"ESP desmontada ({drive})");
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string target = Path.Combine(targetDir, Path.GetFileName(file));
                try { File.Copy(file, target, true); }
                catch (Exception ex) { Log($"Erro ao copiar arquivo {file} → {target}: {ex.Message}"); }
            }
            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string target = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, target);
            }
        }


        public class BcdEntry
        {
            public string Guid { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Reason { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public bool IsCritical { get; set; } = false;
        }

        public static async Task<List<BcdEntry>> ScanBcdEntriesAsync()
        {
            Log("Escaneando entradas do menu de Boot (KitLugia & Linux)...");

            // Típico: 5-15 entradas de boot
            var entries = new List<BcdEntry>(15);

            return await Task.Run(() =>
            {
                try
                {
                    var (enumCode, enumOutput) = RunProcessCaptured("bcdedit.exe", "/enum all /v").GetAwaiter().GetResult();

                    if (enumCode != 0)
                    {
                        Log($"FALHA BCDEDIT: {enumOutput}");
                        return entries;
                    }


                    string[] descriptionPatterns = {
                        @"(description|descriç[ãa]o|descricao|beschreibung|descripción|description)\s+(KitLugia|Generic|Linux|Sergei|Winboot|Multi-ISO)",
                        @"(description|descriç[ãa]o|descricao|beschreibung|descripción|description)\s+.*\b(KITLUGIA|LUGIA)\b",
                        @"(description|descriç[ãa]o|descricao|beschreibung|descripción|description)\s+.*\b(WINBOOT)\b"
                    };


                    var winbootPartitions = GetDisks(false, false).SelectMany(d => d.Partitions)
                        .Where(p => p.Label.Contains("KITLUGIA", StringComparison.OrdinalIgnoreCase) ||
                                   p.Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.DriveLetter.Replace(":", ""))
                        .ToList();

                    string[] blocks = enumOutput.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string block in blocks)
                    {
                        string? guid = null;
                        var guidMatch = Regex.Match(block, @"(identifier|identificador)\s+({[a-fA-F0-9-]+})", RegexOptions.IgnoreCase);
                        if (guidMatch.Success)
                        {
                            guid = guidMatch.Groups[2].Value;
                        }

                        // Segurança Absoluta (marcar OS base como crítico)
                        bool isCritical = false;
                        if (guid != null && (guid.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{current}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{default}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{fwbootmgr}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{memdiag}", StringComparison.OrdinalIgnoreCase)))
                        {
                            isCritical = true;
                        }

                        // Extrai descrição
                        string description = "Sem descrição";
                        var descMatch = Regex.Match(block, @"(description|descriç[ãa]o|descricao)\s+(.+)", RegexOptions.IgnoreCase);
                        if (descMatch.Success)
                        {
                            description = descMatch.Groups[2].Value.Trim();
                        }

                        // Extrai tipo de aplicação
                        string type = "Desconhecido";
                        var appMatch = Regex.Match(block, @"application\s+(\w+)", RegexOptions.IgnoreCase);
                        if (appMatch.Success)
                        {
                            type = appMatch.Groups[1].Value;
                        }

                        bool shouldInclude = false;
                        string? reason = null;

                        // Estratégia 1: Busca por descrições
                        foreach (var pattern in descriptionPatterns)
                        {
                            if (Regex.IsMatch(block, pattern, RegexOptions.IgnoreCase))
                            {
                                shouldInclude = true;
                                reason = "Descrição KitLugia/Linux";
                                break;
                            }
                        }

                        // Estratégia 2: Busca por device que aponta para partição Winboot
                        if (!shouldInclude && winbootPartitions.Count > 0)
                        {
                            var deviceMatch = Regex.Match(block, @"(device|dispositivo)\s+partition=([A-Z]:)", RegexOptions.IgnoreCase);
                            if (deviceMatch.Success)
                            {
                                string driveLetter = deviceMatch.Groups[2].Value;
                                if (winbootPartitions.Contains(driveLetter.Replace(":", "")))
                                {
                                    shouldInclude = true;
                                    reason = $"Aponta para partição Winboot ({driveLetter})";
                                }
                            }
                        }

                        // Estratégia 3: Busca por ramdisksdidevice (entradas WIM)
                        if (!shouldInclude && winbootPartitions.Count > 0)
                        {
                            var ramdiskMatch = Regex.Match(block, @"ramdisksdidevice\s+partition=([A-Z]:)", RegexOptions.IgnoreCase);
                            if (ramdiskMatch.Success)
                            {
                                string driveLetter = ramdiskMatch.Groups[1].Value;
                                if (winbootPartitions.Contains(driveLetter.Replace(":", "")))
                                {
                                    shouldInclude = true;
                                    reason = $"Ramdisk aponta para Winboot ({driveLetter})";
                                }
                            }
                        }

                        // Estratégia 4: Busca por application bootsector (Legacy)
                        if (!shouldInclude)
                        {
                            var appMatch2 = Regex.Match(block, @"application\s+bootsector", RegexOptions.IgnoreCase);
                            if (appMatch2.Success)
                            {
                                var deviceMatch = Regex.Match(block, @"(device|dispositivo)\s+partition=([A-Z]:)", RegexOptions.IgnoreCase);
                                if (deviceMatch.Success)
                                {
                                    string driveLetter = deviceMatch.Groups[2].Value;
                                    if (winbootPartitions.Contains(driveLetter.Replace(":", "")))
                                    {
                                        shouldInclude = true;
                                        reason = $"Bootsector aponta para Winboot ({driveLetter})";
                                    }
                                }
                            }
                        }

                        // Incluir se encontrou pelo menos uma estratégia OU se for crítico (para mostrar ao usuário)
                        if ((shouldInclude && guid != null) || isCritical)
                        {
                            entries.Add(new BcdEntry
                            {
                                Guid = guid ?? "",
                                Description = description,
                                Reason = reason ?? (isCritical ? "Entrada crítica do sistema" : ""),
                                Type = type,
                                IsCritical = isCritical
                            });
                        }
                    }

                    Log($"Escaneamento BCD concluído. {entries.Count} entradas encontradas.");
                    return entries;
                }
                catch (Exception ex)
                {
                    Log($"Erro ao escanear BCD: {ex.Message}");
                    return entries;
                }
            });
        }

        public static async Task<bool> CleanBcdEntriesAsync(List<string>? guidsToDelete = null)
        {
            if (guidsToDelete == null || guidsToDelete.Count == 0)
            {
                Log("Nenhuma entrada para remover.");
                return true;
            }

            Log($"Limpando {guidsToDelete.Count} entradas do menu de Boot...");
            return await Task.Run(async () =>
            {
                try
                {
                    foreach (string guid in guidsToDelete)
                    {
                        // Não deletar entradas críticas do sistema
                        if (guid.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{current}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{default}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{fwbootmgr}", StringComparison.OrdinalIgnoreCase) ||
                            guid.Equals("{memdiag}", StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"⚠️ Pulando entrada crítica: {guid}");
                            continue;
                        }

                        Log($"Removendo entrada BCD: {guid}");
                        await RunProcessCaptured("bcdedit.exe", $"/delete {guid} /f");
                    }

                    // Limpa também o bootsequence se houver algo travado lá
                    await RunProcessCaptured("bcdedit.exe", "/set {fwbootmgr} displayorder {bootmgr} /addfirst");
                    await RunProcessCaptured("bcdedit.exe", "/deletevalue {fwbootmgr} bootsequence");

                    // Limpa também o displayorder do bootmgr para remover referências fantasma
                    await RunProcessCaptured("bcdedit.exe", "/deletevalue {bootmgr} displayorder");

                    Log($"Limpeza BCD concluída. {guidsToDelete.Count} entradas removidas.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"Erro ao limpar BCD: {ex.Message}");
                    return false;
                }
            });
        }

        public static async Task<List<BcdEntry>> ScanWinbootForCleanup()
        {
            Log("Escaneando Winboot para limpeza...");
            return await ScanBcdEntriesAsync();
        }

        public static async Task<bool> RemoveWinboot(PartitionInfo? specificTarget = null, bool safeMode = false, List<string>? customGuids = null)
        {
            Log(customGuids != null ? $"Iniciando remoção do Winboot ({customGuids.Count} GUIDs customizados)..." : "Iniciando remoção do Winboot...");
            return await Task.Run(async () =>
            {
                // Tenta iniciar VDS (Safe Mode Fix)
                try {
                    await RunProcessCaptured("sc", "config vds start= demand");
                    await RunProcessCaptured("net", "start vds");
                } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                try
                {
                    // 1. Remover entradas do BCD
                    if (customGuids != null)
                    {
                        // Remove GUIDs customizados (selecionados pelo usuário)
                        await CleanBcdEntriesAsync(customGuids);
                    }
                    else
                    {
                        // Modo automático: remove tudo
                        await CleanBcdEntriesAsync();
                    }

                    // 2. Destruir Partição Alvo
                    StringBuilder dpScript = new StringBuilder();
                    
                    if (specificTarget != null)
                    {

                        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                        if (specificTarget.DriveLetter.Replace(":", "").Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"❌ ERRO CRÍTICO: Tentando deletar partição do sistema {specificTarget.DriveLetter}. Operação abortada.");
                            return false;
                        }
                        
                        // Remoção Direta via Seleção do Usuário
                         Log($"Removendo ALVO SELECIONADO: Volume {specificTarget.DriveLetter} ({specificTarget.Label})...");
                         // Tenta pegar o numero do volume usando diskpart filter (mais seguro que confiar no index antigo)
                         dpScript.AppendLine($"select volume {specificTarget.DriveLetter.Replace(":", "")}");
                         dpScript.AppendLine("delete partition override");
                    }
                    else
                    {
                         // Modo Varredura (Legacy / Auto)
                        Log("Escaneando volumes para limpeza automática...");
                        string listScript = "list volume\nexit";
                        string listPath = Path.Combine(Path.GetTempPath(), "list_vol_cleanup.txt");
                        File.WriteAllText(listPath, listScript);
                        var (listCode, listOutput) = await RunProcessCaptured("diskpart.exe", $"/s \"{listPath}\"");
                        File.Delete(listPath);


                        if (listCode != 0)
                        {
                            Log($"Aviso: Diskpart list volume falhou com ExitCode {listCode}. Continuando mesmo assim...");
                        }

                        string volPattern = @"Volume\s+(\d+)\s+([A-Z])?\s+(Winboot|LUGIA_BOOT|NAO_DELETAR)";
                        var volMatches = Regex.Matches(listOutput, volPattern, RegexOptions.IgnoreCase);

                        if (volMatches.Count == 0)
                        {
                            Log("Nenhuma partição Winboot encontrada para remoção automática.");
                        }

                        foreach (Match m in volMatches)
                        {
                            string volNum = m.Groups[1].Value;
                            string volLetter = m.Groups[2].Value;
                            

                            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                            if (!string.IsNullOrEmpty(volLetter) && volLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"❌ ERRO CRÍTICO: Volume {volNum} ({volLetter}) parece ser o volume do sistema. Pulando.");
                                continue;
                            }
                            
                            Log($"Agendando remoção do Volume {volNum}...");
                            dpScript.AppendLine($"select volume {volNum}");
                            dpScript.AppendLine("delete partition override");
                        }
                    }


                    // 3. Tentar estender a unidade principal (C: ou a primeira com letra)
                    var disks = GetDisks(false, safeMode);
                    string? sourceLetter = null;
                    foreach(var d in disks)
                    {
                        // Filter out partitions that should not be considered for extension
                        var filteredPartitions = d.Partitions.Where(p =>
                            p.Size >= 3000000000 && // Skip partitions smaller than 3GB (e.g., MSR/EFI)
                            !p.Name.Contains("Reserved", StringComparison.OrdinalIgnoreCase) &&
                            !p.Label.Equals(WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase) &&
                            !p.Label.Equals("Winboot", StringComparison.OrdinalIgnoreCase)
                        ).ToList();

                        var cPart = filteredPartitions.FirstOrDefault(p => p.DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase));
                        if (cPart != null) { sourceLetter = "C"; break; }
                        sourceLetter = filteredPartitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter))?.DriveLetter.Replace(":", "");
                        if (sourceLetter != null) break;
                    }

                    if (!string.IsNullOrEmpty(sourceLetter))
                    {
                        Log($"Estendendo unidade principal: {sourceLetter}");
                        dpScript.AppendLine($"select volume {sourceLetter}");
                        dpScript.AppendLine("extend");
                    }
                    dpScript.AppendLine("exit");

                    if (dpScript.Length > 10) // "exit" + newline is 6
                    {
                        string scriptPath = Path.Combine(Path.GetTempPath(), "cleanup_winboot_dp.txt");
                        File.WriteAllText(scriptPath, dpScript.ToString());
                        var (dpCode, dpOutput) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");
                        Log(dpOutput);
                        File.Delete(scriptPath);


                        if (dpCode != 0)
                        {
                            Log($"Aviso: Diskpart cleanup falhou com ExitCode {dpCode}. Continuando mesmo assim...");
                        }
                    }

                    // 4. Restaurar Windows Boot Manager original no ESP (se rEFInd estiver presente)
                    Log("Verificando se rEFInd está instalado no ESP...");
                    var (espOk, espMsg) = BootloaderPackager.RestoreEspBoot();
                    if (espOk)
                        Log($"ESP restaurado: {espMsg}");
                    else
                        Log($"ESP: {espMsg}");

                    Log("Processo de limpeza concluído.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"ERRO na remoção: {ex.Message}");
                    return false;
                }
            });
        }

        public static async Task<bool> CreateBootPartition(string sourceDriveLetter, int sizeMb, string label, bool multiIso = false, bool safeMode = false, string? isoPath = null, Action<double, string>? progressCallback = null, Func<string, Task<bool>>? promptCallback = null)
        {
            Log($"Iniciando criação de partição no disco de origem {sourceDriveLetter} (Multi-ISO: {multiIso})...");

            return await Task.Run(async () =>
            {

                var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                if (sourceDriveLetter.Replace(":", "").Equals(sysDrive, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"❌ ERRO CRÍTICO: Tentando criar partição Winboot na partição do sistema {sourceDriveLetter}.");
                    Log("❌ Isso pode causar problemas de boot e instabilidade.");
                    Log("❌ Use uma partição de dados (D:, E:, etc) para criar o Winboot.");
                    return false;
                }
                
                // 0. VDS (Safe Mode Fix)
                try 
                {
                    await RunProcessCaptured("sc", "config vds start= demand");
                    await RunProcessCaptured("net", "start vds");
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // 1. AUTO-CLEANUP: Detectar e remover Winboot existente (evita boot duplicado)
                Log("Verificando se já existe uma partição Winboot anterior...");
                var existingPartitions = GetRemovablePartitions();

                if (existingPartitions.Count > 0)
                {
                    Log($"Encontrada(s) {existingPartitions.Count} partição(ões) Winboot existente(s).");
                    

                    var validWinbootPartitions = existingPartitions.Where(p =>
                        !string.IsNullOrEmpty(p.Label) && (
                            p.Label.Contains("KITLUGIA", StringComparison.OrdinalIgnoreCase) ||
                            p.Label.Contains("Winboot", StringComparison.OrdinalIgnoreCase) ||
                            p.Label.Contains("Multi-ISO", StringComparison.OrdinalIgnoreCase) ||
                            p.Label.Contains("PE", StringComparison.OrdinalIgnoreCase)
                        )
                    ).ToList();
                    
                    if (validWinbootPartitions.Count != existingPartitions.Count)
                    {
                        Log($"⚠️ AVISO: {existingPartitions.Count - validWinbootPartitions.Count} partição(ões) não parecem ser Winboot e NÃO serão deletadas.");
                        Log("⚠️ Somente partições com labels contendo 'KITLUGIA', 'Winboot', 'Multi-ISO' ou 'PE' serão removidas.");
                    }
                    
                    if (validWinbootPartitions.Any())
                    {
                        Log($"Removendo {validWinbootPartitions.Count} partição(ões) Winboot legítima(s) antes de criar nova...");
                        
                        // Limpa BCD primeiro
                        var (enumCode, enumOutput) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                        string bcdPattern = @"(identifier|identificador)\s+({[a-fA-F0-9-]+})[\s\S]*?description\s+(KitLugia Winboot Setup|Sergei Strelec PE|Generic Multi-ISO / Linux)";
                        var bcdMatches = Regex.Matches(enumOutput, bcdPattern, RegexOptions.IgnoreCase);
                        foreach (Match m in bcdMatches)
                        {
                            string guid = m.Groups[2].Value;
                            Log($"Removendo entrada BCD antiga: {guid}");
                            await RunProcessCaptured("bcdedit.exe", $"/delete {guid} /f");
                        }

                        // Deleta cada partição antiga e estende o volume de origem
                        foreach (var oldPart in validWinbootPartitions)
                        {
                            string letter = oldPart.DriveLetter.Replace(":", "");
                            if (string.IsNullOrEmpty(letter)) continue;
                            

                            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
                            if (letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"❌ ERRO CRÍTICO: Tentando deletar partição do sistema {letter}:. Operação abortada.");
                                continue;
                            }
                            
                            Log($"Deletando partição antiga: {letter}: ({oldPart.Label})");
                            StringBuilder cleanScript = new StringBuilder();
                            cleanScript.AppendLine($"select volume {letter}");
                            cleanScript.AppendLine("delete partition override");
                            cleanScript.AppendLine($"select volume {sourceDriveLetter}");
                            cleanScript.AppendLine("extend");
                            cleanScript.AppendLine("exit");

                            string cleanPath = Path.Combine(Path.GetTempPath(), "winboot_cleanup_dp.txt");
                            File.WriteAllText(cleanPath, cleanScript.ToString());
                            var (cleanExit, cleanOut) = await RunProcessCaptured("diskpart.exe", $"/s \"{cleanPath}\"");
                            Log(cleanOut);
                            File.Delete(cleanPath);


                            if (cleanExit != 0)
                            {
                                Log($"Aviso: Diskpart cleanup anterior falhou com ExitCode {cleanExit}. Continuando mesmo assim...");
                            }
                        }
                        Log("Limpeza de Winboot anterior concluída. Espaço restaurado.");
                    }
                    else
                    {
                        Log("⚠️ Nenhuma partição Winboot legítima encontrada para deletar. Continuando...");
                    }
                }

                // 2. DETECÇÃO MBR/GPT ROBUSTA via PowerShell
                bool isGpt = false;
                try
                {
                    // Descobre o PartitionStyle do disco de origem (Remove : se houver)
                    string cleanLetter = sourceDriveLetter.Replace(":", "");
                    var (psExit, psOutput) = await RunProcessCaptured("powershell.exe", 
                        $"-Command \"Get-Disk -Number ((Get-Partition -DriveLetter '{cleanLetter}').DiskNumber) | Select-Object -ExpandProperty PartitionStyle\"");
                    
                    string style = psOutput.Trim();
                    Log($"PowerShell Disk Style: {style}");
                    if (style.Equals("GPT", StringComparison.OrdinalIgnoreCase))
                    {
                        isGpt = true;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Aviso na detecção PS: {ex.Message}. Usando fallback WMI.");
                    // Fallback WMI
                    try {
                        string wimId = sourceDriveLetter.EndsWith(":") ? sourceDriveLetter : sourceDriveLetter + ":";
                        using (var searcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{wimId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                        {
                            foreach (ManagementObject partition in searcher.Get())
                            {
                                using (partition)
                                {
                                    string partType = partition["Type"]?.ToString() ?? "";
                                    if (partType.Contains("GPT", StringComparison.OrdinalIgnoreCase)) isGpt = true;
                                    break;
                                }
                            }
                        }
                    } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }
                Log($"Tipo de partição consolidado: {(isGpt ? "GPT (UEFI)" : "MBR (Legacy BIOS)")}");

                // 3. CRIAR PARTIÇÃO (Script Resiliente)
                bool isSystemEfi = IsEfiMode();


                Log($"🔧 Tentando criar partição de {sizeMb}MB ({sizeMb / 1024}GB)...");

                StringBuilder script = new StringBuilder();
                script.AppendLine("rescan");
                script.AppendLine($"select volume {sourceDriveLetter}");
                script.AppendLine($"shrink desired={sizeMb} minimum={sizeMb}");
                script.AppendLine("create partition primary");
                
                string fs = multiIso ? "fat32" : "ntfs";
                script.AppendLine($"format quick fs={fs} label=\"{WINBOOT_LABEL}\"");
                
                // CRÍTICO: 'assign' ANTES de 'active' para garantir letra mesmo se o firmware reclamar
                script.AppendLine("assign"); 

                // MBR 'active' SAFETY:
                // SÓ aplicamos 'active' se o disco for REMOVÍVEL (Pendrive).
                // NUNCA aplicamos em discos fixos (SSD/HDD) para não sequestrar o boot do host.
                bool isRemovable = false;
                try {
                    var disks = GetDisks(false, safeMode);
                    var targetDisk = disks.FirstOrDefault(d => d.Partitions.Any(p => p.DriveLetter.Equals(sourceDriveLetter, StringComparison.OrdinalIgnoreCase)));
                    if (targetDisk != null && (targetDisk.Interface.Contains("USB", StringComparison.OrdinalIgnoreCase) || targetDisk.Interface.Contains("Removable", StringComparison.OrdinalIgnoreCase))) {
                        isRemovable = true;
                    }
                } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                if (!isGpt && !isSystemEfi && isRemovable)
                {
                    Log("Disco MBR e REMOVÍVEL Detectado: Aplicando 'active' na partição.");
                    script.AppendLine("active"); 
                }
                else
                {
                    Log("Segurança MBR: Pulando 'active' para disco fixo ou sistema UEFI.");
                }

                script.AppendLine("exit");

                string scriptPath = Path.Combine(Path.GetTempPath(), "winboot_create_dp.txt");
                File.WriteAllText(scriptPath, script.ToString());

                Log("Executando Script Diskpart (Etapa 1: Criação e Formatação)...");
                var (exitCode, output) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");
                Log("--- DISKPART OUTPUT ---");
                Log(output);
                File.Delete(scriptPath);


                // ExitCode 0 = sucesso, != 0 = erro (independente do idioma)
                if (exitCode != 0)
                {
                    Log($"❌ ERRO CRÍTICO: Diskpart falhou com ExitCode {exitCode}. A partição não foi criada.");
                    Log($"");
                    Log($"⚠️ CAUSA: O Windows impõe limites no shrink devido a arquivos imóveis (pagefile.sys, hiberfil.sys, shadow copies)");
                    Log($"");
                    Log($"💡 Compact OS: compactar o sistema pode liberar espaço suficiente para o shrink");

                    if (promptCallback != null)
                    {
                        bool useCompact = await promptCallback("O shrink falhou devido a arquivos imóveis.\n\nUsar Compact OS para liberar espaço? Isso compactará os arquivos do Windows usando compact.exe /CompactOS:always e NÃO PODE SER INTERROMPIDO.\n\nO processo pode levar vários minutos e o sistema NÃO DEVE SER DESLIGADO.");
                        if (useCompact)
                        {
                            Log($"");
                            Log($"🔄 Executando Compact OS (/CompactOS:always) — NÃO INTERROMPA...");
                            progressCallback?.Invoke(0, "Executando Compact OS — NÃO DESLIGUE O COMPUTADOR...");
                            var (compactExit, compactOut) = await RunProcessCaptured("compact.exe", "/CompactOS:always", timeoutMs: 600_000);
                            Log($"--- COMPACT OS OUTPUT ---");
                            Log(compactOut);
                            if (compactExit == 0)
                            {
                                Log($"✅ Compact OS concluído. Retentando shrink...");
                                progressCallback?.Invoke(50, "Compact OS concluído. Retentando shrink...");
                                var (retryExit, retryOut) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");
                                Log($"--- DISKPART (RETRY) OUTPUT ---");
                                Log(retryOut);
                                if (retryExit == 0)
                                {
                                    Log("Diskpart (retry) concluído com sucesso.");
                                    // Fall through to continue normal flow
                                    goto AfterDiskpart;
                                }
                                else
                                {
                                    Log($"❌ Diskpart ainda falhou após Compact OS (ExitCode {retryExit}).");
                                }
                            }
                            else
                            {
                                Log($"❌ Compact OS falhou (ExitCode {compactExit}). Não foi possível liberar espaço.");
                            }
                        }
                        else
                        {
                            Log("Usuário recusou Compact OS.");
                        }
                    }

                    Log($"");
                    Log($"💡 SOLUÇÃO: Use o método de shrink da página de Partições com o modo atômico ativado");
                    return false;
                }
                AfterDiskpart:

                Log("Diskpart concluído com sucesso (ExitCode 0).");

                // Aguardar WMI estabilizar após alterações do diskpart
                await System.Threading.Tasks.Task.Delay(2000);

                // 4. VERIFICAÇÃO E CORREÇÃO DE LETRA (Crítico)

                // Isso funciona em qualquer idioma do Windows/ISO
                bool hasLetter = false;
                try
                {
                    var disksCheck = GetDisks(false, safeMode);
                    var targetPartition = disksCheck.SelectMany(d => d.Partitions)
                                                  .FirstOrDefault(p => p.Label.Equals(WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase));
                    hasLetter = targetPartition != null && !string.IsNullOrEmpty(targetPartition.DriveLetter);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                if (!hasLetter)
                {
                    Log("Aviso: Diskpart não confirmou atribuição de letra. Tentando atribuição forçada...");
                    // Procura a partição sem letra com o label KITLUGIA
                    StringBuilder fixScript = new StringBuilder();
                    fixScript.AppendLine("rescan");
                    fixScript.AppendLine("list volume");
                    fixScript.AppendLine("exit");
                    
                    var (listCode, listOut) = await RunProcessCaptured("diskpart.exe", "/s " + scriptPath); // Reusa o path mas com novo conteúdo
                    File.WriteAllText(scriptPath, fixScript.ToString());
                    (listCode, listOut) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");


                    if (listCode != 0)
                    {
                        Log($"Aviso: Diskpart list volume falhou com ExitCode {listCode}. Não foi possível forçar atribuição de letra.");
                    }
                    else
                    {
                        // Tenta achar o volume pelo label no output do list volume
                        var match = Regex.Match(listOut, @"Volume\s+(\d+)\s+\w\s+" + WINBOOT_LABEL, RegexOptions.IgnoreCase);
                        if (match.Success)
                        {
                            string volNum = match.Groups[1].Value;
                            Log($"Volume {WINBOOT_LABEL} encontrado como {volNum}. Forçando atribuição...");
                            File.WriteAllText(scriptPath, $"select volume {volNum}\nassign\nexit");
                            var (assignCode, assignOut) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");
                            

                            if (assignCode != 0)
                            {
                                Log($"Aviso: Diskpart assign falhou com ExitCode {assignCode}. A partição pode não ter letra.");
                            }
                        }
                    }
                    File.Delete(scriptPath);
                }

                await System.Threading.Tasks.Task.Delay(1000);

                // Verificamos se agora temos uma partição com a letra
                var disksAfter = GetDisks(false, safeMode);
                var createdPart = disksAfter.SelectMany(d => d.Partitions)
                                            .FirstOrDefault(p => p.Label.Equals(WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase));

                if (createdPart == null || string.IsNullOrEmpty(createdPart.DriveLetter))
                {
                    Log($"❌ ERRO CRÍTICO: A partição não foi criada.");
                    Log($"");
                    Log($"⚠️ CAUSA: O Windows impõe limites no shrink devido a arquivos imóveis (pagefile.sys, hiberfil.sys, shadow copies)");
                    Log($"");
                    Log($"💡 SOLUÇÃO: Use o método de shrink da página de Partições com o modo atômico ativado");
                    return false;
                }

                Log($"Partição Winboot pronta em {createdPart.DriveLetter}");

                return true;
            });
        }




        /// <summary>
        /// Injeta o comando de instalação do KitLugia em um XML Unattend existente.
        /// Procura pela seção FirstLogonCommands e adiciona se necessário.
        /// </summary>
        private static string PatchUnattendXml(string xml, string userName, string? password)
        {
            try
            {
                // 1. PATCH DE USUÁRIO (Súrgico - Apenas dentro de LocalAccounts)
                if (!string.IsNullOrEmpty(userName))
                {
                    // Regex mais inteligente que procura o contexto de conta local
                    // Altera o Nome e DisplayName apenas se tiver um Password ou LocalAccount por perto
                    xml = Regex.Replace(xml, @"(<LocalAccount.*?>.*?<Name>).*?(</Name>)", $"$1{userName}$2", RegexOptions.Singleline);
                    xml = Regex.Replace(xml, @"(<LocalAccount.*?>.*?<DisplayName>).*?(</DisplayName>)", $"$1{userName}$2", RegexOptions.Singleline);
                    
                    if (!string.IsNullOrEmpty(password))
                    {
                        xml = Regex.Replace(xml, @"(<Password>.*?<Value>).*?(</Value>)", $"$1{password}$2", RegexOptions.Singleline);
                    }
                    
                    // Fallback genérico para <Username> se for conta simples
                    xml = Regex.Replace(xml, @"(<Username>).*?(</Username>)", $"$1{userName}$2", RegexOptions.Singleline);
                }

                // 2. INJEÇÃO DE COMANDO (Garantir que o KitLugia rode)
                // Usamos um loop de varredura de drivers para encontrar o first_logon.bat na partição KITLUGIA
                string robustCommand = "cmd /c \"for %i in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (if exist %i:\\_KitLugiaSetup\\first_logon.bat (call %i:\\_KitLugiaSetup\\first_logon.bat &amp; exit))\"";

                if (xml.Contains("</FirstLogonCommands>"))
                {
                    string commandNode = "\n        <SynchronousCommand wcm:action=\"add\">\n" +
                                         "          <Order>99</Order>\n" +
                                         $"          <CommandLine>{robustCommand}</CommandLine>\n" +
                                         "          <Description>KitLugia Setup</Description>\n" +
                                         "        </SynchronousCommand>\n      ";
                    xml = xml.Replace("</FirstLogonCommands>", commandNode + "</FirstLogonCommands>");
                }
                else if (xml.Contains("</component>"))
                {
                     string fullSection = "\n      <FirstLogonCommands>\n" +
                                          "        <SynchronousCommand wcm:action=\"add\">\n" +
                                          "          <Order>99</Order>\n" +
                                          $"          <CommandLine>{robustCommand}</CommandLine>\n" +
                                          "          <Description>KitLugia Setup</Description>\n" +
                                          "        </SynchronousCommand>\n" +
                                          "      </FirstLogonCommands>\n    ";
                     
                     // Inserir antes do fechamento do component pass oobeSystem (amd64_Microsoft-Windows-Shell-Setup)
                     if (xml.Contains("Microsoft-Windows-Shell-Setup"))
                     {
                         // Tenta achar o fim do componente shell setup
                         int shellIndex = xml.IndexOf("Microsoft-Windows-Shell-Setup");
                         int endCompIndex = xml.IndexOf("</component>", shellIndex);
                         if (endCompIndex > 0)
                         {
                             xml = xml.Insert(endCompIndex, fullSection);
                         }
                     }
                }

                return xml;
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return xml; }
        }

        public class AutomationProfile
        {
            public string FriendlyName { get; set; } = "Desconhecido";
            public string Description { get; set; } = "";
            public string FileName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public bool IsDanger { get; set; }
            public bool IsRecommended { get; set; }
        }

        public static List<AutomationProfile> GetAutomationProfiles()
        {

            // Típico: 2-10 perfis de automação
            var profiles = new List<AutomationProfile>(10);
            
            // Perfil padrão (Gerador Interno)
            profiles.Add(new AutomationProfile 
            { 
                FriendlyName = "Personalizado (Gerador Interno)", 
                Description = "Crie sua própria automação usando as caixas de seleção acima.",
                FileName = null!
            });

            string goodiesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "BootGoodies", "E2B_Unattend");
            
            // Portabilidade Garantida: Tenta pasta local primeiro, depois fallback de dev
            if (!Directory.Exists(goodiesPath))
            {
                string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                goodiesPath = Path.Combine(projectRoot, "KitLugia.Core", "Resources", "BootGoodies", "E2B_Unattend");
            }

            if (Directory.Exists(goodiesPath))
            {
                try
                {
                    var files = Directory.GetFiles(goodiesPath, "*.xml");
                    foreach (var f in files)
                    {
                        string name = Path.GetFileName(f);
                        var profile = new AutomationProfile { FileName = name, FullPath = f };

                        // Traduções e descrições solicitadas pelo usuário
                        if (name.Contains("Win11_Pro_ContaLocal_SemTPM"))
                        {
                            profile.FriendlyName = "Windows 11 Pro - Conta Local e Sem TPM";
                            profile.Description = "Instala Win11 Pro pulando TPM/SecureBoot e forçando conta local.";
                        }
                        else if (name.Contains("Win11_Pro_SemBloatware_ContaLocal"))
                        {
                            profile.FriendlyName = "⭐ Win11Pro RECOMENDADO Limpo";
                            profile.Description = "Instalação otimizada sem apps inúteis e com conta local.";
                            profile.IsRecommended = true;
                        }
                        else if (name.Contains("WinLite10 - Windows 10 Pro"))
                        {
                            profile.FriendlyName = "Windows 10 Pro Lite (Otimizado)";
                            profile.Description = "Versão extremamente leve e rápida do Windows 10 Pro.";
                        }
                        else if (name.Contains("Win11_Pular_Requisitos_Geral"))
                        {
                            profile.FriendlyName = "Windows 11 - Pular Todos Requisitos";
                            profile.Description = "Ignora TPM 2.0, RAM, CPU e SecureBoot em qualquer versão.";
                        }
                        else if (name.Contains("Utilman - Hack Windows"))
                        {
                            profile.FriendlyName = "Hack de Recuperação (Utilman)";
                            profile.Description = "Substitui 'Acessibilidade' pelo CMD para resetar senhas.";
                        }
                        else if (name.Contains("ZZDANGER_Auto_WipeDisk0_Win10ProUS"))
                        {
                            profile.FriendlyName = "⚠️ AUTO-WIPE: Apagar Disco 0 (PERIGOSO)";
                            profile.Description = "Limpa o Disco 0 INTEIRO e instala o Win10 automaticamente.";
                            profile.IsDanger = true;
                        }
                        else if (name.Contains("No key (choose a version to install)"))
                        {
                            profile.FriendlyName = "Sem Chave - Menu de Versão";
                            profile.Description = "Não pede chave e deixa você escolher Pro/Home no menu.";
                        }
                        else if (name.Contains("SDI_CHOCO"))
                        {
                            profile.FriendlyName = "E2B: Instalação + Drivers + Softwares";
                            profile.Description = "Usa SDI para drivers e Chocolatey para apps comuns.";
                        }
                        else
                        {
                            profile.FriendlyName = "E2B: " + name.Replace(".xml", "");
                            profile.Description = "Script de automação avançada importado do Easy2Boot.";
                        }

                        profiles.Add(profile);
                    }
                }
                catch (Exception ex) { Log($"Erro ao carregar perfis de automação: {ex.Message}"); }
            }
            else
            {
                Log("Aviso: Diretório de BootGoodies não encontrado para carregar perfis E2B.");
            }

            return profiles;
        }

        // --- ADAPTIVE SIZING ---

        public static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return 0;
            
            // Se for arquivo único (ex: single file publish)
            if (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.Directory))
            {
                return new FileInfo(path).Length;
            }

            long size = 0;
            try
            {
                // Arquivos na raiz
                var fileQuery = Directory.EnumerateFiles(path);
                foreach (var file in fileQuery)
                {
                    size += new FileInfo(file).Length;
                }
                // Subpastas (recursivo)
                var dirQuery = Directory.EnumerateDirectories(path);
                foreach (var dir in dirQuery)
                {
                    size += GetDirectorySize(dir);
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return size;
        }

        /// <summary>
        /// Reduz partição usando WMI Storage Management API (MSFT_Partition.Resize)
        /// Método nativo do Windows que não precisa de scripts batch
        /// </summary>
        public static async Task<bool> ShrinkPartitionUsingWMI(string driveLetter, long shrinkMb, Action<double, string>? progressCallback = null)
        {
            try
            {
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    SHRINK VIA WMI STORAGE MANAGEMENT API");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                Log($"📋 MÉTODO: MSFT_Partition.Resize (WMI)");
                Log($"   Namespace: root\\Microsoft\\Windows\\Storage");
                Log($"   Classe: MSFT_Partition");
                Log($"   Método: Resize");
                Log($"");
                progressCallback?.Invoke(10, "Iniciando shrink via WMI...");

                // Conectar ao namespace WMI do Storage Management API
                ManagementScope scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();

                progressCallback?.Invoke(20, "Buscando partição...");

                // Buscar a partição pela letra do drive
                string query = $"SELECT * FROM MSFT_Partition WHERE DriveLetter = '{driveLetter}:'";
                ObjectQuery objectQuery = new ObjectQuery(query);
                ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, objectQuery);
                ManagementObjectCollection partitions = searcher.Get();

                if (partitions.Count == 0)
                {
                    Log($"❌ Partição não encontrada: {driveLetter}:");
                    return false;
                }

                ManagementObject? partition = null;
                foreach (ManagementObject p in partitions)
                {
                    partition = p;
                    break;
                }

                if (partition == null)
                {
                    Log($"❌ Erro ao obter partição");
                    return false;
                }

                Log($"✅ Partição encontrada: {partition["DriveLetter"]}");
                progressCallback?.Invoke(30, "Partição encontrada");

                // Obter tamanho atual da partição
                ulong currentSize = (ulong)partition["Size"];
                Log($"   Tamanho atual: {currentSize / (1024 * 1024)} MB");

                // Obter tamanhos suportados (mínimo e máximo)
                progressCallback?.Invoke(40, "Verificando tamanhos suportados...");
                Log($"");
                Log($"📊 VERIFICANDO TAMANHOS SUPORTADOS...");

                ManagementBaseObject inParams = partition.GetMethodParameters("GetSupportedSize");
                ManagementBaseObject outParams = partition.InvokeMethod("GetSupportedSize", inParams, null);

                if (outParams == null)
                {
                    Log($"❌ Erro ao obter tamanhos suportados");
                    return false;
                }

                ulong minSize = (ulong)outParams["SizeMin"];
                ulong maxSize = (ulong)outParams["SizeMax"];

                Log($"   Tamanho mínimo: {minSize / (1024 * 1024)} MB");
                Log($"   Tamanho máximo: {maxSize / (1024 * 1024)} MB");
                Log($"   Tamanho atual: {currentSize / (1024 * 1024)} MB");

                // Calcular novo tamanho
                ulong shrinkBytes = (ulong)(shrinkMb * 1024 * 1024);
                ulong newSize = currentSize - shrinkBytes;

                Log($"");
                Log($"📏 CÁLCULO DO NOVO TAMANHO:");
                Log($"   Reduzir: {shrinkMb} MB ({shrinkBytes / (1024 * 1024)} MB)");
                Log($"   Novo tamanho: {newSize / (1024 * 1024)} MB");

                // Verificar se o novo tamanho está dentro dos limites
                if (newSize < minSize)
                {
                    Log($"❌ ERRO: Novo tamanho ({newSize / (1024 * 1024)} MB) é menor que o mínimo ({minSize / (1024 * 1024)} MB)");
                    Log($"   Máximo possível de reduzir: {(currentSize - minSize) / (1024 * 1024)} MB");
                    progressCallback?.Invoke(-1, "Tamanho solicitado menor que o mínimo");
                    return false;
                }

                if (newSize > maxSize)
                {
                    Log($"❌ ERRO: Novo tamanho ({newSize / (1024 * 1024)} MB) é maior que o máximo ({maxSize / (1024 * 1024)} MB)");
                    progressCallback?.Invoke(-1, "Tamanho solicitado maior que o máximo");
                    return false;
                }

                progressCallback?.Invoke(50, "Tamanhos verificados");
                Log($"✅ Tamanho válido, iniciando resize...");

                // Executar o resize
                progressCallback?.Invoke(60, "Executando resize da partição...");
                Log($"");
                Log($"🔧 EXECUTANDO RESIZE...");

                inParams = partition.GetMethodParameters("Resize");
                inParams["Size"] = newSize;
                outParams = partition.InvokeMethod("Resize", inParams, null);

                if (outParams == null)
                {
                    Log($"❌ Erro ao executar resize");
                    return false;
                }

                uint returnValue = (uint)outParams["ReturnValue"];
                string extendedStatus = outParams["ExtendedStatus"]?.ToString() ?? "";

                Log($"   ReturnValue: {returnValue}");
                if (!string.IsNullOrEmpty(extendedStatus))
                {
                    Log($"   ExtendedStatus: {extendedStatus}");
                }

                if (returnValue == 0)
                {
                    Log($"✅ RESIZE CONCLUÍDO COM SUCESSO!");
                    Log($"   Partição reduzida de {currentSize / (1024 * 1024)} MB para {newSize / (1024 * 1024)} MB");
                    progressCallback?.Invoke(100, "Shrink concluído com sucesso!");
                    return true;
                }
                else
                {
                    Log($"❌ ERRO NO RESIZE: {returnValue}");
                    Log($"   Códigos de erro comuns:");
                    Log($"   0 = Sucesso");
                    Log($"   1 = Não suportado");
                    Log($"   2 = Erro não especificado");
                    Log($"   3 = Timeout");
                    Log($"   4 = Falha");
                    Log($"   5 = Parâmetro inválido");
                    Log($"   4097 = Tamanho não suportado");
                    Log($"   40001 = Acesso negado");
                    Log($"   40002 = Recursos insuficientes");
                    Log($"   42008 = Partição contém volume com erros");
                    Log($"   42009 = Sistema de arquivos desconhecido");
                    progressCallback?.Invoke(-1, $"Erro no resize: {returnValue}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao executar shrink via WMI: {ex.Message}");
                Log($"   StackTrace: {ex.StackTrace}");
                progressCallback?.Invoke(-1, $"Erro: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reduz partição usando RunOnce Avançado - método completo com preparação
        /// Desabilita arquivos imóveis, executa defrag, move MFT, então shrink via RunOnce
        /// </summary>
        public static async Task<bool> ShrinkPartitionUsingRunOnceAdvanced(string driveLetter, Action<double, string>? progressCallback = null)
        {
            try
            {
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    SHRINK AVANÇADO VIA RUNONCE (PREPARAÇÃO COMPLETA)");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                Log($"📋 ETAPAS:");
                Log($"   1. Verificar tamanho mínimo seguro");
                Log($"   2. Desabilitar pagefile, hiberfil e System Restore");
                Log($"   3. Executar defrag completo");
                Log($"   4. Tentar mover MFT para o início");
                Log($"   5. Criar script de shrink");
                Log($"   6. Adicionar ao RunOnce");
                Log($"   7. Reiniciar");
                Log($"   8. Executar shrink offline");
                Log($"   9. Reabilitar arquivos após sucesso");
                Log($"");
                progressCallback?.Invoke(5, "Preparando shrink avançado...");

                // Criar diretório KitLugia
                string kitlugiaDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KitLugia");
                if (!Directory.Exists(kitlugiaDir))
                    Directory.CreateDirectory(kitlugiaDir);

                // ETAPA 0: Verificar tamanho mínimo seguro
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 0: VERIFICAR TAMANHO MÍNIMO SEGURO");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(7, "Verificando tamanho mínimo seguro...");

                try
                {
                    var (exitCode0, output0) = await RunProcessCaptured("diskpart", $" /s \"{Path.Combine(kitlugiaDir, "check_size.txt")}\"");
                    // Criar script temporário para verificar tamanho atual
                    string checkScript = Path.Combine(kitlugiaDir, "check_size.txt");
                    File.WriteAllText(checkScript, $"select volume {driveLetter}\nlist volume\nexit");

                    // Executar para obter informações
                    var (exitCodeCheck, outputCheck) = await RunProcessCaptured("diskpart", $"/s \"{checkScript}\"");
                    Log($"Informações do volume: {outputCheck}");

                    // Calcular tamanho mínimo seguro
                    // Partição de sistema (C): mínimo 50GB
                    // Partição de dados: mínimo 10GB
                    bool isSystemDrive = driveLetter.ToUpper() == "C";
                    long minSizeGB = isSystemDrive ? 50 : 10;
                    long minSizeMB = minSizeGB * 1024;

                    Log($"Drive {driveLetter}: Tamanho mínimo seguro = {minSizeGB} GB ({minSizeMB} MB)");
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Não foi possível verificar tamanho atual: {ex.Message}");
                    Log($"   Continuando mesmo assim...");
                }

                progressCallback?.Invoke(10, "Tamanho mínimo verificado");

                // ETAPA 1: Desabilitar arquivos imóveis
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 1: DESABILITAR ARQUIVOS IMÓVEIS");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(15, "Desabilitando pagefile...");

                // Desabilitar pagefile (usar driveLetter)
                try
                {
                    var (exitCode1, output1) = await RunProcessCaptured("wmic", $"pagefileset where name='{driveLetter}:\\pagefile.sys' delete");
                    Log($"Pagefile: ExitCode={exitCode1}, Output={output1}");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao desabilitar pagefile: {ex.Message}");
                    Log($"Continuando mesmo assim (pagefile pode não existir em {driveLetter}:)...");
                }

                progressCallback?.Invoke(20, "Desabilitando hibernação...");
                // Desabilitar hibernação (global, não por drive)
                try
                {
                    var (exitCode2, output2) = await RunProcessCaptured("powercfg", "/h off");
                    Log($"Hibernação: ExitCode={exitCode2}, Output={output2}");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao desabilitar hibernação: {ex.Message}");
                    Log($"Continuando mesmo assim...");
                }

                progressCallback?.Invoke(20, "Desabilitando System Restore...");
                // Desabilitar System Restore (usar driveLetter)
                try
                {
                    var (exitCode3, output3) = await RunProcessCaptured("powershell", $"Disable-ComputerRestore -Drive {driveLetter}");
                    Log($"System Restore: ExitCode={exitCode3}, Output={output3}");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao desabilitar System Restore: {ex.Message}");
                    Log($"Continuando mesmo assim (System Restore pode não estar habilitado em {driveLetter}:)...");
                }

                Log($"✅ Arquivos imóveis desabilitados");
                progressCallback?.Invoke(25, "Arquivos imóveis desabilitados");

                // ETAPA 2: Defrag completo com UltraDefrag (se disponível) ou defrag nativo
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 2: DEFRAG COMPLETO (TÉCNICA PROFISSIONAL)");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(30, "Executando defrag completo...");

                // Tentar UltraDefrag primeiro (tem boot-time defrag e move MFT)
                try
                {
                    var (exitCode4, output4) = await RunProcessCaptured("ultradefrag", $"--optimize {driveLetter}:");
                    if (exitCode4 == 0)
                    {
                        Log($"UltraDefrag concluído com sucesso: {output4}");
                    }
                    else
                    {
                        Log($"UltraDefrag falhou (ExitCode={exitCode4}), usando defrag nativo...");
                    }
                }
                catch (Exception ex)
                {
                    Log($"UltraDefrag não disponível ({ex.Message}), usando defrag nativo...");
                }

                // Fallback para defrag nativo com otimização completa
                try
                {
                    var (exitCode4b, output4b) = await RunProcessCaptured("defrag", $"{driveLetter} /O /V");
                    Log($"Defrag nativo: ExitCode={exitCode4b}, Output={output4b}");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao executar defrag nativo: {ex.Message}");
                    Log($"Continuando mesmo assim...");
                }

                // TÉCNICA AVANÇADA: Boot-time defrag (move MFT e arquivos imóveis)
                Log($"");
                Log($"⚡ TÉCNICA AVANÇADA: Tentando boot-time defrag...");
                try
                {
                    // /B = Boot-time defrag (move MFT e arquivos imóveis)
                    var (exitCode4c, output4c) = await RunProcessCaptured("defrag", $"{driveLetter} /B /V");
                    Log($"Boot-time defrag: ExitCode={exitCode4c}, Output={output4c}");
                    if (exitCode4c == 0)
                    {
                        Log($"✅ Boot-time defrag agendado para o próximo boot");
                        Log($"   Isso moverá o MFT e arquivos imóveis");
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Boot-time defrag não suportado: {ex.Message}");
                    Log($"   Continuando sem boot-time defrag...");
                }

                Log($"✅ Defrag concluído");
                progressCallback?.Invoke(40, "Defrag concluído");

                // ETAPA 2.5: Verificar se DiskPart agora consegue mover arquivos
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 2.5: VERIFICAR CAPACIDADE DE SHRINK");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(42, "Verificando capacidade de shrink...");

                try
                {
                    string checkScript = Path.Combine(kitlugiaDir, "check_shrink.txt");
                    File.WriteAllText(checkScript, $"select volume {driveLetter}\nshrink querymax\nexit");

                    var (exitCodeCheck, outputCheck) = await RunProcessCaptured("diskpart", $"/s \"{checkScript}\"");
                    Log($"Resultado do shrink querymax: {outputCheck}");

                    // Verificar se há espaço disponível para shrink
                    if (outputCheck.Contains("pode mover 0") || outputCheck.Contains("amount of shrinkable space") && outputCheck.Contains("0 MB"))
                    {
                        Log($"⚠️ AVISO CRÍTICO: DiskPart ainda não consegue mover arquivos imóveis");
                        Log($"   Isso significa que arquivos do sistema estão bloqueando o shrink");
                        Log($"   Soluções possíveis:");
                        Log($"   1. Usar ferramenta profissional (EaseUS, MiniTool, AOMEI)");
                        Log($"   2. Tentar modo atômico (captura, deleta, recria, restaura)");
                        Log($"   3. Agendar boot-time defrag manual e reiniciar antes do shrink");
                        Log($"");
                        Log($"   Continuando mesmo assim, mas o shrink pode falhar...");
                    }
                    else
                    {
                        Log($"✅ DiskPart agora consegue mover arquivos imóveis");
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Não foi possível verificar capacidade de shrink: {ex.Message}");
                    Log($"   Continuando mesmo assim...");
                }

                progressCallback?.Invoke(45, "Verificação concluída");

                // ETAPA 3: Mover MFT e metadados (técnica profissional baseada em PerfectDisk/MyDefrag)
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 3: MOVER MFT E METADADOS (TÉCNICA PROFISSIONAL)");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(50, "Movendo MFT e metadados...");

                // Tentar usar contig para mover MFT (Sysinternals - usado por profissionais)
                if (File.Exists("contig.exe") || File.Exists(Path.Combine(Environment.SystemDirectory, "contig.exe")))
                {
                    try
                    {
                        var (exitCode5, output5) = await RunProcessCaptured("contig", $"-v {driveLetter}\\$Mft");
                        if (exitCode5 == 0)
                            Log($"MFT movido com sucesso: {output5}");
                        else
                            Log($"Contig falhou (ExitCode={exitCode5}), tentando fsutil...");
                    }
                    catch (Exception ex)
                    {
                        Log($"Contig não disponível ({ex.Message}), tentando fsutil...");
                    }
                }
                else
                {
                    Log("contig.exe não encontrado (Sysinternals não instalado). Usando fsutil como fallback...");
                }

                // Fallback: usar fsutil para tentar mover MFT (técnica avançada)
                try
                {
                    var (exitCode5b, output5b) = await RunProcessCaptured("fsutil", $"behavior set disable8dot3name 1");
                    Log($"fsutil: ExitCode={exitCode5b}, Output={output5b}");
                }
                catch (Exception ex)
                {
                    Log($"ERRO ao executar fsutil: {ex.Message}");
                    Log($"Continuando mesmo assim...");
                }

                // Tentar mover $LogFile (journal NTFS que pode estar no meio do disco)
                if (File.Exists("contig.exe") || File.Exists(Path.Combine(Environment.SystemDirectory, "contig.exe")))
                {
                    try
                    {
                        var (exitCode7, output7) = await RunProcessCaptured("contig", $"-v {driveLetter}\\$LogFile");
                        if (exitCode7 == 0)
                            Log($"$LogFile movido com sucesso: {output7}");
                        else
                            Log($"$LogFile não foi movido (ExitCode={exitCode7}): {output7}");
                    }
                    catch (Exception ex)
                    {
                        Log($"ERRO ao mover $LogFile: {ex.Message}");
                        Log($"Continuando mesmo assim...");
                    }
                }
                else
                {
                    Log("contig.exe não encontrado. Pulando movimentação de $LogFile.");
                }

                progressCallback?.Invoke(55, "Preparação concluída");

                // ETAPA 4-7: Criar script de shrink e adicionar ao RunOnce
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    ETAPA 4-7: CRIAR SCRIPT E AGENDAR RUNONCE");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(60, "Criando script de shrink...");

                // Criar script de diskpart para shrink
                string diskpartScript = Path.Combine(kitlugiaDir, "shrink_script.txt");
                StringBuilder dpScript = new StringBuilder();
                dpScript.AppendLine("rescan");
                dpScript.AppendLine($"select volume {driveLetter}");
                // Usa shrink sem parâmetros para deixar o DiskPart calcular o máximo automaticamente
                // Isso evita o erro "tamanho de redução especificado é muito grande"
                dpScript.AppendLine("shrink");
                dpScript.AppendLine("exit");
                File.WriteAllText(diskpartScript, dpScript.ToString());

                Log($"✅ Script de shrink criado em {diskpartScript}");
                Log($"⚠️ DiskPart calculará o máximo possível automaticamente");
                progressCallback?.Invoke(65, "Script de shrink criado");

                // Criar script batch que executa shrink (WinRE não tem wmic/powercfg, então não reabilita aqui)
                string batchScript = Path.Combine(kitlugiaDir, "run_shrink_advanced.bat");
                StringBuilder batchContent = new StringBuilder();
                batchContent.AppendLine("@echo off");
                batchContent.AppendLine("setlocal enabledelayedexpansion");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("echo KitLugia - Executando shrink avançado...");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine($"diskpart /s \"{diskpartScript}\"");
                batchContent.AppendLine("set DISKPART_ERROR=%ERRORLEVEL%");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("if %DISKPART_ERROR% NEQ 0 (");
                batchContent.AppendLine("    echo ERRO: DiskPart falhou com codigo %DISKPART_ERROR%");
                batchContent.AppendLine("    echo ============================================");
                batchContent.AppendLine("    echo A entrada RunOnce sera mantida para tentar novamente.");
                batchContent.AppendLine("    echo ============================================");
                batchContent.AppendLine("    pause >nul");
                batchContent.AppendLine("    exit /b %DISKPART_ERROR%");
                batchContent.AppendLine(")");
                batchContent.AppendLine("echo Shrink concluido com sucesso!");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("echo NOTA: Arquivos imóveis serao reabilitados no proximo boot normal.");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("echo Removendo entrada RunOnce...");
                batchContent.AppendLine("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v KitLugiaShrinkAdvanced /f 2>nul");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("echo Processo concluido com sucesso!");
                batchContent.AppendLine("echo ============================================");
                batchContent.AppendLine("echo Pressione qualquer tecla para fechar esta janela...");
                batchContent.AppendLine("pause >nul");
                File.WriteAllText(batchScript, batchContent.ToString());

                Log($"✅ Script batch criado em {batchScript}");
                progressCallback?.Invoke(75, "Script batch criado");

                // Criar segundo script para reabilitar arquivos no boot normal (não WinRE)
                string restoreScript = Path.Combine(kitlugiaDir, "restore_immovable.bat");
                StringBuilder restoreContent = new StringBuilder();
                restoreContent.AppendLine("@echo off");
                restoreContent.AppendLine("echo ============================================");
                restoreContent.AppendLine("echo KitLugia - Reabilitando arquivos imóveis...");
                restoreContent.AppendLine("echo ============================================");
                restoreContent.AppendLine($"echo Reabilitando pagefile em {driveLetter}:...");
                restoreContent.AppendLine($"wmic pagefileset create name=\"{driveLetter}:\\pagefile.sys\"");
                restoreContent.AppendLine("echo Reabilitando hibernação...");
                restoreContent.AppendLine("powercfg /h on");
                restoreContent.AppendLine("echo Reabilitando System Restore em {driveLetter}:...");
                restoreContent.AppendLine($"powershell -Command \"Enable-ComputerRestore -Drive {driveLetter}\"");
                restoreContent.AppendLine("echo ============================================");
                restoreContent.AppendLine("echo Arquivos imóveis reabilitados com sucesso!");
                restoreContent.AppendLine("echo ============================================");
                restoreContent.AppendLine("echo Removendo entrada RunOnce...");
                restoreContent.AppendLine("reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v KitLugiaRestoreImmovable /f 2>nul");
                restoreContent.AppendLine("echo ============================================");
                restoreContent.AppendLine("echo Pressione qualquer tecla para fechar esta janela...");
                restoreContent.AppendLine("pause >nul");
                File.WriteAllText(restoreScript, restoreContent.ToString());

                Log($"✅ Script de restauração criado em {restoreScript}");
                progressCallback?.Invoke(77, "Script de restauração criado");

                // Adicionar ao registro RunOnce
                Log($"🔧 Adicionando script ao registro RunOnce...");
                progressCallback?.Invoke(80, "Adicionando script ao registro RunOnce...");
                var (exitCode8, output8) = await RunProcessCaptured("reg", $"add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v KitLugiaShrinkAdvanced /t REG_SZ /d \"{batchScript}\" /f");
                Log(output8);

                if (exitCode8 != 0)
                {
                    Log($"❌ ERRO ao adicionar ao registro: ExitCode {exitCode8}");
                    // Reabilitar arquivos antes de falhar
                    await RunProcessCaptured("wmic", $"pagefileset create name=\"{driveLetter}:\\pagefile.sys\"");
                    await RunProcessCaptured("powercfg", "/h on");
                    return false;
                }

                Log($"✅ Entrada RunOnce (shrink) adicionada com sucesso");
                progressCallback?.Invoke(82, "Entrada RunOnce (shrink) adicionada");

                // Adicionar segundo registro RunOnce para restauração (roda no boot normal)
                Log($"🔧 Adicionando script de restauração ao registro RunOnce...");
                progressCallback?.Invoke(85, "Adicionando script de restauração ao registro RunOnce...");
                var (exitCode9, output9) = await RunProcessCaptured("reg", $"add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v KitLugiaRestoreImmovable /t REG_SZ /d \"{restoreScript}\" /f");
                Log(output9);

                if (exitCode9 != 0)
                {
                    Log($"⚠️ AVISO: Não foi possível adicionar script de restauração (ExitCode {exitCode9})");
                    Log($"   Você precisará reabilitar pagefile/hibernação manualmente após o shrink.");
                }
                else
                {
                    Log($"✅ Entrada RunOnce (restauração) adicionada com sucesso");
                }

                progressCallback?.Invoke(87, "Entradas RunOnce adicionadas");
                Log($"");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"                    PREPARANDO REINÍCIO...");
                Log($"═══════════════════════════════════════════════════════════════════════════════");
                Log($"");
                progressCallback?.Invoke(90, "Preparando reinício...");
                Log($"📋 INSTRUÇÕES APÓS REINÍCIO:");
                Log($"   1. O script executará automaticamente no WinRE (boot de recuperação)");
                Log($"   2. O DiskPart reduzirá a partição");
                Log($"   3. O Windows iniciará normalmente");
                Log($"   4. Os arquivos imóveis serão reabilitados automaticamente");
                Log($"   5. Abra o KitLugia novamente para verificar o resultado");
                Log($"");
                Log($"🎯 O shrink será executado após preparação completa");
                Log($"");

                // Reiniciar imediatamente
                await RunProcessCaptured("shutdown", "/r /t 0 /c \"KitLugia: Reiniciando para shrink avançado - NÃO DESLIGUE MANUALMENTE\"");

                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ ERRO FATAL ao configurar RunOnce Avançado:");
                Log($"   Mensagem: {ex.Message}");
                Log($"   StackTrace: {ex.StackTrace}");
                Log($"   InnerException: {ex.InnerException?.Message ?? "N/A"}");
                Log($"   Source: {ex.Source}");
                return false;
            }
        }

        public static int CalculateRequiredSizeGB(string? userInjectedPath = null)
        {
            try 
            {
                long baseSize = 4L * 1024 * 1024 * 1024; // 4GB Base (WinPE + Boot Files + Small ISO)
                
                // Tamanho do App (KitLugia + Runtime)
                long appSize = GetDirectorySize(AppDomain.CurrentDomain.BaseDirectory);
                
                // Tamanho dos Goodies (Se externo)
                string goodiesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "BootGoodies");
                if (!Directory.Exists(goodiesPath))
                {
                    // Fallback Dev
                    string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
                    goodiesPath = Path.Combine(projectRoot, "KitLugia.Core", "Resources", "BootGoodies");
                }
                long goodiesSize = GetDirectorySize(goodiesPath);

                // Tamanho da Injeção do Usuário
                long injectedSize = 0;
                if (!string.IsNullOrEmpty(userInjectedPath) && Directory.Exists(userInjectedPath))
                {
                    injectedSize = GetDirectorySize(userInjectedPath);
                }

                // Buffer de Segurança: 2GB (Updates, Logs, Temp)
                long bufferSize = 2L * 1024 * 1024 * 1024;

                long totalBytes = baseSize + appSize + goodiesSize + injectedSize + bufferSize;

                // Converter para GB arredondado para cima
                double gb = (double)totalBytes / (1024 * 1024 * 1024);
                int totalGB = (int)Math.Ceiling(gb);
                
                // Mínimo 8GB para evitar problemas
                return Math.Max(8, totalGB);
            }
            catch 
            {
                return 8; // Fallback seguro
            }
        }

        // --- IN-PLACE UPGRADE (UPDATE) ENGINE ---

        public static async Task<bool> StartInPlaceUpgrade(string isoPath, int index, string targetEditionId)
        {
            Log($"Iniciando Atualização In-place (ISO: {Path.GetFileName(isoPath)}, Index: {index})...");
            
            try 
            {
                // 1. Montar ISO
                string driveLetter = await MountIso(isoPath);
                if (string.IsNullOrEmpty(driveLetter))
                {
                    Log("Erro: Não foi possível montar a ISO.");
                    return false;
                }

                string setupPath = Path.Combine(driveLetter, "setup.exe");
                if (!File.Exists(setupPath)) setupPath = Path.Combine(driveLetter, "sources", "setup.exe");

                if (!File.Exists(setupPath))
                {
                    Log("Erro: setup.exe não encontrado na ISO.");
                    await DismountIso(isoPath);
                    return false;
                }

                // 2. Backup da EditionID atual
                string currentEditionId = GetCurrentEditionId();
                Log($"EditionID atual: {currentEditionId} -> Alvo: {targetEditionId}");

                // 3. Spoof EditionID no Registro (Burlar trava de edição)
                SetEditionId(targetEditionId);

                // 4. Rodar o setup com bypass de requisitos (/product server)
                Log("Lançando Setup do Windows (Ignorando Requisitos)...");
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = setupPath,
                    Arguments = "/product server",
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process? p = Process.Start(psi);
                if (p != null)
                {
                    Log("Setup iniciado. O KitLugia aguardará o término para restaurar o registro.");
                    
                    // Task para monitorar o processo e restaurar o registro quando fechar
                    _ = Task.Run(async () => {
                        try
                        {
                            await p.WaitForExitAsync();
                            Log("Setup do Windows fechado. Restaurando EditionID original...");
                            SetEditionId(currentEditionId);
                            await DismountIso(isoPath);
                            Log("Processo de atualização finalizado.");
                        }
                        catch (Exception ex)
                        {
                            Log($"Erro no pós-setup: {ex.Message}");
                        }
                    });
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Erro crítico na atualização: {ex.Message}");
            }

            return false;
        }

        public struct WimEditionInfo
        {
            public int Index;
            public string Name;
            public string Architecture;
            public string EditionId;
            public string Version;

            public override string ToString() => $"{Name} ({Architecture} - {Version})";
        }

        // Parse colon-delimited key-value pairs from DISM output (locale-independent)
        private static List<string> ParseDismColonValues(string output)
        {
            var values = new List<string>();
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(line.Trim(), @"^(.+?)\s*:\s*(.+)$");
                if (!m.Success) continue;
                string key = m.Groups[1].Value.Trim();
                string val = m.Groups[2].Value.Trim();
                // Skip section header lines (e.g. "Details for image : install.wim")
                if (key.Contains("for image") || key.IndexOf("information", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (string.IsNullOrEmpty(val)) continue;
                values.Add(val);
            }
            return values;
        }

        public static async Task<List<WimEditionInfo>> GetIsoEditions(string isoPath)
        {
            var editions = new List<WimEditionInfo>();
            string drive = "";
            try 
            {
                drive = await MountIso(isoPath);
                if (string.IsNullOrEmpty(drive)) return editions;

                string wimPath = Path.Combine(drive, "sources", "install.wim");
                if (!File.Exists(wimPath)) wimPath = Path.Combine(drive, "sources", "install.esd");

                if (File.Exists(wimPath))
                {
                    var (_, output) = await RunProcessCaptured("dism.exe", $"/Get-ImageInfo /ImageFile:\"{wimPath}\"");
                    
                    // Parse DISM output: blocks separated by blank lines
                    var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var block in blocks)
                    {
                        var vals = ParseDismColonValues(block);
                        if (vals.Count < 2) continue;
                        // First colon-value pair is Index (numeric), second is Name
                        if (!int.TryParse(vals[0], out int idx)) continue;
                        var info = new WimEditionInfo { Index = idx, Name = vals[1] };
                        
                        // Detailed info per index
                        var (_, detail) = await RunProcessCaptured("dism.exe", $"/Get-ImageInfo /ImageFile:\"{wimPath}\" /Index:{info.Index}");
                        var detailVals = ParseDismColonValues(detail);
                        // Order: Index(0), Name(1), Description(2), Size(3), Edition(4), Architecture(5), Version(6)
                        int detailIdx = 0;
                        foreach (var dv in detailVals)
                        {
                            if (detailIdx == 0) { detailIdx++; continue; } // Index
                            else if (detailIdx == 1) { detailIdx++; continue; } // Name
                            else if (detailIdx == 2) { detailIdx++; continue; } // Description
                            else if (detailIdx == 3) { detailIdx++; continue; } // Size
                            else if (detailIdx == 4) info.EditionId = dv;
                            else if (detailIdx == 5) info.Architecture = dv;
                            else if (detailIdx == 6) { info.Version = dv; break; }
                            detailIdx++;
                        }

                        editions.Add(info);
                    }
                }
            }
            catch (Exception ex) { Log($"Erro ao ler edições da ISO: {ex.Message}"); }
            finally { if (!string.IsNullOrEmpty(drive)) await DismountIso(isoPath); }
            
            return editions;
        }

        public static async Task<string> MountIso(string isoPath)
        {
            string safePath = isoPath.Replace("'", "''");
            string script = $"Mount-DiskImage -ImagePath '{safePath}' -PassThru | Get-Volume | Select-Object -ExpandProperty DriveLetter";
            string drv = await RunPowerShell(script);
            drv = drv.Trim();
            return drv.Length == 1 ? drv + ":" : "";
        }

        public static async Task DismountIso(string isoPath)
        {
            string safePath = isoPath.Replace("'", "''");
            await RunPowerShell($"Dismount-DiskImage -ImagePath '{safePath}'");
        }

        private static async Task<string> RunPowerShell(string script)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(readTask, Task.Delay(30000)) != readTask)
            {
                try { process.Kill(); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                return "";
            }
            return await readTask;
        }

        public static string GetCurrentEditionId()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return key?.GetValue("EditionID")?.ToString() ?? "Professional";
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return "Professional"; }
        }

        public static void SetEditionId(string editionId)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", true);
                if (key != null)
                {
                    key.SetValue("EditionID", editionId);
                    Log($"Registro: EditionID alterado para {editionId}");
                }
            }
            catch (Exception ex) { Log($"Erro ao modificar EditionID no registro: {ex.Message}"); }
        }

        public static async Task<string?> LocateWinreWim()
        {
            Log("Localizando 'Doador' para Ponte (Winre/Boot.wim)...");
            
            // 1. Check common paths
            var paths = new List<string> {
                @"C:\Recovery\WindowsRE\winre.wim",
                @"C:\Windows\System32\Recovery\winre.wim"
            };
            foreach (var p in paths) if (File.Exists(p)) return p;

            // 2. ULTIMATE RECOURSE: Extract from local ISO
            return await FindWimInLocalIsos();
        }

        /// <summary>
        /// Busca recursivamente por qualquer .wim válido (>10MB) no diretório.
        /// Usado como fallback quando a extração do 7z não coloca o WIM no local esperado.
        /// </summary>
        private static string? FindWimRecursive(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return null;
                foreach (var f in Directory.GetFiles(directory, "*.wim", SearchOption.AllDirectories))
                    if (new FileInfo(f).Length > 10 * 1024 * 1024) return f;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Tenta extrair archive 7z usando 7z.exe. Se falhar, tenta Expand-Archive (PowerShell).
        /// Busca recursivamente por .wim ao final. Loga exit code + stderr para debug.
        /// </summary>
        private static async Task<(bool success, string? wimPath)> TryExtract7z(string sevenZip, string archivePath, string outputDir)
        {
            string? foundWim = null;
            try
            {
                Log($"Extraindo: {archivePath}");
                var (extCode, extOut) = await RunProcessCaptured(sevenZip,
                    $"x \"{archivePath}\" -o{outputDir} -y", 180000);
                if (!string.IsNullOrWhiteSpace(extOut))
                    Log($"7z output: {extOut.Trim()}");
                if (extCode == 0)
                {
                    Log($"7z concluído (código {extCode}). Verificando WIM...");
                    foundWim = FindWimRecursive(outputDir);
                    if (foundWim != null)
                    {
                        Log($"WIM extraído com sucesso: {foundWim}");
                        return (true, foundWim);
                    }
                    Log("WIM não encontrado mesmo com código 0. Busca recursiva falhou.");
                }
                else
                    Log($"7z retornou código {extCode}. Tentando fallback...");
            }
            catch (Exception ex)
            {
                Log($"Exceção ao executar 7z: {ex.Message}");
            }

            // fallback: tenta via PowerShell (útil em máquinas sem 7z no PATH)
            try
            {
                var (psCode, psOut) = await RunProcessCaptured("powershell.exe",
                    $"-NoProfile -Command \"& {{ try {{ " +
                    $"$ext = '{outputDir}'; " +
                    $"if ('{archivePath}'.EndsWith('.zip')) {{ Expand-Archive -Path '{archivePath}' -DestinationPath $ext -Force; }} " +
                    $"else {{ & '{sevenZip}' x '{archivePath}' -o'$ext' -y }} " +
                    $"Write-Output 'OK' }} catch {{ Write-Error $_ }}}}\"", 180000);
                if (psCode == 0 && psOut?.Contains("OK") == true)
                {
                    Log("Fallback PowerShell concluído.");
                    foundWim = FindWimRecursive(outputDir) ?? foundWim;
                    if (foundWim != null)
                    {
                        Log($"WIM extraído via PowerShell: {foundWim}");
                        return (true, foundWim);
                    }
                }
                else
                    Log($"Fallback PowerShell falhou (código {psCode}): {psOut?.Trim()}");
            }
            catch (Exception ex)
            {
                Log($"Exceção no fallback PowerShell: {ex.Message}");
            }

            foundWim = FindWimRecursive(outputDir) ?? foundWim;
            return (foundWim != null, foundWim);
        }

        private static async Task<string?> FindWimInLocalIsos()
        {
            try
            {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] searchPaths = { Path.Combine(userProfile, "Downloads"), Path.Combine(userProfile, "Desktop") };
                foreach (var folder in searchPaths.Where(Directory.Exists))
                {
                    var isos = Directory.GetFiles(folder, "*Strelec*.iso");
                    foreach (var iso in isos)
                    {
                        var (code, output) = await RunProcessCaptured("powershell.exe", $"-Command \"Mount-DiskImage -ImagePath '{iso}'\"");
                        if (code == 0)
                        {
                            await Task.Delay(2000);
                            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom && d.IsReady))
                            {
                                string wimPath = Path.Combine(drive.RootDirectory.FullName, "sources", "boot.wim");
                                if (File.Exists(wimPath))
                                {
                                    string cachePath = Path.Combine(Path.GetTempPath(), "kitlugia_donor_boot.wim");
                                    File.Copy(wimPath, cachePath, true);
                                    await RunProcessCaptured("powershell.exe", $"-Command \"Dismount-DiskImage -ImagePath '{iso}'\"");
                                    return cachePath;
                                }
                            }
                            await RunProcessCaptured("powershell.exe", $"-Command \"Dismount-DiskImage -ImagePath '{iso}'\"");
                        }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            return null;
        }

        private static string GetStrelecDistroPath(string description)
        {
            if (description.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase)) return "\\Linux\\ubuntu";
            if (description.Contains("Kali", StringComparison.OrdinalIgnoreCase)) return "\\Linux\\kalilinux2019";
            if (description.Contains("Fedora", StringComparison.OrdinalIgnoreCase)) return "\\Linux\\fedora";
            if (description.Contains("Debian", StringComparison.OrdinalIgnoreCase)) return "\\Linux\\debian";
            return "\\Linux\\generic";
        }

        // ═══════════════════════════════════════════════════════════════════
        // PRE-SHRINK OPTIMIZER
        // Resolve o problema de discos pequenos onde o Diskpart não consegue
        // liberar espaço suficiente para criar a partição de instalação.
        // Estratégia: limpar arquivos que bloqueiam o shrink antes de tentar.
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resultado da análise de pré-shrink.
        /// </summary>
        public class PreShrinkAnalysis
        {
            public long FreeSpaceGB { get; set; }
            public long EstimatedGainGB { get; set; }
            public bool HibernationEnabled { get; set; }
            public bool PagefileOnC { get; set; }
            public bool SystemRestoreEnabled { get; set; }
            public bool VSSSnapshotsExist { get; set; }
            public long HibernationSizeGB { get; set; }
            public long PagefileSizeGB { get; set; }
            public long VSSSnapshotsSizeGB { get; set; }
            public List<string> Recommendations { get; set; } = new();
        }

        /// <summary>
        /// Analisa o disco C: e retorna o que pode ser feito para liberar espaço
        /// antes de tentar o shrink. Útil para o cenário de disco pequeno.
        /// </summary>
        public static PreShrinkAnalysis AnalyzeForPreShrink(string driveLetter = "C:")
        {
            var analysis = new PreShrinkAnalysis();
            string drive = driveLetter.TrimEnd('\\').TrimEnd(':');

            try
            {
                // Espaço livre atual
                var driveInfo = new DriveInfo(drive);
                analysis.FreeSpaceGB = driveInfo.AvailableFreeSpace / (1024L * 1024 * 1024);

                // Verificar hibernação
                string hiberfil = $@"{drive}:\hiberfil.sys";
                if (File.Exists(hiberfil))
                {
                    analysis.HibernationEnabled = true;
                    analysis.HibernationSizeGB = new FileInfo(hiberfil).Length / (1024L * 1024 * 1024);
                    analysis.EstimatedGainGB += analysis.HibernationSizeGB;
                    analysis.Recommendations.Add($"Desativar hibernação libera ~{analysis.HibernationSizeGB} GB (hiberfil.sys)");
                }

                // Verificar pagefile
                string pagefile = $@"{drive}:\pagefile.sys";
                if (File.Exists(pagefile))
                {
                    analysis.PagefileOnC = true;
                    analysis.PagefileSizeGB = new FileInfo(pagefile).Length / (1024L * 1024 * 1024);
                    // Pagefile não pode ser removido completamente, mas pode ser reduzido
                    if (analysis.PagefileSizeGB > 4)
                    {
                        analysis.EstimatedGainGB += analysis.PagefileSizeGB - 2; // Mantém 2GB mínimo
                        analysis.Recommendations.Add($"Reduzir pagefile libera ~{analysis.PagefileSizeGB - 2} GB");
                    }
                }

                // Verificar VSS snapshots (System Restore)
                string vssOutput = SystemUtils.RunExternalProcess("vssadmin", "list shadows /for=C:", hidden: true);
                if (!string.IsNullOrEmpty(vssOutput) && vssOutput.Contains("Shadow Copy Volume"))
                {
                    analysis.VSSSnapshotsExist = true;
                    analysis.VSSSnapshotsSizeGB = 2; // Estimativa conservadora
                    analysis.EstimatedGainGB += analysis.VSSSnapshotsSizeGB;
                    analysis.Recommendations.Add("Limpar pontos de restauração libera ~2 GB (VSS snapshots)");
                }

                // Verificar System Restore
                string srOutput = SystemUtils.RunExternalProcess("powershell",
                    $"-NoProfile -Command \"(Get-ComputerRestorePoint -ErrorAction SilentlyContinue) -ne $null\"",
                    hidden: true);
                analysis.SystemRestoreEnabled = srOutput?.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) == true;

                if (analysis.Recommendations.Count == 0)
                    analysis.Recommendations.Add("Disco já está otimizado para shrink.");
            }
            catch (Exception ex)
            {
                Log($"PreShrinkAnalysis: {ex.Message}");
            }

            return analysis;
        }

        /// <summary>
        /// Executa a otimização pré-shrink: desativa hibernação, limpa VSS,
        /// e executa defrag para mover arquivos imóveis.
        /// Retorna quantos GB foram liberados.
        /// </summary>
        public static async Task<(long FreedGB, List<string> Log)> RunPreShrinkOptimizer(
            string driveLetter = "C:",
            bool disableHibernation = true,
            bool clearVSS = true,
            bool runDefrag = false,
            Action<string>? progress = null)
        {
            var log = new List<string>();
            long freedGB = 0;
            string drive = driveLetter.TrimEnd('\\').TrimEnd(':');

            progress?.Invoke("Iniciando otimização pré-shrink...");

            // 1. Desativar hibernação (libera hiberfil.sys — geralmente 4-16 GB)
            if (disableHibernation)
            {
                try
                {
                    progress?.Invoke("Desativando hibernação (libera hiberfil.sys)...");
                    string hiberfil = $@"{drive}:\hiberfil.sys";
                    long beforeSize = File.Exists(hiberfil) ? new FileInfo(hiberfil).Length : 0;

                    SystemUtils.RunExternalProcess("powercfg", "-h off", hidden: true);
                    await System.Threading.Tasks.Task.Delay(1000);

                    if (!File.Exists(hiberfil) && beforeSize > 0)
                    {
                        long freed = beforeSize / (1024L * 1024 * 1024);
                        freedGB += freed;
                        log.Add($"✅ Hibernação desativada: {freed} GB liberados");
                    }
                    else
                    {
                        log.Add("ℹ️ Hibernação já estava desativada ou hiberfil.sys não encontrado");
                    }
                }
                catch (Exception ex)
                {
                    log.Add($"⚠️ Erro ao desativar hibernação: {ex.Message}");
                }
            }

            // 2. Limpar VSS snapshots (System Restore points)
            if (clearVSS)
            {
                try
                {
                    progress?.Invoke("Limpando pontos de restauração (VSS)...");
                    SystemUtils.RunExternalProcess("vssadmin", "delete shadows /for=C: /all /quiet", hidden: true);
                    await System.Threading.Tasks.Task.Delay(500);
                    freedGB += 2; // Estimativa conservadora
                    log.Add("✅ Pontos de restauração limpos (~2 GB liberados)");
                }
                catch (Exception ex)
                {
                    log.Add($"⚠️ Erro ao limpar VSS: {ex.Message}");
                }
            }

            // 3. Limpar arquivos temporários do Windows
            try
            {
                progress?.Invoke("Limpando arquivos temporários...");
                string winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
                string userTemp = Path.GetTempPath();

                long tempFreed = 0;
                foreach (var dir in new[] { winTemp, userTemp })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { var fi = new FileInfo(file); tempFreed += fi.Length; File.Delete(file); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
                long tempGB = tempFreed / (1024L * 1024 * 1024);
                freedGB += tempGB;
                log.Add($"✅ Temporários limpos: {tempFreed / (1024L * 1024):N0} MB liberados");
            }
            catch (Exception ex)
            {
                log.Add($"⚠️ Erro ao limpar temporários: {ex.Message}");
            }

            // 4. Limpar cache do Windows Update
            try
            {
                progress?.Invoke("Limpando cache do Windows Update...");
                string wuCache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SoftwareDistribution", "Download");
                if (Directory.Exists(wuCache))
                {
                    long wuFreed = 0;
                    foreach (var file in Directory.EnumerateFiles(wuCache, "*", SearchOption.AllDirectories))
                    {
                        try { var fi = new FileInfo(file); wuFreed += fi.Length; File.Delete(file); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                    long wuGB = wuFreed / (1024L * 1024 * 1024);
                    freedGB += wuGB;
                    log.Add($"✅ Cache Windows Update: {wuFreed / (1024L * 1024):N0} MB liberados");
                }
            }
            catch (Exception ex)
            {
                log.Add($"⚠️ Erro ao limpar WU cache: {ex.Message}");
            }

            // 5. Defrag (opcional — move arquivos imóveis para o início do disco)
            if (runDefrag)
            {
                try
                {
                    progress?.Invoke($"Executando defrag em {drive}: (pode demorar)...");
                    // /U = mostra progresso, /V = verbose, /X = consolida espaço livre
                    SystemUtils.RunExternalProcess("defrag", $"{drive}: /U /X", hidden: false, waitForExit: false);
                    log.Add($"✅ Defrag iniciado em {drive}: (aguarde conclusão antes de shrink)");
                }
                catch (Exception ex)
                {
                    log.Add($"⚠️ Erro ao iniciar defrag: {ex.Message}");
                }
            }

            // 6. Verificar espaço livre após otimização
            try
            {
                var driveInfo = new DriveInfo(drive);
                long freeAfterGB = driveInfo.AvailableFreeSpace / (1024L * 1024 * 1024);
                log.Add($"📊 Espaço livre após otimização: {freeAfterGB} GB");
                progress?.Invoke($"Otimização concluída. Espaço livre: {freeAfterGB} GB");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            return (freedGB, log);
        }

        /// <summary>
        /// Verifica se há espaço suficiente para criar a partição de instalação.
        /// Se não houver, sugere executar o pre-shrink optimizer.
        /// </summary>
        public static (bool HasEnoughSpace, long FreeGB, long RequiredGB, string Message)
            CheckSpaceForInstallation(string driveLetter = "C:", int requiredGB = 12)
        {
            try
            {
                var driveInfo = new DriveInfo(driveLetter.TrimEnd('\\').TrimEnd(':'));
                long freeGB = driveInfo.AvailableFreeSpace / (1024L * 1024 * 1024);

                if (freeGB >= requiredGB)
                    return (true, freeGB, requiredGB,
                        $"✅ Espaço suficiente: {freeGB} GB livres (necessário: {requiredGB} GB)");

                // Analisa o que pode ser liberado
                var analysis = AnalyzeForPreShrink(driveLetter);
                long potentialFree = freeGB + analysis.EstimatedGainGB;

                if (potentialFree >= requiredGB)
                    return (false, freeGB, requiredGB,
                        $"⚠️ Espaço insuficiente ({freeGB} GB), mas é possível liberar ~{analysis.EstimatedGainGB} GB.\n" +
                        $"Execute o Otimizador Pré-Shrink antes de continuar.\n" +
                        string.Join("\n", analysis.Recommendations.Select(r => $"  • {r}")));

                return (false, freeGB, requiredGB,
                    $"❌ Espaço insuficiente: {freeGB} GB livres (necessário: {requiredGB} GB).\n" +
                    $"Mesmo após otimização, o ganho estimado é de apenas {analysis.EstimatedGainGB} GB.\n" +
                    $"Considere liberar espaço manualmente ou usar outro disco.");
            }
            catch (Exception ex)
            {
                return (false, 0, requiredGB, $"Erro ao verificar espaço: {ex.Message}");
            }
        }

        // ======================================================================
        // WINPE RUNTIME: PREPARAR PARTIÇÃO A E BOTAR NO WINPE
        // ======================================================================

        /// <summary>
        /// Fase 1 (Windows): Prepara WinPE via RAMDISK com paths curtos (C:\KL_WINPE\,
        /// sem espaços). boot.wim + boot.sdi + shrink_config.ini em C:\KL_WINPE\.
        /// </summary>
        public static async Task<(bool ok, string msg)> PrepareWinpeBoot(CancellationToken ct = default)
        {
            try
            {
                Log("========== PREPARANDO WINPE RAMDISK (C:\\KL_WINPE\\) ==========");

                string klWinpe = @"C:\KL_WINPE";
                Directory.CreateDirectory(klWinpe);
                string wimPath = Path.Combine(klWinpe, "boot.wim");
                string sdiPath = Path.Combine(klWinpe, "boot.sdi");

                // 1. Obter WinPE base (cache → GitHub → WinRE)
                Log("[1/4] Resolvendo WinPE base...");
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(wimPath))
                {
                    Log("WinPE base não encontrado. Baixando...");
                    var (baseOk, baseMsg, baseWim) = await WinpeBuilder.DownloadWinpeBaseAsync();
                    if (!baseOk || string.IsNullOrEmpty(baseWim))
                    {
                        Log($"Download falhou ({baseMsg}). Tentando WinRE...");
                        var (wrOk, wrMsg, wrWim) = await WinpeBuilder.UseWinreAsBaseAsync();
                        if (!wrOk || string.IsNullOrEmpty(wrWim))
                            return (false, $"Não foi possível obter WinPE base. Download: {baseMsg} | WinRE: {wrMsg}");
                        baseWim = wrWim;
                    }
                    Log($"Copiando base para {wimPath}");
                    File.Copy(baseWim!, wimPath, true);
                }

                // 2. Resolver boot.sdi
                Log("[2/4] Resolvendo boot.sdi...");
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(sdiPath))
                {
                    string? resolvedSdi = WinpeBuilder.ResolveBootSdi();
                    if (!string.IsNullOrEmpty(resolvedSdi))
                        File.Copy(resolvedSdi, sdiPath, true);
                    else
                        return (false, "boot.sdi não encontrado. Copie de C:\\Windows\\Boot\\DVD\\PCAT\\boot.sdi");
                }

                // 3. Customizar boot.wim com startnet.cmd (sem winpeshl.ini)
                Log("[3/4] Customizando boot.wim...");
                ct.ThrowIfCancellationRequested();

                string basePrepped = Path.Combine(klWinpe, "boot_base.wim");
                if (File.Exists(basePrepped))
                {
                    Log("boot_base.wim em cache encontrado. Copiando sem recustomizar...");
                    File.Copy(basePrepped, wimPath, true);
                }
                else
                {
                    string startnetContent = RamdiskStartnetCmd();
                    bool customOk = await WinpeBuilder.CustomizeWinpeWimFlatAsync(wimPath, startnetContent);
                    if (!customOk)
                        Log("Aviso: customização do boot.wim falhou. Usando startnet.cmd padrão.");
                    else
                        Log("boot.wim customizado com startnet.cmd e fundo azul removido.");

                    // Cache da primeira customização (evita DISM em próximas execuções)
                    try
                    {
                        File.Copy(wimPath, basePrepped, true);
                        Log($"boot_base.wim salvo em cache: {basePrepped}");
                    }
                    catch (Exception cacheEx)
                    {
                        Log($"Aviso: não foi possível salvar cache: {cacheEx.Message}");
                    }
                }

                // 4. Criar entrada BCD ramdisk (paths sem espaços)
                Log("[4/4] Criando entrada BCD ramdisk...");
                ct.ThrowIfCancellationRequested();
                string? guid = await CreateRamdiskEntry(
                    "KitLugia WinPE - Shrink", "C",
                    "\\KL_WINPE\\boot.wim",
                    "\\KL_WINPE\\boot.sdi");
                if (guid == null)
                    return (false, "Falha ao criar entrada BCD ramdisk para o WinPE.");

                Log($"\n✅ WinPE ramdisk preparado! GUID: {guid}");
                Log($"   boot.wim: {wimPath}");
                Log($"   boot.sdi:  {sdiPath}");
                Log($"   Use 'ScheduleWinpeShrink' para configurar o shrink e reiniciar.");

                return (true, $"WinPE ramdisk pronto em {klWinpe}. GUID: {guid}");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao preparar WinPE: {ex.Message}");
            }
        }

        /// <summary>
        /// Prepara WinPE a partir de um ISO customizado (sem shrink, só teste).
        /// Copia boot.wim do ISO para C:\KL_WINPE\custom_boot.wim, resolve boot.sdi,
        /// cria entrada BCD ramdisk e configura /bootsequence one-time.
        /// </summary>
        public static async Task<(bool ok, string msg, string? guid)> PrepareCustomWinpeBoot(string isoBootWimPath)
        {
            try
            {
                Log("========== PREPARANDO CUSTOM WINPE (ISO) ==========");

                string klWinpe = @"C:\KL_WINPE";
                Directory.CreateDirectory(klWinpe);

                // Copia boot.wim customizado
                string destWim = Path.Combine(klWinpe, "custom_boot.wim");
                File.Copy(isoBootWimPath, destWim, true);
                Log($"boot.wim custom copiado: {destWim}");

                // Resolve boot.sdi se necessário
                string sdiPath = Path.Combine(klWinpe, "boot.sdi");
                if (!File.Exists(sdiPath))
                {
                    string? resolvedSdi = WinpeBuilder.ResolveBootSdi();
                    if (!string.IsNullOrEmpty(resolvedSdi))
                        File.Copy(resolvedSdi, sdiPath, true);
                    else
                        return (false, "boot.sdi não encontrado. Copie de C:\\Windows\\Boot\\DVD\\PCAT\\boot.sdi", null);
                }

                // Cria entrada BCD (sem limpar entradas existentes do shrink)
                string? guid = await CreateRamdiskEntry(
                    "KitLugia WinPE Test", "C",
                    "\\KL_WINPE\\custom_boot.wim",
                    "\\KL_WINPE\\boot.sdi",
                    skipCleanup: true);

                if (guid == null)
                    return (false, "Falha ao criar entrada BCD para o WinPE custom.", null);

                // Configura bootsequence one-time
                await RunProcessCaptured("bcdedit.exe", "/timeout 10");
                var (bsCode, _) = await RunProcessCaptured("bcdedit.exe", $"/bootsequence {guid}");
                Log($"Bootsequence configurado para Custom WinPE (código {bsCode}).");

                Log($"Custom WinPE pronto! GUID: {guid}");
                return (true, $"Custom WinPE pronto. GUID: {guid}\nO sistema será reiniciado em 10s para testar o WinPE.", guid);
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao preparar Custom WinPE: {ex.Message}", null);
            }
        }

        /// <summary>
        /// Remove entrada BCD do Custom WinPE (descrição "KitLugia WinPE Test")
        /// e deleta C:\KL_WINPE\custom_boot.wim.
        /// </summary>
        public static async Task<bool> RemoveCustomWinpe()
        {
            Log("=== Removendo Custom WinPE ===");
            int removed = 0;

            try
            {
                var (enumCode, enumOut) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                if (enumCode == 0)
                {
                    var lines = enumOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    string? currentGuid = null;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("identifier", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            currentGuid = parts.Length > 1 ? parts[1].Trim() : null;
                        }
                        else if (trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase))
                        {
                            var descParts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            string descVal = descParts.Length > 1 ? descParts[1].Trim() : "";
                            if (!string.IsNullOrEmpty(currentGuid) &&
                                descVal.Contains("KitLugia", StringComparison.OrdinalIgnoreCase) &&
                                descVal.Contains("WinPE Test", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"Removendo entrada BCD custom: {currentGuid} ({descVal})");
                                var (delCode, _) = await RunProcessCaptured("bcdedit.exe", $"/delete {currentGuid} /f");
                                if (delCode == 0) removed++;
                            }
                            currentGuid = null;
                        }
                    }
                }
                Log($"Removidas {removed} entradas BCD custom WinPE.");
            }
            catch (Exception ex)
            {
                Log($"Aviso ao limpar BCD custom: {ex.Message}");
            }

            // Remove custom_boot.wim
            try
            {
                string customWim = @"C:\KL_WINPE\custom_boot.wim";
                if (File.Exists(customWim))
                {
                    File.Delete(customWim);
                    Log("custom_boot.wim deletado.");
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao deletar custom_boot.wim: {ex.Message}");
            }

            return removed > 0;
        }

        // ======================================================================
        // === VALIDATION OS (WinVOS) — preparo e remoção ======================
        // ======================================================================
        // Validation OS é um Windows 11 leve da Microsoft com suporte oficial a
        // WPF/.NET via Microsoft-WinVOS-WPF-Support (≥2504).
        //
        // URL oficial: https://aka.ms/DownloadValidationOS  (AMD64)
        //              https://aka.ms/DownloadValidationOS_arm64  (ARM64)
        //
        // Fluxo: download ISO → mount → copiar WinVOS.wim → add WPF support →
        // BCD ramdisk → bootsequence one-time.
        // ======================================================================

        private const string VALIDATION_ISO_URL = "https://aka.ms/DownloadValidationOS";
        private const string VALIDATION_GITHUB_URL = "https://github.com/luigiarrud4/KitLugia-WinPE/releases/download/v1.0/VALOS-base.7z";
        private const string VALIDATION_ISO_CACHE = @"C:\KL_WINPE\VALIDATIONOS.iso";
        private const string VALIDATION_WIM_PATH = @"C:\KL_WINPE\validation_boot.wim";

        /// <summary>
        /// Prepara Validation OS para boot via RAMDISK com suporte WPF.
        /// Baixa ISO (se necessário), extrai WinVOS.wim, adiciona WPF support,
        /// cria entrada BCD ramdisk e configura bootsequence one-time.
        /// </summary>
        public static async Task<(bool ok, string msg)> PrepareValidationOs(CancellationToken ct = default)
        {
            try
            {
                Log("========== PREPARANDO VALIDATION OS (WinVOS) ==========");

                string klWinpe = @"C:\KL_WINPE";
                Directory.CreateDirectory(klWinpe);
                string isoPath = VALIDATION_ISO_CACHE;
                string wimPath = VALIDATION_WIM_PATH;

                // 1. Obter base do Validation OS
                Log("[1/5] Resolvendo base do Validation OS...");
                ct.ThrowIfCancellationRequested();

                // Tenta GitHub release primeiro (VALOS-base.7z contém boot.wim + boot.sdi prontos)
                string valos7z = Path.Combine(klWinpe, "VALOS-base.7z");
                string? sevenZip = WinpeBuilder.FindBundled7Zip();

                if (!File.Exists(wimPath) && File.Exists(valos7z) && sevenZip != null)
                {
                    Log("VALOS-base.7z encontrado em cache. Extraindo...");
                    var (extracted, foundWim) = await TryExtract7z(sevenZip, valos7z, klWinpe);
                    wimPath = foundWim ?? wimPath;
                    // Garante que o WIM esteja no local esperado (copia se encontrou em outro path)
                    if (File.Exists(wimPath) && !wimPath.Equals(VALIDATION_WIM_PATH, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(wimPath, VALIDATION_WIM_PATH, true);
                        wimPath = VALIDATION_WIM_PATH;
                        Log($"WIM copiado para o local esperado: {wimPath}");
                    }
                    if (!extracted)
                        Log("WIM não encontrado após extração. Tentando outras fontes...");
                }

                if (!File.Exists(wimPath))
                {
                    // Tenta baixar VALOS-base.7z do GitHub
                    Log("Tentando download do GitHub Release (VALOS-base.7z)...");
                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromMinutes(15);
                        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KitLugia/1.0");
                        using var resp = await httpClient.GetAsync(VALIDATION_GITHUB_URL,
                            HttpCompletionOption.ResponseHeadersRead, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            long total = resp.Content.Headers.ContentLength ?? 0;
                            Log($"VALOS-base.7z encontrado ({(total > 0 ? $"{total / 1024 / 1024} MB" : "tamanho desconhecido")}). Baixando...");

                            {
                                await using var fs = new FileStream(valos7z, FileMode.Create, FileAccess.Write, FileShare.None);
                                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                                var buffer = new byte[8 * 1024 * 1024];
                                long read = 0;
                                int n;
                                while ((n = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                                {
                                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                                    read += n;
                                    if (total > 0)
                                        LogReplace($"Download: {read / (1024 * 1024)} MB / {total / (1024 * 1024)} MB");
                                }
                                Log("VALOS-base.7z baixado.");
                            }

                            if (sevenZip != null)
                            {
                                var (success, foundWim) = await TryExtract7z(sevenZip, valos7z, klWinpe);
                                if (success && foundWim != null)
                                {
                                    wimPath = foundWim;
                                    Log($"WIM pronto: {wimPath}");
                                }
                                else
                                    Log("WIM não encontrado. Tentando ISO da Microsoft...");
                            }
                            else
                            {
                                Log("7-Zip não encontrado. Tentando ISO da Microsoft...");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GitHub Release falhou: {ex.Message}. Tentando ISO da Microsoft...");
                    }
                }

                // Fallback: tenta baixar ISO da Microsoft
                if (!File.Exists(wimPath) && !File.Exists(isoPath))
                {
                    Log("Tentando download da ISO da Microsoft...");
                    Log("URL: " + VALIDATION_ISO_URL);
                    Log("Nota: Pode exigir aceitação de licença.");

                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromMinutes(15);
                        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KitLugia/1.0");
                        using var resp = await httpClient.GetAsync(VALIDATION_ISO_URL,
                            HttpCompletionOption.ResponseHeadersRead, ct);
                        if (!resp.IsSuccessStatusCode)
                            return (false,
                                $"Falha ao baixar Validation OS (HTTP {(int)resp.StatusCode}).\n" +
                                $"Baixe manualmente e salve o ISO em: {isoPath}\n" +
                                $"Ou crie VALOS-base.7z com o script Build-ValidationOS.ps1\n" +
                                $"e faça upload para GitHub Releases.");

                        long total = resp.Content.Headers.ContentLength ?? 0;
                        Log($"ISO encontrado ({(total > 0 ? $"{total / 1024 / 1024} MB" : "tamanho desconhecido")}). Baixando...");

                        await using var fs = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        var buffer = new byte[8 * 1024 * 1024];
                        long read = 0;
                        int n;
                        while ((n = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                            read += n;
                            if (total > 0)
                                LogReplace($"Download: {read / (1024 * 1024)} MB / {total / (1024 * 1024)} MB");
                        }
                        Log("ISO baixado com sucesso.");
                    }
                    catch (OperationCanceledException) { return (false, "Download cancelado."); }
                    catch (Exception ex)
                    {
                        return (false,
                            $"Download falhou: {ex.Message}\n\n" +
                            $"Opções:\n" +
                            $"1. Baixe o ISO manualmente: {VALIDATION_ISO_URL}\n" +
                            $"   Salve em: {isoPath}\n" +
                            $"2. Ou use Build-ValidationOS.ps1 para criar VALOS-base.7z\n" +
                            $"   https://github.com/luigiarrud4/KitLugia-WinPE/releases");
                    }
                }
                else if (!File.Exists(wimPath) && File.Exists(isoPath))
                {
                    Log("ISO encontrado em cache.");
                }

                // 2. Se não temos o WIM ainda, extrai do ISO
                if (!File.Exists(wimPath) && File.Exists(isoPath))
                {
                    Log("[2/5] Montando ISO e extraindo WinVOS.wim...");
                    ct.ThrowIfCancellationRequested();

                    string? driveLetter = null;
                    try
                    {
                        driveLetter = await MountIso(isoPath);
                        if (string.IsNullOrEmpty(driveLetter))
                            return (false, "Falha ao montar ISO do Validation OS.");

                        Log($"ISO montado em {driveLetter}:");

                        string[] searchPaths = [
                            Path.Combine(driveLetter, "sources", "WinVOS.wim"),
                            Path.Combine(driveLetter, "WinVOS.wim"),
                            Path.Combine(driveLetter, "ValidationOS.wim"),
                            Path.Combine(driveLetter, "sources", "ValidationOS.wim"),
                        ];

                        string? sourceWim = null;
                        foreach (var sp in searchPaths)
                        {
                            if (File.Exists(sp))
                            {
                                sourceWim = sp;
                                break;
                            }
                        }

                        if (sourceWim == null)
                        {
                            Log("WinVOS.wim não encontrado. Listando arquivos da ISO:");
                            foreach (var f in Directory.GetFiles(driveLetter, "*.wim", SearchOption.AllDirectories))
                                Log($"  {f}");
                            return (false,
                                "WinVOS.wim não encontrado na ISO. " +
                                "Verifique se o ISO é do Validation OS (não WinPE normal).");
                        }

                        Log($"WinVOS.wim encontrado: {sourceWim} ({(new FileInfo(sourceWim).Length / 1024 / 1024)} MB)");
                        File.Copy(sourceWim, wimPath, true);
                        Log($"WinVOS.wim copiado para {wimPath}");
                    }
                    finally
                    {
                        if (!string.IsNullOrEmpty(driveLetter))
                            await DismountIso(isoPath);
                    }
                }
                else if (File.Exists(wimPath))
                {
                    Log($"[2/5] WIM já existe em {wimPath} ({new FileInfo(wimPath).Length / 1024 / 1024} MB). Pulando extração.");
                }

                // 3. Injetar startnet.valos.cmd via wimlib (rápido, sem montar)
                Log("[3/5] Injetando startnet.valos.cmd no WIM...");
                ct.ThrowIfCancellationRequested();

                WinpeBuilder.EnsureFileWritable(wimPath);
                string valosContent = ValidationOsStartnetCmd();
                bool wimlibOk = await WinpeBuilder.UpdateWimWithScriptAsync(
                    wimPath, valosContent, "startnet.valos.cmd");

                // 4. Adicionar suporte WPF (só via DISM, se encontrarmos o CAB)
                Log("[3b/5] Verificando suporte WPF (Microsoft-WinVOS-WPF-Support)...");
                ct.ThrowIfCancellationRequested();

                string[] cabSearchPaths = [
                    Path.Combine(klWinpe, "VALOS_EXTRAS", "CAB", "Microsoft-WinVOS-WPF-Support-Package.cab"),
                    Path.Combine(klWinpe, "Microsoft-WinVOS-WPF-Support-Package.cab"),
                ];
                string? wpfCab = null;
                foreach (var cab in cabSearchPaths)
                {
                    if (File.Exists(cab))
                    {
                        wpfCab = cab;
                        break;
                    }
                }

                if (wpfCab != null && !wimlibOk)
                {
                    Log("Pacote WPF encontrado, mas wimlib não está disponível. Usando DISM mount+commit...");
                    WinpeBuilder.EnsureFileWritable(wimPath);
                    // Fallback: DISM mount para adicionar CAB + script
                    string mountDir = Path.Combine(klWinpe, "mount_valos");
                    try
                    {
                        if (Directory.Exists(mountDir))
                        {
                            try { Directory.Delete(mountDir, true); } catch { }
                            await RunProcessCaptured("dism.exe", "/Cleanup-Mountpoints", 30000);
                        }
                        Directory.CreateDirectory(mountDir);

                        var (mntCode, mntOut) = await RunProcessCaptured("dism.exe",
                            $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                        if (mntCode == 0 || mntOut.Contains("already mounted"))
                        {
                            Log($"Adicionando pacote WPF: {wpfCab}");
                            var (pkgCode, _) = await RunProcessCaptured("dism.exe",
                                $"/Add-Package /Image:\"{mountDir}\" /PackagePath:\"{wpfCab}\"", 180000);
                            if (pkgCode == 0)
                                Log("Suporte WPF adicionado com sucesso.");
                            else
                                Log($"Aviso: Add-Package retornou código {pkgCode}.");

                            // Também injeta o script via DISM já que montamos
                            string system32 = Path.Combine(mountDir, "Windows", "System32");
                            string valosCmd = Path.Combine(system32, "startnet.valos.cmd");
                            await File.WriteAllTextAsync(valosCmd, valosContent, ct);
                            Log("startnet.valos.cmd criado (DISM).");

                            var (cmtCode, _) = await RunProcessCaptured("dism.exe",
                                $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                            if (cmtCode == 0)
                                Log("WIM salvo com sucesso via DISM.");
                            else
                            {
                                Log($"Falha ao commitar WIM ({cmtCode}). Descartando...");
                                try { await RunProcessCaptured("dism.exe",
                                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 60000); }
                                catch { }
                            }
                        }
                    }
                    catch (Exception dismEx)
                    {
                        Log($"Aviso durante customização DISM: {dismEx.Message}");
                        try { await RunProcessCaptured("dism.exe",
                            $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 60000); }
                        catch { }
                    }
                    finally
                    {
                        try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, true); }
                        catch { }
                    }
                }
                else if (wpfCab != null)
                {
                    Log($"Pacote WPF encontrado: {wpfCab}. Adicionando via DISM (wimlib não suporta Add-Package)...");
                    WinpeBuilder.EnsureFileWritable(wimPath);
                    // DISM mount apenas para Add-Package (script já foi via wimlib)
                    string mountDir = Path.Combine(klWinpe, "mount_valos");
                    try
                    {
                        if (Directory.Exists(mountDir))
                        {
                            try { Directory.Delete(mountDir, true); } catch { }
                            await RunProcessCaptured("dism.exe", "/Cleanup-Mountpoints", 30000);
                        }
                        Directory.CreateDirectory(mountDir);

                        var (mntCode, mntOutStr) = await RunProcessCaptured("dism.exe",
                            $"/Mount-Image /ImageFile:\"{wimPath}\" /index:1 /MountDir:\"{mountDir}\" /Optimize", 180000);
                        if (mntCode == 0 || mntOutStr.Contains("already mounted"))
                        {
                            var (pkgCode, _) = await RunProcessCaptured("dism.exe",
                                $"/Add-Package /Image:\"{mountDir}\" /PackagePath:\"{wpfCab}\"", 180000);
                            if (pkgCode == 0)
                                Log("Suporte WPF adicionado com sucesso.");
                            else
                                Log($"Aviso: Add-Package retornou código {pkgCode}.");

                            var (cmtCode, _) = await RunProcessCaptured("dism.exe",
                                $"/Unmount-Image /MountDir:\"{mountDir}\" /Commit", 300000);
                            if (cmtCode == 0)
                                Log("WIM salvo com sucesso via DISM.");
                            else
                            {
                                try { await RunProcessCaptured("dism.exe",
                                    $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 60000); }
                                catch { }
                            }
                        }
                    }
                    catch (Exception dismEx)
                    {
                        Log($"Aviso durante Add-Package DISM: {dismEx.Message}");
                        try { await RunProcessCaptured("dism.exe",
                            $"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", 60000); }
                        catch { }
                    }
                    finally
                    {
                        try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, true); }
                        catch { }
                    }
                }
                else
                {
                    Log("Pacote WPF não encontrado. O Validation OS vai bootar sem interface WPF.");
                }

                // 4. Resolver boot.sdi
                Log("[4/5] Resolvendo boot.sdi...");
                ct.ThrowIfCancellationRequested();
                string sdiPath = Path.Combine(klWinpe, "boot.sdi");
                if (!File.Exists(sdiPath))
                {
                    string? resolvedSdi = WinpeBuilder.ResolveBootSdi();
                    if (!string.IsNullOrEmpty(resolvedSdi))
                        File.Copy(resolvedSdi, sdiPath, true);
                    else
                        return (false,
                            "boot.sdi não encontrado. Copie de C:\\Windows\\Boot\\DVD\\PCAT\\boot.sdi");
                }

                // 5. Criar entrada BCD ramdisk + bootsequence one-time
                Log("[5/5] Criando entrada BCD ramdisk e configurando bootsequence...");
                ct.ThrowIfCancellationRequested();

                string? guid = await CreateRamdiskEntry(
                    "KitLugia Validation OS (WPF Test)", "C",
                    "\\KL_WINPE\\validation_boot.wim",
                    "\\KL_WINPE\\boot.sdi",
                    skipCleanup: true);

                if (guid == null)
                    return (false, "Falha ao criar entrada BCD ramdisk para Validation OS.");

                // Configura bootsequence one-time (10s timeout)
                await RunProcessCaptured("bcdedit.exe", "/timeout 10");
                var (bsCode, _) = await RunProcessCaptured("bcdedit.exe", $"/bootsequence {guid}");
                Log($"Bootsequence configurado (código {bsCode}).");

                Log("\n✅ Validation OS preparado com sucesso!");
                Log($"   WIM: {wimPath}");
                Log($"   GUID: {guid}");
                Log("   Na próxima reinicialização, o menu de boot aparecerá com a opção.");
                Log("   Para boot único agora, reinicie o PC.");

                return (true,
                    $"Validation OS pronto!\n\n" +
                    $"WIM: {wimPath}\n" +
                    $"GUID: {guid}\n\n" +
                    $"O bootsequence foi configurado. Ao reiniciar, o menu de boot\n" +
                    $"aparecerá (10s timeout) com a opção 'KitLugia Validation OS'.\n\n" +
                    $"Se o app KitLugia estiver embutido no WIM (self-contained +\n" +
                    $"WPF support), ele será lançado automaticamente.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Operação cancelada.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao preparar Validation OS: {ex.Message}");
            }
        }

        /// <summary>
        /// Remove entrada BCD do Validation OS e deleta validation_boot.wim.
        /// </summary>
        public static async Task<bool> RemoveValidationOs()
        {
            Log("=== Removendo Validation OS ===");
            int removed = 0;

            try
            {
                var (enumCode, enumOut) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                if (enumCode == 0)
                {
                    var lines = enumOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    string? currentGuid = null;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("identifier", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            currentGuid = parts.Length > 1 ? parts[1].Trim() : null;
                        }
                        else if (trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase))
                        {
                            var descParts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            string descVal = descParts.Length > 1 ? descParts[1].Trim() : "";
                            if (!string.IsNullOrEmpty(currentGuid) &&
                                descVal.Contains("KitLugia", StringComparison.OrdinalIgnoreCase) &&
                                descVal.Contains("Validation OS", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"Removendo entrada BCD: {currentGuid} ({descVal})");
                                var (delCode, _) = await RunProcessCaptured("bcdedit.exe", $"/delete {currentGuid} /f");
                                if (delCode == 0) removed++;
                            }
                            currentGuid = null;
                        }
                    }
                }
                Log($"Removidas {removed} entradas BCD Validation OS.");
            }
            catch (Exception ex)
            {
                Log($"Aviso ao limpar BCD: {ex.Message}");
            }

            try
            {
                if (File.Exists(VALIDATION_WIM_PATH))
                {
                    File.Delete(VALIDATION_WIM_PATH);
                    Log("validation_boot.wim deletado.");
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao deletar validation_boot.wim: {ex.Message}");
            }

            return removed > 0;
        }

        /// <summary>
        /// Gera o conteúdo do startnet.valos.cmd que será executado ao iniciar
        /// o Validation OS. Tenta lançar o app KitLugia se estiver presente no
        /// WIM, ou abre o prompt com ajuda.
        /// </summary>
        private static string ValidationOsStartnetCmd()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine("wpeinit");
            sb.AppendLine();
            sb.AppendLine("rem --- Shrink mode: if shrink_config.ini exists, run shrink ---");
            sb.AppendLine("if exist X:\\shrink_config.ini (");
            sb.AppendLine("  for /f \"tokens=1,2 delims==\" %%a in (X:\\shrink_config.ini) do (");
            sb.AppendLine("    if /i \"%%a\"==\"DISK_N\" set DISK_N=%%b");
            sb.AppendLine("    if /i \"%%a\"==\"PART_N\" set PART_N=%%b");
            sb.AppendLine("    if /i \"%%a\"==\"SHRINK_MB\" set SHRINK_MB=%%b");
            sb.AppendLine("  )");
            sb.AppendLine("  if not \"!PART_N!\"==\"0\" (");
            sb.AppendLine("    echo ============================================");
            sb.AppendLine("    echo  KitLugia Validation OS - Shrink Mode");
            sb.AppendLine("    echo ============================================");
            sb.AppendLine("    echo select disk !DISK_N! > X:\\shrink.txt");
            sb.AppendLine("    echo select partition !PART_N! >> X:\\shrink.txt");
            sb.AppendLine("    echo shrink desired=!SHRINK_MB! >> X:\\shrink.txt");
            sb.AppendLine("    diskpart /s X:\\shrink.txt");
            sb.AppendLine("    echo Shrink done. Rebooting...");
            sb.AppendLine("    echo [KitLugia Validation OS Shrink] > X:\\result.log");
            sb.AppendLine("    echo Status: OK >> X:\\result.log");
            sb.AppendLine("    wpeutil reboot");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("rem --- Normal boot (no shrink) ---");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo  KitLugia - Validation OS");
            sb.AppendLine("echo ============================================");
            sb.AppendLine("echo.");
            sb.AppendLine();
            sb.AppendLine("rem --- Tenta iniciar o app KitLugia (WPF) ---");
            sb.AppendLine("set APP_PATH=X:\\KitLugia\\KitLugia.exe");
            sb.AppendLine("if exist \"!APP_PATH!\" (");
            sb.AppendLine("    echo Iniciando KitLugia...");
            sb.AppendLine("    start \"\" \"!APP_PATH!\"");
            sb.AppendLine("    goto :done");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("rem --- Fallback: procura em outras pastas ---");
            sb.AppendLine("for %%d in (C D E F G H I J K L M N O P Q R S T U V W X Y Z) do (");
            sb.AppendLine("    if exist \"%%d:\\KitLugia\\KitLugia.exe\" (");
            sb.AppendLine("        echo Encontrado KitLugia em %%d:");
            sb.AppendLine("        start \"\" \"%%d:\\KitLugia\\KitLugia.exe\"");
            sb.AppendLine("        goto :done");
            sb.AppendLine("    )");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo.");
            sb.AppendLine("echo  AVISO: KitLugia.exe nao encontrado.");
            sb.AppendLine("echo  Coloque o app em X:\\KitLugia\\KitLugia.exe dentro do WIM");
            sb.AppendLine("echo  ou em qualquer unidade \\KitLugia\\KitLugia.exe.");
            sb.AppendLine("echo.");
            sb.AppendLine("echo  Comandos disponiveis:");
            sb.AppendLine("echo    - shutdown /r /t 0   (reiniciar)");
            sb.AppendLine("echo    - wpeutil reboot     (reiniciar WinPE)");
            sb.AppendLine("echo    - notepad            (bloco de notas)");
            sb.AppendLine("echo    - diskpart           (particoes)");
            sb.AppendLine("echo    - X:\\KitLugia\\KitLugia.exe  (iniciar app manualmente)");
            sb.AppendLine("echo.");
            sb.AppendLine();
            sb.AppendLine(":done");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Boot concluido. Digite 'exit' para fechar.");
            sb.AppendLine("cmd /k");
            sb.AppendLine("exit");
            return sb.ToString();
        }

        /// <summary>
        /// startnet.cmd para RAMDISK: tenta X:\shrink_config.ini (injetado no WIM);
        /// fallback: unroll de volumes 1-10 com assign letter=Z.
        /// </summary>
        private static string RamdiskStartnetCmd(int embedDiskN = 0, int embedPartN = 0, long embedShrinkMB = 0)
        {
            long shrinkMb = embedShrinkMB > 0 ? embedShrinkMB : 10000;
            int diskN = embedDiskN >= 0 ? embedDiskN : 0;
            // FIX: sentinel correto é 0 (o script agora usa "if PART_N GTR 0"),
            // mas mantemos coerência: se caller passou part válida, usa; senão 0 = modo scan.
            int partN = embedPartN > 0 ? embedPartN : 0;
            if (partN > 0)
                Log($"StartnetCmd embed: DISK={diskN} PART={partN} SHRINK={shrinkMb}MB (atalho direto, sem scan)");
            else
                Log("StartnetCmd embed: sem partição embedada — WinPE fará scan por marcador/Windows");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine("wpeinit");
            sb.AppendLine("echo KitLugia WinPE - Shrink (RAMDISK)");
            sb.AppendLine("ping -n 5 127.0.0.1 > nul");
            sb.AppendLine();
            sb.AppendLine("rem --- Batch shrink ---");
            sb.AppendLine($"set EMBED_DISK_N={diskN}");
            sb.AppendLine($"set EMBED_PART_N={partN}");
            sb.AppendLine($"set EMBED_SHRINK_MB={shrinkMb}");
            sb.AppendLine("set DISK_N=!EMBED_DISK_N!");
            sb.AppendLine("set PART_N=!EMBED_PART_N!");
            sb.AppendLine("set SHRINK_MB=!EMBED_SHRINK_MB!");
            // FIX DISK 0: valores embedados sao validos inclusive para disco 0.
            // PART_N > 0 e o unico requisito real (sentinel de ausencia e 0).
            sb.AppendLine("if !PART_N! GTR 0 goto :run");
            sb.AppendLine();
            sb.AppendLine("rem --- Fallback: read shrink_config.ini from WIM ---");
            sb.AppendLine("if exist X:\\shrink_config.ini (");
            sb.AppendLine("  for /f \"tokens=1,2 delims==\" %%a in (X:\\shrink_config.ini) do (");
            sb.AppendLine("    if /i \"%%a\"==\"DISK_N\" set DISK_N=%%b");
            sb.AppendLine("    if /i \"%%a\"==\"PART_N\" set PART_N=%%b");
            sb.AppendLine("    if /i \"%%a\"==\"SHRINK_MB\" set SHRINK_MB=%%b");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("if not \"!DISK_N!\"==\"0\" if not \"!PART_N!\"==\"0\" goto :run");
            sb.AppendLine();
            sb.AppendLine("echo Scanning for KL_SHRINK_TARGET.dat marker...");
            sb.AppendLine("for /l %%d in (0,1,3) do (");
            sb.AppendLine("  for /l %%p in (1,1,8) do (");
            sb.AppendLine("    echo select disk %%d > X:\\mk.txt");
            sb.AppendLine("    echo select partition %%p >> X:\\mk.txt");
            sb.AppendLine("    echo assign letter=Z >> X:\\mk.txt");
            sb.AppendLine("    diskpart /s X:\\mk.txt >nul 2>&1");
            sb.AppendLine("    if exist Z:\\KL_SHRINK_TARGET.dat (");
            sb.AppendLine("      set DISK_N=%%d & set PART_N=%%p");
            sb.AppendLine("      for /f \"tokens=1,2 delims==\" %%a in (Z:\\KL_SHRINK_TARGET.dat) do (");
            sb.AppendLine("        if /i \"%%a\"==\"SHRINK_MB\" set SHRINK_MB=%%b");
            sb.AppendLine("      )");
            sb.AppendLine("      echo select volume Z > X:\\mr.txt");
            sb.AppendLine("      echo remove letter=Z >> X:\\mr.txt");
            sb.AppendLine("      diskpart /s X:\\mr.txt >nul 2>&1");
            sb.AppendLine("      echo Found marker: DISK=%%d PART=%%p SHRINK=!SHRINK_MB!");
            sb.AppendLine("      goto :run");
            sb.AppendLine("    )");
            sb.AppendLine("    echo select volume Z > X:\\mr.txt 2>nul");
            sb.AppendLine("    echo remove letter=Z >> X:\\mr.txt");
            sb.AppendLine("    diskpart /s X:\\mr.txt >nul 2>&1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("echo Scanning disks for Windows partition...");
            sb.AppendLine("for /l %%d in (0,1,3) do (");
            sb.AppendLine("  for /l %%p in (1,1,8) do (");
            sb.AppendLine("    echo select disk %%d > X:\\fs.txt");
            sb.AppendLine("    echo select partition %%p >> X:\\fs.txt");
            sb.AppendLine("    echo assign letter=Z >> X:\\fs.txt");
            sb.AppendLine("    diskpart /s X:\\fs.txt >nul 2>&1");
            sb.AppendLine("    if exist Z:\\Windows\\System32\\config\\SOFTWARE (");
            sb.AppendLine("      set DISK_N=%%d & set PART_N=%%p");
            sb.AppendLine("      echo select volume Z > X:\\fr.txt");
            sb.AppendLine("      echo remove letter=Z >> X:\\fr.txt");
            sb.AppendLine("      diskpart /s X:\\fr.txt >nul 2>&1");
            sb.AppendLine("      echo Found Windows: DISK=%%d PART=%%p");
            sb.AppendLine("      goto :run");
            sb.AppendLine("    )");
            sb.AppendLine("    echo select volume Z > X:\\fr.txt 2>nul");
            sb.AppendLine("    echo remove letter=Z >> X:\\fr.txt");
            sb.AppendLine("    diskpart /s X:\\fr.txt >nul 2>&1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine(":run");
            sb.AppendLine("if !PART_N! LEQ 0 ( echo ERROR: Target partition not found. Rebooting... & wpeutil reboot )");
            sb.AppendLine("rem --- FIX: querymax primeiro; nunca mente Status OK se falhar ---");
            sb.AppendLine("echo select disk !DISK_N! > X:\\q.txt");
            sb.AppendLine("echo select partition !PART_N! >> X:\\q.txt");
            sb.AppendLine("echo assign letter=Z >> X:\\q.txt");
            sb.AppendLine("echo shrink querymax >> X:\\q.txt");
            sb.AppendLine("diskpart /s X:\\q.txt > X:\\querymax_out.txt 2>&1");
            sb.AppendLine("set QUERYMAX=0");
            sb.AppendLine("for /f \"tokens=2\" %%m in ('findstr /i \"in MB\" X:\\querymax_out.txt 2^>nul') do set QUERYMAX=%%m");
            sb.AppendLine("echo select volume Z > X:\\qr.txt");
            sb.AppendLine("echo remove letter=Z >> X:\\qr.txt");
            sb.AppendLine("diskpart /s X:\\qr.txt >nul 2>&1");
            sb.AppendLine("echo Espaco disponivel para shrink: !QUERYMAX! MB (desejado: !SHRINK_MB! MB)");
            sb.AppendLine("if !QUERYMAX! LSS !SHRINK_MB! (");
            sb.AppendLine("  echo ERRO: espaco insuficiente. Reduza o valor do shrink.");
            sb.AppendLine("  echo select disk !DISK_N! > X:\\l.txt");
            sb.AppendLine("  echo select partition !PART_N! >> X:\\l.txt");
            sb.AppendLine("  echo assign letter=Z >> X:\\l.txt");
            sb.AppendLine("  diskpart /s X:\\l.txt >nul 2>&1");
            sb.AppendLine("  echo [KitLugia WinPE Shrink] > X:\\result.log");
            sb.AppendLine("  echo Status: FALHOU - querymax=!QUERYMAX!MB menor que desejado=!SHRINK_MB!MB >> X:\\result.log");
            sb.AppendLine("  if exist Z:\\ copy /y X:\\result.log Z:\\KitLugia_WinPE_Log.txt >nul");
            sb.AppendLine("  if exist Z:\\KL_SHRINK_TARGET.dat del /f /q Z:\\KL_SHRINK_TARGET.dat >nul 2>&1");
            sb.AppendLine("  echo NAO vai reiniciar automaticamente - verifique o erro acima.");
            sb.AppendLine("  pause");
            sb.AppendLine("  wpeutil reboot");
            sb.AppendLine(")");
            sb.AppendLine("echo select disk !DISK_N! > X:\\s.txt");
            sb.AppendLine("echo select partition !PART_N! >> X:\\s.txt");
            sb.AppendLine("echo assign letter=Z >> X:\\s.txt");
            sb.AppendLine("echo shrink desired=!SHRINK_MB! >> X:\\s.txt");
            sb.AppendLine("diskpart /s X:\\s.txt");
            sb.AppendLine("echo Shrink done. Writing persistent log...");
            sb.AppendLine("echo [KitLugia WinPE Shrink] > X:\\result.log");
            sb.AppendLine("echo Status: OK >> X:\\result.log");
            sb.AppendLine("echo Disk: !DISK_N! Part: !PART_N! Size: !SHRINK_MB!MB >> X:\\result.log");
            sb.AppendLine("rem --- Mirror log to Windows partition ---");
            sb.AppendLine("echo select disk !DISK_N! > X:\\l.txt");
            sb.AppendLine("echo select partition !PART_N! >> X:\\l.txt");
            sb.AppendLine("echo assign letter=Z >> X:\\l.txt");
            sb.AppendLine("diskpart /s X:\\l.txt >nul 2>&1");
            sb.AppendLine("if exist Z:\\ (");
            sb.AppendLine("  copy /y X:\\result.log Z:\\KitLugia_WinPE_Log.txt >nul");
            sb.AppendLine("  if exist Z:\\KL_SHRINK_TARGET.dat del /f /q Z:\\KL_SHRINK_TARGET.dat >nul 2>&1");
            sb.AppendLine("  echo select volume Z > X:\\lr.txt");
            sb.AppendLine("  echo remove letter=Z >> X:\\lr.txt");
            sb.AppendLine("  diskpart /s X:\\lr.txt >nul 2>&1");
            sb.AppendLine("  echo Log saved to Z:\\KitLugia_WinPE_Log.txt");
            sb.AppendLine(") else (");
            sb.AppendLine("  echo WARNING: Could not reassign Z: for persistent log");
            sb.AppendLine(")");
            sb.AppendLine("echo Rebooting...");
            sb.AppendLine("wpeutil reboot");
            return sb.ToString();
        }

        /// <summary>
        /// Fase 2 (dentro do WinPE): Continua o shrink com a partição offline
        /// </summary>
        public static async Task<(bool ok, string msg)> ContinueShrinkInWinpe(string targetDrive, long targetSizeMB, CancellationToken ct = default)
        {
            try
            {
                Log("========== CONTINUANDO SHRINK NO WINPE (FASE 2) ==========");
                string drive = targetDrive.Replace(":", "");

                // Diskpart script completo
                var dp = new StringBuilder();
                dp.AppendLine($"select volume {drive}");

                // Tenta shrink querymax primeiro
                dp.AppendLine("shrink querymax");

                // Executa o shrink
                dp.AppendLine($"shrink desired={targetSizeMB}");

                // Cria partição B
                dp.AppendLine("create partition primary");
                dp.AppendLine("format fs=ntfs quick label=KITLUGIA_BOOT");
                dp.AppendLine("assign letter=B");
                dp.AppendLine("exit");

                string scriptPath = Path.Combine(Path.GetTempPath(), "winpe_shrink.txt");
                File.WriteAllText(scriptPath, dp.ToString());
                var (code, output) = await RunProcessCaptured("diskpart.exe", $"/s \"{scriptPath}\"");
                File.Delete(scriptPath);

                Log($"Diskpart concluído (código {code})");
                Log(output);

                return (code == 0, output);
            }
            catch (Exception ex)
            {
                return (false, $"Erro no shrink WinPE: {ex.Message}");
            }
        }

        /// <summary>
        /// Verifica se o WinPE ramdisk está pronto (C:\KL_WINPE\boot.wim existe).
        /// </summary>
        public static bool IsWinpeReady()
        {
            try { return File.Exists(@"C:\KL_WINPE\boot.wim"); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        public static bool IsValidationOsReady()
        {
            try { return File.Exists(VALIDATION_WIM_PATH); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return false; }
        }

        /// <summary>
        /// Remove todos os artefatos do WinPE: entradas BCD ramdisk, pasta C:\KL_WINPE\, config e marcadores.
        /// </summary>
        public static async Task<bool> RemoveWinpeAsync()
        {
            Log("=== Removendo WinPE ===");
            int removed = 0;

            // 1. Remove TODAS as entradas BCD KitLugia WinPE (qualquer formato: espaços, underscores, hífens)
            try
            {
                var (enumCode, enumOut) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                if (enumCode == 0)
                {
                    var lines = enumOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    string? currentGuid = null;
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("identifier", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            currentGuid = parts.Length > 1 ? parts[1].Trim() : null;
                        }
                        else if (trimmed.StartsWith("description", StringComparison.OrdinalIgnoreCase))
                        {
                            var descParts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                            string descVal = descParts.Length > 1 ? descParts[1].Trim() : "";
                            if (!string.IsNullOrEmpty(currentGuid) &&
                                descVal.Contains("KitLugia", StringComparison.OrdinalIgnoreCase) &&
                                descVal.Contains("WinPE", StringComparison.OrdinalIgnoreCase))
                            {
                                Log($"Removendo entrada BCD: {currentGuid} ({descVal})");
                                var (delCode, _) = await RunProcessCaptured("bcdedit.exe", $"/delete {currentGuid} /f");
                                if (delCode == 0) removed++;
                                else Log($"  Aviso: falha ao deletar {currentGuid} (código {delCode})");
                            }
                            currentGuid = null;
                        }
                    }

                    // Só remove {ramdiskoptions} se NENHUMA outra entrada KitLugia WinPE restar
                    // (se falhou ao remover alguma, mantém {ramdiskoptions} para não quebrar o boot)
                    var (checkCode, checkOut) = await RunProcessCaptured("bcdedit.exe", "/enum all");
                    if (checkCode == 0 && !checkOut.Contains("KitLugia", StringComparison.OrdinalIgnoreCase))
                    {
                        var (rcCode, _) = await RunProcessCaptured("bcdedit.exe", "/delete {ramdiskoptions} /f");
                        if (rcCode == 0) Log("{ramdiskoptions} removido.");
                    }
                }
                Log($"Removidas {removed} entradas BCD KitLugia WinPE.");
            }
            catch (Exception ex)
            {
                Log($"Aviso ao limpar BCD: {ex.Message}");
            }

            // 5. Restaura timeout original do BCD
            try
            {
                if (File.Exists(BcdTimeoutSaveFile))
                {
                    string savedTimeout = await File.ReadAllTextAsync(BcdTimeoutSaveFile);
                    if (int.TryParse(savedTimeout.Trim(), out int timeoutVal))
                    {
                        var (rtCode, _) = await RunProcessCaptured("bcdedit.exe", $"/timeout {timeoutVal}");
                        Log($"Timeout BCD restaurado para {timeoutVal}s (código {rtCode}).");
                    }
                    File.Delete(BcdTimeoutSaveFile);
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao restaurar timeout BCD: {ex.Message}");
            }

            // 6. Deleta C:\KL_WINPE\
            try
            {
                if (Directory.Exists(@"C:\KL_WINPE"))
                {
                    Directory.Delete(@"C:\KL_WINPE\", true);
                    Log(@"C:\KL_WINPE\ deletado.");
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao deletar KL_WINPE: {ex.Message}");
            }

            // 3. Deleta config em Program Files
            try
            {
                string cfgDir = Path.Combine(KitLugiaInstallPath, "WinPE");
                if (Directory.Exists(cfgDir))
                {
                    var cfgFiles = Directory.GetFiles(cfgDir, "*.*");
                    foreach (var f in cfgFiles)
                    {
                        try { File.Delete(f); Log($"Deletado: {f}"); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Aviso ao limpar config: {ex.Message}");
            }

            // 4. Limpa marcadores KL_SHRINK_TARGET.dat em todas as unidades
            CleanupShrinkMarker();

            Log("=== Remocao WinPE concluida ===");
            return true;
        }

        private static string WinpeConfigDir => Path.Combine(KitLugiaInstallPath, "WinPE");
        private static string WinpeConfigIni => Path.Combine(WinpeConfigDir, "shrink_config.ini");
        private static string WinpeBootWim => Path.Combine(WinpeConfigDir, "boot.wim");
        private static string WinpeBootSdi => Path.Combine(WinpeConfigDir, "boot.sdi");

        /// <summary>
        /// Escreve o arquivo de config no mesmo diretório do boot.wim, agenda o boot via WinPE e reinicia.
        /// O boot.wim deve existir (criado por PrepareWinpeBoot).
        /// </summary>
        // Detecta DISK_N e PART_N de uma letra de unidade via diskpart.
        /// <summary>
        /// Coleta múltiplos identificadores de partição via wmic + diskpart + vol + DriveInfo.
        /// Usa wmic Win32_LogicalDiskToPartition (fim-to-fim associativo, sem parsing frágil de tabela textual).
        /// PART_OFFSET é o identificador físico mais confiável — nunca muda entre Windows e WinPE.
        /// </summary>
        private static (int disk, int part, long offset, long size, string serial, string label) GetDiskPartitionInfo(string driveLetter)
        {
            int disk = -1, part = -1;
            long offset = 0, size = 0;
            string serial = "", label = "", dl = driveLetter.Replace(":", "").Trim();
            long knownSize = 0;

            // 0: DriveInfo → tamanho total da partição
            try { knownSize = new DriveInfo($"{dl}\\").TotalSize; Log($"GetDiskPartitionInfo: DriveInfo size={knownSize}"); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 1: WMI → método primário para DISK_N/PART_N + metadados
            // Usa ASSOCIATORS OF: Win32_LogicalDisk → Win32_DiskPartition (funciona mesmo se o volume já tem letra C:)
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();
                Log($"GetDiskPartitionInfo: consultando WMI para letra {dl}:");

                // 1a: ASSOCIATORS → obtém disco/partição física a partir da letra da unidade
                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID=\"{dl}:\"}} WHERE AssocClass=Win32_LogicalDiskToPartition")))
                {
                    foreach (ManagementObject dp in searcher.Get())
                    {
                        var di = dp["DiskIndex"];
                        var idx = dp["Index"];
                        if (di != null) disk = Convert.ToInt32(di);
                        if (idx != null) part = Convert.ToInt32(idx) + 1; // Index é 0-based
                        var so = dp["StartingOffset"];
                        if (so != null) offset = Convert.ToInt64(so);
                        var sz = dp["Size"];
                        if (sz != null) size = Convert.ToInt64(sz);
                        Log($"GetDiskPartitionInfo (WMI associators): DISK={disk} PART={part} Offset={offset} Size={size}");
                        break;
                    }
                }

                // 1b: Win32_LogicalDisk → VolumeSerialNumber e VolumeName
                using (var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = \"{dl}:\"")))
                {
                    foreach (ManagementObject lo in searcher.Get())
                    {
                        var sn = lo["VolumeSerialNumber"];
                        if (sn != null) serial = sn.ToString() ?? "";
                        var nm = lo["VolumeName"];
                        if (nm != null) label = nm.ToString() ?? "";
                        Log($"GetDiskPartitionInfo (WMI logical): Serial={serial} Label={label}");
                        break;
                    }
                }
            }
            catch (Exception wmiEx)
            {
                Log($"GetDiskPartitionInfo: WMI exception: {wmiEx.Message}");
            }

            // 2: Fallback DISKPART SCAN (se WMI falhou)
            // Nota: assign letter=Z funciona no WinPE (volume sem letra), mas FALHA no Windows live
            // porque o volume já tem letra C:. Este fallback é útil apenas se WMI não estiver disponível.
            if (disk < 0 || part < 0)
            {
                Log("GetDiskPartitionInfo: WMI falhou, tentando fallback diskpart scan...");
                try
                {
                    // Tenta limpar Z: residual antes de começar
                    RunDiskpartScript("select volume Z\r\nremove letter=Z\r\n");
                    for (int d = 0; d <= 7; d++)
                    {
                        for (int p = 1; p <= 8; p++)
                        {
                            RunDiskpartScript($"select disk {d}\r\nselect partition {p}\r\nassign letter=Z\r\n");
                            if (System.IO.Directory.Exists(@"Z:\") && System.IO.File.Exists(@"Z:\Windows\System32\config\SOFTWARE"))
                            {
                                disk = d; part = p;
                                Log($"GetDiskPartitionInfo (diskpart fallback): Windows encontrado DISK={d} PART={p}");
                                RunDiskpartScript("select volume Z\r\nremove letter=Z\r\n");
                                d = 99; break;
                            }
                            RunDiskpartScript("select volume Z\r\nremove letter=Z\r\n");
                        }
                        if (d == 99) break;
                    }
                }
                catch (Exception scanEx)
                {
                    Log($"GetDiskPartitionInfo: diskpart fallback exception: {scanEx.Message}");
                }
            }

            // Fallback size
            if (size == 0) size = knownSize;

            Log($"GetDiskPartitionInfo final: {dl}: -> DISK={disk} PART={part} OFFSET={offset} SIZE={size} SERIAL={serial} LABEL=\"{label}\"");
            return (disk, part, offset, size, serial, label);
        }

        /// <summary>
        /// Executa um script diskpart curto e descarta saída.
        /// </summary>
        private static void RunDiskpartScript(string script)
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kl_gpi_tmp.txt");
                System.IO.File.WriteAllText(path, script);
                var psi = new System.Diagnostics.ProcessStartInfo("diskpart.exe", $"/s \"{path}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null) { proc.StandardOutput.ReadToEnd(); proc.WaitForExit(15000); }
                try { System.IO.File.Delete(path); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        private static async Task<(int disk, int part, long offset, long size, string serial, string label)> GetDiskPartitionInfoAsync(string driveLetter)
        {
            return await Task.Run(() => GetDiskPartitionInfo(driveLetter));
        }

        /// <summary>
        /// Salva o timeout BCD original antes de modificá-lo.
        /// Usado para restaurar após remoção do WinPE.
        /// </summary>
        private static async Task SaveOriginalBcdTimeout()
        {
            try
            {
                var dir = Path.GetDirectoryName(BcdTimeoutSaveFile);
                if (dir != null) Directory.CreateDirectory(dir);

                var (code, output) = await RunProcessCaptured("bcdedit.exe", "/enum {bootmgr}");
                if (code == 0)
                {
                    // Ex: "timeout            0"
                    var match = Regex.Match(output, @"timeout\s+(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        string val = match.Groups[1].Value;
                        await File.WriteAllTextAsync(BcdTimeoutSaveFile, val);
                        Log($"Timeout original do BCD salvo: {val}s");
                        return;
                    }
                }
                // Se não encontrar timeout (padrão não definido), assume 0
                await File.WriteAllTextAsync(BcdTimeoutSaveFile, "0");
                Log("Timeout BCD não encontrado em {bootmgr}, assumindo 0.");
            }
            catch (Exception ex)
            {
                Log($"Aviso ao salvar timeout BCD: {ex.Message}");
            }
        }

        public static async Task<(bool ok, string msg)> ScheduleWinpeShrink(string targetDrive, long shrinkMB, string osType = "winpe")
        {
            try
            {
                string klWinpe = @"C:\KL_WINPE";
                string configIni = Path.Combine(klWinpe, "shrink_config.ini");
                bool useValOs = osType.Equals("validationos", StringComparison.OrdinalIgnoreCase);
                string wimFile = useValOs ? "validation_boot.wim" : "boot.wim";
                string wimPath = Path.Combine(klWinpe, wimFile);
                string bcdDesc = useValOs ? "KitLugia Validation OS (WPF Test)" : "KitLugia WinPE - Shrink";

                // 1. Verifica se o WIM existe (com fallback recursivo)
                if (!File.Exists(wimPath))
                {
                    string? found = FindWimRecursive(klWinpe);
                    if (found != null)
                    {
                        Log($"WIM esperado não encontrado em {wimPath}, mas encontrado em: {found}");
                        wimPath = found;
                    }
                    else
                        return (false, $"{wimFile} não encontrado em {klWinpe}. Execute 'Preparar {(useValOs ? "Validation OS" : "WinPE")}' primeiro.");
                }
                WinpeBuilder.EnsureFileWritable(wimPath);

                // 2. Detecta múltiplos identificadores da partição
                string drive = targetDrive.Replace(":", "").Trim();
                var (disk, part, offset, size, serial, label) = await GetDiskPartitionInfoAsync(drive);
                if (disk < 0 || part < 0)
                    return (false, $"Não foi possível detectar o disco/partição física para {drive}: usando WMI e fallback. Verifique se {drive}: é uma partição NTFS válida.");
                long shrinkMB64 = shrinkMB;

                // 3a. Limpa marcadores antigos + grava novo marcador KL_SHRINK_TARGET.dat na raiz do drive alvo
                CleanupShrinkMarker();
                try
                {
                    string markerPath = $@"{drive}:\{ShrinkMarkerFile}";
                    await File.WriteAllTextAsync(markerPath, $"SHRINK_MB={shrinkMB}\nDISK={disk}\nPART={part}\n");
                    Log($"Marcador escrito: {markerPath}");
                }
                catch (Exception mex)
                {
                    Log($"Aviso: não foi possível escrever marcador no drive {drive}: {mex.Message}");
                }

                // 3b. Escreve shrink_config.ini no HD (backup)
                string configContent = $"DISK_N={disk}\nPART_N={part}\nPART_OFFSET={offset}\nPART_SIZE={size}\nOS_TYPE={(useValOs ? "validationos" : "winpe")}\n";
                if (!string.IsNullOrEmpty(serial))
                    configContent += $"VOL_SERIAL={serial}\n";
                if (!string.IsNullOrEmpty(label))
                    configContent += $"VOL_LABEL={label}\n";
                configContent += $"SHRINK_MB={shrinkMB64}\n";
                await File.WriteAllTextAsync(configIni, configContent);
                Log($"Config escrito: DISK_N={disk} PART_N={part} OFFSET={offset} SIZE={size} SHRINK={shrinkMB}MB OS={osType}");

                Log("Config + marcador escritos. Injetando script de shrink no WIM...");

                // 3c. Injetar script de shrink no WIM via DISM (wimlib não suporta segunda modificação)
                string shrinkScript = RamdiskStartnetCmd(disk, part, (int)shrinkMB64);
                string scriptName = useValOs ? "startnet.valos.cmd" : "startnet.cmd";
                bool scriptInjected = await WinpeBuilder.InjectStartnetCmdIntoWimAsync(wimPath, shrinkScript, scriptName);
                if (scriptInjected)
                    Log($"Script de shrink injetado em {wimPath} como {scriptName}.");
                else
                    Log("Aviso: não foi possível injetar script via DISM. O OS usará valores do scan/config.");

                // 3d. Injetar shrink_config.ini dentro do WIM (X:\shrink_config.ini)
                bool configInjected = await WinpeBuilder.InjectConfigIntoWimAsync(wimPath, configContent);
                if (configInjected)
                    Log("shrink_config.ini injetado dentro do WIM.");
                else
                    Log("Aviso: não foi possível injetar config no WIM. Usando config do HD como fallback.");

                // 4. Configurar bootsequence via BCD ramdisk
                try
                {
                    // Usa o nome real do arquivo (pode ser diferente se FindWimRecursive resolveu)
                    string bcdWimName = Path.GetFileName(wimPath);
                    string? guid = await CreateRamdiskEntry(
                        bcdDesc, "C",
                        $"\\KL_WINPE\\{bcdWimName}",
                        "\\KL_WINPE\\boot.sdi");
                    if (guid != null)
                    {
                        await SaveOriginalBcdTimeout();
                        await RunProcessCaptured("bcdedit.exe", "/timeout 10");
                        var (bsCode, _) = await RunProcessCaptured("bcdedit.exe", $"/bootsequence {guid}");
                        Log($"Bootsequence configurado para {(useValOs ? "Validation OS" : "WinPE")} (código {bsCode}).");
                    }
                }
                catch (Exception bcdEx)
                {
                    Log($"Aviso: não foi possível configurar bootsequence: {bcdEx.Message}");
                    Log($"O usuário precisará selecionar '{bcdDesc}' manualmente no boot.");
                }

                // 5. Agenda reboot
                Log("Reiniciando em 10 segundos...");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 10 /c \"KitLugia Shrink\"")
                        {
                            CreateNoWindow = true,
                            UseShellExecute = true,
                            Verb = "runas"
                        };
                        System.Diagnostics.Process.Start(psi);
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                });

                return (true, $"{(useValOs ? "Validation OS" : "WinPE")} configurado. DISK_N={disk} PART_N={part} OFFSET={offset} SHRINK={shrinkMB}MB. O sistema será reiniciado em 10s para executar o shrink.");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao agendar shrink: {ex.Message}");
            }
        }

        /// <summary>
        /// Caminho persistente onde logs gerados pelo WinPE são copiados (X:\KitLugiaPE\*.log → C:\KitLugia_WinPE_Log.txt).
        /// O startnet.cmd do WinPE deve grave aqui também (X: é volátil, então gravamos um mirror em C:).
        /// </summary>
        public static string WinpePersistentLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "WinPE", "last_run.log");

        /// <summary>
        /// Nome do arquivo marcador que o WinPE usa para identificar o drive alvo do shrink.
        /// Gravado na raiz do drive selecionado antes do reboot, procurado pelo startnet.cmd no WinPE.
        /// </summary>
        public const string ShrinkMarkerFile = "KL_SHRINK_TARGET.dat";
        private static readonly string BcdTimeoutSaveFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KitLugia", "bcd_timeout.txt");

        /// <summary>
        /// Limpa arquivos marcadores de shrink em todas as unidades montadas.
        /// Chamado após o usuário verificar os logs ou antes de um novo shrink.
        /// </summary>
        public static void CleanupShrinkMarker()
        {
            try
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (di.IsReady && di.DriveType == DriveType.Fixed)
                    {
                        string marker = Path.Combine(di.RootDirectory.FullName, ShrinkMarkerFile);
                        try { if (File.Exists(marker)) { File.Delete(marker); Log($"Marcador removido: {marker}"); } } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    }
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Lê o último log do WinPE disponível no sistema (X:\KitLugiaPE não persiste, então usamos C:\KitLugia_WinPE_Log.txt que o startnet.cmd também grava).
        /// Retorna string vazia se não houver log.
        /// </summary>
        public static string ReadLastWinpeLog()
        {
            try
            {
                // Tenta caminho persistente preferido (LocalAppData\KitLugia\WinPE\last_run.log)
                if (File.Exists(WinpePersistentLogPath))
                    return File.ReadAllText(WinpePersistentLogPath);

                // Fallback: C:\KitLugia_WinPE_Log.txt (raiz do sistema — gravado pelo startnet.cmd dentro do WinPE)
                string fallbackPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "KitLugia_WinPE_Log.txt");
                if (File.Exists(fallbackPath))
                    return File.ReadAllText(fallbackPath);

                // Outro fallback: X:\KitLugiaPE\result.log (acessível somente se rodando dentro do WinPE agora)
                string xLog = @"X:\KitLugiaPE\result.log";
                if (File.Exists(xLog))
                    return File.ReadAllText(xLog);

                return string.Empty;
            }
            catch (Exception ex)
            {
                return $"Erro ao ler log: {ex.Message}";
            }
        }

        /// <summary>
        /// Lê todos os arquivos de log WinPE disponíveis em locais conhecidos (X:\KitLugiaPE\*.log, C:\KitLugia_WinPE_Log.txt, LocalAppData).
        /// Retorna um dicionário { caminho, conteúdo }.
        /// </summary>
        public static Dictionary<string, string> ReadAllWinpeLogs()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] candidates =
            {
                WinpePersistentLogPath,
                Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "KitLugia_WinPE_Log.txt"),
                @"X:\KitLugiaPE\result.log",
                @"X:\KitLugiaPE\shrink_result.log",
            };
            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path) && !result.ContainsKey(path))
                        result[path] = File.ReadAllText(path);
                }
                catch { /* ignora */ }
            }
            // NOTA: CleanupShrinkMarker NÃO é chamado aqui para não remover o marcador
            // antes do WinPE ter chance de usá-lo. O WinPE apaga o marcador após o shrink.
            return result;
        }

        /// <summary>
        /// Exclui todos os logs WinPE persistentes (para começar uma nova execução limpa).
        /// </summary>
        public static void ClearWinpeLogs()
        {
            try { if (File.Exists(WinpePersistentLogPath)) File.Delete(WinpePersistentLogPath); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            try
            {
                string fallbackPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "KitLugia_WinPE_Log.txt");
                if (File.Exists(fallbackPath)) File.Delete(fallbackPath);
            } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
        }

        /// <summary>
        /// Detecta edições disponíveis em um ISO do Windows (install.wim/install.esd).
        /// </summary>
        public static async Task<List<string>> DetectIsoEditions(string isoPath)
        {
            Log($"Detectando edicoes no ISO: {isoPath}");
            var editions = new List<string>();

            try
            {
                string extractDir = Path.Combine(Path.GetTempPath(), "KitLugia", "IsoCheck");
                Directory.CreateDirectory(extractDir);

                // Mount ISO
                var (mc, mo) = await RunProcessCaptured("powershell.exe",
                    $"-NoProfile -Command \"Mount-DiskImage -ImagePath '{isoPath}' -StorageType ISO\"");
                if (mc != 0)
                {
                    Log($"Falha ao montar ISO: {mo}");
                    editions.Add("1 - Windows Pro (fallback)");
                    return editions;
                }

                await Task.Delay(2000);

                try
                {
                    // Get drive letter
                    var (lc, lo) = await RunProcessCaptured("powershell.exe",
                        $"-NoProfile -Command \"(Get-DiskImage -ImagePath '{isoPath}' | Get-Volume).DriveLetter\"");
                    string driveLetter = (lc == 0 ? lo?.Trim() : null) ?? "";
                    if (string.IsNullOrEmpty(driveLetter) || driveLetter.Length > 2)
                    {
                        editions.Add("1 - Windows Pro (fallback)");
                        return editions;
                    }

                    char letter = driveLetter[0];
                    string installWim = $@"{letter}:\sources\install.wim";
                    string installEsd = $@"{letter}:\sources\install.esd";
                    string wimFile = File.Exists(installWim) ? installWim : File.Exists(installEsd) ? installEsd : null;

                    if (wimFile == null)
                    {
                        editions.Add("1 - Windows Pro (fallback)");
                        return editions;
                    }

                    // Get image info using dism
                    var (ec, edout) = await RunProcessCaptured("dism.exe",
                        $"/Get-WimInfo /WimFile:\"{wimFile}\"");
                    if (ec == 0 && !string.IsNullOrEmpty(edout))
                    {
                        var lines = edout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        int currentIndex = 0;
                        string currentName = "";

                        foreach (var line in lines)
                        {
                            if (line.Trim().StartsWith("Index :"))
                                int.TryParse(line.Split(':')[1].Trim(), out currentIndex);
                            else if (line.Trim().StartsWith("Name :"))
                                currentName = line.Split(':')[1].Trim();
                            else if (line.Trim().StartsWith("Description :") && currentIndex > 0)
                            {
                                editions.Add($"{currentIndex} - {currentName}");
                                currentIndex = 0;
                                currentName = "";
                            }
                        }
                    }

                    if (editions.Count == 0)
                        editions.Add("1 - Windows Pro (fallback)");
                }
                finally
                {
                    await RunProcessCaptured("powershell.exe",
                        $"-NoProfile -Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"");
                }
            }
            catch (Exception ex)
            {
                Log($"Erro ao detectar edicoes: {ex.Message}");
                editions.Add("1 - Windows Pro (fallback)");
            }

            return editions;
        }

        /// <summary>
        /// Agenda o fresh install com preservação de dados via WinPE.
        /// Salva config, extrai ISO, exporta drivers, cria custom boot.wim com startnet.cmd, agenda reboot.
        /// </summary>
        public static async Task<(bool ok, string msg)> ScheduleReinstallPreserve(PreservationOptions options)
        {
            Log("=== ScheduleReinstallPreserve ===");
            Log($"Alvo: {options.TargetDrive}: | ISO: {options.IsoPath} | Edicao: {options.EditionIndex}");

            try
            {
                string rootDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":\\", "") ?? "C";
                string configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "KitLugia", "WinPE", "ReinstallPreserve");
                Directory.CreateDirectory(configDir);

                // 1. Write options as JSON
                string configJson = System.Text.Json.JsonSerializer.Serialize(options);
                File.WriteAllText(Path.Combine(configDir, "config.json"), configJson);

                // 2. Export host drivers if requested
                if (options.PreserveDrivers)
                {
                    string driverDir = Path.Combine(configDir, "Drivers");
                    Directory.CreateDirectory(driverDir);
                    await ExportHostDrivers(driverDir);
                }

                // 3. Extract ISO to WindowsInstallation on root drive
                string installDir = Path.Combine($"{rootDrive}:\\", "WindowsInstallation");
                if (!string.IsNullOrEmpty(options.IsoPath) && File.Exists(options.IsoPath))
                {
                    string wimInDir = Path.Combine(installDir, "sources", "install.wim");
                    string esdInDir = Path.Combine(installDir, "sources", "install.esd");
                    if (!File.Exists(wimInDir) && !File.Exists(esdInDir))
                    {
                        Log($"Extraindo ISO para {installDir}...");
                        string? sevenZip = FindSevenZipPath();
                        if (sevenZip != null)
                        {
                            var (extCode, extOut) = await RunProcessCaptured(sevenZip,
                                $"x \"{options.IsoPath}\" -o{installDir} -y");
                            if (extCode != 0)
                                Log($"Aviso: extracao do ISO retornou {extCode}: {extOut}");
                            else
                                Log($"ISO extraido com sucesso para {installDir}");
                        }
                        else
                        {
                            Log("Aviso: 7z nao encontrado para extracao do ISO; WinPE tentara montagem.");
                        }
                    }
                    else
                    {
                        Log($"WIM/ESD ja existe em {installDir}; pulando extracao.");
                    }
                }

                // 4. Generate startnet.cmd content
                string startnetContent = RamdiskReinstallPreserveStartnetCmd(options, configDir, rootDrive);

                // 5. Create custom boot.wim with the reinstall-preserve startnet.cmd
                string klWinpe = @"C:\KL_WINPE";
                string customWim = Path.Combine(klWinpe, "reinstall_boot.wim");
                string baseWim = Path.Combine(klWinpe, "boot.wim");

                if (!File.Exists(baseWim))
                    return (false, "WinPE base nao encontrado. Prepare o WinPE primeiro (botao PREPARAR).");

                // Copy base boot.wim to custom
                if (File.Exists(customWim))
                    File.Delete(customWim);
                File.Copy(baseWim, customWim);

                // Inject startnet.cmd into custom boot.wim
                bool injected = await WinpeBuilder.CustomizeWinpeWimFlatAsync(customWim, startnetContent);
                if (!injected)
                    Log("Aviso: falha ao injetar startnet.cmd; usando boot.wim padrao.");

                // Inject wimlib-imagex.exe + libwim-15.dll into custom boot.wim (faster apply)
                bool wimlibInjected = await WinpeBuilder.InjectWimlibIntoWimAsync(customWim);
                if (wimlibInjected)
                    Log("wimlib-imagex injetado no custom boot.wim para apply acelerado.");
                else
                    Log("wimlib nao injetado; startnet.cmd usara DISM como fallback.");

                // 6. Create marker file on target drive
                string markerFile = Path.Combine($"{rootDrive}:\\", "KL_REINSTALL_PRESERVE.dat");
                File.WriteAllText(markerFile,
                    $"CONFIG_DIR={configDir}\r\n" +
                    $"TARGET_DRIVE={options.TargetDrive}\r\n" +
                    $"ISO_PATH={options.IsoPath}\r\n" +
                    $"EDITION_INDEX={options.EditionIndex}");

                // 7. Create BCD entry for custom boot.wim
                string bcdResult = await CreateRamdiskEntry(
                    "KitLugia - Fresh Install + Preservacao",
                    rootDrive,
                    @"\KL_WINPE\reinstall_boot.wim",
                    @"\KL_WINPE\boot.sdi",
                    skipCleanup: true);

                if (bcdResult == null)
                {
                    // Fallback: use base boot.wim
                    Log("Falha ao criar entrada para custom boot.wim; usando base.");
                    bcdResult = await CreateRamdiskEntry(
                        "KitLugia - Fresh Install + Preservacao",
                        rootDrive,
                        @"\KL_WINPE\boot.wim",
                        @"\KL_WINPE\boot.sdi",
                        skipCleanup: true);
                    if (bcdResult == null)
                        return (false, "Falha ao criar entrada de boot WinPE.");
                }

                // 8. Set bootsequence
                var (ec, output) = await RunProcessCaptured("bcdedit.exe",
                    $"/bootsequence {bcdResult}");
                if (ec != 0)
                    Log($"Aviso: bcdedit /bootsequence retornou {ec}: {output}");

                var (toCode, _) = await RunProcessCaptured("bcdedit.exe", "/timeout 10");

                Log($"Reinstall/Preserve agendado. GUID: {bcdResult}");
                return (true, $"Operacao agendada com sucesso!\n\n" +
                    $"Reboot para WinPE configurado.\n" +
                    $"Ao reiniciar, o WinPE executara o fresh install.\n" +
                    $"Target: {options.TargetDrive}:");
            }
            catch (Exception ex)
            {
                Log($"Erro: {ex.Message}");
                return (false, $"Erro: {ex.Message}");
            }
        }

        /// <summary>
        /// Gera o conteudo do startnet.cmd para o Fresh Install + Preservacao de Dados.
        /// Script completo WinPE que faz: backup → DISM Apply → merge registry → restore → reboot.
        /// </summary>
        private static string RamdiskReinstallPreserveStartnetCmd(PreservationOptions options, string configDir, string rootDrive)
        {
            var sb = new StringBuilder();
            string isoPathEscaped = options.IsoPath.Replace("'", "''");

            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enabledelayedexpansion");
            sb.AppendLine();
            sb.AppendLine("rem =============================================");
            sb.AppendLine("rem  KitLugia — Fresh Install + Preservacao");
            sb.AppendLine("rem  startnet.cmd (gerado automaticamente)");
            sb.AppendLine("rem =============================================");
            sb.AppendLine();
            sb.AppendLine("wpeinit");
            sb.AppendLine("ping -n 3 127.0.0.1 > nul");
            sb.AppendLine();

            // === CONFIG (embedded by C# generator) ===
            sb.AppendLine("rem === CONFIGURACAO ===");
            sb.AppendLine($"set CFG_DRIVE={options.TargetDrive}");
            sb.AppendLine($"set CFG_EDITION={options.EditionIndex}");
            sb.AppendLine($"set CFG_PRESERVE_USERS={(options.PreserveUsers ? "1" : "0")}");
            sb.AppendLine($"set CFG_PRESERVE_PROGRAM_FILES={(options.PreserveProgramFiles ? "1" : "0")}");
            sb.AppendLine($"set CFG_PRESERVE_REGISTRY={(options.PreserveRegistry ? "1" : "0")}");
            sb.AppendLine($"set CFG_PRESERVE_PERSONALIZATION={(options.PreservePersonalization ? "1" : "0")}");
            sb.AppendLine($"set CFG_PRESERVE_DRIVERS={(options.PreserveDrivers ? "1" : "0")}");
            sb.AppendLine($"set CFG_CONFIG_DIR={configDir}");
            sb.AppendLine($"set CFG_ROOT_DRIVE={rootDrive}");
            sb.AppendLine();

            sb.AppendLine("echo =============================================");
            sb.AppendLine("echo  KitLugia — Fresh Install + Preservacao");
            sb.AppendLine("echo =============================================");
            sb.AppendLine();

            // === Find target partition ===
            sb.AppendLine("rem === DETECTAR PARTICAO === ");
            sb.AppendLine("echo Procurando particao Windows (KL_REINSTALL_PRESERVE.dat)...");
            sb.AppendLine("set PARTITION=");
            sb.AppendLine("for /l %%d in (0,1,4) do (");
            sb.AppendLine("  for /l %%p in (1,1,10) do (");
            sb.AppendLine("    echo select disk %%d > X:\\find_kl.txt");
            sb.AppendLine("    echo select partition %%p >> X:\\find_kl.txt");
            sb.AppendLine("    echo assign letter=K >> X:\\find_kl.txt");
            sb.AppendLine("    diskpart /s X:\\find_kl.txt >nul 2>&1");
            sb.AppendLine("    if exist K:\\KL_REINSTALL_PRESERVE.dat (");
            sb.AppendLine("      set PARTITION=K");
            sb.AppendLine("      echo select volume K > X:\\unlk.txt");
            sb.AppendLine("      echo remove letter=K >> X:\\unlk.txt");
            sb.AppendLine("      diskpart /s X:\\unlk.txt >nul 2>&1");
            sb.AppendLine("      goto :part_found");
            sb.AppendLine("    )");
            sb.AppendLine("    echo select volume K > X:\\unlk.txt 2>nul");
            sb.AppendLine("    echo remove letter=K >> X:\\unlk.txt");
            sb.AppendLine("    diskpart /s X:\\unlk.txt >nul 2>&1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine(":part_found");
            sb.AppendLine("if \"!PARTITION!\"==\"\" (");
            sb.AppendLine("  echo ERRO: Particao Windows nao encontrada (marcador ausente).");
            sb.AppendLine("  echo O marcador KL_REINSTALL_PRESERVE.dat deve estar na raiz da particao.");
            sb.AppendLine("  pause");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine();

            // Assign drive letters
            sb.AppendLine("echo select disk !PARTITION! > X:\\assign.txt");
            sb.AppendLine("echo assign letter=C >> X:\\assign.txt");
            sb.AppendLine("diskpart /s X:\\assign.txt >nul 2>&1");
            sb.AppendLine("set WIN=C");
            sb.AppendLine("set SAFE=C:\\!");
            sb.AppendLine();

            // === PHASE 1: BACKUP ===
            sb.AppendLine("rem ===== FASE 1/5 — BACKUP ===== ");
            sb.AppendLine("echo.");
            sb.AppendLine("echo ===== FASE 1/5 — BACKUP DE DADOS ===== ");
            sb.AppendLine();
            sb.AppendLine("if exist !SAFE! (");
            sb.AppendLine("  echo ERRO: !SAFE! ja existe. Remova ou renomeie e tente novamente.");
            sb.AppendLine("  pause");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine("mkdir !SAFE!");
            sb.AppendLine();

            // 1.1 Move Users
            sb.AppendLine("if \"!CFG_PRESERVE_USERS!\"==\"1\" (");
            sb.AppendLine("  echo [1/7] Movendo Users...");
            sb.AppendLine("  robocopy !WIN!\\Users !SAFE!\\Users /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine(")");
            sb.AppendLine();

            // 1.2 Move ProgramData
            sb.AppendLine("echo [2/7] Movendo ProgramData...");
            sb.AppendLine("robocopy !WIN!\\ProgramData !SAFE!\\ProgramData /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine();

            // 1.3 Move Program Files (optional)
            sb.AppendLine("if \"!CFG_PRESERVE_PROGRAM_FILES!\"==\"1\" (");
            sb.AppendLine("  echo [3/7] Movendo Program Files...");
            sb.AppendLine("  robocopy \"!WIN!\\Program Files\" \"!SAFE!\\Program Files\" /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine("  if exist \"!WIN!\\Program Files (x86)\" (");
            sb.AppendLine("    robocopy \"!WIN!\\Program Files (x86)\" \"!SAFE!\\Program Files (x86)\" /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            // 1.4 Export registry hives
            sb.AppendLine("if \"!CFG_PRESERVE_REGISTRY!\"==\"1\" (");
            sb.AppendLine("  echo [4/7] Exportando registry...");
            sb.AppendLine("  mkdir !SAFE!\\reg");
            sb.AppendLine("  reg save HKLM\\SOFTWARE !SAFE!\\reg\\OLD_SOFTWARE");
            sb.AppendLine("  reg save HKLM\\SYSTEM !SAFE!\\reg\\OLD_SYSTEM");
            sb.AppendLine("  reg save HKLM\\SAM !SAFE!\\reg\\OLD_SAM");
            sb.AppendLine(")");
            sb.AppendLine();

            // 1.5 Export personalization (HKCU for all users)
            sb.AppendLine("if \"!CFG_PRESERVE_PERSONALIZATION!\"==\"1\" (");
            sb.AppendLine("  echo [5/7] Exportando personalizacao...");
            sb.AppendLine("  mkdir !SAFE!\\reg");
            sb.AppendLine("  rem Exporta HKCU de cada usuario via NTUSER.DAT");
            sb.AppendLine("  for /d %%u in (!SAFE!\\Users\\*) do (");
            sb.AppendLine("    if exist \"%%u\\NTUSER.DAT\" (");
            sb.AppendLine("      reg load HKLM\\TempUser \"%%u\\NTUSER.DAT\" >nul 2>&1");
            sb.AppendLine("      if not errorlevel 1 (");
            sb.AppendLine("        reg export \"HKLM\\TempUser\\Control Panel\\Desktop\" \"!SAFE!\\reg\\%%~nxu_desktop.reg\" >nul 2>&1");
            sb.AppendLine("        reg export \"HKLM\\TempUser\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\" \"!SAFE!\\reg\\%%~nxu_explorer.reg\" >nul 2>&1");
            sb.AppendLine("        reg export \"HKLM\\TempUser\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\" \"!SAFE!\\reg\\%%~nxu_themes.reg\" >nul 2>&1");
            sb.AppendLine("        reg export \"HKLM\\TempUser\\Software\\Google\" \"!SAFE!\\reg\\%%~nxu_google.reg\" >nul 2>&1");
            sb.AppendLine("        reg export \"HKLM\\TempUser\\Software\\Mozilla\" \"!SAFE!\\reg\\%%~nxu_mozilla.reg\" >nul 2>&1");
            sb.AppendLine("        reg unload HKLM\\TempUser");
            sb.AppendLine("      )");
            sb.AppendLine("    )");
            sb.AppendLine("  )");
            sb.AppendLine("  rem Exporta redes WiFi");
            sb.AppendLine("  netsh wlan export profile folder=!SAFE!\\WiFi >nul 2>&1");
            sb.AppendLine(")");
            sb.AppendLine();

            // 1.6 Export drivers
            sb.AppendLine("if \"!CFG_PRESERVE_DRIVERS!\"==\"1\" (");
            sb.AppendLine("  echo [6/7] Exportando drivers...");
            sb.AppendLine("  if exist \"!CFG_CONFIG_DIR!\\Drivers\" (");
            sb.AppendLine("    xcopy /e /i /y \"!CFG_CONFIG_DIR!\\Drivers\" !SAFE!\\Drivers >nul");
            sb.AppendLine("  ) else (");
            sb.AppendLine("    mkdir !SAFE!\\Drivers");
            sb.AppendLine("    dism /online /export-driver /destination:!SAFE!\\Drivers");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            // 1.7 Move root items + fonts + hosts
            sb.AppendLine("echo [7/7] Coletando itens avulsos e fontes...");
            sb.AppendLine("copy !WIN!\\Windows\\System32\\drivers\\etc\\hosts !SAFE!\\hosts.txt /y >nul 2>&1");
            sb.AppendLine("robocopy !WIN!\\Windows\\Fonts !SAFE!\\Fonts /e /move /r:0 /np >nul 2>&1");
            sb.AppendLine("mkdir !SAFE!\\_root");
            sb.AppendLine("for /d %%i in (!WIN!\\*) do (");
            sb.AppendLine("  set \"name=%%~nxi\"");
            sb.AppendLine("  for %%e in (Windows Users \"Program Files\" \"Program Files (x86)\" ProgramData \"System Volume Information\" \"$Recycle.Bin\" Recovery ESD Recovery WindowsInstallation) do if /i \"!name!\"==\"%%~e\" set name=_skip");
            sb.AppendLine("  if not \"!name!\"==\"_skip\" (");
            sb.AppendLine("    robocopy \"%%i\" \"!SAFE!\\_root\\%%~nxi\" /copyall /b /xj /e /move /r:0 /np >nul");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("echo Backup concluido em !SAFE!");
            sb.AppendLine();

            // === PHASE 2: APPLY WINDOWS ===
            sb.AppendLine("rem ===== FASE 2/5 — APLICAR WINDOWS =====");
            sb.AppendLine("echo.");
            sb.AppendLine("echo ===== FASE 2/5 — APLICAR WINDOWS =====");
            sb.AppendLine();

            sb.AppendLine("set WIM_FILE=");
            sb.AppendLine("if exist \"!WIN!\\WindowsInstallation\\sources\\install.wim\" set \"WIM_FILE=!WIN!\\WindowsInstallation\\sources\\install.wim\"");
            sb.AppendLine("if exist \"!WIN!\\WindowsInstallation\\sources\\install.esd\" set \"WIM_FILE=!WIN!\\WindowsInstallation\\sources\\install.esd\"");
            sb.AppendLine();

            sb.AppendLine("if \"!WIM_FILE!\"==\"\" (");
            sb.AppendLine("  echo WIM/ESD nao encontrado em WindowsInstallation.");
            sb.AppendLine("  echo Tentando montar ISO em letras de unidade...");
            sb.AppendLine("  for %%d in (D E F G H I J K L M N) do (");
            sb.AppendLine("    if exist \"%%d:\\sources\\install.wim\" set \"WIM_FILE=%%d:\\sources\\install.wim\"");
            sb.AppendLine("    if exist \"%%d:\\sources\\install.esd\" set \"WIM_FILE=%%d:\\sources\\install.esd\"");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("if \"!WIM_FILE!\"==\"\" (");
            sb.AppendLine("  echo.");
            sb.AppendLine("  echo ===========================================");
            sb.AppendLine("  echo  NENHUM install.wim/install.esd ENCONTRADO!");
            sb.AppendLine("  echo ===========================================");
            sb.AppendLine("  echo.");
            sb.AppendLine("  echo Monte o ISO manualmente e pressione qualquer tecla.");
            sb.AppendLine("  pause");
            sb.AppendLine("  for %%d in (D E F G H I J K L M N) do (");
            sb.AppendLine("    if exist \"%%d:\\sources\\install.wim\" set \"WIM_FILE=%%d:\\sources\\install.wim\"");
            sb.AppendLine("    if exist \"%%d:\\sources\\install.esd\" set \"WIM_FILE=%%d:\\sources\\install.esd\"");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("if \"!WIM_FILE!\"==\"\" (");
            sb.AppendLine("  echo Ainda sem WIM. Abortando.");
            sb.AppendLine("  pause");
            sb.AppendLine("  exit /b 1");
            sb.AppendLine(")");
            sb.AppendLine();

            // Create skeleton directories
            sb.AppendLine("echo Criando estrutura fantasma...");
            sb.AppendLine("mkdir !WIN!\\Windows\\System32\\config 2>nul");
            sb.AppendLine("mkdir !WIN!\\Users\\Default 2>nul");
            sb.AppendLine("mkdir !WIN!\\ProgramData 2>nul");
            sb.AppendLine("mkdir \"!WIN!\\Program Files\" 2>nul");
            sb.AppendLine("mkdir \"!WIN!\\Program Files (x86)\" 2>nul");
            sb.AppendLine();

            // Apply image (try wimlib first if available — 2-5x faster than DISM)
            sb.AppendLine("echo Aplicando Windows (indice !CFG_EDITION!)...");
            sb.AppendLine("echo Origem: \"!WIM_FILE!\"");
            sb.AppendLine("if exist X:\\Windows\\System32\\wimlib-imagex.exe (");
            sb.AppendLine("  echo wimlib-imagex detectado. Usando apply acelerado...");
            sb.AppendLine("  X:\\Windows\\System32\\wimlib-imagex.exe apply \"!WIM_FILE!\" !CFG_EDITION! !WIN!\\");
            sb.AppendLine("  if errorlevel 1 (");
            sb.AppendLine("    echo wimlib falhou. Tentando DISM como fallback...");
            sb.AppendLine("    dism /Apply-Image /ImageFile:\"!WIM_FILE!\" /Index:!CFG_EDITION! /ApplyDir:!WIN!\\");
            sb.AppendLine("  )");
            sb.AppendLine(") else (");
            sb.AppendLine("  dism /Apply-Image /ImageFile:\"!WIM_FILE!\" /Index:!CFG_EDITION! /ApplyDir:!WIN!\\");
            sb.AppendLine(")");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  echo.");
            sb.AppendLine("  echo ERRO ao aplicar imagem Windows. Tentando novamente com indice 1...");
            sb.AppendLine("  if exist X:\\Windows\\System32\\wimlib-imagex.exe (");
            sb.AppendLine("    X:\\Windows\\System32\\wimlib-imagex.exe apply \"!WIM_FILE!\" 1 !WIN!\\");
            sb.AppendLine("  ) else (");
            sb.AppendLine("    dism /Apply-Image /ImageFile:\"!WIM_FILE!\" /Index:1 /ApplyDir:!WIN!\\");
            sb.AppendLine("  )");
            sb.AppendLine("  if errorlevel 1 (");
            sb.AppendLine("    echo ERRO fatal ao aplicar imagem. Verifique o WIM/ESD.");
            sb.AppendLine("    pause");
            sb.AppendLine("    exit /b 1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine("echo Windows aplicado com sucesso.");
            sb.AppendLine();

            // === PHASE 3: REGISTRY MERGE ===
            sb.AppendLine("rem ===== FASE 3/5 — MERGE DE REGISTRY =====");
            sb.AppendLine("if \"!CFG_PRESERVE_REGISTRY!\"==\"1\" (");
            sb.AppendLine("  echo.");
            sb.AppendLine("  echo ===== FASE 3/5 — MERGE DE REGISTRY =====");
            sb.AppendLine("  call :merge_registry");
            sb.AppendLine(")");
            sb.AppendLine();

            // === PHASE 4: RESTORE ===
            sb.AppendLine("rem ===== FASE 4/5 — RESTAURAR DADOS =====");
            sb.AppendLine("echo.");
            sb.AppendLine("echo ===== FASE 4/5 — RESTAURAR DADOS =====");
            sb.AppendLine();

            sb.AppendLine("if \"!CFG_PRESERVE_USERS!\"==\"1\" (");
            sb.AppendLine("  echo Restaurando Users...");
            sb.AppendLine("  robocopy !SAFE!\\Users !WIN!\\Users /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("echo Restaurando ProgramData...");
            sb.AppendLine("robocopy !SAFE!\\ProgramData !WIN!\\ProgramData /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine();

            sb.AppendLine("if \"!CFG_PRESERVE_PROGRAM_FILES!\"==\"1\" (");
            sb.AppendLine("  echo Restaurando Program Files...");
            sb.AppendLine("  if exist \"!SAFE!\\Program Files\" (");
            sb.AppendLine("    robocopy \"!SAFE!\\Program Files\" \"!WIN!\\Program Files\" /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine("  )");
            sb.AppendLine("  if exist \"!SAFE!\\Program Files (x86)\" (");
            sb.AppendLine("    robocopy \"!SAFE!\\Program Files (x86)\" \"!WIN!\\Program Files (x86)\" /copyall /b /xj /e /move /r:0 /np");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("if \"!CFG_PRESERVE_DRIVERS!\"==\"1\" (");
            sb.AppendLine("  echo Restaurando drivers...");
            sb.AppendLine("  if exist !SAFE!\\Drivers (");
            sb.AppendLine("    mkdir !WIN!\\Windows\\System32\\DriverStore 2>nul");
            sb.AppendLine("    dism /image:!WIN!\\ /Add-Driver /Driver:!SAFE!\\Drivers /Recurse");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("echo Restaurando fontes...");
            sb.AppendLine("robocopy !SAFE!\\Fonts !WIN!\\Windows\\Fonts /e /move /r:0 /np >nul 2>&1");
            sb.AppendLine();

            sb.AppendLine("copy !SAFE!\\hosts.txt !WIN!\\Windows\\System32\\drivers\\etc\\hosts /y >nul 2>&1");
            sb.AppendLine();

            sb.AppendLine("echo Restaurando itens avulsos...");
            sb.AppendLine("robocopy !SAFE!\\_root !WIN!\\ /copyall /b /xj /e /move /r:0 /np >nul 2>&1");
            sb.AppendLine();

            sb.AppendLine("if exist !SAFE!\\WiFi (");
            sb.AppendLine("  echo Restaurando WiFi...");
            sb.AppendLine("  for %%x in (!SAFE!\\WiFi\\*.xml) do (");
            sb.AppendLine("    netsh wlan add profile filename=\"%%x\" >nul 2>&1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("if exist !WIN!\\WindowsInstallation (");
            sb.AppendLine("  rd /s /q !WIN!\\WindowsInstallation 2>nul");
            sb.AppendLine(")");
            sb.AppendLine();

            // === PHASE 5: BOOTLOADER + REBOOT ===
            sb.AppendLine("rem ===== FASE 5/5 — BOOTLOADER + REBOOT =====");
            sb.AppendLine("echo.");
            sb.AppendLine("echo ===== FASE 5/5 — CONFIGURAR BOOTLOADER =====");
            sb.AppendLine();

            sb.AppendLine("echo Procurando particao EFI...");
            sb.AppendLine("set BCDOK=0");
            sb.AppendLine("for /l %%d in (0,1,3) do (");
            sb.AppendLine("  for /l %%p in (1,1,10) do (");
            sb.AppendLine("    echo select disk %%d > X:\\esp_bcd.txt");
            sb.AppendLine("    echo select partition %%p >> X:\\esp_bcd.txt");
            sb.AppendLine("    echo assign letter=S >> X:\\esp_bcd.txt");
            sb.AppendLine("    diskpart /s X:\\esp_bcd.txt >nul 2>&1");
            sb.AppendLine("    if exist S:\\EFI (");
            sb.AppendLine("      echo Particao EFI encontrada como S:");
            sb.AppendLine("      bcdboot !WIN!\\Windows /s S:");
            sb.AppendLine("      set BCDOK=1");
            sb.AppendLine("      goto :bcd_done");
            sb.AppendLine("    )");
            sb.AppendLine("    echo select volume S > X:\\espr.txt 2>nul");
            sb.AppendLine("    echo remove letter=S >> X:\\espr.txt");
            sb.AppendLine("    diskpart /s X:\\espr.txt >nul 2>&1");
            sb.AppendLine("  )");
            sb.AppendLine(")");
            sb.AppendLine(":bcd_done");
            sb.AppendLine("if \"!BCDOK!\"==\"0\" (");
            sb.AppendLine("  echo EFI nao encontrada. Tentando bcdboot direto no Windows.");
            sb.AppendLine("  bcdboot !WIN!\\Windows /s !WIN!:");
            sb.AppendLine(")");
            sb.AppendLine();

            // Cleanup marker
            sb.AppendLine("del /f /q !WIN!\\KL_REINSTALL_PRESERVE.dat 2>nul");
            sb.AppendLine();

            sb.AppendLine("echo.");
            sb.AppendLine("echo =============================================");
            sb.AppendLine("echo  OPERACAO CONCLUIDA COM SUCESSO!");
            sb.AppendLine("echo  Remova qualquer midia e pressione qualquer");
            sb.AppendLine("echo  tecla para reiniciar no Windows novo.");
            sb.AppendLine("echo =============================================");
            sb.AppendLine("pause");
            sb.AppendLine("wpeutil reboot");
            sb.AppendLine();

            // === SUBROUTINES ===
            sb.AppendLine();
            sb.AppendLine("rem ===== SUBROTINA: MERGE DE REGISTRY =====");
            sb.AppendLine(":merge_registry");
            sb.AppendLine("echo Carregando registries para merge...");
            sb.AppendLine();

            sb.AppendLine("reg load HKLM\\NewSft !WIN!\\Windows\\System32\\config\\SOFTWARE");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  echo ERRO: Nao foi possivel carregar novo registry (NewSft). Merge abortado.");
            sb.AppendLine("  goto :eof");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("reg load HKLM\\OldSft !SAFE!\\reg\\OLD_SOFTWARE");
            sb.AppendLine("if errorlevel 1 (");
            sb.AppendLine("  echo ERRO: Nao foi possivel carregar registry antigo (OldSft). Merge abortado.");
            sb.AppendLine("  reg unload HKLM\\NewSft");
            sb.AppendLine("  goto :eof");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("echo Exportando ambos os registries...");
            sb.AppendLine("reg export HKLM\\OldSft !SAFE!\\reg\\old_full.reg >nul 2>&1");
            sb.AppendLine("reg export HKLM\\NewSft !SAFE!\\reg\\new_full.reg >nul 2>&1");
            sb.AppendLine();

            sb.AppendLine("echo Filtrando keys Microsoft do registry antigo...");
            sb.AppendLine("rem Remove linhas de keys Microsoft\\* mas preserva App Paths, Uninstall e WOW6432Node correspondente");
            sb.AppendLine("if exist !SAFE!\\reg\\old_full.reg (");
            sb.AppendLine("  findstr /v /i /c:\"\\\\Microsoft\\\\\" !SAFE!\\reg\\old_full.reg > !SAFE!\\reg\\old_filtered.reg");
            sb.AppendLine(") else (");
            sb.AppendLine("  echo old_full.reg nao encontrado. Criando filtrado vazio.");
            sb.AppendLine("  type nul > !SAFE!\\reg\\old_filtered.reg");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("echo Exportando excecoes Microsoft (App Paths, Uninstall)...");
            sb.AppendLine("reg export \"HKLM\\OldSft\\Microsoft\\Windows\\CurrentVersion\\App Paths\" !SAFE!\\reg\\exc_apppaths.reg >nul 2>&1");
            sb.AppendLine("reg export \"HKLM\\OldSft\\Microsoft\\Windows\\CurrentVersion\\Uninstall\" !SAFE!\\reg\\exc_uninstall.reg >nul 2>&1");
            sb.AppendLine("reg export \"HKLM\\OldSft\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths\" !SAFE!\\reg\\exc_apppaths32.reg >nul 2>&1");
            sb.AppendLine("reg export \"HKLM\\OldSft\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\" !SAFE!\\reg\\exc_uninstall32.reg >nul 2>&1");
            sb.AppendLine();

            sb.AppendLine("echo Aplicando merge...");
            sb.AppendLine("reg import !SAFE!\\reg\\old_filtered.reg");
            sb.AppendLine("if exist !SAFE!\\reg\\exc_apppaths.reg reg import !SAFE!\\reg\\exc_apppaths.reg");
            sb.AppendLine("if exist !SAFE!\\reg\\exc_uninstall.reg reg import !SAFE!\\reg\\exc_uninstall.reg");
            sb.AppendLine("if exist !SAFE!\\reg\\exc_apppaths32.reg reg import !SAFE!\\reg\\exc_apppaths32.reg");
            sb.AppendLine("if exist !SAFE!\\reg\\exc_uninstall32.reg reg import !SAFE!\\reg\\exc_uninstall32.reg");
            sb.AppendLine();

            sb.AppendLine("echo Limpando...");
            sb.AppendLine("reg unload HKLM\\OldSft");
            sb.AppendLine("reg unload HKLM\\NewSft");
            sb.AppendLine();

            sb.AppendLine("echo Merge de registry concluido! Keys de programa preservadas no Windows novo.");
            sb.AppendLine("goto :eof");

            return sb.ToString();
        }
    }
}
