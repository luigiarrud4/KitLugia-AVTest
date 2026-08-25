using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

using Application = System.Windows.Application;
using MainWindow = KitLugia.GUI.MainWindow;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class ForceStopUnlockPage : Page
    {
        private bool _isLoading;

        public ForceStopUnlockPage()
        {
            InitializeComponent();
            this.Loaded += async (s, e) => await RefreshStatus();
            this.Unloaded += (s, e) => Cleanup();
        }

        public void Cleanup()
        {
            this.DataContext = null;
        }

        private async Task RefreshStatus()
        {
            _isLoading = true;
            try
            {
                await Task.Run(() =>
                {
                    bool isAdded = SystemTweaks.IsForceStopUnlockAdded();
                    string handlePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "External", "ForceStopUnlock", "handle64.exe");
                    bool handleExists = File.Exists(handlePath);

                    Dispatcher.Invoke(() =>
                    {
                        ChkEnable.IsChecked = isAdded;
                        TxtMenuStatus.Text = isAdded ? "✅ Ativo no menu de contexto" : "❌ Inativo";
                        TxtMenuStatus.Foreground = isAdded
                            ? Brushes.LightGreen
                            : Brushes.Gray;

                        TxtHandleStatus.Text = handleExists ? "✅ Incluso no Kit" : "⚠️ Não encontrado";
                        TxtHandleStatus.Foreground = handleExists
                            ? Brushes.LightGreen
                            : new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    });
                });
            }
            catch { Logger.LogWarning("ForceStopUnlock", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void ChkEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool target = ChkEnable.IsChecked == true;
                await Task.Run(() =>
                {
                    if (target) SystemTweaks.AddForceStopUnlock();
                    else SystemTweaks.RemoveForceStopUnlock();
                });

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (target)
                        mw.ShowSuccess("FORCE STOP UNLOCK", "Opção adicionada ao menu de contexto do Explorer.");
                    else
                        mw.ShowInfo("FORCE STOP UNLOCK", "Opção removida do menu de contexto.");
                }

                await RefreshStatus();
            }
            catch { Logger.LogWarning("ForceStopUnlock", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        /// <summary>
        /// Called by MainWindow when user right-clicks a file/folder in Explorer.
        /// Pre-fills the path and triggers analysis automatically.
        /// </summary>
        public void PreFillAndAnalyze(string path)
        {
            TxtQuickPath.Text = path;
            // Trigger the analyze button click
            if (BtnQuickAnalyze != null)
            {
                BtnQuickAnalyze.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent, BtnQuickAnalyze));
            }
        }

        private List<KitLugia.Core.BlockingProcessInfo> _quickResults = new();

        private async void BtnQuickAnalyze_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtQuickPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            Logger.Log($"[FORCE STOP UI] === Analisar clicado para: {path}");
            Logger.Log($"[FORCE STOP UI] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            // List folder contents immediately
            string folderContents = ListFolderContents(path);
            Logger.Log($"[FORCE STOP UI] Conteudo do caminho:\n{folderContents}");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Logger.Log($"[FORCE STOP UI] Caminho nao encontrado no sistema!");
                QuickResultPanel.Visibility = Visibility.Visible;
                TxtQuickResult.Text = "❌ Caminho não encontrado no sistema.";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TxtQuickDetail.Text = folderContents;
                QuickProcessList.ItemsSource = null;
                BtnQuickRelease.Visibility = Visibility.Collapsed;
                return;
            }

            BtnQuickAnalyze.IsEnabled = false;
            TxtQuickResult.Text = "🔍 Analisando...";
            TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TxtQuickDetail.Text = folderContents;
            QuickResultPanel.Visibility = Visibility.Visible;
            QuickProcessList.ItemsSource = null;
            BtnQuickRelease.Visibility = Visibility.Collapsed;

            try
            {
                Logger.Log($"[FORCE STOP UI] Chamando FindBlockingProcesses...");
                _quickResults = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));
                Logger.Log($"[FORCE STOP UI] FindBlockingProcesses retornou: {_quickResults.Count} resultado(s)");

                if (_quickResults.Count == 0)
                {
                    TxtQuickResult.Text = "✅ Nenhum processo bloqueador encontrado!";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    TxtQuickDetail.Text = $"O arquivo/pasta esta livre para uso.\n\n{folderContents}";
                }
                else
                {
                    foreach (var r in _quickResults)
                        Logger.Log($"[FORCE STOP UI] Bloqueador: {r.DisplayLabel} | {r.DetailLabel}");
                    TxtQuickResult.Text = $"⚠️ {_quickResults.Count} processo(s) bloqueando este caminho:";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    TxtQuickDetail.Text = "Selecione o que deseja liberar e clique no botão abaixo:";

                    QuickProcessList.ItemsSource = _quickResults;
                    BtnQuickRelease.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP UI] ERRO na analise: {ex.GetType().Name}: {ex.Message}");
                TxtQuickResult.Text = $"❌ Erro: {ex.Message}";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TxtQuickDetail.Text = folderContents;
            }
            finally
            {
                BtnQuickAnalyze.IsEnabled = true;
            }
        }

        private async void BtnTryDelete_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtQuickPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            Logger.Log($"[FORCE STOP UI] === Tentar Deletar clicado para: {path}");
            Logger.Log($"[FORCE STOP UI] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            // List folder contents immediately
            string folderContents = ListFolderContents(path);
            Logger.Log($"[FORCE STOP UI] Conteudo do caminho:\n{folderContents}");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Logger.Log($"[FORCE STOP UI] Caminho nao encontrado no sistema!");
                QuickResultPanel.Visibility = Visibility.Visible;
                TxtQuickResult.Text = "❌ Caminho não encontrado no sistema.";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TxtQuickDetail.Text = folderContents;
                QuickProcessList.ItemsSource = null;
                BtnQuickRelease.Visibility = Visibility.Collapsed;
                return;
            }

            BtnTryDelete.IsEnabled = false;
            BtnTryDelete.Content = "⏳ Deletando...";
            TxtQuickResult.Text = "🔍 Tentando deletar...";
            TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TxtQuickDetail.Text = folderContents;
            QuickResultPanel.Visibility = Visibility.Visible;
            QuickProcessList.ItemsSource = null;
            BtnQuickRelease.Visibility = Visibility.Collapsed;

            try
            {
                // Force delete via cmd /c (admin)
                Logger.Log($"[FORCE STOP UI] Executando ForceDeleteViaCmd...");
                var (deleted, errorMsg) = await Task.Run(() => ForceDeleteViaCmd(path));
                Logger.Log($"[FORCE STOP UI] Resultado do delete: Success={deleted}, Error={errorMsg}");

                // Check if file still exists after delete attempt
                bool stillExists = File.Exists(path) || Directory.Exists(path);
                Logger.Log($"[FORCE STOP UI] Arquivo ainda existe apos delete: {stillExists}");

                if (deleted)
                {
                    TxtQuickResult.Text = "\u2705 Arquivo/pasta deletado com sucesso!";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    TxtQuickDetail.Text = "";
                    QuickProcessList.ItemsSource = null;
                    BtnQuickRelease.Visibility = Visibility.Collapsed;
                    return;
                }

                // Delete failed - run full analysis to find what is blocking
                TxtQuickResult.Text = "\U0001f512 Arquivo bloqueado - identificando bloqueadores...";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                TxtQuickDetail.Text = $"Windows nao conseguiu deletar: {errorMsg}";
                await Task.Delay(300);
                Logger.Log($"[FORCE STOP UI] Chamando FindBlockingProcesses...");
                _quickResults = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));
                Logger.Log($"[FORCE STOP UI] FindBlockingProcesses retornou: {_quickResults.Count} resultado(s)");

                if (_quickResults.Count == 0)
                {
                    // No process found - try driver scan specifically
                    Logger.Log($"[FORCE STOP UI] Nenhum processo encontrado, tentando driver scan especifico...");
                    _quickResults = await Task.Run(() =>
                    {
                        var drivers = DriverUnlockService.FindBlockingDrivers(path);
                        Logger.Log($"[FORCE STOP UI] Driver scan direto retornou: {drivers.Count} driver(es)");
                        return drivers.Select(d => new KitLugia.Core.BlockingProcessInfo
                        {
                            Pid = d.Pid,
                            ProcessName = d.DriverName,
                            ExecutablePath = d.DriverPath,
                            HandleId = $"DRV:{d.ServiceName}",
                            HandleType = "Driver (.sys)",
                            AccessRights = d.CurrentState,
                            LockedPath = path,
                            IsSystemProcess = false,
                            IsSelected = true
                        }).ToList();
                    });
                }

                if (_quickResults.Count == 0)
                {
                    Logger.Log($"[FORCE STOP UI] NENHUM bloqueador encontrado em todas as tentativas.");
                    TxtQuickResult.Text = "\u26a0\ufe0f Delete falhou mas nenhum bloqueador encontrado.";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    TxtQuickDetail.Text = $"Tente como Administrador ou aguarde o Windows liberar o arquivo.\n\n{folderContents}";
                }
                else
                {
                    Logger.Log($"[FORCE STOP UI] {_quickResults.Count} bloqueador(es) encontrado(s): {_quickResults[0].ProcessName} (PID {_quickResults[0].Pid})");
                    TxtQuickResult.Text = $"\U0001f512 {_quickResults.Count} bloqueador(es) encontrado(s):";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    TxtQuickDetail.Text = "Selecione e clique Liberar, depois o delete sera retryado automaticamente:";
                    QuickProcessList.ItemsSource = _quickResults;
                    BtnQuickRelease.Visibility = Visibility.Visible;
                    _pendingDeletePath = path;
                    _retryDeleteAfterRelease = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE STOP UI] ERRO inesperado: {ex.GetType().Name}: {ex.Message}");
                TxtQuickResult.Text = $"\u274c Erro inesperado: {ex.Message}";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            }
            finally
            {
                BtnTryDelete.IsEnabled = true;
                BtnTryDelete.Content = "\U0001f5d1 Tentar Deletar";
            }

        }


        /// <summary>
        /// Force-delete a file or folder using cmd /c with admin privileges.
        /// Bypasses .NET File.Delete restrictions on files held by kernel drivers.
        /// Returns (success, errorMessage).
        /// </summary>
        private static (bool Success, string Error) ForceDeleteViaCmd(string path)
        {
            Logger.Log($"[FORCE DELETE] Iniciado para: {path}");
            Logger.Log($"[FORCE DELETE] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Logger.Log($"[FORCE DELETE] Arquivo/pasta ja nao existe.");
                    return (true, ""); // already gone
                }

                bool isDir = Directory.Exists(path);
                string cmd = isDir
                    ? $"cmd /c rmdir /s /q \"{path}\""
                    : $"cmd /c del /f /q \"{path}\"";

                Logger.Log($"[FORCE DELETE] Comando: {cmd}");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {cmd}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    Logger.Log($"[FORCE DELETE] FALHA: Nao foi possivel iniciar cmd.exe");
                    return (false, "Nao foi possivel iniciar cmd.exe");
                }

                proc.WaitForExit(10000); // 10s timeout

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                Logger.Log($"[FORCE DELETE] cmd.exe exit code: {proc.ExitCode}");
                if (!string.IsNullOrEmpty(stdout))
                    Logger.Log($"[FORCE DELETE] stdout: {stdout.Trim()}");
                if (!string.IsNullOrEmpty(stderr))
                    Logger.Log($"[FORCE DELETE] stderr: {stderr.Trim()}");

                // Check if file/folder is actually gone
                bool gone = !File.Exists(path) && !Directory.Exists(path);
                Logger.Log($"[FORCE DELETE] Arquivo existe apos comando: {!gone}");
                if (gone) return (true, "");

                // File still exists after del — held by a driver or system
                string reason = proc.ExitCode == 0
                    ? "Arquivo segurado por driver ou processo do sistema (del retornou 0 mas arquivo ainda existe)"
                    : $"cmd.exe exit code: {proc.ExitCode}";
                Logger.Log($"[FORCE DELETE] Falha: {reason}");
                return (false, reason);
            }
            catch (System.ComponentModel.Win32Exception w32)
            {
                Logger.Log($"[FORCE DELETE] Win32Exception: {w32.Message} (NativeErrorCode={w32.NativeErrorCode})");
                return (false, w32.Message);
            }
            catch (Exception ex)
            {
                Logger.Log($"[FORCE DELETE] Exception: {ex.GetType().Name}: {ex.Message}");
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Lists all files and subfolders in the given path for debugging.
        /// Returns a formatted string with the listing.
        /// </summary>
        private static string ListFolderContents(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    return $"Arquivo: {path}\nTamanho: {fi.Length} bytes\nModificado: {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}\nCriado: {fi.CreationTime:yyyy-MM-dd HH:mm:ss}";
                }

                if (!Directory.Exists(path))
                    return $"Caminho nao existe: {path}";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Pasta: {path}");
                sb.AppendLine($"---");

                // List subdirectories
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        sb.AppendLine($"[DIR]  {dirInfo.Name}/");
                    }
                }
                catch (Exception ex) { sb.AppendLine($"Erro ao listar pastas: {ex.Message}"); }

                // List files
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path))
                    {
                        var fi = new FileInfo(file);
                        string sizeStr = fi.Length > 1024 * 1024
                            ? $"{fi.Length / (1024.0 * 1024.0):F1} MB"
                            : $"{fi.Length / 1024.0:F1} KB";
                        string sysMark = fi.Extension.Equals(".sys", StringComparison.OrdinalIgnoreCase) ? " [DRIVER]" : "";
                        sb.AppendLine($"[FILE] {fi.Name} ({sizeStr}, {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}){sysMark}");
                    }
                }
                catch (Exception ex) { sb.AppendLine($"Erro ao listar arquivos: {ex.Message}"); }

                // Recursive listing of subdirectories (1 level)
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(dir))
                            {
                                var fi = new FileInfo(file);
                                string sizeStr = fi.Length > 1024 * 1024
                                    ? $"{fi.Length / (1024.0 * 1024.0):F1} MB"
                                    : $"{fi.Length / 1024.0:F1} KB";
                                string sysMark = fi.Extension.Equals(".sys", StringComparison.OrdinalIgnoreCase) ? " [DRIVER]" : "";
                                sb.AppendLine($"  {fi.Name} ({sizeStr}){sysMark}");
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Erro ao listar conteudo: {ex.Message}";
            }
        }

        private string? _pendingDeletePath;
        private bool _retryDeleteAfterRelease;

        private async void BtnQuickRelease_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtQuickPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path) || _quickResults.Count == 0) return;

            var selected = _quickResults.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtQuickDetail.Text = "Nenhum processo selecionado.";
                return;
            }

            Logger.Log($"[FORCE STOP UI] === Liberar Selecionados para: {path}");
            foreach (var s in selected)
                Logger.Log($"[FORCE STOP UI] Selecionado: {s.DisplayLabel} | {s.DetailLabel}");

            BtnQuickRelease.IsEnabled = false;
            BtnQuickRelease.Content = "⏳ Liberando...";
            TxtQuickDetail.Text = "Liberando processos...";

            try
            {
                var result = await Task.Run(() => ForceStopUnlockService.Unlock(path, selected));

                if (result.Success)
                {
                    TxtQuickResult.Text = $"✅ {result.Message}";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                }
                else
                {
                    TxtQuickResult.Text = $"⚠️ {result.Message}";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                }

                TxtQuickDetail.Text = result.Errors.Count > 0
                    ? "Erros: " + string.Join("; ", result.Errors.Take(3))
                    : "Re-analisando...";

                // Re-analyze to verify
                await Task.Delay(500);
                _quickResults = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));

                if (_quickResults.Count == 0)
                {
                    TxtQuickResult.Text = "✅ Liberado com sucesso!";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    QuickProcessList.ItemsSource = null;
                    BtnQuickRelease.Visibility = Visibility.Collapsed;

                    // Retry the delete if it was triggered by Tentar Deletar
                    if (_retryDeleteAfterRelease && !string.IsNullOrEmpty(_pendingDeletePath))
                    {
                        TxtQuickDetail.Text = "Re-tentando delete...";
                        try
                        {
                            var (retryOk, retryErr) = await Task.Run(() => ForceDeleteViaCmd(_pendingDeletePath));
                            if (retryOk)
                            {
                                TxtQuickResult.Text = "Arquivo/pasta deletado com sucesso!";
                                TxtQuickResult.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 220, 100));
                                TxtQuickDetail.Text = "";
                            }
                            else
                            {
                                TxtQuickResult.Text = "Liberado mas delete ainda falhou:";
                                TxtQuickResult.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 200, 100));
                                TxtQuickDetail.Text = retryErr;
                            }

                        }
                        catch (Exception deleteEx)
                        {
                            TxtQuickResult.Text = "⚠️ Liberado mas delete ainda falhou:";
                            TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                            TxtQuickDetail.Text = deleteEx.Message;
                        }
                        _retryDeleteAfterRelease = false;
                        _pendingDeletePath = null;
                    }
                    else
                    {
                        TxtQuickDetail.Text = "Nenhum processo bloqueador restante.";
                    }
                }
                else
                {
                    TxtQuickDetail.Text = $"Ainda bloqueado: {_quickResults.Count} processo(s) restante(s).";
                    QuickProcessList.ItemsSource = _quickResults;
                }
            }
            catch (Exception ex)
            {
                TxtQuickResult.Text = $"❌ Erro: {ex.Message}";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            }
            finally
            {
                BtnQuickRelease.IsEnabled = true;
                BtnQuickRelease.Content = "⚡ Liberar Selecionados";
            }
        }

        private void BtnOpenWindow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new KitLugia.GUI.Windows.ForceStopUnlockWindow
                {
                    Owner = Window.GetWindow(this)
                };

                // Pass the path from the quick scan if available
                string path = TxtQuickPath?.Text?.Trim();
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    // Set the path in the window after it loads
                    win.Loaded += (s, ev) =>
                    {
                        var txtPath = win.FindName("TxtPath") as TextBox;
                        if (txtPath != null)
                            txtPath.Text = path;
                    };
                }

                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não foi possível abrir a janela: {ex.Message}",
                    "Force Stop Unlock", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ─── Inline Context Menu Manager ──────────────────────────────

        private List<ForceStopUnlockService.ContextMenuEntry> _menuEntries = new();

        private async void BtnScanMenu_Click(object sender, RoutedEventArgs e)
        {
            TxtMenuEmpty.Text = "Escaneando...";
            TxtMenuEmpty.Visibility = Visibility.Visible;
            MenuEntryList.Visibility = Visibility.Collapsed;
            MenuFilterBar.Visibility = Visibility.Collapsed;
            MenuActionBar.Visibility = Visibility.Collapsed;

            try
            {
                _menuEntries = await System.Threading.Tasks.Task.Run(() =>
                    ForceStopUnlockService.ScanContextMenuEntries());

                ApplyMenuFilter();
                MenuFilterBar.Visibility = Visibility.Visible;
                MenuActionBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                TxtMenuEmpty.Text = $"Erro: {ex.Message}";
            }
        }

        private void MenuFilter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyMenuFilter();
        }

        private void ApplyMenuFilter()
        {
            var filtered = _menuEntries.AsEnumerable();

            if (RdMenuKit?.IsChecked == true)
                filtered = filtered.Where(e => e.IsKitEntry);
            else if (RdMenuThird?.IsChecked == true)
                filtered = filtered.Where(e => !e.IsKitEntry);

            var list = filtered.ToList();
            MenuEntryList.ItemsSource = list;
            TxtMenuCount.Text = $"{list.Count} entrada(s)";

            if (list.Count == 0)
            {
                TxtMenuEmpty.Text = "Nenhuma entrada encontrada com este filtro";
                TxtMenuEmpty.Visibility = Visibility.Visible;
                MenuEntryList.Visibility = Visibility.Collapsed;
            }
            else
            {
                TxtMenuEmpty.Visibility = Visibility.Collapsed;
                MenuEntryList.Visibility = Visibility.Visible;
            }
        }

        private async void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _menuEntries.Where(e => e.IsSelected).ToList();
            if (selected.Count == 0) return;

            var names = string.Join("\n", selected.Take(10).Select(s => $"\u2022 {s.Label} ({s.Root})"));
            if (selected.Count > 10) names += $"\n... e mais {selected.Count - 10}";

            var confirm = MessageBox.Show(
                $"Remover {selected.Count} entrada(s)?\n\n{names}",
                "Gerenciador de Menu de Contexto",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var (removed, failed) = await System.Threading.Tasks.Task.Run(() =>
                    ForceStopUnlockService.RemoveSelectedEntries(selected));

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (removed > 0)
                        mw.ShowSuccess("MENU DE CONTEXTO", $"{removed} entrada(s) removida(s).{(failed > 0 ? $" {failed} falhou." : "")}");
                    else
                        mw.ShowInfo("MENU DE CONTEXTO", "Nenhuma entrada foi removida.");
                }

                // Rescan
                await System.Threading.Tasks.Task.Run(() =>
                {
                    _menuEntries = ForceStopUnlockService.ScanContextMenuEntries();
                });
                ApplyMenuFilter();
            }
            catch (Exception ex)
            {
                TxtMenuEmpty.Text = $"Erro: {ex.Message}";
            }
        }
    }
}
