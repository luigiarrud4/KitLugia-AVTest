using System;
using System.Windows;

namespace KitLugia.GUI.Controls
{
    public partial class StartupArgumentsDialog : Window
    {
        public string? ExecutablePath { get; private set; }
        public string? Arguments { get; private set; }
        public string? FinalResult { get; private set; }

        public StartupArgumentsDialog(string executablePath)
        {
            InitializeComponent();
            ExecutablePath = executablePath;
            TxtExecutablePath.Text = executablePath;
            TxtArguments.TextChanged += TxtArguments_TextChanged;
            UpdateFinalResult();
        }

        private void TxtArguments_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateFinalResult();
        }

        private void UpdateFinalResult()
        {
            string args = TxtArguments.Text.Trim();
            Arguments = string.IsNullOrEmpty(args) ? null : args;
            
            if (string.IsNullOrEmpty(Arguments))
            {
                FinalResult = ExecutablePath;
            }
            else
            {
                FinalResult = $"\"{ExecutablePath}\" {Arguments}";
            }
            
            TxtFinalResult.Text = FinalResult ?? "";
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
