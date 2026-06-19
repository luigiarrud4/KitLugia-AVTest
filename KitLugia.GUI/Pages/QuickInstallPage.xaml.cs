using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Pages
{
    public partial class QuickInstallPage : Page
    {
        private string? _isoPath;
        private string? _extractPath;

        public QuickInstallPage()
        {
            try
            {
                InitializeComponent();
                Loaded += async (_, _) => await RefreshDrives();
            }
            catch (Exception ex)
            {
                Log("ERRO INIT: " + ex.Message);
            }
        }

        public void Cleanup()
        {
            if (_extractPath != null && Directory.Exists(_extractPath))
                try { Directory.Delete(_extractPath, true); } catch { }
        }

        private async Task<(int ExitCode, string Output, string Error)> Run(string file, string args, int timeoutMs = 300000)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };
                var proc = Process.Start(psi);
                if (proc == null) return (-1, "", "Process.Start returned null");

                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var allTask = Task.WhenAll(outTask, errTask);

                if (timeoutMs > 0)
                {
                    if (await Task.WhenAny(allTask, Task.Delay(timeoutMs)) != allTask)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        return (-1, "", "TIMEOUT");
                    }
                }
                else await allTask;

                await proc.WaitForExitAsync();
                return (proc.ExitCode, outTask.Result, errTask.Result);
            }
            catch (Exception ex)
            {
                return (-1, "", $"Run exception: {ex.Message}");
            }
        }

        private void Log(string msg)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        TxtLog.Text += $"\n[{DateTime.Now:HH:mm:ss}] {msg}";
                        if (TxtLog.Parent is ScrollViewer sv)
                            sv.ScrollToBottom();
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void SetBusy(bool busy)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    BtnInstall.IsEnabled = !busy;
                    BtnBrowseIso.IsEnabled = !busy;
                    CmbTargetDrive.IsEnabled = !busy;
                    ProgressArea.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                }
                catch { }
            });
        }

        public class DriveItem
        {
            public string Letter { get; set; } = "";
            public string Label { get; set; } = "";
            public long SizeBytes { get; set; }
            public long FreeBytes { get; set; }
            public bool HasWindows { get; set; }
            public bool IsSystem { get; set; }
            public string DisplayName
            {
                get
                {
                    string size = SizeBytes > 1073741824
                        ? $"{SizeBytes / 1073741824.0:F1} GB"
                        : $"{SizeBytes / 1048576.0:F0} MB";
                    string extra = HasWindows ? " [WINDOWS]" : "";
                    if (IsSystem) extra += " [ATUAL]";
                    return $"{Letter}\\  {Label}{extra}  ({size})";
                }
            }
        }

        private async Task RefreshDrives()
        {
            try
            {
                var items = new List<DriveItem>();
                foreach (var di in DriveInfo.GetDrives())
                {
                    if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                    string letter = di.Name.TrimEnd('\\');
                    bool hasWin = Directory.Exists(Path.Combine(di.Name, "Windows", "System32"));
                    items.Add(new DriveItem
                    {
                        Letter = letter,
                        Label = di.VolumeLabel ?? "",
                        SizeBytes = (long)di.TotalSize,
                        FreeBytes = (long)di.AvailableFreeSpace,
                        HasWindows = hasWin,
                        IsSystem = hasWin && string.Equals(
                            Path.GetPathRoot(Environment.SystemDirectory), di.Name,
                            StringComparison.OrdinalIgnoreCase)
                    });
                }

                bool winPe = DetectWinPe();

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        CmbTargetDrive.Items.Clear();
                        foreach (var d in items.OrderByDescending(x => x.IsSystem)
                                               .ThenByDescending(x => x.HasWindows)
                                               .ThenBy(x => x.Letter))
                            CmbTargetDrive.Items.Add(d);

                        if (CmbTargetDrive.Items.Count > 0)
                            CmbTargetDrive.SelectedIndex = 0;

                        TxtWinpeHint.Visibility = winPe ? Visibility.Visible : Visibility.Collapsed;
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                Log("ERRO RefreshDrives: " + ex.Message);
            }
        }

        private static bool DetectWinPe()
        {
            try
            {
                string winDir = Environment.GetEnvironmentVariable("SystemRoot") ?? "";
                return winDir.StartsWith("X:", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(Path.Combine(winDir, "explorer.exe"));
            }
            catch { return false; }
        }

        private void ChkOriginalMode_Checked(object sender, RoutedEventArgs e)
        {
            bool original = ChkOriginalMode.IsChecked == true;
            BdrAdvanced.Opacity = original ? 0.4 : 1.0;
            BdrAdvanced.IsEnabled = !original;
        }

        private void BtnBrowseIso_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Arquivos ISO (*.iso)|*.iso|Todos os arquivos (*.*)|*.*",
                    Title = "Selecione a ISO do Windows"
                };

                if (dialog.ShowDialog() == true)
                {
                    _isoPath = dialog.FileName;
                    TxtIsoPath.Text = _isoPath;
                    TxtIsoPath.Foreground = System.Windows.Media.Brushes.White;
                    Log($"ISO: {_isoPath}");
                    BtnInstall.IsEnabled = CmbTargetDrive.SelectedItem != null;
                }
            }
            catch (Exception ex)
            {
                Log("ERRO BrowseISO: " + ex.Message);
            }
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ─── VALIDAÇÃO INICIAL ──────────────────────────────────────
                Log("Iniciando instalação...");

                if (string.IsNullOrEmpty(_isoPath) || !File.Exists(_isoPath))
                {
                    MessageBox.Show("Selecione uma ISO válida.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (CmbTargetDrive.SelectedItem is not DriveItem target)
                {
                    MessageBox.Show("Selecione um disco de destino.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string targetLetter = target.Letter.TrimEnd('\\');
                bool originalMode = ChkOriginalMode.IsChecked == true;
                bool preserve = !originalMode && ChkPreserveUsers.IsChecked == true;
                bool runBcdboot = ChkRunBcdboot.IsChecked == true;

                string msg = $"Instalar Windows em {targetLetter}\\ ?\nISO: {Path.GetFileName(_isoPath)}\n\n";
                if (originalMode)
                    msg += "Modo ORIGINAL: instalação limpa, sem backup/restore.";
                else if (preserve)
                    msg += "Modo AVANÇADO: backup → instalar → restore completo (contas + dados).";
                else
                    msg += "Sem preservação. Instalação limpa.";

                if (MessageBox.Show(msg, "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                SetBusy(true);
                Log("══════ INICIANDO ══════");

                // ─── BACKUP (apenas modo avançado) ──────────────────────────
                if (preserve)
                {
                    try
                    {
                        Log("Backup das pastas do sistema...");
                        TxtProgressStatus.Text = "Backup...";

                        string backupRoot = Path.Combine(targetLetter, "_SysBackup");
                        Directory.CreateDirectory(backupRoot);

                        string[] backupDirs = {
                            "Users", "ProgramData", "Program Files", "Program Files (x86)"
                        };
                        foreach (string name in backupDirs)
                        {
                            string src = Path.Combine(targetLetter, name);
                            if (!Directory.Exists(src)) continue;

                            string dst = Path.Combine(backupRoot, name);
                            if (Directory.Exists(dst))
                                Directory.Delete(dst, true);

                            Directory.Move(src, dst);
                            Log($"  {name} → _SysBackup\\");
                        }

                        // Registry hives
                        string regSrc = Path.Combine(targetLetter, "Windows", "System32", "config");
                        string regDst = Path.Combine(backupRoot, "Registry");
                        Directory.CreateDirectory(regDst);
                        foreach (string hive in new[] { "SAM", "SYSTEM", "SECURITY" })
                        {
                            string f = Path.Combine(regSrc, hive);
                            if (File.Exists(f))
                            {
                                File.Copy(f, Path.Combine(regDst, hive), true);
                                Log($"  Registry {hive} salvo");
                            }
                        }
                        Log("Backup concluído.");
                    }
                    catch (Exception ex)
                    {
                        Log("ERRO no backup: " + ex.Message);
                        Log("Continuando sem backup...");
                        preserve = false;
                    }
                }

                // ─── 7-ZIP EXTRAIR ISO ──────────────────────────────────────
                string sevenZip = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Resources", "App", "7Zip", "7z.exe");

                if (!File.Exists(sevenZip))
                {
                    Log("ERRO: 7z.exe não encontrado em Resources/App/7Zip/");
                    SetBusy(false);
                    return;
                }

                string extractDir = Path.Combine(Path.GetTempPath(),
                    "KL_WIN_" + Guid.NewGuid().ToString("N").AsSpan(0, 8).ToString());
                _extractPath = extractDir;
                Directory.CreateDirectory(extractDir);

                Log("Extraindo ISO com 7z...");
                TxtProgressStatus.Text = "Extraindo ISO...";
                var (extCode, _, extErr) = await Run(sevenZip,
                    $"x \"{_isoPath}\" -o\"{extractDir}\" -y", 600000);

                if (extCode != 0 && extCode != 1)
                {
                    Log($"ERRO 7z (código {extCode}): {extErr}");
                    SetBusy(false);
                    return;
                }
                Log("ISO extraída.");

                // ─── LOCALIZAR INSTALL.WIM ──────────────────────────────────
                string wim = Path.Combine(extractDir, "sources", "install.wim");
                if (!File.Exists(wim)) wim = Path.Combine(extractDir, "sources", "install.esd");
                if (!File.Exists(wim))
                {
                    Log("ERRO: install.wim ou install.esd não encontrado.");
                    SetBusy(false);
                    return;
                }
                Log($"WIM: {Path.GetFileName(wim)}");

                // ─── DISM APPLY ─────────────────────────────────────────────
                Log("Aplicando Windows com DISM (Index 1)...");
                TxtProgressStatus.Text = "Aplicando Windows (10-30 min)...";
                Log("dism /Apply-Image /Index:1 ...");

                var (dismCode, _, dismErr) = await Run("dism.exe",
                    $"/Apply-Image /ImageFile:\"{wim}\" /Index:1 /ApplyDir:{targetLetter}\\",
                    1800000);

                if (dismCode != 0)
                {
                    Log($"ERRO DISM (código {dismCode}):");
                    string err = dismErr.Length > 2000 ? dismErr[^2000..] : dismErr;
                    Log(err);
                    SetBusy(false);
                    return;
                }
                Log("DISM concluído!");

                // ─── RESTORE (apenas se backup foi feito) ───────────────────
                if (preserve)
                {
                    try
                    {
                        Log("Restaurando backup...");
                        TxtProgressStatus.Text = "Restaurando...";

                        string backupRoot = Path.Combine(targetLetter, "_SysBackup");

                        foreach (string name in new[] { "Users", "ProgramData", "Program Files", "Program Files (x86)" })
                        {
                            string src = Path.Combine(backupRoot, name);
                            string dst = Path.Combine(targetLetter, name);
                            if (!Directory.Exists(src)) continue;

                            if (Directory.Exists(dst))
                                Directory.Delete(dst, true);
                            Directory.Move(src, dst);
                            Log($"  {name} restaurado");
                        }

                        string regBackup = Path.Combine(backupRoot, "Registry");
                        string regTarget = Path.Combine(targetLetter, "Windows", "System32", "config");
                        if (Directory.Exists(regBackup))
                        {
                            foreach (string hive in new[] { "SAM", "SYSTEM", "SECURITY" })
                            {
                                string src = Path.Combine(regBackup, hive);
                                string dst = Path.Combine(regTarget, hive);
                                if (File.Exists(src))
                                {
                                    File.Copy(src, dst, true);
                                    Log($"  Registry {hive} restaurado");
                                }
                            }
                        }

                        try { Directory.Delete(backupRoot, true); } catch { }
                        Log("Restore concluído.");
                    }
                    catch (Exception ex)
                    {
                        Log("ERRO no restore: " + ex.Message);
                        Log("O backup está em _SysBackup para recuperação manual.");
                    }
                }

                // ─── BCDBOOT ────────────────────────────────────────────────
                if (runBcdboot)
                {
                    Log("Configurando bootloader...");
                    TxtProgressStatus.Text = "Bootloader...";
                    var (bcdCode, _, bcdErr) = await Run("bcdboot.exe",
                        $"{targetLetter}\\Windows", 60000);

                    if (bcdCode == 0)
                        Log("Bootloader OK.");
                    else
                        Log($"bcdboot código {bcdCode}: {bcdErr}");
                }

                Log("══════ CONCLUÍDO ══════");
                TxtProgressStatus.Text = "Concluído!";

                string final = $"Instalação concluída em {targetLetter}\\!";
                if (preserve)
                    final += "\n\nBackup restaurado! Suas contas e dados devem estar intactos.";
                else if (originalMode)
                    final += "\n\nModo Original: instalação limpa concluída.";
                final += "\n\nReinicie o PC.";

                MessageBox.Show(final, "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"ERRO FATAL: {ex.Message}");
                Log(ex.StackTrace ?? "");
                try
                {
                    MessageBox.Show($"Erro: {ex.Message}\n\nDetalhes no log.", "Falha",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
            finally
            {
                SetBusy(false);
            }
        }
    }
}
