using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.GUI.Helpers;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Pages
{
    public partial class ShrinkPage : Page
    {
        public class PartitionInfo
        {
            public uint DiskIndex { get; set; }
            public uint Index { get; set; }
            public long Size { get; set; }
            public string DriveLetter { get; set; } = "";
            public string DisplayText => $"{DriveLetter}:  ({Size / 1024 / 1024 / 1024} GB)  Disk {DiskIndex} Partição {Index}";
        }

        private List<PartitionInfo> _partitions = new();
        private bool _isBusy;

        public ShrinkPage()
        {
            InitializeComponent();
            Loaded += ShrinkPage_Loaded;
        }

        private async void ShrinkPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadPartitionsAsync();
        }

        private async Task LoadPartitionsAsync()
        {
            try
            {
                AppendLog("Carregando partições...");
                ComboPartitions.Items.Clear();
                _partitions.Clear();

                var list = await Task.Run(() =>
                {
                    var result = new List<PartitionInfo>();

                    // Mapeia partição -> letra via Win32_LogicalDiskToPartition
                    var assocMap = new Dictionary<(uint disk, uint part), string>();
                    using (var assocQuery = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
                    {
                        foreach (var assoc in assocQuery.Get())
                        {
                            var antec = assoc["Antecedent"]?.ToString() ?? "";
                            var dep = assoc["Dependent"]?.ToString() ?? "";
                            // Antecedent: Win32_DiskPartition.DeviceID="Disk #0, Partition #0"
                            // Dependent: Win32_LogicalDisk.DeviceID="C:"
                            if (string.IsNullOrEmpty(antec) || string.IsNullOrEmpty(dep)) continue;

                            var partMatch = System.Text.RegularExpressions.Regex.Match(antec, @"Disk\s+#(\d+),\s+Partition\s+#(\d+)");
                            var driveMatch = System.Text.RegularExpressions.Regex.Match(dep, @"DeviceID=""([A-Za-z]):""");
                            if (partMatch.Success && driveMatch.Success)
                            {
                                var key = (uint.Parse(partMatch.Groups[1].Value), uint.Parse(partMatch.Groups[2].Value));
                                assocMap.TryAdd(key, driveMatch.Groups[1].Value);
                            }
                        }
                    }

                    using var ps = new System.Management.ManagementObjectSearcher(
                        "SELECT DeviceID, DiskIndex, Index, Size, Type FROM Win32_DiskPartition"
                    );
                    foreach (var obj in ps.Get())
                    {
                        var type = obj["Type"]?.ToString() ?? "";
                        if (type.Equals("Extended", StringComparison.OrdinalIgnoreCase)) continue;

                        var diskIdx = Convert.ToUInt32(obj["DiskIndex"]);
                        var partIdx = Convert.ToUInt32(obj["Index"]);
                        var dl = assocMap.GetValueOrDefault((diskIdx, partIdx), "");

                        result.Add(new PartitionInfo
                        {
                            DiskIndex = diskIdx,
                            Index = partIdx,
                            Size = Convert.ToInt64(obj["Size"]),
                            DriveLetter = dl
                        });
                    }
                    return result;
                });

                _partitions = list.OrderBy(p => p.DriveLetter).ToList();
                foreach (var p in _partitions)
                    ComboPartitions.Items.Add(p);

                if (_partitions.Count > 0)
                    ComboPartitions.SelectedIndex = 0;

                AppendLog($"{_partitions.Count} partições encontradas.");
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO ao carregar partições: {ex.Message}");
            }
        }

        private void ChkEmergencyPreBoot_Checked(object sender, RoutedEventArgs e)
        {
            if (TxtStatus != null)
                TxtStatus.Text = "Modo Emergency: o sistema será reiniciado para o Alpine Linux.";
        }

        private void ChkEmergencyPreBoot_Unchecked(object sender, RoutedEventArgs e)
        {
            if (TxtStatus != null)
                TxtStatus.Text = "Pronto.";
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                var selected = ComboPartitions.SelectedItem as PartitionInfo;
                if (selected == null)
                {
                    AppendLog("ERRO: Selecione uma partição.");
                    return;
                }

                if (!int.TryParse(TxtShrinkSize.Text, out int shrinkMb) || shrinkMb < 1024)
                {
                    AppendLog("ERRO: Informe um tamanho válido (mínimo 1024 MB).");
                    return;
                }

                AppendLog($"=== INICIANDO SHRINK ===");
                AppendLog($"Partição: {selected.DisplayText}");
                AppendLog($"Shrink: {shrinkMb} MB");
                AppendLog($"Modo: {(ChkEmergencyPreBoot.IsChecked == true ? "Emergency Pre-Boot (Alpine + rEFInd)" : "UEFI Recovery (kitlugia_shrink.efi)")}");

                var result = MessageBox.Show(
                    "⚠️ SHRINK DE PARTIÇÃO\n\n" +
                    $"O KitLugia vai:\n" +
                    $"1. Reduzir {selected.DriveLetter}: em {shrinkMb}MB\n" +
                    $"2. Deploy do ambiente de recuperação no ESP\n" +
                    (ChkEmergencyPreBoot.IsChecked == true
                        ? "3. REINICIAR para Alpine Linux\n4. Executar shrink + criar partição KITLUGIA\n5. Reiniciar de volta para Windows"
                        : "3. REINICIAR para kitlugia_shrink.efi\n4. Executar shrink + criar partição KITLUGIA\n5. Reiniciar de volta para Windows") +
                    "\n\nDeseja continuar?",
                    "Shrink Partição",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    AppendLog("Operação cancelada pelo usuário.");
                    return;
                }

                OverlayBusy.Visibility = Visibility.Visible;
                TxtProgressStatus.Text = "Implantando ambiente de recuperação...";

                if (ChkEmergencyPreBoot.IsChecked == true)
                {
                    var (ok, msg) = await EmergencyPreBootManager.DeployAsync(
                        (int)selected.DiskIndex,
                        (int)selected.Index,
                        selected.Size,
                        selected.DriveLetter,
                        shrinkMb,
                        "KITLUGIA",
                        UpdateProgress
                    );

                    if (!ok)
                    {
                        AppendLog($"ERRO: {msg}");
                        MessageBox.Show($"Falha: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    AppendLog("Ambiente Alpine implantado com sucesso!");
                    AppendLog(msg);
                    TxtStatus.Text = "✅ Ambiente pronto. Reiniciando...";

                    MessageBox.Show(msg + "\n\nO sistema será reiniciado AGORA.",
                        "Shrink - KitLugia", MessageBoxButton.OK, MessageBoxImage.Information);

                    await EmergencyPreBootManager.TriggerReboot();
                }
                else
                {
                    var (ok, msg) = await EmergencyUEFIManager.DeployAsync(
                        (int)selected.DiskIndex,
                        (int)selected.Index,
                        selected.Size,
                        selected.DriveLetter,
                        shrinkMb,
                        "KITLUGIA",
                        UpdateProgress
                    );

                    if (!ok)
                    {
                        AppendLog($"ERRO: {msg}");
                        MessageBox.Show($"Falha: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    AppendLog("Ambiente UEFI implantado com sucesso!");
                    AppendLog(msg);
                    TxtStatus.Text = "✅ Ambiente pronto. Reiniciando...";

                    MessageBox.Show(msg + "\n\nO sistema será reiniciado AGORA.",
                        "Shrink - KitLugia", MessageBoxButton.OK, MessageBoxImage.Information);

                    await EmergencyUEFIManager.TriggerReboot();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"FATAL: {ex.Message}");
                TxtStatus.Text = "❌ Erro. Veja o log.";
                MessageBox.Show($"Erro: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                OverlayBusy.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateProgress(double pct, string label)
        {
            Dispatcher.Invoke(() =>
            {
                TxtProgressStatus.Text = label;
                AppendLog($"[{pct:F0}%] {label}");
            });
        }

        private void AppendLog(string line)
        {
            Dispatcher.Invoke(() =>
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.AppendText($"[{ts}] {line}\n");
                if (LogScroll != null)
                    LogScroll.ScrollToEnd();
                Core.Logger.Log($"[SHRINK] {line}");
            });
        }
    }
}
