using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Application = System.Windows.Application;
using MainWindow = KitLugia.GUI.MainWindow;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class WindowsPage : Page
    {
        public WindowsPage()
        {
            InitializeComponent();
            this.Unloaded += WindowsPage_Unloaded;
        }

        public void Cleanup()
        {
            this.Unloaded -= WindowsPage_Unloaded;
            this.DataContext = null;
        }

        private void WindowsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void LaunchSettings(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("WindowsPage.LaunchSettings", ex.Message);
            }
        }

        private void ShowMsg(string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("&#x2699;&#xFE0F; WINDOWS", msg);
        }

        // ===== DISPOSITIVOS =====
        private void BtnKeyboard_Click(object sender, RoutedEventArgs e)    => LaunchSettings("ms-settings:easeofaccess-keyboard");
        private void BtnMouse_Click(object sender, RoutedEventArgs e)       => LaunchSettings("ms-settings:mousetouchpad");
        private void BtnSound_Click(object sender, RoutedEventArgs e)       => LaunchSettings("ms-settings:sound");
        private void BtnPrinters_Click(object sender, RoutedEventArgs e)    => LaunchSettings("ms-settings:printers");
        private void BtnBluetooth_Click(object sender, RoutedEventArgs e)   => LaunchSettings("ms-settings:bluetooth");

        // ===== ACESSIBILIDADE =====
        private void BtnAccessibilityDisplay_Click(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:easeofaccess-display");
        private void BtnAccessibilityAudio_Click(object sender, RoutedEventArgs e)   => LaunchSettings("ms-settings:easeofaccess-audio");
        private void BtnAccessibilitySpeech_Click(object sender, RoutedEventArgs e)  => LaunchSettings("ms-settings:easeofaccess-speechrecognition");

        // ===== IDIOMA E REGIAO =====
        private void BtnLanguage_Click(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:regionlanguage");
        private void BtnDateTime_Click(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:dateandtime");

        // ===== PERSONALIZACAO =====
        private void BtnPersonalization_Click(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:personalization-colors");
        private void BtnLockScreen_Click(object sender, RoutedEventArgs e)       => LaunchSettings("ms-settings:lockscreen");
        private void BtnTaskbar_Click(object sender, RoutedEventArgs e)          => LaunchSettings("ms-settings:taskbar");
        private void BtnFonts_Click(object sender, RoutedEventArgs e)            => LaunchSettings("ms-settings:fonts");

        // ===== ENERGIA =====
        private void BtnPowerSleep_Click(object sender, RoutedEventArgs e) => LaunchSettings("ms-settings:powersleep");

        private void BtnForceStopUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.NavigateToPage(PageType.ForceStopUnlock);
        }

        private void BtnPowerPlan_Click(object sender, RoutedEventArgs e)
        {
            if (_isOperation) return;
            _isOperation = true;
            try
            {
                if (!(Application.Current.MainWindow is MainWindow mw)) return;
                mw.ShowInfo("&#x26A1; ENERGIA", "Ativando Bitsum Highest Performance...");

                var result = Toolbox.ImportAndActivateBitsumPlan();
                if (result.Success)
                    mw.ShowSuccess("&#x26A1; ENERGIA", "Plano Bitsum Highest Performance ativado com sucesso!");
                else
                    mw.ShowError("&#x26A1; ERRO", result.Message);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnPowerPlan_Click", ex.Message);
            }
            finally { _isOperation = false; }
        }

        // ===== SISTEMA =====
        private void BtnDisplay_Click(object sender, RoutedEventArgs e)        => LaunchSettings("ms-settings:display");
        private void BtnNotifications_Click(object sender, RoutedEventArgs e)  => LaunchSettings("ms-settings:notifications");
        private void BtnDefaultApps_Click(object sender, RoutedEventArgs e)    => LaunchSettings("ms-settings:defaultapps");
        private void BtnAbout_Click(object sender, RoutedEventArgs e)          => LaunchSettings("ms-settings:about");

        private bool _isOperation;
    }
}