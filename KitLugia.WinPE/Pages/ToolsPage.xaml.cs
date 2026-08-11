using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.WinPE.Pages
{
    public partial class ToolsPage : Page
    {
        public ToolsPage()
        {
            InitializeComponent();
        }

        private async void BtnCleanDisk_Click(object _, RoutedEventArgs e)
        {
            string disk = InputDialog.Show("Clean Disk", "Número do disco:", "0") ?? "";
            if (string.IsNullOrEmpty(disk)) return;

            var r = MessageBox.Show($"Limpar TODO disco {disk}?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            AppendLog($"Limpando disco {disk}...");
            string script = $"select disk {disk}\nclean\nexit";
            await File.WriteAllTextAsync(Path.GetTempPath() + "kl_clean.txt", script);
            await RunCmd($"diskpart /s \"{Path.GetTempPath()}kl_clean.txt\"");
            AppendLog("Disco limpo.");
        }

        private async void BtnListVolumes_Click(object _, RoutedEventArgs e)
        {
            AppendLog("Listando volumes...");
            await RunCmd("diskpart.exe", "list volume");
        }

        private async void BtnSfc_Click(object _, RoutedEventArgs e)
        {
            AppendLog("Executando SFC /SCANNOW...");
            await RunCmd("sfc.exe", "/SCANNOW");
        }

        private async void BtnMountIso_Click(object _, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ISO|*.iso" };
            if (dlg.ShowDialog() == true)
            {
                AppendLog($"Montando ISO: {dlg.FileName}");
                var (ok, msg, _) = await KitLugia.Core.IsoEditorManager.MountIso(dlg.FileName);
                AppendLog(msg);
            }
        }

        private async void BtnDismountIso_Click(object _, RoutedEventArgs e)
        {
            string iso = InputDialog.Show("Desmontar ISO", "Caminho da ISO:") ?? "";
            if (!string.IsNullOrEmpty(iso))
            {
                AppendLog($"Desmontando: {iso}");
                var (ok, msg) = await KitLugia.Core.IsoEditorManager.DismountIso(iso);
                AppendLog(msg);
            }
        }

        private async void BtnWimInfo_Click(object _, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "WIM|*.wim|ESD|*.esd" };
            if (dlg.ShowDialog() == true)
                await RunCmd($"dism.exe", $"/Get-ImageInfo /ImageFile:\"{dlg.FileName}\"");
        }

        private async void BtnManageBcd_Click(object _, RoutedEventArgs e)
        {
            AppendLog("Entradas BCD atuais:");
            await RunCmd("bcdedit.exe", "/enum all");
        }

        private async void BtnRepairBoot_Click(object _, RoutedEventArgs e)
        {
            AppendLog("Executando bootrec /fixboot...");
            await RunCmd("bootrec.exe", "/fixboot");
            AppendLog("Executando bootrec /rebuildbcd...");
            await RunCmd("bootrec.exe", "/rebuildbcd");
        }

        private async void BtnPing_Click(object _, RoutedEventArgs e)
        {
            string host = InputDialog.Show(
                "Teste de Conexão", "Host para ping:", "8.8.8.8") ?? "8.8.8.8";
            await RunCmd("ping.exe", $"{host} -n 4");
        }

        private async void BtnListInterfaces_Click(object _, RoutedEventArgs e)
        {
            await RunCmd("netsh.exe", "interface show interface");
        }

        private async void BtnSetIp_Click(object _, RoutedEventArgs e)
        {
            string iface = InputDialog.Show(
                "Configurar IP", "Nome da interface:", "Ethernet") ?? "";
            string ip = InputDialog.Show(
                "Configurar IP", "IP (ex: 192.168.1.100):", "192.168.1.100") ?? "";
            string mask = InputDialog.Show(
                "Configurar IP", "Máscara:", "255.255.255.0") ?? "";
            string gw = InputDialog.Show(
                "Configurar IP", "Gateway:", "192.168.1.1") ?? "";

            if (!string.IsNullOrEmpty(iface) && !string.IsNullOrEmpty(ip))
            {
                await RunCmd("netsh.exe",
                    $"interface ip set address \"{iface}\" static {ip} {mask} {gw} 1");
            }
        }

        private async void BtnSystemInfo_Click(object _, RoutedEventArgs e)
        {
            AppendLog($"Sistema: {Environment.OSVersion}");
            AppendLog($"Arquitetura: {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
            AppendLog($"Ambiente: {WinPEDetector.GetEnvironment()}");
            AppendLog($"Processadores: {Environment.ProcessorCount}");
            AppendLog($"Diretório: {Environment.CurrentDirectory}");
            AppendLog($"System32: {Environment.SystemDirectory}");
            AppendLog($"Pasta Windows: {Environment.GetFolderPath(Environment.SpecialFolder.Windows)}");
        }

        private async void BtnExportReport_Click(object _, RoutedEventArgs e)
        {
            string report = LogBox.Text;
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"KitLugia_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(path, report);
            AppendLog($"Relatório salvo em: {path}");
        }

        private async Task RunCmd(string cmd, string args = "")
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi)!;
                string output = await proc.StandardOutput.ReadToEndAsync();
                string err = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();

                foreach (var line in (output + err).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AppendLog($"  {line.TrimEnd('\r')}");
                AppendLog($"  → Código: {proc.ExitCode}");
            }
            catch (Exception ex)
            {
                AppendLog($"  Erro: {ex.Message}");
            }
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
                LogBox.ScrollToEnd();
            });
        }
    }
}
