# EXM Premium v0.95 — Lista Completa de Tweaks

> Referência para comparar com KitLugia.

## 1. Windows Settings (wsettings)

### 1.1 Otimizar Windows
- Desabilitar FTH (Fault Tolerant Heap)
- Desabilitar telemetria
- Desabilitar coleta de dados
- Desabilitar Edge boost/background
- Desabilitar DPI scaling
- Desabilitar audio ducking
- Desabilitar boot sound
- Ajustes de mouse (MouseSpeed, Threshold)
- ctfmon no startup
- Video quality on battery
- Explorer: IconsOnly, ListviewShadow
- Desabilitar Windows Update
- Desabilitar Aero Peek
- Bloquear localização, app diagnostics, account info
- Desabilitar SilentInstalledApps, SystemPaneSuggestions, SoftLanding
- Desabilitar RotatingLockScreen
- Desabilitar PublishUserActivities, UploadUserActivities
- Desabilitar background apps global
- Delivery Optimization download mode 0

### 1.2 Disable Ads & Popups
- Desabilitar notificações toast
- Desabilitar notification sound
- Desabilitar critical toasts
- Desabilitar quiet hours
- Desabilitar notificações de: AutoPlay, LowDisk, Print, Security, WiFi
- Desabilitar notification center
- Desabilitar Windows Feeds / News & Interests
- Desabilitar activity feed
- Desabilitar ballon tips
- Desabilitar sync provider notifications
- Bloquear userNotificationListener

### 1.3 Explorer Tweaks
- Desabilitar tracking de programas
- Esconder health icon
- ExtendedUIHoverTime
- DontPrettyPath
- ListviewShadow off
- TaskbarAnimations off
- Esconder security center
- NoLowDiskSpaceChecks
- LinkResolveIgnoreLinkInfo
- NoResolveSearch / NoResolveTrack
- NoInternetOpenWith
- NoInstrumentation

### 1.4 Windows Tweaks
- Desabilitar remote assistance
- Edge startup/background boost
- Desabilitar search history
- SubscribedContent vários
- Desabilitar sync de: Personalization, BrowserSettings, Credentials, Accessibility, Windows
- Set SyncPolicy = 5
- AllUpView / Remove TaskView

### 1.5 Menu Kill Time
- AutoEndTasks = 1
- HungAppTimeout = 1000
- WaitToKillAppTimeout = 2000
- LowLevelHooksTimeout = 1000
- MenuShowDelay = 0
- WaitToKillServiceTimeout = 2000

### 1.6 Additional Tweaks
- Windows Photo Viewer file associations (.tif, .bmp, .gif, .jpg, .png...)
- AppHost ContentEvaluation
- NumberOfSIUFInPeriod = 0
- DisableAutomaticRestartSignOn
- GameConfigStore Flags
- HttpAcceptLanguageOptOut
- AdvertisingInfo
- EnableWebContentEvaluation
- WiFi Sense desabilitado
- HotSpot reporting desabilitado
- Action Center desabilitado

### 1.7 Windows Update Blocker
- Abre ferramenta Wub.exe (terceiros)

### 1.8 Disable Cortana
- AllowCortana = 0
- AllowCloudSearch = 0
- AllowCortanaAboveLock = 0
- AllowSearchToUseLocation = 0
- ConnectedSearchUseWeb = 0
- DisableWebSearch = 0
- Remove-AppxPackage Cortana

### 1.9 Disable Error Reporting
- Stop/disable WerSvc service
- Desabilitar WER: DontShowUI, Disabled, DontSendAdditionalData, LoggingDisabled
- Consent: DefaultConsent = 0
- Policy: DoReport = 0, AutoApproveOSDumps = 0

### 1.10 Game Mode
- ON: AllowAutoGameMode = 1, AutoGameModeEnabled = 1
- OFF: AllowAutoGameMode = 0, AutoGameModeEnabled = 0

### 1.11 Disable Telemetry (Tasks)
- Desabilitar tasks: Consolidator, BthSQM, KernelCeipTask, UsbCeip, Uploader, Compatibility Appraiser, ProgramDataUpdater, StartupAppTask
- FamilySafetyMonitor, Refresh, Upload
- WinSAT

### 1.12 Disable Application Diagnostics/Telemetry
- DisableInventory, AITEnable, DisableUAR
- VDMDisallowed, DisableEngine, DisableWizard, DisablePCA, SbEnable
- Steps-Recorder disabled
- DeviceHealthAttestationService
- CloudContent: ConfigureWindowsSpotlight, DisableThirdPartySuggestions, DisableWindowsSpotlightFeatures, DisableWindowsConsumerFeatures
- ContentDeliveryManager varios
- UserProfileEngagement Scoobe
- AllowOnlineTips = 0
- DisablePushToInstall
- SubscribedContent varios

### 1.13 Disable Synchronization
- SettingSync Groups: Accessibility, AppSync, BrowserSettings, Credentials, DesktopTheme, Language, PackageState, Personalization, StartLayout, Windows

### 1.14 Disable Windows Customer Experience Index
- SQMClient IE/Reliability/Windows CEIP
- DisableOptinExperience
- AppV CEIP
- Messenger CEIP
- Internet Explorer SQM
- DisablePCA

### 1.15 Disable Bluetooth Services
- BTAGService, bthserv, BthAvctpSvc, BluetoothUserService → Start = 4

### 1.16 Track Only Important Failure Events
- Auditpol: Process Termination, RPC Events, Filtering Platform, DPAPI, IPsec, System Events, Security State Change, System Extension, System Integrity
- Autologger: Diagtrack-Listener, DiagLog, WiFiSession → Start = 0

### 1.17 Disable Diagnostic Services
- DiagTrack, dmwappushservice, diagnosticshub.standardcollector.service → stop + disable
- Tasks: StartupAppTask, DiskDiagnosticDataCollector, DiskDiagnosticResolver, Power Efficiency Diagnostics → disable

### 1.18 Disable Printing & Maps
- Spooler, PrintNotify, MapsBroker → Start = 4

### 1.19 Disable Background Apps
- GlobalUserDisabled = 1
- BackgroundAppGlobalToggle = 0
- bam, dam → Start = 4

### 1.20 Stop Reinstalling Preinstalled Apps
- SubscribedContent vários desabilitados

### 1.21 Disable Diagnostic Services (bis)
- ContentDeliveryManager varios

### 1.22 Optimize Windows Privacy Settings
- CapabilityAccessManager ConsentStore: activity, appDiagnostics, appointments, bluetoothSync, broadFileSystemAccess, chat, contacts, documentsLibrary, gazeInput, microphone (Allow), phoneCall, phoneCallHistory, picturesLibrary, radios, userNotificationListener, videosLibrary → Deny

### 1.23 Disable Smart Screen
- EnableSmartScreen = 0
- SmartScreenEnabled = Off
- EnableWebContentEvaluation = 0

### 1.24 Disable Fault Tolerant Heap
- FTH Enabled = 0

### 1.25 Disable Office Telemetry
- Office Common Telemetry
- Office 16.0 Common: sendcustomerdata, qmenable, updatereliabilitydata
- Outlook EnableLogging
- Word EnableLogging
- OSM: Enablelogging, EnableUpload, EnableFileObfuscation
- Prevented applications varios
- Prevented solution types varios

### 1.26 Disable Feedback
- DoNotShowFeedbackNotifications
- Siuf Rules: NumberOfSIUFInPeriod, PeriodInNanoSeconds
- NoExplicitFeedback
- ImplicitFeedback = 0

### 1.27 Optimize CapabilityAccessManager
- ConsentStore: appDiagnostics, appointments, email, phoneCall, userDataTasks → Deny
- DeviceAccess Global GUIDs → Deny
- Chat, contacts, messaging → Deny
- AllowMessageSync = 0

## 2. Network Tweaks

### 2.1 Otimizações de Rede
- (detalhes no script batch — QoS, TCP/IP params, DNS)

## 3. Power Tweaks

### 3.1 Otimizações de Energia
- (detalhes no script batch — powercfg, planos)

## 4. Priority Tweaks

### 4.1 Otimizações de Prioridade
- (detalhes no script batch)

## 5. General Tweaks

### 5.1 Otimizações Gerais
- (detalhes no script batch)

## 6. BCDedit Tweaks

### 6.1 Boot Config
- tscsyncpolicy legacy
- hypervisorlaunchtype off
- linearaddress57 OptOut
- increaseuserva
- isolatedcontext No
- allowedinmemorysettings 0x0
- vsmlaunchtype Off
- vm No
- x2apicpolicy Enable
- uselegacyapicmode No
- configaccesspolicy Default
- MSI Default
- usephysicaldestination No
- usefirmwarepcisettings No

## 7. Visual Tweaks

### 7.1 Animations & Visual
- Desabilitar animações
- Ajustes de desempenho visual

## 8. Storage

### 8.1 Limpeza de Armazenamento
- (detalhes no script batch)

## 9. USB Tweaks

### 9.1 Otimizações USB
- UsbPowerSaving

## 10. Fix Corrupted Files

### 10.1 SFC / DISM
- sfc /scannow
- dism /online /cleanup-image

## 11. RAM Tweaks

### 11.1 Otimizações de Memória
- (detalhes no script batch)

## 12. Full Screen Optimization

### 12.1 FSO Toggle
- Desabilitar Full Screen Optimizations

## 13. GPU Tweaks

### 13.1 Otimizações de Placa de Vídeo
- (detalhes no script batch)

## 14. BIOS Tweaks

### 14.1 Legacy BIOS
- VxD BIOS: CPUPriority, FastDRAM, AGPConcur, PCIConcur
- DMA protection / Core isolation off
- Kernel memory mitigations

## 15. CPU Tweaks

### 15.1 Advanced CPU Registry
- Power settings (GUIDs)
- Processor: AllowPepPerfStates, Cstates, Capabilities
- Power: HighPerformance, HighestPerformance, Throttle, Unpark, etc.
- PowerThrottlingOff
- CPCONCURRENCY, CPHEADROOM
- ControlSet001: ProccesorThrottlingEnabled, CpuIdleThreshold, etc.

### 15.2 Configure C-States
- powercfg: IDLEPROMOTE 98, IDLEDEMOTE 98, IDLECHECK 20000

### 15.3 Disable C-States
- AWAYMODE=0, ALLOWSTANDBY=0, HYBRIDSLEEP=0, PROCTHROTTLEMIN=100

### 15.4 CPU Idle Power Management
- CpuIdleScrub* values (dezenas de valores)

### 15.5 Device Idle Policy: Performance
- DEVICEIDLE = 0

### 15.6 Higher P-States on Lower C-States
- IDLESCALING = 1

### 15.7 Fix CPU Stock Speed
- IntelPPM Start = 3
- AmdPPM Start = 3

### 15.8 Don't Restrict Core Boost
- PERFEPP = 0

### 15.9 Disable Throttle States
- THROTTLING = 0

### 15.10 Enable Turbo Boost
- PERFBOOSTMODE = 1, PERFBOOSTPOL = 100

### 15.11 Disable Core Parking
- CPMINCORES = 100

### 15.12 CPU Cooling Tweaks
- SYSCOOLPOL = 1

### 15.13 Optimize AC Values
- PROCTHROTTLEMAX = 100 (AC)

### 15.14 Optimize DC Values
- PROCTHROTTLEMAX = 100 (DC)

### 15.15 Enable All Logical Processors
- bcdedit numproc = %NUMBER_OF_PROCESSORS%

### 15.16 Disable Away Mode
- powercfg: awaymode = 0

### 15.17 Intel CPU Tweaks
- DistributeTimers = 1
- DisableTsx = 0
- PowerThrottlingOff = 1
- CoalescingTimerInterval = 0
- EnergyEstimationEnabled = 0
- EventProcessorEnabled = 0

### 15.18 AMD CPU Tweaks
- DistributeTimers = 1
- DisableTsx = 1
- WakeEnabled/WdkSelectiveSuspendEnable = 0 (class driver search)

## 16. Mouse & Keyboard

### 16.1 Otimizações de Input
- (detalhes no script batch)

## 17. Clean

### 17.1 Clean Temp Files
- cleanmgr /sagerun:50

### 17.2 Clean Telemetry
- Limpar AutoLogger-Diagtrack-Listener.etl
- Limpar Windows.edb (search index)

### 17.3 Device Cleaner
- Remove-PnpDevice (unknown devices)

### 17.4 Clean Discord Cache
- Remove Discord Cache, Code Cache

## 18. DirectX Tweaks

### 18.1 Otimizações DirectX
- (detalhes no script batch)

## 19. Additional Tweaks (More)

- Sound Settings (abre painel)
- Disable UAC (EnableLUA=0 + todos os policies)
- Better Alt Tab (AltTabSettings=1)
- Reset IP Address (ipconfig /release /renew)
- Better Wallpaper Quality (JPEGImportQuality=100)
- Show File Extensions (HideFileExt=0, NeverShowExt removido)
- Sync Time (w32tm config NTP)
- Arrange Desktop Icons (FFlags)
- Gaming Audio (MMCSS Audio tweaks)
- Pin folders to Start (AllowPinnedFolder*)

## 20. Debloat

### 20.1 Disable Startup Apps
- Discord, Synapse3, Spotify, EpicGames, RiotClient, Steam (REG_BINARY StartupApproved)

### 20.2 Debloat Google Chrome
- Policies Chrome: Translate, TaskManager, Feedback, SpellCheck, Geolocation, Cookies, etc.
- Chrome Update policies

### 20.3 Disable Useless Services (Tasks)
- 50+ tasks desabilitadas (Compatibility Appraiser, CEIP, Defrag, DiskDiagnostic, Maps, etc.)

### 20.4 Uninstall Preinstalled Apps
- BingWeather, GetHelp, Getstarted, Messaging, 3DViewer, Solitaire, StickyNotes, MixedReality, OneConnect, People, Print3D, Skype, Alarms, Camera, Communications, Maps, FeedbackHub, SoundRecorder, YourPhone, ZuneMusic, etc.

### 20.5 Autoruns
- Abre ferramenta Autoruns.exe (Sysinternals)

### 20.6 Disable Unnecessary Bloatware
- TailoredExperiencesWithDiagnosticDataEnabled
- DiagTrack ShowedToastAtLevel
- Input TIPC Enabled
- UploadUserActivities, PublishUserActivities
- SaveZoneInformation
- DisableDiagnosticTracing
- WDI ScenarioExecutionEnabled

## 21. Optimize Games (Fortnite)

### 21.1 Real Fortnite Ping
- Ping para servidores regionais: EU, NA East, NA Central, NA West, Brazil, Asia, Middle East, Oce/Australia

### 21.2 Optimize Fortnite Settings
- Abre ferramenta Fortnite_Settings.exe

## 22. Fixes

### 22.1 Reinstall All Windows Apps
- Get-AppxPackage -allusers | Add-AppxPackage -register

### 22.2 Revert Alt Tab
- AltTabSettings = 0

## 23. BIOS Tweaks (boot)

### 23.1 BCDedit
- tscsyncpolicy legacy
- hypervisorlaunchtype off
- linearaddress57 OptOut, increaseuserva
- isolatedcontext No, allowedinmemorysettings 0x0
- vsmlaunchtype Off, vm No
- DeviceGuard, FVE policies
- x2apicpolicy Enable, uselegacyapicmode No
- configaccesspolicy Default, MSI Default
- usephysicaldestination No, usefirmwarepcisettings No
- numproc

## Serviços Desabilitados (via schtasks)
- ~50 tasks do Windows desabilitadas (lista completa no script)

## Apps Removidos (via PowerShell)
- ~30+ apps UWP (Bing*, Xbox*, Skype, Zune, etc.)
