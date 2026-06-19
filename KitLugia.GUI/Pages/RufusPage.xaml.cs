using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;

using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class RufusPage : Page
    {
        private CancellationTokenSource? _cts;
        private List<string> _multiIsoPaths = new();
        private bool _isRunning;

        public RufusPage()
        {
            InitializeComponent();
            Loaded += RufusPage_Loaded;
            this.Unloaded += RufusPage_Unloaded;
            ChkMultiBoot.Checked += ChkMultiBoot_Checked;
            ChkMultiBoot.Unchecked += ChkMultiBoot_Unchecked;
            ChkBadBlocks.Checked += ChkBadBlocks_Checked;
            ChkBadBlocks.Unchecked += ChkBadBlocks_Unchecked;
            CboDevice.SelectionChanged += CboDevice_SelectionChanged;
        }

        private void ChkMultiBoot_Checked(object sender, RoutedEventArgs e) => MultiIsoPanel.Visibility = Visibility.Visible;
        private void ChkMultiBoot_Unchecked(object sender, RoutedEventArgs e) => MultiIsoPanel.Visibility = Visibility.Collapsed;
        private void ChkBadBlocks_Checked(object sender, RoutedEventArgs e) => CboNBPasses.IsEnabled = true;
        private void ChkBadBlocks_Unchecked(object sender, RoutedEventArgs e) => CboNBPasses.IsEnabled = false;
        private void CboDevice_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        private async void RufusPage_Loaded(object sender, RoutedEventArgs e)
        {
            try { await RefreshDevices(); }
            catch (Exception ex) { AddLog($"Erro ao carregar: {ex.Message}"); }
        }

        private void RufusPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        public void Cleanup()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            Loaded -= RufusPage_Loaded;
            this.Unloaded -= RufusPage_Unloaded;
            ChkMultiBoot.Checked -= ChkMultiBoot_Checked;
            ChkMultiBoot.Unchecked -= ChkMultiBoot_Unchecked;
            ChkBadBlocks.Checked -= ChkBadBlocks_Checked;
            ChkBadBlocks.Unchecked -= ChkBadBlocks_Unchecked;
            CboDevice.SelectionChanged -= CboDevice_SelectionChanged;
            _multiIsoPaths.Clear();
            this.DataContext = null;
        }

        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtLog.Text += $"\n[{time}] {message}";
        }

        private async Task RefreshDevices()
        {
            CboDevice.ItemsSource = null;
            var drives = await Task.Run(() => BootableMediaManager.GetUsbDrives());
            var items = drives.Select(d => new
            {
                Name = $"{d.DriveLetter} ({d.Label} - {FormatSize(d.Size)} - {d.BusType})",
                DiskNumber = d.DiskNumber,
                DriveLetter = d.DriveLetter
            }).ToList();

            if (items.Count == 0)
                items.Add(new { Name = "Nenhum drive detectado", DiskNumber = (uint)0, DriveLetter = "" });

            CboDevice.ItemsSource = items;
            CboDevice.DisplayMemberPath = "Name";
            CboDevice.SelectedIndex = -1;
            if (items.Count > 0) CboDevice.SelectedIndex = 0;
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1073741824 => $"{bytes / 1073741824.0:N1} GB",
            >= 1048576 => $"{bytes / 1048576.0:N1} MB",
            _ => $"{bytes / 1024.0:N1} KB"
        };

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            try { await RefreshDevices(); }
            catch (Exception ex) { AddLog($"Erro: {ex.Message}"); }
        }

        private void UpdateButtons()
        {
            dynamic sel = CboDevice.SelectedItem ?? new { DriveLetter = "" };
            bool hasDevice = !string.IsNullOrEmpty((string)sel.DriveLetter);
            bool hasIsoOrFreeDos = TxtIsoPath.Text == "FreeDOS" || File.Exists(TxtIsoPath.Text);
            BtnStart.IsEnabled = !_isRunning && hasDevice && (hasIsoOrFreeDos || (ChkMultiBoot.IsChecked == true && _multiIsoPaths.Count > 0));
            BtnFormatOnly.IsEnabled = !_isRunning && hasDevice;
        }

        private async void BtnSelectIso_Click(object sender, RoutedEventArgs e)
        {
            string mode = (CboBootType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

            if (mode == "FreeDOS")
            {
                TxtIsoPath.Text = "FreeDOS";
                UpdateButtons();
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "ISOs e imagens (*.iso;*.img;*.vhd)|*.iso;*.img;*.vhd|Arquivo ISO (*.iso)|*.iso|Arquivo IMG (*.img)|*.img|VHD (*.vhd)|*.vhd|Todos (*.*)|*.*",
                Title = "Selecionar imagem de disco"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtIsoPath.Text = dialog.FileName;
                AutoDetectLabel(dialog.FileName);
                UpdateButtons();
                await DetectIsoTypeAsync(dialog.FileName);
            }
        }

        private async Task DetectIsoTypeAsync(string path)
        {
            try
            {
                string type = await BootableMediaManager.DetectIsoType(path);
                string info = type switch
                {
                    "Windows" => "🟦 ISO Windows detectada — modo Standard Windows (bootsect + bcdboot)",
                    "Ubuntu" => "🟧 ISO Ubuntu/Linux detectada — modo DD recomendado se falhar",
                    "Linux" => "🟩 ISO Linux detectada — use bootsect para BIOS, ou DD mode",
                    "Debian" => "🟨 ISO Debian detectada — modo DD se falhar cópia normal",
                    _ => "⬜ ISO detectada — modo padrão. Use DD se falhar"
                };
                AddLog($"📋 {info}");

                // Show Windows options panel only for Windows ISOs
                WindowsOptionsPanel.Visibility = type == "Windows" ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Não foi possível detectar tipo: {ex.Message}");
            }
        }

        private void AutoDetectLabel(string path)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(path).ToUpper();
                var clean = new string(name.Where(c => char.IsLetterOrDigit(c)).ToArray());
                if (clean.Length > 11) clean = clean[..11];
                if (clean.Length > 0) TxtLabel.Text = clean;
            }
            catch { }
        }

        // ─── FORMAT ONLY (no ISO) ─────────────────────────────────
        private async void BtnFormatOnly_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            dynamic sel = CboDevice.SelectedItem ?? new { DriveLetter = "", Name = "" };
            string name = (string)sel.Name;
            string dl = (string)sel.DriveLetter;

            if (dl.Equals(Environment.SystemDirectory.Substring(0, 2), StringComparison.OrdinalIgnoreCase))
            {
                AddLog("❌ BLOQUEADO: Operação cancelada — este é o disco do sistema.");
                return;
            }

            if (!await Confirm($"⚠️ ATENÇÃO — FORMATAR DISCO\n\nDispositivo: {name}\nDrive: {dl}\n\nIsso apagará TODOS os dados.\nNão é possível desfazer.\n\nContinuar?")) return;
            if (!await Confirm($"🔴 CONFIRMAÇÃO FINAL\n\nTem certeza ABSOLUTA que deseja FORMATAR {dl}?\nTodos os arquivos serão PERDIDOS.")) return;

            _isRunning = true;
            _cts = new CancellationTokenSource();
            ShowProgress(true);
            TxtLog.Text = "[Iniciando] Formatação...";

            string fs = (CboFileSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "FAT32";
            bool gpt = (CboScheme.SelectedItem as ComboBoxItem)?.Content?.ToString() == "GPT";

            var options = new BootableMediaOptions
            {
                FileSystem = fs,
                Label = TxtLabel.Text.Trim(),
                UseGPT = gpt,
                QuickFormat = ChkQuickFormat.IsChecked == true
            };

            try
            {
                var result = await BootableMediaManager.FormatDrive(dl, options);
                AddLog(result.Success ? $"✅ {result.Message}" : $"❌ {result.Message}");
            }
            catch (Exception ex) { AddLog($"❌ {ex.Message}"); }
            finally
            {
                _isRunning = false;
                ShowProgress(false);
                UpdateButtons();
            }
        }

        // ─── Apply Windows options ────────────────────────────────
        private async Task<string?> GenerateWindowsUnattend(string targetDrive)
        {
            try
            {
                string tempXml = Path.Combine(Path.GetTempPath(), "autounattend.xml");
                await Task.Run(() => WinbootManager.GenerateAutounattendXml(tempXml,
                    bypassRequirements: ChkBypassTPM.IsChecked == true,
                    localAccount: ChkLocalAccount.IsChecked == true,
                    disablePrivacy: ChkDisableDataColl.IsChecked == true,
                    disableBitlocker: ChkDisableBitlocker.IsChecked == true,
                    userName: TxtWinUser.Text.Trim(),
                    password: TxtWinPass.Password
                ));

                string dest = Path.Combine(targetDrive, "autounattend.xml");
                await Task.Run(() => File.Copy(tempXml, dest, true));
                AddLog("📋 autounattend.xml gerado com opções do Windows.");
                return dest;
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Erro ao gerar autounattend: {ex.Message}");
                return null;
            }
        }

        // ─── START (ISO / DD / Multi) ─────────────────────────────
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            dynamic sel = CboDevice.SelectedItem ?? new { DriveLetter = "", Name = "" };
            string driveLetter = (string)sel.DriveLetter;
            string deviceName = (string)sel.Name;
            if (string.IsNullOrEmpty(driveLetter)) return;

            // System drive guard
            string systemDrive = Environment.SystemDirectory.Substring(0, 2);
            if (driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
            {
                AddLog("❌ BLOQUEADO: Operação cancelada — este é o disco do sistema.");
                return;
            }

            string mode = (CboBootType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Disco ou ISO";
            string fs = (CboFileSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "FAT32";

            // Triple safety confirmation
            string warning = $"⚠️ GRAVAÇÃO DE MÍDIA BOOTÁVEL\n\nDispositivo: {deviceName}\nDrive: {driveLetter}\nModo: {mode}\nFormato: {fs}\n\nIsso apagará TODOS os dados do dispositivo.\nNão é possível desfazer.";
            if (!await Confirm(warning)) return;
            if (!await Confirm($"🔴 CONFIRMAÇÃO FINAL\n\nDispositivo: {driveLetter}\nModo: {mode}\n\nTem certeza absoluta?\nTodo o conteúdo será PERDIDO.")) return;

            _isRunning = true;
            _cts = new CancellationTokenSource();

            bool gpt = (CboScheme.SelectedItem as ComboBoxItem)?.Content?.ToString() == "GPT";
            string label = TxtLabel.Text.Trim();
            bool multi = ChkMultiBoot.IsChecked == true;
            bool dd = mode == "Imagem DD (Raw)";
            bool freedos = mode == "FreeDOS";
            bool dualBoot = mode == "Dual Boot (E2B)";

            UpdateButtons();
            BtnStop.IsEnabled = true;
            ShowProgress(true);
            TxtLog.Text = "[Iniciando]...";

            var options = new BootableMediaOptions { FileSystem = fs, Label = label, UseGPT = gpt, QuickFormat = ChkQuickFormat.IsChecked == true };
            IProgress<(double Percent, string Status)> progress = new Progress<(double Percent, string Status)>(p =>
            {
                ProgressBar.Value = p.Percent;
                TxtProgressPercent.Text = $"{p.Percent:N0}%";
                TxtProgressStatus.Text = p.Status;
                AddLog(p.Status);
            });

            try
            {
                (bool Success, string Message) result;

                if (freedos)
                {
                    progress.Report((0.0, "Formatando drive para FreeDOS..."));
                    result = await BootableMediaManager.FormatDrive(driveLetter, options);
                }
                else if (dualBoot)
                {
                    var isos = new List<string>();
                    if (File.Exists(TxtIsoPath.Text)) isos.Add(TxtIsoPath.Text);
                    isos.AddRange(_multiIsoPaths.Where(File.Exists));
                    result = await BootableMediaManager.CreateDualBootDrive(isos, driveLetter, progress);
                }
                else if (multi)
                {
                    var isos = new List<string>();
                    if (File.Exists(TxtIsoPath.Text)) isos.Add(TxtIsoPath.Text);
                    isos.AddRange(_multiIsoPaths.Where(File.Exists));
                    result = await BootableMediaManager.CreateMultiBootDrive(isos, driveLetter, options, progress);
                }
                else if (dd && File.Exists(TxtIsoPath.Text))
                    result = await BootableMediaManager.WriteImageDD(TxtIsoPath.Text, driveLetter, progress);
                else if (File.Exists(TxtIsoPath.Text))
                {
                    result = await BootableMediaManager.WriteIsoRaw(TxtIsoPath.Text, driveLetter, options, progress);
                    if (result.Success && WindowsOptionsPanel.Visibility == Visibility.Visible)
                    {
                        progress.Report((95.0, "Injetando opções do Windows..."));
                        await GenerateWindowsUnattend(driveLetter);
                    }
                }
                else
                    result = (false, "Nenhuma ISO válida selecionada.");

                AddLog(result.Success ? $"✅ {result.Message}" : $"❌ {result.Message}");
            }
            catch (OperationCanceledException) { AddLog("⏹️ Cancelado."); }
            catch (Exception ex) { AddLog($"❌ {ex.Message}"); }
            finally
            {
                _isRunning = false;
                BtnStop.IsEnabled = false;
                UpdateButtons();
                ShowProgress(false);
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Text = "[Pronto]";
        private async void BtnSaveLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"rufus_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                string log = TxtLog.Text;
                await Task.Run(() => File.WriteAllText(path, log));
                AddLog($"📁 Log salvo: {path}");
            }
            catch (Exception ex) { AddLog($"Erro: {ex.Message}"); }
        }

        private void ShowProgress(bool show)
        {
            ProgressPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            BtnStart.Opacity = show ? 0.5 : 1.0;
            BtnFormatOnly.Opacity = show ? 0.5 : 1.0;
        }

        private async Task<bool> Confirm(string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                return await mw.ShowConfirmationDialog(msg);
            return false;
        }

        private void BtnAddMultiIso_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "ISO (*.iso)|*.iso|Todos (*.*)|*.*",
                Multiselect = true, Title = "Adicionar ISOs"
            };
            if (dialog.ShowDialog() == true)
            {
                foreach (var p in dialog.FileNames)
                    if (!_multiIsoPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) _multiIsoPaths.Add(p);
                ListMultiIsos.ItemsSource = _multiIsoPaths.Select(p => $"{Path.GetFileName(p)} ({FormatSize(new FileInfo(p).Length)})").ToList();
                UpdateButtons();
            }
        }
    }
}
