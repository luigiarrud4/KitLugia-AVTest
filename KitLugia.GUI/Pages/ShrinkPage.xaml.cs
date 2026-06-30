using System;
using System.Collections.Generic;
using System.IO;
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

        public void Cleanup()
        {
            this.Loaded -= ShrinkPage_Loaded;
            this.DataContext = null;
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

                    var assocMap = new Dictionary<(uint disk, uint part), string>();
                    using (var assocQuery = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
                    {
                        foreach (var assoc in assocQuery.Get())
                        {
                            var antec = assoc["Antecedent"]?.ToString() ?? "";
                            var dep = assoc["Dependent"]?.ToString() ?? "";
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

                AppendLog($"=== INICIANDO SHRINK (antiX) ===");
                AppendLog($"Partição fonte: {selected.DriveLetter}:");
                AppendLog($"Tamanho do shrink: {shrinkMb} MB");
                AppendLog($"ISO antiX: antiX-26_x64-core.iso (645MB)");

                var result = MessageBox.Show(
                    "⚠️ SHRINK DE PARTIÇÃO (antiX Live)\n\n" +
                    $"O KitLugia vai:\n" +
                    $"1. Reduzir {selected.DriveLetter}: em {shrinkMb}MB\n" +
                    $"2. Criar partição KITLUGIA no espaço liberado\n" +
                    $"3. Extrair antiX Linux completo (kernel + linuxfs) para a partição\n" +
                    $"4. Substituir bootmgfw.efi pelo rEFInd no ESP\n" +
                    $"5. REINICIAR — rEFInd mostra menu (timeout 20s)\n" +
                    $"6. Selecione \"antiX Live\" para boot completo\n" +
                    $"7. Execute gparted ou ntfsresize manualmente\n\n" +
                    $"Deseja continuar?",
                    "Shrink Partição",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    AppendLog("Operação cancelada pelo usuário.");
                    return;
                }

                OverlayBusy.Visibility = Visibility.Visible;
                TxtProgressStatus.Text = "Criando partição KITLUGIA + extraindo antiX...";

                var (ok, msg) = await EmergencyBcdBootManager.DeployAntiXAsync(
                    selected.DriveLetter,
                    shrinkMb,
                    UpdateProgress
                );

                if (!ok)
                {
                    AppendLog($"ERRO: {msg}");
                    MessageBox.Show($"Falha: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppendLog("antiX + rEFInd implantado com sucesso!");
                AppendLog(msg);
                TxtStatus.Text = "✅ Pronto. Reinicie e selecione antiX Live.";

                var reboot = MessageBox.Show(
                    msg + "\n\nDeseja reiniciar AGORA?",
                    "Shrink - KitLugia", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (reboot == MessageBoxResult.Yes)
                    await RefindManager.TriggerReboot();
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

        private async void BtnInstallRefind_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                OverlayBusy.Visibility = Visibility.Visible;
                TxtProgressStatus.Text = "Instalando rEFInd...";

                AppendLog("=== INSTALAR rEFInd ===");

                var (ok, msg) = await RefindManager.InstallRefindOnlyAsync();

                if (ok)
                {
                    AppendLog("rEFInd instalado.");
                    TxtStatus.Text = "✅ rEFInd instalado (substituiu bootmgfw.efi)";
                }
                else
                {
                    AppendLog($"ERRO: {msg}");
                    MessageBox.Show(msg, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO: {ex.Message}");
                TxtStatus.Text = "❌ Erro";
                MessageBox.Show($"Erro ao instalar rEFInd: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                OverlayBusy.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnRemoveRefind_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            _isBusy = true;

            try
            {
                var confirm = MessageBox.Show(
                    "Restaurar o Windows Boot Manager original (bootmgfw.efi)?\n" +
                    "Isso removerá o rEFInd do ESP.\n\n" +
                    "Deseja também remover a partição KITLUGIA?",
                    "Desinstalar rEFInd",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Cancel)
                    return;

                OverlayBusy.Visibility = Visibility.Visible;
                TxtProgressStatus.Text = "Removendo rEFInd...";
                AppendLog("=== DESINSTALAR rEFInd ===");

                bool removePartition = confirm == MessageBoxResult.Yes;
                var (ok, msg) = await EmergencyBcdBootManager.CleanupAsync(removePartition);

                if (ok)
                    TxtStatus.Text = "✅ Windows Boot Manager restaurado";
                else
                    TxtStatus.Text = $"❌ {msg}";
            }
            catch (Exception ex)
            {
                AppendLog($"ERRO: {ex.Message}");
                TxtStatus.Text = "❌ Erro";
                MessageBox.Show($"Erro ao remover rEFInd: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isBusy = false;
                OverlayBusy.Visibility = Visibility.Collapsed;
            }
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
