using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KitLugia.WinPE.Pages
{
    public partial class InstallWindowsPage : Page
    {
        public InstallWindowsPage()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadDrives();
        }

        private void LoadDrives()
        {
            TargetDriveCombo.Items.Clear();
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                string label = NativeMethods.GetDriveLabel(d.Name);
                TargetDriveCombo.Items.Add($"{d.Name.TrimEnd('\\')} [{label}]");
            }
            if (TargetDriveCombo.Items.Count > 0)
                TargetDriveCombo.SelectedIndex = 0;
        }

        private void BtnBrowseIso_Click(object _, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Arquivos ISO|*.iso|Arquivos WIM|*.wim|Todos|*.*",
                Title = "Selecione a ISO do Windows"
            };
            if (dlg.ShowDialog() == true)
                IsoPathBox.Text = dlg.FileName;
        }

        private void BtnRefreshDrives_Click(object _, RoutedEventArgs e) => LoadDrives();

        private async void BtnInstall_Click(object _, RoutedEventArgs e)
        {
            string isoPath = IsoPathBox.Text.Trim();
            if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath))
            {
                MessageBox.Show("Selecione uma ISO ou WIM válida.");
                return;
            }

            if (TargetDriveCombo.SelectedItem == null)
            {
                MessageBox.Show("Selecione um destino.");
                return;
            }

            string target = (TargetDriveCombo.SelectedItem as string)?.Split(' ')[0] ?? "C:";
            if (!target.EndsWith("\\")) target += "\\";

            var result = MessageBox.Show(
                $"Instalar Windows de:\n{isoPath}\n\nEm: {target}\n\n" +
                (ChkFormat.IsChecked == true ? "⚠️ A partição será formatada!\n" : "") +
                "Continuar?", "Confirmar Instalação",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            await Task.Run(() => InstallWindows(isoPath, target));
        }

        private async Task InstallWindows(string isoPath, string targetDrive)
        {
            try
            {
                string mountDir = Path.Combine(Path.GetTempPath(), "KL_WINPE_MOUNT_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(mountDir);

                AppendLog("Extraindo ISO...");
                await RunProcessAsync("7z.exe", $"x \"{isoPath}\" -o\"{mountDir}\" -y");

                string? wimPath = Directory.GetFiles(mountDir, "install.wim", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (wimPath == null)
                {
                    wimPath = Directory.GetFiles(mountDir, "install.esd", SearchOption.AllDirectories)
                        .FirstOrDefault();
                }

                if (wimPath == null)
                {
                    AppendLog("❌ install.wim não encontrado na ISO.");
                    return;
                }

                AppendLog($"WIM encontrado: {wimPath}");

                if (ChkFormat.IsChecked == true)
                {
                    AppendLog($"Formatando {targetDrive}...");
                    string formatScript = $"select volume {targetDrive.TrimEnd('\\')}\nformat fs=ntfs quick\nassign\nexit";
                    string scriptPath = Path.Combine(Path.GetTempPath(), "kl_format.txt");
                    await File.WriteAllTextAsync(scriptPath, formatScript);
                    await RunProcessAsync("diskpart.exe", $"/s \"{scriptPath}\"");
                    try { File.Delete(scriptPath); } catch { }
                }

                AppendLog("Aplicando imagem via DISM...");
                await RunProcessAsync("dism.exe",
                    $"/Apply-Image /ImageFile:\"{wimPath}\" /Index:1 /ApplyDir:\"{targetDrive}\"");

                AppendLog("Configurando boot (bcdboot)...");
                await RunProcessAsync("bcdboot.exe",
                    $"{targetDrive}Windows /s {targetDrive} /f ALL");

                if (ChkBypass.IsChecked == true)
                {
                    AppendLog("Aplicando bypass Win11...");
                    string bypassDir = Path.Combine(targetDrive, "Windows", "Setup", "Scripts");
                    Directory.CreateDirectory(bypassDir);
                    string bypassScript = Path.Combine(bypassDir, "SetupComplete.cmd");
                    await File.WriteAllTextAsync(bypassScript,
                        "reg add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassTPMCheck /t REG_DWORD /d 1 /f\n" +
                        "reg add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassRAMCheck /t REG_DWORD /d 1 /f\n" +
                        "reg add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassSecureBootCheck /t REG_DWORD /d 1 /f\n" +
                        "reg add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassStorageCheck /t REG_DWORD /d 1 /f\n" +
                        "reg add \"HKLM\\SYSTEM\\Setup\\LabConfig\" /v BypassCPUCheck /t REG_DWORD /d 1 /f\n");
                }

                if (ChkLocalAccount.IsChecked == true)
                {
                    AppendLog("Configurando conta local...");
                    string setupDir = Path.Combine(targetDrive, "Windows", "Setup", "Scripts");
                    Directory.CreateDirectory(setupDir);
                    string oobeScript = Path.Combine(setupDir, "OobeLocalOnly.cmd");
                    await File.WriteAllTextAsync(oobeScript,
                        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f\n");
                }

                AppendLog("✅ Instalação concluída com sucesso!");
                AppendLog("Reinicie o PC para iniciar a configuração do Windows.");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Erro: {ex.Message}");
            }
        }

        private async Task RunProcessAsync(string filename, string args)
        {
            AppendLog($"  > {filename} {args}");
            var psi = new ProcessStartInfo(filename, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi)!;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
                AppendLog($"  ⚠️ Código {proc.ExitCode}");
        }

        private void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
                LogBox.ScrollToEnd();
                ProgressText.Text = text;
            });
        }
    }
}
