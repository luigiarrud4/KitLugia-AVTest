using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.WinPE.Pages
{
    public partial class ShrinkPage : Page
    {
        public ShrinkPage()
        {
            InitializeComponent();
            Loaded += (_, _) => LoadDrives();
        }

        private void LoadDrives()
        {
            DriveCombo.Items.Clear();
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                string label = NativeMethods.GetDriveLabel(d.Name);
                DriveCombo.Items.Add($"{d.Name.TrimEnd('\\')} [{label}]");
            }
            if (DriveCombo.Items.Count > 0)
                DriveCombo.SelectedIndex = 0;
        }

        private void BtnAnalyze_Click(object _, RoutedEventArgs e)
        {
            if (DriveCombo.SelectedItem == null) return;
            string drive = (DriveCombo.SelectedItem as string)?.Split(' ')[0] ?? "C:";
            if (!drive.EndsWith("\\")) drive += "\\";

            try
            {
                ulong total = NativeMethods.GetDriveTotalSize(drive);
                ulong free = NativeMethods.GetDriveFreeSpace(drive);
                CurrentSize.Text = FormatBytes(total);
                FreeSpace.Text = FormatBytes(free);
                AppendLog($"Unidade {drive}: {FormatBytes(total)} total, {FormatBytes(free)} livre");
            }
            catch (Exception ex)
            {
                AppendLog($"Erro ao analisar: {ex.Message}");
            }
        }

        private async void BtnScheduleShrink_Click(object _, RoutedEventArgs e)
        {
            if (DriveCombo.SelectedItem == null)
            {
                MessageBox.Show("Selecione uma unidade primeiro.");
                return;
            }

            string drive = (DriveCombo.SelectedItem as string)?.Split(' ')[0] ?? "C:";
            if (!int.TryParse(ShrinkMbInput.Text, out int shrinkMb) || shrinkMb < 100)
            {
                MessageBox.Show("Insira um valor válido para shrink (mín. 100 MB).");
                return;
            }

            AppendLog($"Preparando shrink de {shrinkMb} MB na unidade {drive}...");
            AppendLog("Criando entrada BCD para boot no WinPE...");

            try
            {
                var result = await KitLugia.Core.WinbootManager.ScheduleWinpeShrink(drive, shrinkMb);
                if (result.ok)
                {
                    AppendLog($"✅ Shrink agendado com sucesso!");
                    AppendLog($"Mensagem: {result.msg}");
                    AppendLog("Reinicie o PC para iniciar o shrink no WinPE.");
                }
                else
                {
                    AppendLog($"❌ Falha: {result.msg}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Erro: {ex.Message}");
            }
        }

        private void AppendLog(string text) 
        {
            LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {text}\n";
            LogBox.ScrollToEnd();
        }

        private static string FormatBytes(ulong bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024UL * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024UL * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
