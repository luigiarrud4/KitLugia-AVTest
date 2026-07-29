using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.WinPE.Pages
{
    public partial class DashboardPage : Page
    {
        public DashboardPage()
        {
            InitializeComponent();
            Loaded += (_, _) => RefreshStatus();
        }

        private void RefreshStatus()
        {
            string env = WinPEDetector.GetEnvironment();
            EnvValue.Text = env;
            BootMode.Text = Environment.Is64BitOperatingSystem ? "x64" : "x86";

            bool isValOs = WinPEDetector.IsValOS();
            ValOsBanner.Visibility = isValOs ? Visibility.Visible : Visibility.Collapsed;

            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();
            DriveCount.Text = drives.Count.ToString();
            DriveDetails.Text = string.Join(", ",
                drives.Select(d => $"{d.Name.TrimEnd('\\')} [{NativeMethods.GetDriveLabel(d.Name)}]"));

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string wx = Path.Combine(baseDir, "WinXShell", "WinXShell.exe");
            if (!File.Exists(wx))
                wx = Path.Combine(baseDir, "WinXShell", "WinXShell_x64.exe");
            bool shellOk = File.Exists(wx);
            ShellValue.Text = shellOk ? "✅ Pronto" : "❄️ Ausente";
            ShellHint.Text = shellOk
                ? "Clique em 'Iniciar WinXShell' na barra lateral"
                : "Coloque WinXShell.exe em WinXShell/";
        }

        private void OpenExplorer_Click(object _, RoutedEventArgs e)
        {
            ((MainWindow)Window.GetWindow(this)).NavigateTo("FileExplorer");
        }

        private void OpenPartitions_Click(object _, RoutedEventArgs e)
        {
            ((MainWindow)Window.GetWindow(this)).NavigateTo("Partitions");
        }

        private void OpenShrink_Click(object _, RoutedEventArgs e)
        {
            ((MainWindow)Window.GetWindow(this)).NavigateTo("Shrink");
        }

        private void OpenInstall_Click(object _, RoutedEventArgs e)
        {
            ((MainWindow)Window.GetWindow(this)).NavigateTo("InstallWindows");
        }
    }
}
