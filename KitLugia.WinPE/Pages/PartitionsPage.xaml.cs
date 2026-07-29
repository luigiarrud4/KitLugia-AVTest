using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.WinPE.Pages
{
    public partial class PartitionsPage : Page
    {
        private readonly ObservableCollection<string> _volumes = new();

        public PartitionsPage()
        {
            InitializeComponent();
            VolumeList.ItemsSource = _volumes;
            Loaded += (_, _) => BtnRefresh_Click(null!, null!);
        }

        private async void BtnRefresh_Click(object _, RoutedEventArgs e)
        {
            _volumes.Clear();
            LogBox.Text = "";

            AppendLog("Executando diskpart list volume...");
            var (code, output) = await RunDiskpartAsync("list volume");
            AppendLog($"Código: {code}");

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _volumes.Add(line.TrimEnd('\r'));
        }

        private async void BtnCreatePartition_Click(object _, RoutedEventArgs e)
        {
            string disk = InputDialog.Show(
                "Criar Partição", "Número do disco:", "0") ?? "";
            string size = InputDialog.Show(
                "Criar Partição", "Tamanho em MB (deixe vazio para todo espaço livre):", "") ?? "";
            if (string.IsNullOrEmpty(disk)) return;

            string cmd = $"select disk {disk}\ncreate partition primary"
                + (string.IsNullOrEmpty(size) ? "" : $" size={size}")
                + "\nassign";
            AppendLog($"Executando: {cmd.Replace("\n", " | ")}");
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private async void BtnDeletePartition_Click(object _, RoutedEventArgs e)
        {
            string disk = InputDialog.Show(
                "Deletar Partição", "Número do disco:", "0") ?? "";
            string part = InputDialog.Show(
                "Deletar Partição", "Número da partição:", "1") ?? "";
            if (string.IsNullOrEmpty(disk) || string.IsNullOrEmpty(part)) return;

            var result = MessageBox.Show($"Deletar partição {part} do disco {disk}?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            string cmd = $"select disk {disk}\nselect partition {part}\ndelete partition override";
            AppendLog($"Executando: {cmd.Replace("\n", " | ")}");
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private async void BtnFormat_Click(object _, RoutedEventArgs e)
        {
            string letter = InputDialog.Show(
                "Formatar Volume", "Letra do volume (ex: D):", "D") ?? "";
            string fs = InputDialog.Show(
                "Formatar Volume", "Sistema de arquivos (NTFS/FAT32/exFAT):", "NTFS") ?? "";
            if (string.IsNullOrEmpty(letter) || string.IsNullOrEmpty(fs)) return;

            string label = InputDialog.Show(
                "Formatar Volume", "Rótulo (opcional):", "") ?? "";

            string cmd = $"select volume {letter}\nformat fs={fs} quick"
                + (string.IsNullOrEmpty(label) ? "" : $" label=\"{label}\"")
                + "\nassign";
            AppendLog($"Executando format {letter}:{fs}...");
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private async void BtnAssignLetter_Click(object _, RoutedEventArgs e)
        {
            string part = InputDialog.Show(
                "Atribuir Letra", "Número do volume:", "1") ?? "";
            string letter = InputDialog.Show(
                "Atribuir Letra", "Letra (ex: Z):", "Z") ?? "";
            if (string.IsNullOrEmpty(part) || string.IsNullOrEmpty(letter)) return;

            string cmd = $"select volume {part}\nassign letter={letter}";
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private async void BtnRemoveLetter_Click(object _, RoutedEventArgs e)
        {
            string letter = InputDialog.Show(
                "Remover Letra", "Letra para remover (ex: Z):", "Z") ?? "";
            if (string.IsNullOrEmpty(letter)) return;

            string cmd = $"select volume {letter}\nremove";
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private async void BtnCheckDisk_Click(object _, RoutedEventArgs e)
        {
            string letter = InputDialog.Show(
                "Verificar Erros", "Letra do volume (ex: C):", "C") ?? "";
            if (string.IsNullOrEmpty(letter)) return;

            AppendLog($"Executando chkdsk {letter}:...");
            try
            {
                var psi = new ProcessStartInfo("chkdsk.exe", $"{letter}: /f")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi)!;
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                AppendLog($"Código: {proc.ExitCode}\n{output}");
            }
            catch (Exception ex) { AppendLog($"Erro: {ex.Message}"); }
        }

        private async void BtnCleanDisk_Click(object _, RoutedEventArgs e)
        {
            string disk = InputDialog.Show(
                "Clean Disk", "NÚMERO DO DISCO PARA LIMPAR COMPLETAMENTE:", "0") ?? "";
            if (string.IsNullOrEmpty(disk)) return;

            var result = MessageBox.Show(
                $"Você tem CERTEZA que quer limpar TODO o disco {disk}?\n\n" +
                "TODOS OS DADOS SERÃO PERDIDOS!",
                "⚠️ PERIGO", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            string cmd = $"select disk {disk}\nclean";
            AppendLog($"Executando CLEAN no disco {disk}...");
            var (code, output) = await RunDiskpartAsync(cmd);
            AppendLog($"Código: {code}\n{output}");
            BtnRefresh_Click(null!, null!);
        }

        private void BtnCopyLog_Click(object _, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(LogBox.Text);
                AppendLog("Log copiado para a área de transferência.");
            }
            catch { }
        }

        private void AppendLog(string text)
        {
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
            LogBox.ScrollToEnd();
        }

        private static async Task<(int Code, string Output)> RunDiskpartAsync(string commands)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "kl_diskpart.txt");
            try
            {
                await File.WriteAllTextAsync(scriptPath, commands + "\nexit");
                var psi = new ProcessStartInfo("diskpart.exe", $"/s \"{scriptPath}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                var proc = Process.Start(psi)!;
                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                return (proc.ExitCode, output);
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
    }
}
