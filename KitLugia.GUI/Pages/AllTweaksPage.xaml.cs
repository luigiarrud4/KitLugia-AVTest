using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    public partial class AllTweaksPage : Page
    {
        private List<TweakItem> _allTweaks = new();
        private string _currentFilter = "";
        private string _currentCategory = "Todas as Categorias";
        private bool _isLoaded;

        public AllTweaksPage()
        {
            InitializeComponent();
            Loaded += AllTweaksPage_Loaded;
            Unloaded += (_, _) => Cleanup();
        }

        public void Cleanup()
        {
            DataContext = null;
            _allTweaks.Clear();
        }

        private async void AllTweaksPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadTweaksAsync();
            _isLoaded = true;
        }

        private async System.Threading.Tasks.Task LoadTweaksAsync()
        {
            _allTweaks = DefineSystemTweaks();
            ApplyFilter();
        }

        private List<TweakItem> DefineSystemTweaks()
        {
            return new List<TweakItem>
            {
                // ===== CPU & KERNEL =====
                new("Power Throttling (CPU)", "Impede Windows de throttlar CPU de processos em segundo plano.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.IsPowerThrottlingDisabled(),
                    () => { var r = SystemTweaks.DisablePowerThrottling(); return r.Success; },
                    () => { var r = SystemTweaks.EnablePowerThrottling(); return r.Success; }),

                new("Core Parking", "Mantém todos os cores CPU ativos sem desligar nenhum.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["CoreParking"],
                    () => { var r = SystemTweaks.DisableCoreParking(); return r.Success; },
                    () => {
                        try {
                            string[] keys = { @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\0cc5b647-c1df-4637-891a-dec35c318583", @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c8-3b32988b1dd4\ea4be0c1-7c65-46f8-8c17-f298766665d9" };
                            foreach (var k in keys) { using var rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(k, true); rk?.DeleteValue("ValueMax", false); rk?.DeleteValue("ValueMin", false); }
                            return true;
                        } catch { return false; }
                    }),

                new("Timer Coalescing", "Desativa coalescência de timers para maior precisão.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["TimerCoalescing"],
                    () => { var r = SystemTweaks.DisableTimerCoalescing(); return r.Success; },
                    () => {
                        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", true); k?.DeleteValue("CoalescingTimerInterval", false); return true; }
                        catch { return false; }
                    }),

                new("Win32 Priority Separation", "Ajusta prioridade de processos em foreground para gaming.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["Win32PrioritySeparation"],
                    () => { var r = SystemTweaks.ApplyFullGamingLatencyProfile(0x26); return r.Success; },
                    () => { var r = SystemTweaks.SetWin32PrioritySeparation(2); return r.Success; }),

                new("Global Timer Resolution", "Permite que aplicações solicitem timers de 1ms para baixa latência.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["GlobalTimerResolution"],
                    () => { var r = SystemTweaks.EnableGlobalTimerResolution(); return r.Success; },
                    () => {
                        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", true); k?.DeleteValue("GlobalTimerResolutionRequests", false); return true; }
                        catch { return false; }
                    }),

                new("System Responsiveness (Gaming)", "Define responsividade do sistema para máxima performance em jogos.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["SystemResponsiveness"],
                    () => { var r = SystemTweaks.SetSystemResponsivenessGaming(); return r.Success; },
                    () => { Microsoft.Win32.Registry.SetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 20, Microsoft.Win32.RegistryValueKind.DWord); return true; }),

                new("Timer Resolution (HPET)", "Otimiza timer resolution para baixa latência via BCD.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.IsTimerResolutionOptimized(),
                    () => { var r = SystemTweaks.ToggleTimerResolution(); return r.Success; },
                    () => { var r = SystemTweaks.ToggleTimerResolution(); return r.Success; }),

                new("Paging Executive (Kernel na RAM)", "Mantém kernel do Windows na RAM, sem paginar para o disco.", "Performance (Memória)", "SystemTweaks",
                    () => SystemTweaks.IsMemoryPaginationDisabled(),
                    () => { SystemTweaks.DisableMemoryPagination(); return true; },
                    () => { SystemTweaks.EnableMemoryPagination(); return true; }),

                new("Prefetch / Superfetch", "Controla o prefetch do Windows para otimizar inicialização.", "Performance (Disco)", "SystemTweaks",
                    () => SystemTweaks.IsPrefetchParametersDisabled(),
                    () => { SystemTweaks.DisablePrefetchParameters(); return true; },
                    () => { SystemTweaks.EnablePrefetchParameters(); return true; }),

                new("Boot Optimization", "Otimiza o processo de boot do Windows.", "Performance (Disco)", "SystemTweaks",
                    () => SystemTweaks.IsBootOptimizeDisabled(),
                    () => { SystemTweaks.DisableBootOptimize(); return true; },
                    () => { SystemTweaks.EnableBootOptimize(); return true; }),

                new("I/O Page Lock Limit", "Aumenta o limite de lock de páginas de I/O para transferências.", "Performance (Memória)", "SystemTweaks",
                    () => SystemTweaks.IsIoPageLockLimitEnabled(),
                    () => { SystemTweaks.EnableIoPageLockLimit(); return true; },
                    () => { SystemTweaks.DisableIoPageLockLimit(); return true; }),

                new("Memory Usage (Cache)", "Aumenta o cache de memória do sistema para melhor performance.", "Performance (Memória)", "SystemTweaks",
                    () => SystemTweaks.IsMemoryUsageEnabled(),
                    () => { var r = SystemTweaks.ToggleMemoryUsage(); return r.Success; },
                    () => { var r = SystemTweaks.ToggleMemoryUsage(); return r.Success; }),

                new("Gaming Profile (MMCSS)", "Define perfil Games como Alta Prioridade no MMCSS.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.IsGamingOptimized(),
                    () => { SystemTweaks.ApplyGamingOptimizations(); return true; },
                    () => { SystemTweaks.RevertGamingOptimizations(); return true; }),

                new("Gaming Profile Advanced", "Avançado: GPU Priority=8, Scheduling=High para jogos.", "Performance (CPU)", "SystemTweaks",
                    () => SystemTweaks.IsGamingProfileAdvancedApplied(),
                    () => { var r = SystemTweaks.OptimizeGamingProfileAdvanced(); return r.Success; },
                    () => false),

                new("NDU (Network Diagnostic Usage)", "Remove limitação de rede do NDU e melhora desempenho.", "Rede e Latência", "SystemTweaks",
                    () => SystemTweaks.IsNDUDisabled(),
                    () => { SystemTweaks.DisableNDU(); return true; },
                    () => { SystemTweaks.EnableNDU(); return true; }),

                new("System Responsiveness (Padrão)", "Define SystemResponsiveness para 20 (padrão balanceado).", "Performance (CPU)", "SystemTweaks",
                    () => !SystemTweaks.CheckGamingLatencyStatus()["SystemResponsiveness"],
                    () => { SystemTweaks.OptimizeSystemResponsiveness(); return true; },
                    () => SystemTweaks.RevertGamingLatencyProfile().Success),

                // ===== GPU =====
                new("Multi-Plane Overlay (MPO)", "Desativa MPO para corrigir flickering e stuttering em GPUs.", "Performance (GPU)", "SystemTweaks",
                    () => SystemTweaks.IsMpoDisabled(),
                    () => { var r = SystemTweaks.ToggleMpo(); return r.Success; },
                    () => { var r = SystemTweaks.ToggleMpo(); return r.Success; }),

                new("GDI Scaling", "Desativa GDI Scaling de apps legados para reduzir carga da GPU.", "Performance (GPU)", "SystemTweaks",
                    () => SystemTweaks.IsGdiScalingDisabled(),
                    () => { var r = SystemTweaks.DisableGdiScaling(); return r.Success; },
                    () => { var r = SystemTweaks.EnableGdiScaling(); return r.Success; }),

                new("VRAM Dedicada (Segment Size)", "Aumenta VRAM dedicada via registro (Aplica em próximo boot).", "Performance (GPU)", "SystemTweaks",
                    () => false,
                    () => true,
                    () => true),

                // ===== REDE =====
                new("Network Throttling", "Remove limitação de rede em segundo plano (10% padrão).", "Rede e Latência", "SystemTweaks",
                    () => SystemTweaks.IsNetworkThrottlingDisabled(),
                    () => { SystemTweaks.DisableNetworkThrottling(); return true; },
                    () => false),

                new("Nagle Algorithm (TCP)", "Desativa algoritmo Nagle para reduzir latência de rede.", "Rede e Latência", "SystemTweaks",
                    () => false,
                    () => { var r = SystemTweaks.DisableNagleAlgorithm(); return r.Success; },
                    () => { var r = SystemTweaks.RevertNagleAlgorithm(); return r.Success; }),

                new("TCP/IP Auto-Tuning", "Otimiza autotuning de TCP/IP para gaming.", "Rede e Latência", "SystemTweaks",
                    () => false,
                    () => { var r = SystemTweaks.OptimizeNetworkForGaming(); return r.Success; },
                    () => false),

                new("DNS (Custom)", "Define servidores DNS personalizados para menor latência.", "Rede e Latência", "SystemTweaks",
                    () => false,
                    () => false,
                    () => false),

                new("Network Driver Optimizations", "Desativa Interrupt Moderation e EEE na placa de rede.", "Rede e Latência", "SystemTweaks",
                    () => false,
                    () => { SystemTweaks.ToggleNetworkDriverOptimizations(""); return true; },
                    () => false),

                // ===== PRIVACIDADE =====
                new("Bing Search", "Desativa busca Bing no menu Iniciar.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsBingDisabled(),
                    () => { SystemTweaks.ApplyBingTweak(); return true; },
                    () => { SystemTweaks.RevertRegistryValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"); return true; }),

                new("Telemetria (DiagTrack)", "Desativa o serviço de telemetria Connected User Experiences.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsDiagTrackDisabled(),
                    () => { SystemTweaks.DisableDiagTrack(); return true; },
                    () => { SystemTweaks.EnableDiagTrack(); return true; }),

                new("Windows Error Reporting", "Desativa relatório de erros do Windows.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsErrorReportingDisabled(),
                    () => { SystemTweaks.DisableErrorReporting(); return true; },
                    () => { SystemTweaks.EnableErrorReporting(); return true; }),

                new("Custom Inking (Ink Workspace)", "Desativa o Windows Ink Workspace (caneta/digitador).", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsCustomInkingDisabled(),
                    () => { SystemTweaks.DisableCustomInking(); return true; },
                    () => { SystemTweaks.EnableCustomInking(); return true; }),

                new("Lock Screen", "Remove a tela de bloqueio do Windows.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsLockScreenDisabled(),
                    () => { SystemTweaks.DisableLockScreen(); return true; },
                    () => { SystemTweaks.EnableLockScreen(); return true; }),

                new("Web Search (Search)", "Desativa busca web no menu Iniciar.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsWebSearchDisabled(),
                    () => { SystemTweaks.DisableWebSearch(); return true; },
                    () => { SystemTweaks.EnableWebSearch(); return true; }),

                new("Cloud Search (MSA)", "Desativa busca cloud com conta Microsoft.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsMSACloudSearchDisabled(),
                    () => { SystemTweaks.DisableMSACloudSearch(); return true; },
                    () => { SystemTweaks.EnableMSACloudSearch(); return true; }),

                new("Cloud Search (AAD)", "Desativa busca cloud com conta corporativa.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsAADCloudSearchDisabled(),
                    () => { SystemTweaks.DisableAADCloudSearch(); return true; },
                    () => { SystemTweaks.EnableAADCloudSearch(); return true; }),

                new("Search History (Device)", "Desativa histórico de busca no dispositivo.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsDeviceSearchHistoryDisabled(),
                    () => { SystemTweaks.DisableDeviceSearchHistory(); return true; },
                    () => { SystemTweaks.EnableDeviceSearchHistory(); return true; }),

                new("Ads on Lock Screen", "Remove propagandas na tela de bloqueio.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsAdsOnLockScreenDisabled(),
                    () => { SystemTweaks.DisableAdsOnLockScreen(); return true; },
                    () => { SystemTweaks.EnableAdsOnLockScreen(); return true; }),

                new("Auto Install Apps", "Impede Windows de reinstalar aplicativos automaticamente.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsAutoInstallationAppsDisabled(),
                    () => { SystemTweaks.DisableAutoInstallationApps(); return true; },
                    () => { SystemTweaks.EnableAutoInstallationApps(); return true; }),

                new("AutoPlay", "Desativa execução automática de mídias (USB/CD).", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsAutoplayDisabled(),
                    () => { SystemTweaks.DisableAutoplay(); return true; },
                    () => { SystemTweaks.EnableAutoplay(); return true; }),

                new("Tailored Experiences", "Desativa experiências personalizadas com dados de diagnóstico.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsTailoredExperiencesDisabled(),
                    () => { SystemTweaks.DisableTailoredExperiences(); return true; },
                    () => { SystemTweaks.EnableTailoredExperiences(); return true; }),

                new("Tips & Suggestions", "Desativa dicas e sugestões do Windows.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsTipsAndSuggestionsDisabled(),
                    () => { SystemTweaks.DisableTipsAndSuggestions(); return true; },
                    () => { SystemTweaks.EnableTipsAndSuggestions(); return true; }),

                new("Offer Suggestions", "Desativa sugestões de ofertas no Windows.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsOfferSuggestionsDisabled(),
                    () => { SystemTweaks.DisableOfferSuggestions(); return true; },
                    () => { SystemTweaks.EnableOfferSuggestions(); return true; }),

                new("Personalized Ads (Store)", "Desativa anúncios personalizados baseados em uso.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsPersonalizedAdsStoreAppsDisabled(),
                    () => { SystemTweaks.DisablePersonalizedAdsStoreApps(); return true; },
                    () => { SystemTweaks.EnablePersonalizedAdsStoreApps(); return true; }),

                new("Visual Studio Telemetry", "Desativa telemetria do Visual Studio (SQM).", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsVisualStudioTelemetryDisabled(),
                    () => { SystemTweaks.DisableVisualStudioTelemetry(); return true; },
                    () => { SystemTweaks.EnableVisualStudioTelemetry(); return true; }),

                new("Windows Feedback", "Desativa solicitações de feedback do Windows.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsWindowsFeedbackDisabled(),
                    () => { SystemTweaks.DisableWindowsFeedback(); return true; },
                    () => { SystemTweaks.EnableWindowsFeedback(); return true; }),

                new("Start Menu Suggestions", "Remove sugestões de apps no menu Iniciar.", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsStartMenuAppSuggestionsDisabled(),
                    () => { SystemTweaks.DisableStartMenuAppSuggestions(); return true; },
                    () => { SystemTweaks.EnableStartMenuAppSuggestions(); return true; }),

                new("Diagnostic Data (Telemetry Off)", "Desliga coleta de dados de diagnóstico (AllowTelemetry=0).", "Privacidade e Telemetria", "SystemTweaks",
                    () => SystemTweaks.IsDiagnosticDataOff(),
                    () => { SystemTweaks.DiagnosticDataOff(); return true; },
                    () => { SystemTweaks.DiagnosticDataOn(); return true; }),

                // ===== EXPLORER & INTERFACE =====
                new("Dark Mode", "Ativa tema escuro para apps e sistema.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsDarkModeEnabled(),
                    () => { SystemTweaks.EnableDarkMode(); return true; },
                    () => { SystemTweaks.DisableDarkMode(); return true; }),

                new("Classic Context Menu", "Menu de contexto clássico (Windows 10) em vez do Windows 11.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsClassicContextMenuEnabled(),
                    () => { SystemTweaks.EnableClassicContextMenu(); return true; },
                    () => { SystemTweaks.DisableClassicContextMenu(); return true; }),

                new("File Extensions", "Mostrar extensões de arquivos conhecidas.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsExtensionsShown(),
                    () => { SystemTweaks.ShowExtensions(); return true; },
                    () => { SystemTweaks.HideExtensions(); return true; }),

                new("Hidden Files", "Mostrar arquivos ocultos no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsHiddenShown(),
                    () => { SystemTweaks.ShowHidden(); return true; },
                    () => { SystemTweaks.HideHidden(); return true; }),

                new("System Files", "Mostrar arquivos protegidos do sistema no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsHiddenSystemShown(),
                    () => { SystemTweaks.ShowHiddenSystem(); return true; },
                    () => { SystemTweaks.HideHiddenSystem(); return true; }),

                new("Open Explorer to This PC", "Abre 'Este Computador' em vez de 'Início' no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsFileExplorerThisPCEnabled(),
                    () => { SystemTweaks.OpenFileExplorerThisPC(); return true; },
                    () => { SystemTweaks.DisableOpenFileExplorerThisPC(); return true; }),

                new("Icon Cache", "Aumenta cache de ícones para melhor performance visual.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsIconCacheIncreased(),
                    () => { SystemTweaks.IncreaseIconCache(); return true; },
                    () => { SystemTweaks.ResetIconCache(); return true; }),

                new("AutoEndTasks", "Finaliza automaticamente programas travados ao desligar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsAutoEndTasksEnabled(),
                    () => { SystemTweaks.EnableAutoEndTasks(); return true; },
                    () => { SystemTweaks.DisableAutoEndTasks(); return true; }),

                new("Menu Show Delay", "Remove delay de abertura de menus (aparecem instantaneamente).", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsMenuShowDelayDisabled(),
                    () => { SystemTweaks.DisableMenuShowDelay(); return true; },
                    () => { SystemTweaks.EnableMenuShowDelay(); return true; }),

                new("Mouse Hover Time", "Ajusta tempo para hover do mouse (mais rápido).", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsMouseHoverTimeEnabled(),
                    () => { SystemTweaks.EnableMouseHoverTime(); return true; },
                    () => { SystemTweaks.DisableMouseHoverTime(); return true; }),

                new("Shortcut Text (link)", "Remove o texto '- Atalho' de novos atalhos.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsShortcutTextDisabled(),
                    () => { SystemTweaks.DisableShortcutText(); return true; },
                    () => { SystemTweaks.EnableShortcutText(); return true; }),

                new("Recent Files", "Desativa lista de arquivos recentes no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsRecentFilesDisabled(),
                    () => { SystemTweaks.DisableRecentFiles(); return true; },
                    () => { SystemTweaks.EnableRecentFiles(); return true; }),

                new("Frequent Folders", "Desativa pastas frequentes no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsFrequentFoldersDisabled(),
                    () => { SystemTweaks.DisableFrequentFolders(); return true; },
                    () => { SystemTweaks.EnableFrequentFolders(); return true; }),

                new("Sync Provider Notifications", "Remove notificações de sincronização (OneDrive).", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsSyncProviderNotificationsDisabled(),
                    () => { SystemTweaks.DisableSyncProviderNotifications(); return true; },
                    () => { SystemTweaks.EnableSyncProviderNotifications(); return true; }),

                new("Most Used Apps (Start)", "Mostra aplicativos mais usados no menu Iniciar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsMostUsedAppsHidden(),
                    () => { SystemTweaks.HideMostUsedApps(); return true; },
                    () => { SystemTweaks.ShowMostUsedApps(); return true; }),

                new("Recently Added (Start)", "Mostra aplicativos adicionados recentemente no Iniciar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsStartMenuRecentlyAddedHidden(),
                    () => { SystemTweaks.HideStartMenuRecentlyAdded(); return true; },
                    () => { SystemTweaks.ShowStartMenuRecentlyAdded(); return true; }),

                new("Recently Opened (Start)", "Mostra itens abertos recentemente no menu Iniciar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsStartMenuRecentlyOpenedHidden(),
                    () => { SystemTweaks.HideStartMenuRecentlyOpened(); return true; },
                    () => { SystemTweaks.ShowStartMenuRecentlyOpened(); return true; }),

                new("Account Notifications (Start)", "Mostra notificações de conta no menu Iniciar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsStartMenuAccountNotificationsHidden(),
                    () => { SystemTweaks.HideStartMenuAccountNotifications(); return true; },
                    () => { SystemTweaks.ShowStartMenuAccountNotifications(); return true; }),

                new("Recommendations (Start)", "Remove recomendações do menu Iniciar.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsStartMenuRecommendationsHidden(),
                    () => { SystemTweaks.HideStartMenuRecommendations(); return true; },
                    () => { SystemTweaks.ShowStartMenuRecommendations(); return true; }),

                new("Animation (Min/Max)", "Desativa animação de minimizar/maximizar janelas.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsAnimationEffectMaxMinDisabled(),
                    () => { SystemTweaks.DisableAnimationEffectMaxMin(); return true; },
                    () => { SystemTweaks.EnableAnimationEffectMaxMin(); return true; }),

                new("AutoComplete Suggestions", "Desativa sugestões de auto-completar no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsAutoSuggestDisabled(),
                    () => { SystemTweaks.DisableAutoSuggest(); return true; },
                    () => { SystemTweaks.EnableAutoSuggest(); return true; }),

                new("Append Completion", "Desativa append de sugestões no Explorer.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsAppendCompletionDisabled(),
                    () => { SystemTweaks.DisableAppendCompletion(); return true; },
                    () => { SystemTweaks.EnableAppendCompletion(); return true; }),

                new("Snipping Print Screen", "Altera tecla Print Screen para abrir Ferramenta de Captura.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsSnippingPrintScreenEnabled(),
                    () => { SystemTweaks.EnableSnippingPrintScreen(); return true; },
                    () => { SystemTweaks.DisableSnippingPrintScreen(); return true; }),

                new("NumLock on Startup", "Ativa NumLock automaticamente na inicialização.", "Explorer e Interface", "SystemTweaks",
                    () => SystemTweaks.IsNumLockonStartupEnabled(),
                    () => { SystemTweaks.EnableNumLockonStartup(); return true; },
                    () => { SystemTweaks.DisableNumLockonStartup(); return true; }),

                // ===== SERVIÇOS =====
                new("Background Apps", "Bloqueia apps em segundo plano (forçar deny).", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsBackgroundAppsDisabled(),
                    () => { SystemTweaks.DisableBackgroundApps(); return true; },
                    () => { SystemTweaks.EnableBackgroundApps(); return true; }),

                new("Service Startup", "Define serviços não essenciais como Manual (14 serviços).", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsServiceStartupOptimized(),
                    () => { SystemTweaks.OptimizeServiceStartup(); return true; },
                    () => { SystemTweaks.RevertServiceStartup(); return true; }),

                new("Diagnostic Services (DPS/Wdi)", "Desativa serviços de diagnóstico DPS, WdiServiceHost, WdiSystemHost.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsDiagnosticServicesDisabled(),
                    () => SystemTweaks.DisableDiagnosticServices(),
                    () => SystemTweaks.EnableDiagnosticServices()),

                new("Windows Search", "Desativa o serviço Windows Search (indexador).", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsWindowsSearchDisabled(),
                    () => { SystemTweaks.DisableWindowsSearch(); return true; },
                    () => { SystemTweaks.EnableWindowsSearch(); return true; }),

                new("Print Spooler", "Desativa o serviço de spooler de impressão.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsPrintSpoolerDisabled(),
                    () => { SystemTweaks.DisablePrintSpooler(); return true; },
                    () => { SystemTweaks.EnablePrintSpooler(); return true; }),

                new("Windows Defender", "Desativa Windows Defender via política de grupo.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsMSDefenderDisabled(),
                    () => { SystemTweaks.DisableMSDefender(); return true; },
                    () => { SystemTweaks.EnableMSDefender(); return true; }),

                new("OneDrive", "Desinstala o OneDrive completamente do sistema.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsOneDriveUninstalled(),
                    () => { SystemTweaks.UninstallOneDrive(); return true; },
                    () => false),

                new("Google Update Tasks", "Desativa tarefas agendadas do Google Update.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsGoogleUpdateTaskDisabled(),
                    () => { SystemTweaks.DisableGoogleUpdateTask(); return true; },
                    () => { SystemTweaks.EnableGoogleUpdateTask(); return true; }),

                new("Edge Update Task", "Desativa tarefa agendada do Microsoft Edge Update.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsMicrosoftEdgeUpdateTaskDisabled(),
                    () => { SystemTweaks.DisableMicrosoftEdgeUpdateTask(); return true; },
                    () => { SystemTweaks.EnableMicrosoftEdgeUpdateTask(); return true; }),

                new("Scheduled Defrag", "Desativa desfragmentação agendada do Windows.", "Serviços do Windows", "SystemTweaks",
                    () => SystemTweaks.IsScheduledDefragDisabled(),
                    () => { SystemTweaks.DisableScheduledDefrag(); return true; },
                    () => { SystemTweaks.EnableScheduledDefrag(); return true; }),

                // ===== ENERGIA =====
                new("Hibernate", "Desativa hibernação (libera hiberfil.sys).", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsHibernateDisabled(),
                    () => { SystemTweaks.DisableHibernate(); return true; },
                    () => { SystemTweaks.EnableHibernate(); return true; }),

                new("Hybrid Sleep", "Desativa sleep híbrido (para evitar wakes noturnos).", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsHybridSleepDisabled(),
                    () => { SystemTweaks.DisableHybridSleep(); return true; },
                    () => { SystemTweaks.EnableHybridSleep(); return true; }),

                new("Sleep Mode", "Desativa suspensão automática do sistema.", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsSleepDisabled(),
                    () => { SystemTweaks.DisableSleep(); return true; },
                    () => { SystemTweaks.EnableSleep(); return true; }),

                new("Display Timeout", "Desativa desligamento automático do monitor.", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsTurnOffDisplayDisabled(),
                    () => { SystemTweaks.DisableTurnOffDisplay(); return true; },
                    () => { SystemTweaks.EnableTurnOffDisplay(); return true; }),

                new("PCIe ASPM", "Desativa ASPM (Active State Power Management) do PCIe.", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsPcieLinkStatePowerManagementDisabled(),
                    () => { var r = SystemTweaks.DisablePcieLinkStatePowerManagement(); return r.Success; },
                    () => { var r = SystemTweaks.EnablePcieLinkStatePowerManagement(); return r.Success; }),

                new("USB Power Saving", "Desativa suspensão seletiva de USB (Selective Suspend).", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsUsbPowerSavingDisabled(),
                    () => false,
                    () => false),

                new("Turn Off Display + Disk Timeout", "Desliga timeout de disco e monitor (Nunca desligar).", "Energia e Suspensão", "SystemTweaks",
                    () => SystemTweaks.IsHardDiskDisplayTimeoutDisabled(),
                    () => { var r = SystemTweaks.DisableHardDiskDisplayTimeout(); return r.Success; },
                    () => { var r = SystemTweaks.EnableHardDiskDisplayTimeout(); return r.Success; }),

                // ===== WINDOWS UPDATE =====
                new("No Auto Reboot", "Impede reinicialização automática após atualizações.", "Windows Update", "SystemTweaks",
                    () => SystemTweaks.IsNoAutoRebootEnabled(),
                    () => { SystemTweaks.EnableNoAutoReboot(); return true; },
                    () => { SystemTweaks.DisableNoAutoReboot(); return true; }),

                new("Auto Updates", "Desativa Windows Update automático.", "Windows Update", "SystemTweaks",
                    () => SystemTweaks.IsAutoWindowsUpdatesDisabled(),
                    () => { SystemTweaks.DisableAutoWindowsUpdates(); return true; },
                    () => { SystemTweaks.EnableAutoWindowsUpdates(); return true; }),

                new("AU Options (Política)", "Define política de atualização automática manual.", "Windows Update", "SystemTweaks",
                    () => SystemTweaks.IsAUOptionsEnabled(),
                    () => { SystemTweaks.EnableAUOptions(); return true; },
                    () => { SystemTweaks.DisableAUOptions(); return true; }),

                new("System Restore", "Desativa proteção do sistema (pontos de restauração).", "Windows Update", "SystemTweaks",
                    () => SystemTweaks.IsSystemRestoreDisabled(),
                    () => { SystemTweaks.DisableSystemRestore(); return true; },
                    () => { SystemTweaks.EnableSystemRestore(); return true; }),

                new("VBS / HVCI", "Desativa Virtualization-Based Security e integridade de memória.", "Windows Update", "SystemTweaks",
                    () => SystemTweaks.IsVbsEnabled(),
                    () => { var r = SystemTweaks.ToggleVbs(); return r.Success; },
                    () => { var r = SystemTweaks.ToggleVbs(); return r.Success; }),

                // ===== OUTROS =====
                new("Crash Auto Reboot", "Desativa reinicialização automática em caso de crash (BSOD).", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsCrashAutoRebootDisabled(),
                    () => { SystemTweaks.DisableCrashAutoReboot(); return true; },
                    () => { SystemTweaks.EnableCrashAutoReboot(); return true; }),

                new("AeDebug (Just-In-Time)", "Desativa depurador JIT do Windows (AeDebug).", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsAeDebugDisabled(),
                    () => { SystemTweaks.DisableAeDebug(); return true; },
                    () => { SystemTweaks.EnableAeDebug(); return true; }),

                new("Low Disk Space Check", "Desativa verificação de espaço em disco baixo.", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsLowDiskSpaceChecksDisabled(),
                    () => { SystemTweaks.DisableLowDiskSpaceChecks(); return true; },
                    () => { SystemTweaks.EnableLowDiskSpaceChecks(); return true; }),

                new("Auto Defrag (Idle)", "Desativa desfragmentação automática em idle.", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsAutoDefragIdleDisabled(),
                    () => { SystemTweaks.DisableAutoDefragIdle(); return true; },
                    () => { SystemTweaks.EnableAutoDefragIdle(); return true; }),

                new("Security Notifications (Windows Security)", "Esconde notificações de segurança do Windows Security Center.", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsWindowsSecurityNotificationsHidden(),
                    () => { SystemTweaks.HideWindowsSecurityNotifications(); return true; },
                    () => { SystemTweaks.ShowWindowsSecurityNotifications(); return true; }),

                new("Non-critical Security Notifications", "Esconde notificações não críticas do Windows Defender.", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsWindowsSecurityNoncriticalNotificationsHidden(),
                    () => { SystemTweaks.HideWindowsSecurityNoncriticalNotifications(); return true; },
                    () => { SystemTweaks.ShowWindowsSecurityNoncriticalNotifications(); return true; }),

                new("Fast Shutdown", "Acelera desligamento (AutoEndTasks + reduz timeouts).", "Outros", "SystemTweaks",
                    () => SystemTweaks.IsFastShutdownEnabled(),
                    () => { SystemTweaks.ToggleFastShutdown(); return true; },
                    () => { SystemTweaks.ToggleFastShutdown(); return true; }),

                new("Input Queue (Keyboard/Mouse)", "Aumenta buffer de dados de teclado e mouse (30 vs 100).", "Outros", "SystemTweaks",
                    () => SystemTweaks.CheckGamingLatencyStatus()["InputQueue"],
                    () => { var r = SystemTweaks.OptimizeInputQueue(); return r.Success; },
                    () => SystemTweaks.RevertGamingLatencyProfile().Success),

                new("GPU MSI Mode", "Força MSI (Message Signaled Interrupts) na GPU.", "Outros", "SystemTweaks",
                    () => false,
                    () => { SystemTweaks.ToggleGpuMsiMode(""); return true; },
                    () => { SystemTweaks.ToggleGpuMsiMode(""); return true; }),

                // ===== EXTRAS (Toolbox) =====
                new("GodMode", "Cria ou remove o atalho 'GodMode' na área de trabalho com acesso a todas as configurações do sistema.", "Outros", "Toolbox",
                    () => Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}")),
                    () => Toolbox.ToggleGodMode().Success,
                    () => Toolbox.ToggleGodMode().Success),

                new("Obter Controle Total (Menu Contexto)", "Adiciona ou remove 'Obter Controle Total' (Take Ownership) no menu de clique direito de arquivos e pastas.", "Explorer e Interface", "Toolbox",
                    () => Toolbox.IsContextMenuItemInstalled(@"*\shell\runas"),
                    () => Toolbox.ToggleTakeOwnershipContext().Success,
                    () => Toolbox.ToggleTakeOwnershipContext().Success),

                new("CMD Aqui (Admin)", "Adiciona ou remove o atalho 'Abrir CMD aqui como Admin' no menu de contexto de pastas.", "Explorer e Interface", "Toolbox",
                    () => Toolbox.IsContextMenuItemInstalled(@"Directory\Background\shell\runascmd"),
                    () => Toolbox.ToggleCmdContext().Success,
                    () => Toolbox.ToggleCmdContext().Success),

                new("Arquivos Ocultos", "Mostra ou esconde arquivos e pastas ocultas no Explorador de Arquivos.", "Explorer e Interface", "Toolbox",
                    () => Toolbox.AreHiddenFilesVisible(),
                    () => Toolbox.ToggleHiddenFiles().Success,
                    () => Toolbox.ToggleHiddenFiles().Success),

                new("Extensões de Arquivos", "Mostra ou esconde as extensões de arquivos conhecidos no Explorador de Arquivos.", "Explorer e Interface", "Toolbox",
                    () => Toolbox.AreExtensionsVisible(),
                    () => Toolbox.ToggleFileExtensions().Success,
                    () => Toolbox.ToggleFileExtensions().Success),

                // ===== REDE (Toolbox) =====
                new("RSS (Receive Side Scaling)", "Distribui processamento de rede entre múltiplos cores de CPU para melhor throughput.", "Rede e Latência", "Toolbox",
                    () => Toolbox.IsRSSEnabled(),
                    () => Toolbox.EnableRSS().Success,
                    () => Toolbox.DisableRSS().Success),

                new("Task Offload (NIC)", "Permite que a placa de rede processe tarefas para reduzir carga da CPU.", "Rede e Latência", "Toolbox",
                    () => Toolbox.IsTaskOffloadEnabled(),
                    () => Toolbox.EnableTaskOffload().Success,
                    () => Toolbox.DisableTaskOffload().Success),

                new("TCP Registry Tweaks", "Aplica tweaks avançados de TCP/IP no registro (portas, buffers, TTL) para gaming.", "Rede e Latência", "Toolbox",
                    () => Toolbox.IsTcpRegistryTweaksApplied(),
                    () => Toolbox.ApplyTcpRegistryTweaks().Success,
                    () => Toolbox.RevertTcpRegistryTweaks().Success),

                new("Latency Congestion Control", "Ajusta algoritmo de congestionamento TCP para CTCP — reduz jitter em jogos e VoIP.", "Rede e Latência", "Toolbox",
                    () => Toolbox.IsCTCPConfigured(),
                    () => Toolbox.ApplyLatencyCongestionControl().Success,
                    () => Toolbox.RevertLatencyCongestionControl().Success),
            };
        }

        private void ApplyFilter()
        {
            if (!_isLoaded || ListTweaks == null) return;

            var filtered = _allTweaks.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(_currentFilter))
            {
                var f = _currentFilter.ToLower();
                filtered = filtered.Where(t =>
                    t.Name.ToLower().Contains(f) ||
                    t.Description.ToLower().Contains(f) ||
                    t.Category.ToLower().Contains(f) ||
                    t.Source.ToLower().Contains(f));
            }

            if (_currentCategory != "Todas as Categorias")
            {
                filtered = filtered.Where(t => t.Category == _currentCategory);
            }

            var list = filtered.ToList();
            ListTweaks.ItemsSource = list;
            if (TxtCount != null)
                TxtCount.Text = $"{list.Count} tweaks";
            if (TxtSubtitle != null)
                TxtSubtitle.Text = list.Count == 0
                    ? "Nenhum tweak encontrado com esses filtros."
                    : $"Exibindo {list.Count} de {_allTweaks.Count} tweaks disponíveis";
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentFilter = TxtSearch.Text;
            BtnClearSearch.Visibility = string.IsNullOrEmpty(_currentFilter) ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilter();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Text = "";
        }

        private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCategory.SelectedItem is ComboBoxItem item)
            {
                _currentCategory = item.Content.ToString() ?? "Todas as Categorias";
                ApplyFilter();
            }
        }

        private async void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is TweakItem tweak)
            {
                btn.IsEnabled = false;

                try
                {
                    bool success;
                    if (tweak.IsActive)
                    {
                        success = await System.Threading.Tasks.Task.Run(() => tweak.RevertAction());
                        if (success)
                        {
                            var mw = Application.Current.MainWindow as MainWindow;
                            mw?.ShowSuccess(tweak.Name, $"Tweak revertido: {tweak.Description}");
                        }
                    }
                    else
                    {
                        success = await System.Threading.Tasks.Task.Run(() => tweak.ApplyAction());
                        if (success)
                        {
                            var mw = Application.Current.MainWindow as MainWindow;
                            mw?.ShowSuccess(tweak.Name, $"Tweak aplicado: {tweak.Description}");
                        }
                    }

                    if (success)
                    {
                        tweak.Refresh();
                    }
                    else
                    {
                        var mw = Application.Current.MainWindow as MainWindow;
                        mw?.ShowError(tweak.Name, "Falha ao alterar tweak. Execute como administrador e tente novamente.");
                    }
                }
                catch (Exception ex)
                {
                    var mw = Application.Current.MainWindow as MainWindow;
                    mw?.ShowError(tweak.Name, $"Erro: {ex.Message}");
                }
                finally
                {
                    btn.IsEnabled = true;
                }
            }
        }
    }

    public class TweakItem : INotifyPropertyChanged
    {
        private bool _isActive;

        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public string Source { get; set; } = "";
        public Func<bool> CheckState { get; set; } = () => false;
        public Func<bool> ApplyAction { get; set; } = () => false;
        public Func<bool> RevertAction { get; set; } = () => false;

        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColor)); OnPropertyChanged(nameof(ToggleLabel)); OnPropertyChanged(nameof(ToggleForeground)); }
        }

        public System.Windows.Media.Brush StatusColor => IsActive
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(102, 102, 102));

        public string ToggleLabel => IsActive ? "ON" : "OFF";

        public System.Windows.Media.Brush ToggleForeground => IsActive
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(153, 153, 153));

        public TweakItem() { }

        public TweakItem(string name, string description, string category, string source,
                         Func<bool> check, Func<bool> apply, Func<bool> revert)
        {
            Name = name;
            Description = description;
            Category = category;
            Source = source;
            CheckState = check;
            ApplyAction = apply;
            RevertAction = revert;
            try { _isActive = check(); } catch { _isActive = false; }
        }

        public void Refresh()
        {
            try { IsActive = CheckState(); } catch { }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
