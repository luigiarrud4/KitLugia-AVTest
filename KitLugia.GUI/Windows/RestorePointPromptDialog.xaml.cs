using System.Windows;
using KitLugia.GUI.Pages;

namespace KitLugia.GUI.Windows
{
    public partial class RestorePointPromptDialog : Window
    {
        public bool CreateRestorePoint { get; private set; } = true;
        public bool RememberChoice { get; private set; }

        public RestorePointPromptDialog(string appName)
        {
            InitializeComponent();
            AppNameText.Text = $"Programa: {appName}";

            // Load saved preference if "remember" was set before
            var settings = AppSettingsHelper.Load();
            if (settings.RememberRestorePointChoice)
            {
                CreateRestorePoint = settings.CreateRestorePointBeforeUninstall;
                RememberChoice = true;
                DialogResult = true;
                Close();
            }
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            CreateRestorePoint = ChkCreateRestorePoint.IsChecked == true;
            RememberChoice = ChkRememberChoice.IsChecked == true;

            if (RememberChoice)
            {
                var settings = AppSettingsHelper.Load();
                settings.CreateRestorePointBeforeUninstall = CreateRestorePoint;
                settings.RememberRestorePointChoice = true;
                AppSettingsHelper.Save(settings);
            }

            DialogResult = true;
            Close();
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            CreateRestorePoint = false;
            RememberChoice = ChkRememberChoice.IsChecked == true;

            if (RememberChoice)
            {
                var settings = AppSettingsHelper.Load();
                settings.CreateRestorePointBeforeUninstall = false;
                settings.RememberRestorePointChoice = true;
                AppSettingsHelper.Save(settings);
            }

            DialogResult = false;
            Close();
        }
    }
}
