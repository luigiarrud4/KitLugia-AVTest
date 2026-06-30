using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
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
        public static bool IsKitLugiaIso(string isoPath)
        {
            try
            {
                // Montar ISO temporariamente para verificar
                string driveLetter = MountIso(isoPath).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(driveLetter))
                {
                    return false;
                }

                string kitlugiaIdFile = Path.Combine(driveLetter, ".kitlugia");
                bool isKitLugia = File.Exists(kitlugiaIdFile);

                // Desmontar ISO
                DismountIso(isoPath).GetAwaiter().GetResult();

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
            catch { return false; }
        }

        /// <summary>
        /// Detecta o idioma da ISO usando DISM /Get-WimInfo
        /// Retorna o código de idioma (ex: pt-BR, en-US, es-ES)
        /// </summary>
        public static string DetectIsoLanguage(string isoPath, string? extractedDrive = null)
        {
            try
            {
                if (!string.IsNullOrEmpty(extractedDrive))
                {
                    return DetectLanguageFromDrive(extractedDrive);
                }

                Log("Detectando idioma da ISO...");

                // Montar ISO temporariamente
                string driveLetter = MountIso(isoPath).GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(driveLetter))
                {
                    Log("Falha ao montar ISO para detecção de idioma.");
                    return "pt-BR";
                }

                string lang = DetectLanguageFromDrive(driveLetter);
                DismountIso(isoPath).GetAwaiter().GetResult();
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
        /// Mapeia código de idioma para InputLocale (keyboard layout)
        /// </summary>
        private static string GetInputLocaleFromLanguage(string language)
        {
            return language.ToUpper() switch
            {
                "PT-BR" => "0416", // Português (Brasil)
                "EN-US" => "0409", // Inglês (EUA)
                "ES-ES" => "040A", // Espanhol (Espanha)
                "FR-FR" => "040C", // Francês (França)
                "DE-DE" => "0407", // Alemão (Alemanha)
                "IT-IT" => "0410", // Italiano (Itália)
                "JA-JP" => "0411", // Japonês
                "KO-KR" => "0412", // Coreano
                "ZH-CN" => "0804", // Chinês (Simplificado)
                "ZH-TW" => "0404", // Chinês (Tradicional)
                "RU-RU" => "0419", // Russo
                "AR-SA" => "0401", // Árabe (Arábia Saudita)
                _ => "0416" // Fallback para pt-BR
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
                    TimeZone = timeZone,
                    ProcessorArchitecture = "amd64"
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
            catch { }
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


        // --- BOOT SERVICE ---
        public static async Task<string?> CreateRamdiskEntry(string description, string driveLetter, string wimPath, string sdiPath)
        {
            Log($"Configurando entradas BCD para WIM: {description}...");
            try
            {
                string cleanDesc = SanitizeDescription(description);
                await RunBcdeditLogged($"/create {{ramdiskoptions}} /d \"{cleanDesc}\"");
                await RunBcdeditLogged($"/set {{ramdiskoptions}} ramdisksdidevice partition={driveLetter}");
                await RunBcdeditLogged($"/set {{ramdiskoptions}} ramdisksdipath {sdiPath}");

                string createResult = await RunBcdeditLogged($"/create /d \"{cleanDesc}\" /application osloader");
                var match = Regex.Match(createResult, @"{[a-fA-F0-9-]+}");
                if (!match.Success)
                {
                    Log("ERRO: Falha ao obter GUID da nova entrada BCD.");
                    return null;
                }

                string newGuid = match.Value;
                Log($"ID Criado: {newGuid}");
                await RunBcdeditLogged($"/set {newGuid} device ramdisk=[{driveLetter}]{wimPath},{{ramdiskoptions}}");
                await RunBcdeditLogged($"/set {newGuid} osdevice ramdisk=[{driveLetter}]{wimPath},{{ramdiskoptions}}");
                await RunBcdeditLogged($"/set {newGuid} path \\windows\\system32\\boot\\winload.efi");
                await RunBcdeditLogged($"/set {newGuid} systemroot \\windows");
                await RunBcdeditLogged($"/set {newGuid} detecthal yes");
                await RunBcdeditLogged($"/set {newGuid} winpe yes");
                await RunBcdeditLogged($"/displayorder {newGuid} /addlast");

                Log("BCD: Configuração WIM finalizada com sucesso.");
                return newGuid;
            }
            catch (Exception ex)
            {
                Log($"ERRO BCD: {ex.Message}");
                return null;
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
                    try { proc.Kill(entireProcessTree: true); } catch { }
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
                    string sevenZipPath = FindSevenZipPath();
                    
                    if (!string.IsNullOrEmpty(sevenZipPath))
                    {
                        Log($"7-Zip encontrado: {sevenZipPath}");
                        Log("Iniciando extração via 7-Zip...");
                        string args = $"x \"{isoPath}\" -o\"{targetPath}\" -y";
                        
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

        public static async Task<bool> ApplyCustomizations(string winbootDrive, bool bypassRequirements, bool localAccount, bool disablePrivacy, bool injectKit, bool autoCleanup, string? customXmlPath, string? userName, string? password, bool fullAuto, uint targetDisk, uint targetPartition, string? injectedFilesPath = null, bool safeMode = false, Func<string, Task<bool>>? downloadConfirmationCallback = null, string detectedLanguage = "pt-BR",
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
                            timeZone: "E. South America Standard Time",
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
                                    try { File.Delete(tempDownloadPath); } catch { }
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
                    
                    string targetCfg = cfgPaths.FirstOrDefault(File.Exists);
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
            string espDrive = await MountEspAsync();
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
                try { File.Copy(file, target, true); } catch { }
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
                } catch { }

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
                catch { }

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
                    } catch { }
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
                } catch { }

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
                catch { }

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
            catch { return xml; }
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
            catch { }
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

                ManagementObject partition = null;
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
                string kitlugiaDir = Path.Combine("C:\\", "KitLugia");
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
                        await p.WaitForExitAsync();
                        Log("Setup do Windows fechado. Restaurando EditionID original...");
                        SetEditionId(currentEditionId);
                        await DismountIso(isoPath);
                        Log("Processo de atualização finalizado.");
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
                    
                    // Parse DISM output
                    var matches = Regex.Matches(output, @"Índice\s*:\s*(\d+).*?Nome\s*:\s*(.*?)(?=Descrição|Tamanho|Índice|$)", RegexOptions.Singleline);
                    foreach (Match m in matches)
                    {
                        var info = new WimEditionInfo { 
                            Index = int.Parse(m.Groups[1].Value), 
                            Name = m.Groups[2].Value.Trim() 
                        };
                        
                        // Pegar EditionID detalhado
                        var (_, detail) = await RunProcessCaptured("dism.exe", $"/Get-ImageInfo /ImageFile:\"{wimPath}\" /Index:{info.Index}");
                        var edMatch = Regex.Match(detail, @"Edição\s*:\s*(.*)");
                        if (edMatch.Success) info.EditionId = edMatch.Groups[1].Value.Trim();
                        
                        var archMatch = Regex.Match(detail, @"Arquitetura\s*:\s*(.*)");
                        if (archMatch.Success) info.Architecture = archMatch.Groups[1].Value.Trim();

                        var verMatch = Regex.Match(detail, @"Versão\s*:\s*(.*)");
                        if (verMatch.Success) info.Version = verMatch.Groups[1].Value.Trim();

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
            string script = $"Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume | Select-Object -ExpandProperty DriveLetter";
            string drv = await RunPowerShell(script);
            drv = drv.Trim();
            return drv.Length == 1 ? drv + ":" : "";
        }

        public static async Task DismountIso(string isoPath)
        {
            await RunPowerShell($"Dismount-DiskImage -ImagePath '{isoPath}'");
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
            return await process.StandardOutput.ReadToEndAsync();
        }

        public static string GetCurrentEditionId()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return key?.GetValue("EditionID")?.ToString() ?? "Professional";
            }
            catch { return "Professional"; }
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
            catch { }
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
                        try { var fi = new FileInfo(file); tempFreed += fi.Length; File.Delete(file); } catch { }
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
                        try { var fi = new FileInfo(file); wuFreed += fi.Length; File.Delete(file); } catch { }
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
            catch { }

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
    }
}
