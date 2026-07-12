using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        private void RadioMode_Checked(object sender, RoutedEventArgs e)
        {
            PanelAntiX.Visibility = RadioAntiX.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelWinPE.Visibility = RadioWinPE.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ========== ANTI-X MODE ==========

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

        // ========== WINPE MODE ==========

        private void BtnSelectWinpe_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecionar arquivo WinPE",
                Filter = "Arquivos WinPE (*.wim;*.iso)|*.wim;*.iso|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog() == true)
                TxtWinpePath.Text = dlg.FileName;
        }

        private async void BtnWinpePrepare_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            var selected = ComboPartitions.SelectedItem as PartitionInfo;
            if (selected == null || string.IsNullOrEmpty(selected.DriveLetter))
            {
                AppendLog("ERRO: Selecione uma partição.");
                return;
            }

            if (!long.TryParse(TxtPartASize.Text, out long partASize) || partASize < 200)
            {
                AppendLog("ERRO: Tamanho da Partição A deve ser no mínimo 200 MB.");
                return;
            }

            if (!long.TryParse(TxtWinpeShrinkSize.Text, out long shrinkSize) || shrinkSize < 1024)
            {
                AppendLog("ERRO: Tamanho do shrink final deve ser no mínimo 1024 MB.");
                return;
            }

            string winpePath = TxtWinpePath.Text.Trim();
            if (!File.Exists(winpePath))
            {
                AppendLog("ERRO: Arquivo WinPE não encontrado: " + winpePath);
                return;
            }

            var confirm = MessageBox.Show(
                "⚠️ PREPARAR BOOT WINPE (Fase 1)\n\n" +
                $"Partição alvo: {selected.DriveLetter}:\n" +
                $"Shrink inicial: {partASize} MB (para Partição A)\n" +
                $"Shrink final no WinPE: {shrinkSize} MB\n" +
                $"WinPE: {winpePath}\n\n" +
                "O sistema será reiniciado após a preparação.\n" +
                "Deseja continuar?",
                "WinPE Shrink - KitLugia",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                AppendLog("Operação cancelada.");
                return;
            }

            _isBusy = true;
            OverlayBusy.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Preparando WinPE...";

            try
            {
                AppendLog($"=== PREPARANDO WINPE (Fase 1) ===");
                AppendLog($"Partição: {selected.DriveLetter}:");
                AppendLog($"Partição A: {partASize} MB");
                AppendLog($"Shrink final: {shrinkSize} MB");
                AppendLog($"WinPE: {winpePath}");

                // Se for ISO, extrair boot.wim
                string wimPath = winpePath;
                if (winpePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("ISO detectado. Montando para extrair boot.wim...");
                    var (isoOk, isoMsg, extractedWim) = await ExtractWimFromIso(winpePath);
                    if (!isoOk)
                    {
                        AppendLog($"ERRO: {isoMsg}");
                        MessageBox.Show($"Falha ao extrair WIM da ISO: {isoMsg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    wimPath = extractedWim;
                }

                // Copiar WIM e SDI para o diretório do app
                string peDir = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory,
                    "WinPE");
                Directory.CreateDirectory(peDir);
                string destWim = Path.Combine(peDir, "boot.wim");
                string destSdi = Path.Combine(peDir, "boot.sdi");
                File.Copy(wimPath, destWim, true);

                string winSdi = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "Boot", "DVD", "PCAT", "boot.sdi");
                if (File.Exists(winSdi))
                    File.Copy(winSdi, destSdi, true);

                AppendLog($"WIM copiado para: {destWim}");

                // Chamar o WinbootManager
                var (ok, msg) = await WinbootManager.PrepareWinpeBoot(selected.DriveLetter, partASize);

                if (!ok)
                {
                    AppendLog($"ERRO: {msg}");
                    TxtStatus.Text = "❌ Falha na preparação.";
                    MessageBox.Show($"Falha ao preparar WinPE: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppendLog($"✅ WinPE preparado!");
                AppendLog(msg);
                TxtStatus.Text = "✅ WinPE pronto. Reinicie e selecione 'KitLugia WinPE' no boot.";

                var reboot = MessageBox.Show(
                    msg + "\n\nDeseja reiniciar AGORA para entrar no WinPE?",
                    "WinPE Shrink - KitLugia",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (reboot == MessageBoxResult.Yes)
                {
                    AppendLog("Reiniciando...");
                    Process.Start("shutdown.exe", "/r /t 5 /f");
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

        private async void BtnWinpeContinue_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            var selected = ComboPartitions.SelectedItem as PartitionInfo;
            if (selected == null || string.IsNullOrEmpty(selected.DriveLetter))
            {
                AppendLog("ERRO: Selecione uma partição.");
                return;
            }

            if (!long.TryParse(TxtWinpeShrinkSize.Text, out long shrinkSize) || shrinkSize < 1024)
            {
                AppendLog("ERRO: Tamanho do shrink deve ser no mínimo 1024 MB.");
                return;
            }

            var confirm = MessageBox.Show(
                "⚠️ CONTINUAR SHRINK NO WINPE (Fase 2)\n\n" +
                $"Partição alvo: {selected.DriveLetter}:\n" +
                $"Shrink desejado: {shrinkSize} MB\n\n" +
                "Esta operação deve ser executada DENTRO do WinPE.\n" +
                $"A Partição A será deletada para liberar o espaço e então\n" +
                $"a Partição B será criada com os {shrinkSize} MB liberados.\n\n" +
                "Continuar?",
                "WinPE Shrink - KitLugia",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                AppendLog("Operação cancelada.");
                return;
            }

            _isBusy = true;
            OverlayBusy.Visibility = Visibility.Visible;
            TxtProgressStatus.Text = "Executando shrink no WinPE...";

            try
            {
                AppendLog($"=== SHRINK WINPE (Fase 2) ===");
                AppendLog($"Partição alvo: {selected.DriveLetter}:");
                AppendLog($"Shrink desejado: {shrinkSize} MB");

                var (ok, msg) = await WinbootManager.ContinueShrinkInWinpe(selected.DriveLetter, shrinkSize);

                if (!ok)
                {
                    AppendLog($"ERRO: {msg}");
                    TxtStatus.Text = "❌ Shrink falhou.";
                    MessageBox.Show($"Falha no shrink: {msg}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                AppendLog("✅ SHRINK CONCLUÍDO!");
                AppendLog(msg);
                TxtStatus.Text = "✅ Shrink concluído! Partição B criada.";

                MessageBox.Show(
                    "✅ Shrink concluído com sucesso!\n\n" +
                    $"Partição {selected.DriveLetter}: reduzida em {shrinkSize} MB\n" +
                    "Partição B (KITLUGIA_BOOT) criada.\n\n" +
                    "Reinicie o sistema para voltar ao Windows.",
                    "Sucesso",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

        private async Task<(bool ok, string msg, string wimPath)> ExtractWimFromIso(string isoPath)
        {
            try
            {
                // Monta a ISO via PowerShell
                string psCommand = $"Mount-DiskImage -ImagePath '{isoPath}' -PassThru | Get-Volume | Select-Object -ExpandProperty DriveLetter";
                var psi = new ProcessStartInfo("powershell.exe", $"-Command \"{psCommand}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return (false, "Falha ao iniciar PowerShell", "");

                using var reader = proc.StandardOutput;
                string output = await reader.ReadToEndAsync();
                await proc.WaitForExitAsync();

                string driveLetter = output?.Trim();
                if (string.IsNullOrEmpty(driveLetter) || driveLetter.Length < 1 || driveLetter == "0")
                    return (false, "Falha ao montar ISO (sem letra de unidade)", "");

                string dl = driveLetter[0].ToString();
                string sourcesDir = $@"{dl}:\sources";
                string wimFile = Path.Combine(sourcesDir, "boot.wim");

                if (File.Exists(wimFile))
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "KitLugia_WinPE");
                    Directory.CreateDirectory(tempDir);
                    string dest = Path.Combine(tempDir, "boot.wim");
                    File.Copy(wimFile, dest, true);

                    // Desmonta ISO
                    Process.Start("powershell.exe", $"-Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"");
                    return (true, "WIM extraído da ISO", dest);
                }

                // Tenta procurar em outros lugares
                var allWims = Directory.GetFiles($@"{dl}\", "*.wim", SearchOption.AllDirectories);
                if (allWims.Length > 0)
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "KitLugia_WinPE");
                    Directory.CreateDirectory(tempDir);
                    string dest = Path.Combine(tempDir, "boot.wim");
                    File.Copy(allWims[0], dest, true);

                    Process.Start("powershell.exe", $"-Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"");
                    return (true, $"WIM extraído de: {allWims[0]}", dest);
                }

                return (false, "boot.wim não encontrado dentro da ISO", "");
            }
            catch (Exception ex)
            {
                return (false, $"Erro ao extrair WIM: {ex.Message}", "");
            }
        }

        // ========== Shared ==========

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
