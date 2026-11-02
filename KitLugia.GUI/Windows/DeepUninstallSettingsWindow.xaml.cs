using System.Windows;
using System.Windows.Input;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    public partial class DeepUninstallSettingsWindow : Window
    {
        private bool _loading;

        public DeepUninstallSettingsWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                _loading = true;
                try
                {
                    ChkRecycleBin.IsChecked = KitLugia.Core.DeepUninstallSettings.SendToRecycleBin;
                    ChkKillProcesses.IsChecked = KitLugia.Core.DeepUninstallSettings.KillProcessesBeforeUninstall;
                    ChkDisableScan.IsChecked = KitLugia.Core.DeepUninstallSettings.DisableScanAfterUninstall;
                    ChkSelectLeftovers.IsChecked = KitLugia.Core.DeepUninstallSettings.SelectLeftoversByDefault;
                    ChkIgnore24H.IsChecked = KitLugia.Core.DeepUninstallSettings.IgnoreRecent24H;
                    ChkAutoCleanConfirmed.IsChecked = KitLugia.Core.DeepUninstallSettings.AutoCleanConfirmed;
                }
                finally { _loading = false; }
            };
        }

        private void ChkToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            SaveAll();
        }

        private void SaveAll()
        {
            KitLugia.Core.DeepUninstallSettings.SendToRecycleBin = ChkRecycleBin.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.KillProcessesBeforeUninstall = ChkKillProcesses.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.DisableScanAfterUninstall = ChkDisableScan.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.SelectLeftoversByDefault = ChkSelectLeftovers.IsChecked == true;
            KitLugia.Core.DeepUninstallSettings.IgnoreRecent24H = ChkIgnore24H.IsChecked == true;
            // Antes so era possivel mexer nisto pelo registro: a opcao existia no
            // Core e nao tinha controle nenhum na janela.
            KitLugia.Core.DeepUninstallSettings.AutoCleanConfirmed = ChkAutoCleanConfirmed.IsChecked == true;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            SaveAll();
            System.Windows.MessageBox.Show(this, "Configurações aplicadas e salvas.", "Aplicar",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}