using KitLugia.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Pages
{
    public partial class HopsShrinkPage : Page
    {
        private List<DiskInfoEx> _disks = new();
        private PartitionInfoEx? SelectedPartition => CmbPartition.SelectedItem as PartitionInfoEx;

        public HopsShrinkPage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await LoadDisksAsync();
        }

        private async Task LoadDisksAsync()
        {
            try
            {
                TxtStatus.Text = "Lendo discos...";
                _disks = await Task.Run(() => PartitionManager.GetAllDisks()) ?? new List<DiskInfoEx>();
                CmbDisk.ItemsSource = _disks;
                CmbDisk.SelectedIndex = _disks.Count > 0 ? 0 : -1;
                TxtStatus.Text = $"{_disks.Count} disco(s)";
            }
            catch (Exception ex) { WriteOutput($"Falha ao enumerar discos: {ex.Message}"); }
        }

        private void CmbDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDisk.SelectedItem is not DiskInfoEx disk)
            {
                CmbPartition.ItemsSource = null;
                return;
            }
            var parts = disk.Partitions?.Where(p => !p.IsUnallocated).ToList() ?? new List<PartitionInfoEx>();
            CmbPartition.ItemsSource = parts;
            CmbPartition.SelectedIndex = parts.FindIndex(p =>
                string.Equals(p.DriveLetter, "C", StringComparison.OrdinalIgnoreCase));
            if (CmbPartition.SelectedIndex < 0 && parts.Count > 0) CmbPartition.SelectedIndex = 0;
        }

        private void CmbPartition_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var p = SelectedPartition;
            if (p == null)
            {
                TxtTarget.Text = "Selecione uma partição NTFS";
                return;
            }
            string letter = string.IsNullOrWhiteSpace(p.DriveLetter) ? "(sem letra)" : p.DriveLetter.TrimEnd(':') + ":";
            TxtTarget.Text = $"Disco {p.DiskIndex}, partição {p.Index} | {letter} | {p.FileSystem} | " +
                $"{p.Size / 1024d / 1024 / 1024:F2} GB | livre {p.FreeSpace / 1024d / 1024 / 1024:F2} GB";
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDisksAsync();

        private char? GetDrive()
        {
            var p = SelectedPartition;
            if (p == null || string.IsNullOrWhiteSpace(p.DriveLetter) ||
                !string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Selecione uma partição NTFS com letra de unidade.", "HOPS Shrink", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            return char.ToUpperInvariant(p.DriveLetter.TrimEnd(':')[0]);
        }

        private bool TryGetShrinkMb(out long mb)
        {
            if (!long.TryParse(TxtShrinkMb.Text, out mb) || mb < 256)
            {
                MessageBox.Show("Informe uma redução de pelo menos 256 MB.", "HOPS Shrink", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private async void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            SetBusy("Analisando evento 259 e limite online...");
            try
            {
                var report = await ShrinkDiagnostics.AnalyzeAsync(drive.Value);
                WriteOutput(report.ToText());
                TxtLimit.Text = $"Limite online: {report.MaxShrinkMb:N0} MB";
            }
            catch (Exception ex) { WriteOutput($"Diagnóstico falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void OnlineShrink_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null || !TryGetShrinkMb(out long mb)) return;
            var p = SelectedPartition!;
            var (maxMb, fits) = await OnlineShrinkManager.AnalyzeAsync(drive.Value, mb);
            TxtLimit.Text = $"Limite online: {maxMb:N0} MB";
            if (!fits)
            {
                WriteOutput($"O Windows permite {maxMb:N0} MB neste momento; pedido: {mb:N0} MB. Use a cascata HOPS ou o método offline.");
                return;
            }
            var confirm = MessageBox.Show($"Reduzir {drive}: em {mb:N0} MB online?", "Confirmar shrink", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            SetBusy("Executando shrink online...");
            try
            {
                long newSize = checked((long)p.Size - checked(mb * 1024L * 1024L));
                bool ok = await PartitionManager.ShrinkPartitionUsingStorageAPI(drive.Value, newSize);
                WriteOutput(ok ? $"Shrink online concluído em {drive}: ({mb:N0} MB)." : "Shrink online falhou; veja o log do Kit.");
                if (ok) await LoadDisksAsync();
            }
            catch (Exception ex) { WriteOutput($"Shrink online falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void Cascade_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            var confirm = MessageBox.Show("A cascata pode desligar hibernação, apagar pontos VSS e o journal USN, além de executar defrag. VSS é irreversível. Pagefile pode exigir reboot. Use somente em VM com snapshot. Continuar?",
                "Confirmar cascata HOPS", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            SetBusy("Executando cascata HOPS...");
            try
            {
                var result = await DeepShrink.RunAsync(drive.Value, true, new Progress<string>(WriteOutput));
                WriteOutput(result.ToText());
            }
            catch (Exception ex) { WriteOutput($"Cascata falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void Defrag_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            SetBusy("Executando defrag /X com retries HOPS...");
            try
            {
                var (ok, before) = await DeepShrink.TryGetMaxShrinkMbAsync(drive.Value);
                var result = await DeepShrink.InvokeDefragAsync(drive.Value, before, new Progress<string>(WriteOutput));
                WriteOutput($"{result}\nConsulta inicial: {(ok ? "OK" : "falhou")}; limite: {before:N0} MB.");
            }
            catch (Exception ex) { WriteOutput($"Defrag falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void MoveMft_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            var confirm = MessageBox.Show("FSCTL_MOVE_FILE move clusters do $Mft::$BITMAP com o Windows ativo. Uma falha pode deixar o volume inacessível. Faça snapshot da VM antes de continuar.",
                "Risco de metadados NTFS", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (confirm != MessageBoxResult.Yes) return;
            SetBusy("Movendo clusters do $Mft::$BITMAP...");
            try
            {
                long moved = await Task.Run(() => NtfsMover.MoveDown(drive.Value.ToString(), "$Mft::$BITMAP", 0.5, 900));
                WriteOutput($"Clusters movidos: {moved:N0}\n{NtfsMover.LastFailure}");
            }
            catch (Exception ex) { WriteOutput($"NtfsMover falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void Offline_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null || !TryGetShrinkMb(out long mb)) return;
            var p = SelectedPartition!;
            var confirm = MessageBox.Show("O ntfsresize offline executará no pré-boot. Snapshot da VM é recomendado. Preparar agora?",
                "Confirmar método offline", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            SetBusy("Preparando pré-boot ntfsresize...");
            try
            {
                var result = await EmergencyPreBootManager.DeployAsync(checked((int)p.DiskIndex), checked((int)p.Index),
                    checked((long)p.Size), drive.Value + ":", mb, "KL_INSTALL",
                    (pct, text) => Dispatcher.Invoke(() => WriteOutput($"{pct:F0}% {text}")));
                WriteOutput(result.Message);
            }
            catch (Exception ex) { WriteOutput($"Preparação offline falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async void HibernateOff_Click(object sender, RoutedEventArgs e) => await RunToolAsync("powercfg.exe", "/h off", "Desligar hibernação");
        private async void HibernateOn_Click(object sender, RoutedEventArgs e) => await RunToolAsync("powercfg.exe", "/h on", "Ligar hibernação");

        private async void DeleteVss_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            var confirm = MessageBox.Show($"Apagar TODOS os pontos de restauração/snapshots VSS do volume {drive}:? Esta ação não pode ser desfeita.",
                "Remoção irreversível de VSS", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (confirm == MessageBoxResult.Yes)
                await RunToolAsync("vssadmin.exe", $"delete shadows /for={drive}: /all /quiet", "Remover sombras VSS");
        }

        private async void DeleteJournal_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            var confirm = MessageBox.Show($"Remover o journal USN de {drive}:? O Windows poderá recriá-lo; indexadores e ferramentas de backup podem precisar reconstruir o índice.",
                "Remover journal USN", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.Yes)
                await RunToolAsync("fsutil.exe", $"usn deletejournal /d {drive}:", "Remover journal USN");
        }

        private async void CreateJournal_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null) return;
            await RunToolAsync("fsutil.exe", $"usn createjournal m=0x2000000 a=0x800000 {drive}:", "Criar journal USN");
        }

        private async void PreparePagefile_Click(object sender, RoutedEventArgs e)
        {
            var drive = GetDrive(); if (drive == null || !TryGetShrinkMb(out long mb)) return;
            var confirm = MessageBox.Show("O fluxo profundo salva o estado de recuperação, ajusta pagefile/obstáculos e pede reinicialização para continuar. Use snapshot da VM. Preparar?",
                "Preparar pagefile para reboot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            SetBusy("Preparando estado de pagefile/reboot...");
            try
            {
                var (ok, message) = await OnlineShrinkManager.PrepareDeepShrinkAsync(drive.Value, mb);
                WriteOutput(ok ? message : $"Preparação falhou: {message}");
            }
            catch (Exception ex) { WriteOutput($"Preparação falhou: {ex}"); }
            finally { SetBusy("Pronto"); }
        }

        private async Task RunToolAsync(string executable, string arguments, string label)
        {
            SetBusy(label + "...");
            try
            {
                var psi = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                if (process == null) throw new InvalidOperationException("Não foi possível iniciar " + executable);
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                string output = (await stdout) + Environment.NewLine + (await stderr);
                WriteOutput($"{label}: exit code {process.ExitCode}\n{output.Trim()}");
            }
            catch (Exception ex) { WriteOutput($"{label} falhou: {ex.Message}"); }
            finally { SetBusy("Pronto"); }
        }

        private void SetBusy(string text) => TxtStatus.Text = text;
        private void WriteOutput(string text)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(TxtOutput.Text)) TxtOutput.AppendText(Environment.NewLine + Environment.NewLine);
                TxtOutput.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}");
                TxtOutput.ScrollToEnd();
            });
        }
    }
}
