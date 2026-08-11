using System.Windows;
using System.Windows.Input;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    public partial class DeepUninstallSettingsWindow : Window
    {
        public DeepUninstallSettingsWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                ChkRecycleBin.IsChecked = KitLugia.Core.DeepUninstallSettings.SendToRecycleBin;
                ChkKillProcesses.IsChecked = KitLugia.Core.DeepUninstallSettings.KillProcessesBeforeUninstall;
                ChkDisableScan.IsChecked = KitLugia.Core.DeepUninstallSettings.DisableScanAfterUninstall;
                ChkSelectLeftovers.IsChecked = KitLugia.Core.DeepUninstallSettings.SelectLeftoversByDefault;
                ChkIgnore24H.IsChecked = KitLugia.Core.DeepUninstallSettings.IgnoreRecent24H;
            };
        }

        private void ChkToggled(object sender, RoutedEventArgs e)
        {
            KitLugia.Core.DeepUninstallSettings.SendToRecycleBin = ChkRecycleBin.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.KillProcessesBeforeUninstall = ChkKillProcesses.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.DisableScanAfterUninstall = ChkDisableScan.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.SelectLeftoversByDefault = ChkSelectLeftovers.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.IgnoreRecent24H = ChkIgnore24H.IsChecked == true;
        }

        private void BtnClose_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}