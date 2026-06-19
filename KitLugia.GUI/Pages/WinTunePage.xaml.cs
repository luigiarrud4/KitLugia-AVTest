using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using System.Threading.Tasks;

// Resolve ambiguidade de MessageBox
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Pages
{
    public partial class WinTunePage : Page
    {
        private bool _isWinTuneOperation;

        public WinTunePage()
        {
            InitializeComponent();
            this.Unloaded += WinTunePage_Unloaded;
            this.Loaded += WinTunePage_Loaded;
        }

        private async void WinTunePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isWinTuneOperation) return;
            _isWinTuneOperation = true;
            try
            {
                await LoadCurrentStatus();
            }
            catch (Exception ex)
            {
                Logger.LogError("WinTunePage_Loaded", ex.Message);
            }
            finally
            {
                _isWinTuneOperation = false;
            }
        }

        // === HANDLERS DE TOGGLE (funcionam na hora) ===

        private void ChkDisableGameBar_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableGameBar.IsChecked == true)
                SystemTweaks.DisableGameBar();
            else
                SystemTweaks.EnableGameBar();
        }

        private void ChkAutoEndTasks_Click(object sender, RoutedEventArgs e)
        {
            if (ChkAutoEndTasks.IsChecked == true)
                SystemTweaks.EnableAutoEndTasks();
            else
                SystemTweaks.DisableAutoEndTasks();
        }

        private void ChkDisableAeDebug_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAeDebug.IsChecked == true)
                SystemTweaks.DisableAeDebug();
            else
                SystemTweaks.EnableAeDebug();
        }

        private void ChkDisableAnimationEffectMaxMin_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAnimationEffectMaxMin.IsChecked == true)
                SystemTweaks.DisableAnimationEffectMaxMin();
            else
                SystemTweaks.EnableAnimationEffectMaxMin();
        }

        private void ChkDisableAutoDefragIdle_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAutoDefragIdle.IsChecked == true)
                SystemTweaks.DisableAutoDefragIdle();
            else
                SystemTweaks.EnableAutoDefragIdle();
        }

        private void ChkDisableBackgroundApps_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableBackgroundApps.IsChecked == true)
                SystemTweaks.DisableBackgroundApps();
            else
                SystemTweaks.EnableBackgroundApps();
        }

        private void ChkDisableBootOptimize_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableBootOptimize.IsChecked == true)
                SystemTweaks.DisableBootOptimize();
            else
                SystemTweaks.EnableBootOptimize();
        }

        private void ChkDisableCustomInking_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableCustomInking.IsChecked == true)
                SystemTweaks.DisableCustomInking();
            else
                SystemTweaks.EnableCustomInking();
        }

        private void ChkDisableCrashAutoReboot_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableCrashAutoReboot.IsChecked == true)
                SystemTweaks.DisableCrashAutoReboot();
            else
                SystemTweaks.EnableCrashAutoReboot();
        }

        private void ChkDisableErrorReporting_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableErrorReporting.IsChecked == true)
                SystemTweaks.DisableErrorReporting();
            else
                SystemTweaks.EnableErrorReporting();
        }

        private void ChkDisableGoogleUpdateTask_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableGoogleUpdateTask.IsChecked == true)
                SystemTweaks.DisableGoogleUpdateTask();
            else
                SystemTweaks.EnableGoogleUpdateTask();
        }

        private void ChkDisableLockScreen_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableLockScreen.IsChecked == true)
                SystemTweaks.DisableLockScreen();
            else
                SystemTweaks.EnableLockScreen();
        }

        private void ChkDisableLowDiskSpaceChecks_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableLowDiskSpaceChecks.IsChecked == true)
                SystemTweaks.DisableLowDiskSpaceChecks();
            else
                SystemTweaks.EnableLowDiskSpaceChecks();
        }

        private void ChkDisableMemoryPagination_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableMemoryPagination.IsChecked == true)
                SystemTweaks.DisableMemoryPagination();
            else
                SystemTweaks.EnableMemoryPagination();
        }

        private void ChkDisableMenuShowDelay_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableMenuShowDelay.IsChecked == true)
                SystemTweaks.DisableMenuShowDelay();
            else
                SystemTweaks.EnableMenuShowDelay();
        }

        private void ChkDisableMicrosoftEdgeUpdateTask_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableMicrosoftEdgeUpdateTask.IsChecked == true)
                SystemTweaks.DisableMicrosoftEdgeUpdateTask();
            else
                SystemTweaks.EnableMicrosoftEdgeUpdateTask();
        }

        private void ChkDisablePrefetchParameters_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisablePrefetchParameters.IsChecked == true)
                SystemTweaks.DisablePrefetchParameters();
            else
                SystemTweaks.EnablePrefetchParameters();
        }

        private void ChkDisableScheduledDefrag_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableScheduledDefrag.IsChecked == true)
                SystemTweaks.DisableScheduledDefrag();
            else
                SystemTweaks.EnableScheduledDefrag();
        }

        private void ChkDisableShortcutText_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableShortcutText.IsChecked == true)
                SystemTweaks.DisableShortcutText();
            else
                SystemTweaks.EnableShortcutText();
        }

        private void ChkIoPageLockLimit_Click(object sender, RoutedEventArgs e)
        {
            if (ChkIoPageLockLimit.IsChecked == true)
                SystemTweaks.EnableIoPageLockLimit();
            else
                SystemTweaks.DisableIoPageLockLimit();
        }

        private void ChkLinkResolveIgnoreLinkInfo_Click(object sender, RoutedEventArgs e)
        {
            if (ChkLinkResolveIgnoreLinkInfo.IsChecked == true)
                SystemTweaks.EnableLinkResolveIgnoreLinkInfo();
            else
                SystemTweaks.DisableLinkResolveIgnoreLinkInfo();
        }

        private void ChkMouseHoverTime_Click(object sender, RoutedEventArgs e)
        {
            if (ChkMouseHoverTime.IsChecked == true)
                SystemTweaks.EnableMouseHoverTime();
            else
                SystemTweaks.DisableMouseHoverTime();
        }

        private void ChkNoInternetOpenWith_Click(object sender, RoutedEventArgs e)
        {
            if (ChkNoInternetOpenWith.IsChecked == true)
                SystemTweaks.EnableNoInternetOpenWith();
            else
                SystemTweaks.DisableNoInternetOpenWith();
        }

        private void ChkNoResolveSearch_Click(object sender, RoutedEventArgs e)
        {
            if (ChkNoResolveSearch.IsChecked == true)
                SystemTweaks.EnableNoResolveSearch();
            else
                SystemTweaks.DisableNoResolveSearch();
        }

        private void ChkNoResolveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (ChkNoResolveTrack.IsChecked == true)
                SystemTweaks.EnableNoResolveTrack();
            else
                SystemTweaks.DisableNoResolveTrack();
        }

        private void ChkNumLockonStartup_Click(object sender, RoutedEventArgs e)
        {
            if (ChkNumLockonStartup.IsChecked == true)
                SystemTweaks.EnableNumLockonStartup();
            else
                SystemTweaks.DisableNumLockonStartup();
        }

        private void ChkOptimizeNetworkTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (ChkOptimizeNetworkTransfer.IsChecked == true)
                SystemTweaks.EnableOptimizeNetworkTransfer();
            else
                SystemTweaks.DisableOptimizeNetworkTransfer();
        }

        private void ChkOptimizeProcessorPerformance_Click(object sender, RoutedEventArgs e)
        {
            if (ChkOptimizeProcessorPerformance.IsChecked == true)
                SystemTweaks.EnableOptimizeProcessorPerformance();
            else
                SystemTweaks.DisableOptimizeProcessorPerformance();
        }

        private void ChkOptimizeRefreshPolicy_Click(object sender, RoutedEventArgs e)
        {
            if (ChkOptimizeRefreshPolicy.IsChecked == true)
                SystemTweaks.EnableOptimizeRefreshPolicy();
            else
                SystemTweaks.DisableOptimizeRefreshPolicy();
        }

        private void ChkShutdownAcceleration_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShutdownAcceleration.IsChecked == true)
                SystemTweaks.EnableShutdownAcceleration();
            else
                SystemTweaks.DisableShutdownAcceleration();
        }

        private void ChkSnippingPrintScreen_Click(object sender, RoutedEventArgs e)
        {
            if (ChkSnippingPrintScreen.IsChecked == true)
                SystemTweaks.EnableSnippingPrintScreen();
            else
                SystemTweaks.DisableSnippingPrintScreen();
        }

        // PRIVACY
        private void ChkDisableWebSearch_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableWebSearch.IsChecked == true)
                SystemTweaks.DisableWebSearch();
            else
                SystemTweaks.EnableWebSearch();
        }

        private void ChkDisableMSACloudSearch_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableMSACloudSearch.IsChecked == true)
                SystemTweaks.DisableMSACloudSearch();
            else
                SystemTweaks.EnableMSACloudSearch();
        }

        private void ChkDisableAADCloudSearch_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAADCloudSearch.IsChecked == true)
                SystemTweaks.DisableAADCloudSearch();
            else
                SystemTweaks.EnableAADCloudSearch();
        }

        private void ChkDisableDeviceSearchHistory_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableDeviceSearchHistory.IsChecked == true)
                SystemTweaks.DisableDeviceSearchHistory();
            else
                SystemTweaks.EnableDeviceSearchHistory();
        }

        private void ChkDisableDiagTrack_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableDiagTrack.IsChecked == true)
                SystemTweaks.DisableDiagTrack();
            else
                SystemTweaks.EnableDiagTrack();
        }

        private void ChkDiagnosticDataOff_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDiagnosticDataOff.IsChecked == true)
                SystemTweaks.DiagnosticDataOff();
            else
                SystemTweaks.DiagnosticDataOn();
        }

        private void ChkDisableAdsOnLockScreen_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAdsOnLockScreen.IsChecked == true)
                SystemTweaks.DisableAdsOnLockScreen();
            else
                SystemTweaks.EnableAdsOnLockScreen();
        }

        private void ChkDisableAutoInstallationApps_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAutoInstallationApps.IsChecked == true)
                SystemTweaks.DisableAutoInstallationApps();
            else
                SystemTweaks.EnableAutoInstallationApps();
        }

        private void ChkDisableAutoplay_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAutoplay.IsChecked == true)
                SystemTweaks.DisableAutoplay();
            else
                SystemTweaks.EnableAutoplay();
        }

        private void ChkDisableVBSCodeIntegrity_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableVBSCodeIntegrity.IsChecked == true)
                SystemTweaks.DisableVBSCodeIntegrity();
            else
                SystemTweaks.EnableVBSCodeIntegrity();
        }

        private void ChkDisableOfferSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableOfferSuggestions.IsChecked == true)
                SystemTweaks.DisableOfferSuggestions();
            else
                SystemTweaks.EnableOfferSuggestions();
        }

        private void ChkDisablePersonalizedAdsStoreApps_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisablePersonalizedAdsStoreApps.IsChecked == true)
                SystemTweaks.DisablePersonalizedAdsStoreApps();
            else
                SystemTweaks.EnablePersonalizedAdsStoreApps();
        }

        private void ChkDisableRemoteRegAccess_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableRemoteRegAccess.IsChecked == true)
                SystemTweaks.DisableRemoteRegAccess();
            else
                SystemTweaks.EnableRemoteRegAccess();
        }

        private void ChkDisableSettingsAppSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableSettingsAppSuggestions.IsChecked == true)
                SystemTweaks.DisableSettingsAppSuggestions();
            else
                SystemTweaks.EnableSettingsAppSuggestions();
        }

        private void ChkDisableTailoredExperiences_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableTailoredExperiences.IsChecked == true)
                SystemTweaks.DisableTailoredExperiences();
            else
                SystemTweaks.EnableTailoredExperiences();
        }

        private void ChkDisableTipsAndSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableTipsAndSuggestions.IsChecked == true)
                SystemTweaks.DisableTipsAndSuggestions();
            else
                SystemTweaks.EnableTipsAndSuggestions();
        }

        private void ChkDisableWCE_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableWCE.IsChecked == true)
                SystemTweaks.DisableWCE();
            else
                SystemTweaks.EnableWCE();
        }

        private void ChkDisableVisualStudioTelemetry_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableVisualStudioTelemetry.IsChecked == true)
                SystemTweaks.DisableVisualStudioTelemetry();
            else
                SystemTweaks.EnableVisualStudioTelemetry();
        }

        private void ChkDisableWindowsFeedback_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableWindowsFeedback.IsChecked == true)
                SystemTweaks.DisableWindowsFeedback();
            else
                SystemTweaks.EnableWindowsFeedback();
        }

        // EXPLORER
        private void ChkDisableAutoSuggest_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAutoSuggest.IsChecked == true)
                SystemTweaks.DisableAutoSuggest();
            else
                SystemTweaks.EnableAutoSuggest();
        }

        private void ChkDisableAppendCompletion_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAppendCompletion.IsChecked == true)
                SystemTweaks.DisableAppendCompletion();
            else
                SystemTweaks.EnableAppendCompletion();
        }

        private void ChkShowExtensions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShowExtensions.IsChecked == true)
                SystemTweaks.ShowExtensions();
            else
                SystemTweaks.HideExtensions();
        }

        private void ChkShowHidden_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShowHidden.IsChecked == true)
                SystemTweaks.ShowHidden();
            else
                SystemTweaks.HideHidden();
        }

        private void ChkShowHiddenSystem_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShowHiddenSystem.IsChecked == true)
                SystemTweaks.ShowHiddenSystem();
            else
                SystemTweaks.HideHiddenSystem();
        }

        private void ChkShowThisPC_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShowThisPC.IsChecked == true)
                SystemTweaks.ShowThisPC();
            else
                SystemTweaks.HideThisPC();
        }

        private void ChkOpenFileExplorerThisPC_Click(object sender, RoutedEventArgs e)
        {
            if (ChkOpenFileExplorerThisPC.IsChecked == true)
                SystemTweaks.OpenFileExplorerThisPC();
            else
                SystemTweaks.DisableOpenFileExplorerThisPC();
        }

        private void ChkIncreaseIconCache_Click(object sender, RoutedEventArgs e)
        {
            if (ChkIncreaseIconCache.IsChecked == true)
                SystemTweaks.IncreaseIconCache();
            else
                SystemTweaks.ResetIconCache();
        }

        private void ChkDisableRecentFiles_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableRecentFiles.IsChecked == true)
                SystemTweaks.DisableRecentFiles();
            else
                SystemTweaks.EnableRecentFiles();
        }

        private void ChkDisableFrequentFolders_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableFrequentFolders.IsChecked == true)
                SystemTweaks.DisableFrequentFolders();
            else
                SystemTweaks.EnableFrequentFolders();
        }

        private void ChkDisableSyncProviderNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableSyncProviderNotifications.IsChecked == true)
                SystemTweaks.DisableSyncProviderNotifications();
            else
                SystemTweaks.EnableSyncProviderNotifications();
        }

        // START MENU
        private void ChkDisableStartMenuAppSuggestions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableStartMenuAppSuggestions.IsChecked == true)
                SystemTweaks.DisableStartMenuAppSuggestions();
            else
                SystemTweaks.EnableStartMenuAppSuggestions();
        }

        private void ChkHideMostUsedApps_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideMostUsedApps.IsChecked == true)
                SystemTweaks.HideMostUsedApps();
            else
                SystemTweaks.ShowMostUsedApps();
        }

        private void ChkHideStartMenuRecentlyAdded_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideStartMenuRecentlyAdded.IsChecked == true)
                SystemTweaks.HideStartMenuRecentlyAdded();
            else
                SystemTweaks.ShowStartMenuRecentlyAdded();
        }

        private void ChkHideStartMenuRecentlyOpened_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideStartMenuRecentlyOpened.IsChecked == true)
                SystemTweaks.HideStartMenuRecentlyOpened();
            else
                SystemTweaks.ShowStartMenuRecentlyOpened();
        }

        private void ChkHideStartMenuAccountNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideStartMenuAccountNotifications.IsChecked == true)
                SystemTweaks.HideStartMenuAccountNotifications();
            else
                SystemTweaks.ShowStartMenuAccountNotifications();
        }

        private void ChkHideStartMenuRecommendations_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideStartMenuRecommendations.IsChecked == true)
                SystemTweaks.HideStartMenuRecommendations();
            else
                SystemTweaks.ShowStartMenuRecommendations();
        }

        // OPTIONAL
        private void ChkEnableDarkMode_Click(object sender, RoutedEventArgs e)
        {
            if (ChkEnableDarkMode.IsChecked == true)
                SystemTweaks.EnableDarkMode();
            else
                SystemTweaks.DisableDarkMode();
        }

        private void ChkClassicContextMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ChkClassicContextMenu.IsChecked == true)
                SystemTweaks.EnableClassicContextMenu();
            else
                SystemTweaks.DisableClassicContextMenu();
        }

        private void ChkAUOptions_Click(object sender, RoutedEventArgs e)
        {
            if (ChkAUOptions.IsChecked == true)
                SystemTweaks.EnableAUOptions();
            else
                SystemTweaks.DisableAUOptions();
        }

        private void ChkDisableAutoWindowsUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableAutoWindowsUpdates.IsChecked == true)
                SystemTweaks.DisableAutoWindowsUpdates();
            else
                SystemTweaks.EnableAutoWindowsUpdates();
        }

        private void ChkDisableHibernate_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableHibernate.IsChecked == true)
                SystemTweaks.DisableHibernate();
            else
                SystemTweaks.EnableHibernate();
        }

        private void ChkDisableHybridSleep_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableHybridSleep.IsChecked == true)
                SystemTweaks.DisableHybridSleep();
            else
                SystemTweaks.EnableHybridSleep();
        }

        private void ChkDisablePrintSpooler_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisablePrintSpooler.IsChecked == true)
                SystemTweaks.DisablePrintSpooler();
            else
                SystemTweaks.EnablePrintSpooler();
        }

        private void ChkDisableSleep_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableSleep.IsChecked == true)
                SystemTweaks.DisableSleep();
            else
                SystemTweaks.EnableSleep();
        }

        private void ChkDisableSystemRestore_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableSystemRestore.IsChecked == true)
                SystemTweaks.DisableSystemRestore();
            else
                SystemTweaks.EnableSystemRestore();
        }

        private void ChkDisableTurnOffDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableTurnOffDisplay.IsChecked == true)
                SystemTweaks.DisableTurnOffDisplay();
            else
                SystemTweaks.EnableTurnOffDisplay();
        }

        private void ChkHideWindowsSecurityNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideWindowsSecurityNotifications.IsChecked == true)
                SystemTweaks.HideWindowsSecurityNotifications();
            else
                SystemTweaks.ShowWindowsSecurityNotifications();
        }

        private void ChkHideWindowsSecurityNoncriticalNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (ChkHideWindowsSecurityNoncriticalNotifications.IsChecked == true)
                SystemTweaks.HideWindowsSecurityNoncriticalNotifications();
            else
                SystemTweaks.ShowWindowsSecurityNoncriticalNotifications();
        }

        private void ChkDisableWindowsSearch_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableWindowsSearch.IsChecked == true)
                SystemTweaks.DisableWindowsSearch();
            else
                SystemTweaks.EnableWindowsSearch();
        }

        private void ChkUninstallOneDrive_Click(object sender, RoutedEventArgs e)
        {
            if (ChkUninstallOneDrive.IsChecked == true)
            {
                var result = MessageBox.Show("⚠️ Isso vai desinstalar o OneDrive completamente. Continuar?", "Aviso", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    SystemTweaks.UninstallOneDrive();
                }
                else
                {
                    ChkUninstallOneDrive.IsChecked = false;
                }
            }
        }

        private void ChkDisableMSDefender_Click(object sender, RoutedEventArgs e)
        {
            if (ChkDisableMSDefender.IsChecked == true)
            {
                var result = MessageBox.Show("⚠️ Isso vai desativar o Microsoft Defender. O sistema ficará vulnerável. Continuar?", "Aviso CRÍTICO", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                {
                    SystemTweaks.DisableMSDefender();
                }
                else
                {
                    ChkDisableMSDefender.IsChecked = false;
                }
            }
            else
            {
                SystemTweaks.EnableMSDefender();
            }
        }

        private async Task LoadCurrentStatus()
        {
            KitLugia.Core.Logger.Log("🔍 WinTune: Carregando estado atual das otimizações...");
            
            // SYSTEM
            if (ChkDisableGameBar != null) ChkDisableGameBar.IsChecked = SystemTweaks.IsGameBarDisabled();
            if (ChkAutoEndTasks != null) ChkAutoEndTasks.IsChecked = SystemTweaks.IsAutoEndTasksEnabled();
            if (ChkDisableAeDebug != null) ChkDisableAeDebug.IsChecked = SystemTweaks.IsAeDebugDisabled();
            if (ChkDisableAnimationEffectMaxMin != null) ChkDisableAnimationEffectMaxMin.IsChecked = SystemTweaks.IsAnimationEffectMaxMinDisabled();
            if (ChkDisableAutoDefragIdle != null) ChkDisableAutoDefragIdle.IsChecked = SystemTweaks.IsAutoDefragIdleDisabled();
            if (ChkDisableBackgroundApps != null) ChkDisableBackgroundApps.IsChecked = SystemTweaks.IsBackgroundAppsDisabled();
            if (ChkDisableBootOptimize != null) ChkDisableBootOptimize.IsChecked = SystemTweaks.IsBootOptimizeDisabled();
            if (ChkDisableCustomInking != null) ChkDisableCustomInking.IsChecked = SystemTweaks.IsCustomInkingDisabled();
            if (ChkDisableCrashAutoReboot != null) ChkDisableCrashAutoReboot.IsChecked = SystemTweaks.IsCrashAutoRebootDisabled();
            if (ChkDisableErrorReporting != null) ChkDisableErrorReporting.IsChecked = SystemTweaks.IsErrorReportingDisabled();
            if (ChkDisableGoogleUpdateTask != null) ChkDisableGoogleUpdateTask.IsChecked = SystemTweaks.IsGoogleUpdateTaskDisabled();
            if (ChkDisableLockScreen != null) ChkDisableLockScreen.IsChecked = SystemTweaks.IsLockScreenDisabled();
            if (ChkDisableLowDiskSpaceChecks != null) ChkDisableLowDiskSpaceChecks.IsChecked = SystemTweaks.IsLowDiskSpaceChecksDisabled();
            if (ChkDisableMemoryPagination != null) ChkDisableMemoryPagination.IsChecked = SystemTweaks.IsMemoryPaginationDisabled();
            if (ChkDisableMenuShowDelay != null) ChkDisableMenuShowDelay.IsChecked = SystemTweaks.IsMenuShowDelayDisabled();
            if (ChkDisableMicrosoftEdgeUpdateTask != null) ChkDisableMicrosoftEdgeUpdateTask.IsChecked = SystemTweaks.IsMicrosoftEdgeUpdateTaskDisabled();
            if (ChkDisablePrefetchParameters != null) ChkDisablePrefetchParameters.IsChecked = SystemTweaks.IsPrefetchParametersDisabled();
            if (ChkDisableScheduledDefrag != null) ChkDisableScheduledDefrag.IsChecked = SystemTweaks.IsScheduledDefragDisabled();
            if (ChkDisableShortcutText != null) ChkDisableShortcutText.IsChecked = SystemTweaks.IsShortcutTextDisabled();
            if (ChkIoPageLockLimit != null) ChkIoPageLockLimit.IsChecked = SystemTweaks.IsIoPageLockLimitEnabled();
            if (ChkLinkResolveIgnoreLinkInfo != null) ChkLinkResolveIgnoreLinkInfo.IsChecked = SystemTweaks.IsLinkResolveIgnoreLinkInfoEnabled();
            if (ChkMouseHoverTime != null) ChkMouseHoverTime.IsChecked = SystemTweaks.IsMouseHoverTimeEnabled();
            if (ChkNoInternetOpenWith != null) ChkNoInternetOpenWith.IsChecked = SystemTweaks.IsNoInternetOpenWithEnabled();
            if (ChkNoResolveSearch != null) ChkNoResolveSearch.IsChecked = SystemTweaks.IsNoResolveSearchEnabled();
            if (ChkNoResolveTrack != null) ChkNoResolveTrack.IsChecked = SystemTweaks.IsNoResolveTrackEnabled();
            if (ChkNumLockonStartup != null) ChkNumLockonStartup.IsChecked = SystemTweaks.IsNumLockonStartupEnabled();
            if (ChkOptimizeNetworkTransfer != null) ChkOptimizeNetworkTransfer.IsChecked = SystemTweaks.IsNetworkTransferOptimized();
            if (ChkOptimizeProcessorPerformance != null) ChkOptimizeProcessorPerformance.IsChecked = SystemTweaks.IsProcessorPerformanceOptimized();
            if (ChkOptimizeRefreshPolicy != null) ChkOptimizeRefreshPolicy.IsChecked = SystemTweaks.IsRefreshPolicyOptimized();
            if (ChkShutdownAcceleration != null) ChkShutdownAcceleration.IsChecked = SystemTweaks.IsShutdownAccelerationEnabled();
            if (ChkSnippingPrintScreen != null) ChkSnippingPrintScreen.IsChecked = SystemTweaks.IsSnippingPrintScreenEnabled();

            // PRIVACY
            if (ChkDisableWebSearch != null) ChkDisableWebSearch.IsChecked = SystemTweaks.IsWebSearchDisabled();
            if (ChkDisableMSACloudSearch != null) ChkDisableMSACloudSearch.IsChecked = SystemTweaks.IsMSACloudSearchDisabled();
            if (ChkDisableAADCloudSearch != null) ChkDisableAADCloudSearch.IsChecked = SystemTweaks.IsAADCloudSearchDisabled();
            if (ChkDisableDeviceSearchHistory != null) ChkDisableDeviceSearchHistory.IsChecked = SystemTweaks.IsDeviceSearchHistoryDisabled();
            if (ChkDisableDiagTrack != null) ChkDisableDiagTrack.IsChecked = SystemTweaks.IsDiagTrackDisabled();
            if (ChkDiagnosticDataOff != null) ChkDiagnosticDataOff.IsChecked = SystemTweaks.IsDiagnosticDataOff();
            if (ChkDisableAdsOnLockScreen != null) ChkDisableAdsOnLockScreen.IsChecked = SystemTweaks.IsAdsOnLockScreenDisabled();
            if (ChkDisableAutoInstallationApps != null) ChkDisableAutoInstallationApps.IsChecked = SystemTweaks.IsAutoInstallationAppsDisabled();
            if (ChkDisableAutoplay != null) ChkDisableAutoplay.IsChecked = SystemTweaks.IsAutoplayDisabled();
            if (ChkDisableVBSCodeIntegrity != null) ChkDisableVBSCodeIntegrity.IsChecked = SystemTweaks.IsVBSCodeIntegrityDisabled();
            if (ChkDisableOfferSuggestions != null) ChkDisableOfferSuggestions.IsChecked = SystemTweaks.IsOfferSuggestionsDisabled();
            if (ChkDisablePersonalizedAdsStoreApps != null) ChkDisablePersonalizedAdsStoreApps.IsChecked = SystemTweaks.IsPersonalizedAdsStoreAppsDisabled();
            if (ChkDisableRemoteRegAccess != null) ChkDisableRemoteRegAccess.IsChecked = SystemTweaks.IsRemoteRegAccessDisabled();
            if (ChkDisableSettingsAppSuggestions != null) ChkDisableSettingsAppSuggestions.IsChecked = SystemTweaks.IsSettingsAppSuggestionsDisabled();
            if (ChkDisableTailoredExperiences != null) ChkDisableTailoredExperiences.IsChecked = SystemTweaks.IsTailoredExperiencesDisabled();
            if (ChkDisableTipsAndSuggestions != null) ChkDisableTipsAndSuggestions.IsChecked = SystemTweaks.IsTipsAndSuggestionsDisabled();
            if (ChkDisableWCE != null) ChkDisableWCE.IsChecked = SystemTweaks.IsWCEDisabled();
            if (ChkDisableVisualStudioTelemetry != null) ChkDisableVisualStudioTelemetry.IsChecked = SystemTweaks.IsVisualStudioTelemetryDisabled();
            if (ChkDisableWindowsFeedback != null) ChkDisableWindowsFeedback.IsChecked = SystemTweaks.IsWindowsFeedbackDisabled();

            // EXPLORER
            if (ChkDisableAutoSuggest != null) ChkDisableAutoSuggest.IsChecked = SystemTweaks.IsAutoSuggestDisabled();
            if (ChkDisableAppendCompletion != null) ChkDisableAppendCompletion.IsChecked = SystemTweaks.IsAppendCompletionDisabled();
            if (ChkShowExtensions != null) ChkShowExtensions.IsChecked = SystemTweaks.IsExtensionsShown();
            if (ChkShowHidden != null) ChkShowHidden.IsChecked = SystemTweaks.IsHiddenShown();
            if (ChkShowHiddenSystem != null) ChkShowHiddenSystem.IsChecked = SystemTweaks.IsHiddenSystemShown();
            if (ChkShowThisPC != null) ChkShowThisPC.IsChecked = SystemTweaks.IsThisPCShown();
            if (ChkOpenFileExplorerThisPC != null) ChkOpenFileExplorerThisPC.IsChecked = SystemTweaks.IsFileExplorerThisPCEnabled();
            if (ChkIncreaseIconCache != null) ChkIncreaseIconCache.IsChecked = SystemTweaks.IsIconCacheIncreased();
            if (ChkDisableRecentFiles != null) ChkDisableRecentFiles.IsChecked = SystemTweaks.IsRecentFilesDisabled();
            if (ChkDisableFrequentFolders != null) ChkDisableFrequentFolders.IsChecked = SystemTweaks.IsFrequentFoldersDisabled();
            if (ChkDisableSyncProviderNotifications != null) ChkDisableSyncProviderNotifications.IsChecked = SystemTweaks.IsSyncProviderNotificationsDisabled();

            // START MENU
            if (ChkDisableStartMenuAppSuggestions != null) ChkDisableStartMenuAppSuggestions.IsChecked = SystemTweaks.IsStartMenuAppSuggestionsDisabled();
            if (ChkHideMostUsedApps != null) ChkHideMostUsedApps.IsChecked = SystemTweaks.IsMostUsedAppsHidden();
            if (ChkHideStartMenuRecentlyAdded != null) ChkHideStartMenuRecentlyAdded.IsChecked = SystemTweaks.IsStartMenuRecentlyAddedHidden();
            if (ChkHideStartMenuRecentlyOpened != null) ChkHideStartMenuRecentlyOpened.IsChecked = SystemTweaks.IsStartMenuRecentlyOpenedHidden();
            if (ChkHideStartMenuAccountNotifications != null) ChkHideStartMenuAccountNotifications.IsChecked = SystemTweaks.IsStartMenuAccountNotificationsHidden();
            if (ChkHideStartMenuRecommendations != null) ChkHideStartMenuRecommendations.IsChecked = SystemTweaks.IsStartMenuRecommendationsHidden();

            // OPTIONAL
            if (ChkEnableDarkMode != null) ChkEnableDarkMode.IsChecked = SystemTweaks.IsDarkModeEnabled();
            if (ChkClassicContextMenu != null) ChkClassicContextMenu.IsChecked = SystemTweaks.IsClassicContextMenuEnabled();
            if (ChkAUOptions != null) ChkAUOptions.IsChecked = SystemTweaks.IsAUOptionsEnabled();
            if (ChkDisableAutoWindowsUpdates != null) ChkDisableAutoWindowsUpdates.IsChecked = SystemTweaks.IsAutoWindowsUpdatesDisabled();
            if (ChkDisableHibernate != null) ChkDisableHibernate.IsChecked = SystemTweaks.IsHibernateDisabled();
            if (ChkDisableHybridSleep != null) ChkDisableHybridSleep.IsChecked = SystemTweaks.IsHybridSleepDisabled();
            if (ChkDisablePrintSpooler != null) ChkDisablePrintSpooler.IsChecked = SystemTweaks.IsPrintSpoolerDisabled();
            if (ChkDisableSleep != null) ChkDisableSleep.IsChecked = SystemTweaks.IsSleepDisabled();
            if (ChkDisableSystemRestore != null) ChkDisableSystemRestore.IsChecked = SystemTweaks.IsSystemRestoreDisabled();
            if (ChkDisableTurnOffDisplay != null) ChkDisableTurnOffDisplay.IsChecked = SystemTweaks.IsTurnOffDisplayDisabled();
            if (ChkHideWindowsSecurityNotifications != null) ChkHideWindowsSecurityNotifications.IsChecked = SystemTweaks.IsWindowsSecurityNotificationsHidden();
            if (ChkHideWindowsSecurityNoncriticalNotifications != null) ChkHideWindowsSecurityNoncriticalNotifications.IsChecked = SystemTweaks.IsWindowsSecurityNoncriticalNotificationsHidden();
            if (ChkDisableWindowsSearch != null) ChkDisableWindowsSearch.IsChecked = SystemTweaks.IsWindowsSearchDisabled();
            if (ChkUninstallOneDrive != null) ChkUninstallOneDrive.IsChecked = SystemTweaks.IsOneDriveUninstalled();
            if (ChkDisableMSDefender != null) ChkDisableMSDefender.IsChecked = SystemTweaks.IsMSDefenderDisabled();

            KitLugia.Core.Logger.Log("✅ WinTune: Estado carregado com sucesso");
            await Task.CompletedTask;
        }

        public void Cleanup()
        {
            this.Unloaded -= WinTunePage_Unloaded;
            this.Loaded -= WinTunePage_Loaded;
            this.DataContext = null;
        }

        private void WinTunePage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn)
            {
                string tooltipText = "";
                if (btn.ToolTip is System.Windows.Controls.ToolTip tip && tip.Content is System.Windows.FrameworkElement fe)
                {
                    var texts = new System.Collections.Generic.List<string>();
                    ExtractTextBlockText(fe, texts);
                    tooltipText = string.Join("\n", texts);
                }

                if (!string.IsNullOrEmpty(tooltipText))
                {
                    var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        _ = mainWindow.ShowConfirmationDialog(tooltipText);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(tooltipText, "Informa\u00E7\u00E3o");
                    }
                }
            }
        }

        private static void ExtractTextBlockText(DependencyObject parent, System.Collections.Generic.List<string> texts)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                    texts.Add(tb.Text);
                else
                    ExtractTextBlockText(child, texts);
            }
        }

        private void BtnRevertWinTune_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Desmarcar todos os checkboxes
                foreach (var child in WinTuneTabControl.Items)
                {
                    if (child is TabItem tabItem)
                    {
                        var content = tabItem.Content;
                        if (content is ScrollViewer scrollViewer)
                        {
                            if (scrollViewer.Content is StackPanel stackPanel)
                            {
                                foreach (var item in stackPanel.Children)
                                {
                                    if (item is System.Windows.Controls.CheckBox checkBox)
                                    {
                                        checkBox.IsChecked = false;
                                    }
                                }
                            }
                        }
                    }
                }

                MessageBox.Show("✅ Todas as seleções foram revertidas.", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Erro ao reverter seleções: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // === SELEÇÃO RÁPIDA POR ABA ===

        private void SelectAllInTab(int tabIndex, bool select)
        {
            if (WinTuneTabControl.Items[tabIndex] is TabItem tabItem)
            {
                var content = tabItem.Content;
                if (content is StackPanel stackPanel)
                {
                    // Pular os botões de seleção rápida (primeiros elementos)
                    int startIndex = 0;
                    foreach (var item in stackPanel.Children)
                    {
                        if (item is StackPanel buttonPanel && buttonPanel.Orientation == System.Windows.Controls.Orientation.Horizontal)
                        {
                            startIndex++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    for (int i = startIndex; i < stackPanel.Children.Count; i++)
                    {
                        if (stackPanel.Children[i] is System.Windows.Controls.CheckBox checkBox)
                        {
                            checkBox.IsChecked = select;
                        }
                    }
                }
            }
        }

    }
}
