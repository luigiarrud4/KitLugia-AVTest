using System.IO;
using System.Windows;
using System.Windows.Controls;
using KitLugia.WinPE.Pages;

namespace KitLugia.WinPE
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            EnvText.Text = $"Ambiente: {WinPEDetector.GetEnvironment()}";
            CheckWinXShell();
            NavigateTo("Dashboard");
        }

        private void CheckWinXShell()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string wxPath = Path.Combine(baseDir, "WinXShell", "WinXShell.exe");
            if (!File.Exists(wxPath))
            {
                wxPath = Path.Combine(baseDir, "WinXShell", "WinXShell_x64.exe");
            }
            bool ok = File.Exists(wxPath);
            BtnLaunchShell.IsEnabled = ok;
            ShellStatus.Text = ok
                ? "WinXShell: pronto"
                : "WinXShell: não encontrado (baixe em WinXShell/)";
        }

        private void Nav_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            string tag = (btn.Tag as string) ?? "Dashboard";
            NavigateTo(tag);
        }

        public void NavigateTo(string pageName)
        {
            Page? page = pageName switch
            {
                "Dashboard" => new DashboardPage(),
                "FileExplorer" => new FileExplorerPage(),
                "Partitions" => new PartitionsPage(),
                "Shrink" => new ShrinkPage(),
                "InstallWindows" => new InstallWindowsPage(),
                "Tools" => new ToolsPage(),
                _ => new DashboardPage()
            };
            MainFrame.Navigate(page);
        }

        private async void BtnLaunchShell_Click(object sender, RoutedEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string wxPath = Path.Combine(baseDir, "WinXShell", "WinXShell.exe");
            if (!File.Exists(wxPath))
                wxPath = Path.Combine(baseDir, "WinXShell", "WinXShell_x64.exe");
            if (!File.Exists(wxPath))
            {
                MessageBox.Show("WinXShell.exe não encontrado em WinXShell/", "Erro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(wxPath, "-winpe")
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(wxPath)
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao iniciar WinXShell: {ex.Message}", "Erro",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
