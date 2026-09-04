using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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
        private int _initialTab = 0;

        public ForceStopUnlockPage(int initialTab = 0)
        {
            _initialTab = initialTab;
            InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                if (MainTabs != null) MainTabs.SelectedIndex = _initialTab;
                UpdateTopToggle();
                await RefreshStatus();
            };
            this.Unloaded += (s, e) => Cleanup();
        }

        public void Cleanup()
        {
            this.DataContext = null;
        }

        /// <summary>
        /// PROVA-DE-TUDO: take ownership / force stop exigem Administrador (SeTakeOwnership/
        /// SeDebugPrivilege). Se o Kit está sem admin, oferece relançar ELEVADO com a mesma
        /// operação (--takeown/--unlock) e não executa aqui (a instância elevada resolve).
        /// Retorna true se deve prosseguir nesta instância.
        /// </summary>
        private bool EnsureElevatedForFileOp(string path, bool isTakeOwn)
        {
            if (SystemUtils.IsRunningAsAdministrator()) return true;

            var ask = MessageBox.Show(
                "O KitLugia está rodando SEM privilégios de administrador.\n\n" +
                "Take Ownership / Force Stop podem falhar em arquivos protegidos " +
                "(ex: Windows.old, TrustedInstaller, processos de outros usuários).\n\n" +
                "Relançar o KitLugia elevado (UAC) para executar agora?",
                "KitLugia — Privilégios de Administrador",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return false;

            try
            {
                string args = (isTakeOwn ? "--takeown " : "--unlock ") + $"\"{path}\"";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Environment.ProcessPath ?? typeof(Program).Assembly.Location)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = args
                });
                Logger.Log($"[FILE OPS] Relançado elevado: {args}");
                return false; // instância elevada fará a operação
            }
            catch (Exception ex)
            {
                Logger.Log($"[FILE OPS] Falha ao relançar elevado: {ex.Message}");
                MessageBox.Show($"Não foi possível relançar elevado: {ex.Message}",
                    "KitLugia", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true; // tenta mesmo assim (mostrará os erros reais)
            }
        }

        private void UpdateTopToggle()
        {
            if (BtnModeUnlock == null || BtnModeTakeOwn == null || MainTabs == null) return;
            bool isUnlock = MainTabs.SelectedIndex == 0;
            BtnModeUnlock.Background = isUnlock ? new SolidColorBrush(Color.FromRgb(0x33, 0x55, 0xAA)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x32));
            BtnModeUnlock.Foreground = isUnlock ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            BtnModeTakeOwn.Background = !isUnlock ? new SolidColorBrush(Color.FromRgb(0xAA, 0x99, 0x33)) : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x32));
            BtnModeTakeOwn.Foreground = !isUnlock ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        }

        private void BtnModeUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs != null) MainTabs.SelectedIndex = 0;
        }

        private void BtnModeTakeOwn_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabs != null) MainTabs.SelectedIndex = 1;
        }

        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateTopToggle();
            // Sync path between tabs so the same path is available in both modes
            try
            {
                if (TxtQuickPath != null && TxtTakeOwnPath != null)
                {
                    if (MainTabs.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(TxtQuickPath.Text) && string.IsNullOrWhiteSpace(TxtTakeOwnPath.Text))
                        TxtTakeOwnPath.Text = TxtQuickPath.Text;
                    else if (MainTabs.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(TxtTakeOwnPath.Text) && string.IsNullOrWhiteSpace(TxtQuickPath.Text))
                        TxtQuickPath.Text = TxtTakeOwnPath.Text;
                }
            }
            catch { }
        }

        private async Task RefreshStatus()
        {
            _isLoading = true;
            try
            {
                await Task.Run(() =>
                {
                    bool isAdded = SystemTweaks.IsForceStopUnlockAdded();
                    bool isTakeOwnAdded = SystemTweaks.IsTakeOwnershipKitAdded();
                    string handlePath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "External", "ForceStopUnlock", "handle64.exe");
                    bool handleExists = File.Exists(handlePath);

                    Dispatcher.Invoke(() =>
                    {
                        if (ChkEnable != null)
                            ChkEnable.IsChecked = isAdded;
                        if (TxtMenuStatus != null)
                        {
                            TxtMenuStatus.Text = isAdded ? "✅ Ativo no menu de contexto" : "❌ Inativo";
                            TxtMenuStatus.Foreground = isAdded ? Brushes.LightGreen : Brushes.Gray;
                        }

                        if (ChkTakeOwnEnable != null)
                            ChkTakeOwnEnable.IsChecked = isTakeOwnAdded;
                        if (TxtTakeOwnMenuStatus != null)
                        {
                            TxtTakeOwnMenuStatus.Text = isTakeOwnAdded ? "✅ Ativo no menu de contexto" : "❌ Inativo";
                            TxtTakeOwnMenuStatus.Foreground = isTakeOwnAdded ? Brushes.LightGreen : Brushes.Gray;
                        }

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
                    // Persiste para o startup reaplicar com a config mais recente
                    SystemTweaks.SaveContextMenuPref("forcestopunlock", target);
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

        private async void ChkTakeOwnEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool target = ChkTakeOwnEnable.IsChecked == true;
                await Task.Run(() =>
                {
                    if (target) SystemTweaks.AddTakeOwnershipKit();
                    else SystemTweaks.RemoveTakeOwnershipKit();
                    // Persiste para o startup reaplicar com a config mais recente
                    SystemTweaks.SaveContextMenuPref("kittakeown", target);
                });

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (target)
                        mw.ShowSuccess("TAKE OWNERSHIP", "Opção 'Take Ownership (KitLugia)' adicionada ao menu de contexto.");
                    else
                        mw.ShowInfo("TAKE OWNERSHIP", "Opção removida do menu de contexto.");
                }

                await RefreshStatus();
            }
            catch { Logger.LogWarning("TakeOwnership", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        /// <summary>
        /// Called by MainWindow when user right-clicks a file/folder for unlock.
        /// Pre-fills the path and triggers analysis automatically (aba 0).
        /// </summary>
        public void PreFillAndAnalyze(string path)
        {
            if (MainTabs != null) MainTabs.SelectedIndex = 0;
            if (TxtQuickPath != null) TxtQuickPath.Text = path;
            if (TxtTakeOwnPath != null) TxtTakeOwnPath.Text = path;
            if (BtnQuickAnalyze != null)
            {
                BtnQuickAnalyze.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent, BtnQuickAnalyze));
            }
        }

        /// <summary>
        /// Called by MainWindow / IPC when --takeown arrives. Abre na aba 1 e pré-preenche.
        /// </summary>
        public void PreFillAndTakeOwn(string path)
        {
            if (MainTabs != null) MainTabs.SelectedIndex = 1;
            if (TxtTakeOwnPath != null) TxtTakeOwnPath.Text = path;
            if (TxtQuickPath != null) TxtQuickPath.Text = path;
            // Auto-analisa permissões
            if (BtnTakeOwnAnalyze != null)
            {
                BtnTakeOwnAnalyze.RaiseEvent(new RoutedEventArgs(
                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent, BtnTakeOwnAnalyze));
            }
        }

        // ─── Take Ownership logic ────────────────────────────────

        private async void BtnTakeOwnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            string path = (TxtTakeOwnPath?.Text ?? TxtQuickPath?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(path))
            {
                TakeOwnResultPanel.Visibility = Visibility.Visible;
                TxtTakeOwnResult.Text = "❌ Cole um caminho primeiro.";
                TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TxtTakeOwnDetail.Text = "";
                return;
            }

            // Sync to other box
            if (TxtQuickPath != null) TxtQuickPath.Text = path;
            if (TxtTakeOwnPath != null) TxtTakeOwnPath.Text = path;

            TakeOwnResultPanel.Visibility = Visibility.Visible;
            TxtTakeOwnResult.Text = "🔍 Verificando permissões...";
            TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
            TxtTakeOwnDetail.Text = "";
            TakeOwnProgress.Visibility = Visibility.Visible;

            try
            {
                var info = await Task.Run(() => GetAclSummary(path));
                TakeOwnProgress.Visibility = Visibility.Collapsed;

                if (info.Exists)
                {
                    TxtTakeOwnResult.Text = $"📋 {info.Name} — dono atual: {info.Owner}";
                    TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                    TxtTakeOwnDetail.Text = info.Detail + "\n\nClique em 'Assumir' para tornar-se dono (Administradores + FullControl).";
                }
                else
                {
                    TxtTakeOwnResult.Text = "❌ Caminho não encontrado.";
                    TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                    TxtTakeOwnDetail.Text = path;
                }
            }
            catch (Exception ex)
            {
                TakeOwnProgress.Visibility = Visibility.Collapsed;
                TxtTakeOwnResult.Text = $"❌ Erro: {ex.Message}";
                TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            }
        }

        private async void BtnTakeOwnExecute_Click(object sender, RoutedEventArgs e)
        {
            string path = (TxtTakeOwnPath?.Text ?? TxtQuickPath?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(path))
            {
                TakeOwnResultPanel.Visibility = Visibility.Visible;
                TxtTakeOwnResult.Text = "❌ Cole um caminho primeiro.";
                TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                return;
            }

            if (!EnsureElevatedForFileOp(path, isTakeOwn: true)) return;

            bool recursive = ChkRecursive?.IsChecked == true;
            bool grantFullControl = ChkTakeOwnFullControl?.IsChecked == true;
            bool isDir = Directory.Exists(path);
            if (!isDir)
            {
                int probe = FileTakeOwnership.ProbePath(path, out bool pExists, out bool pIsDir, out _);
                isDir = pExists ? pIsDir : false;
            }

            BtnTakeOwnExecute.IsEnabled = false;
            BtnTakeOwnExecute.Content = "⏳ Assumindo...";
            TakeOwnResultPanel.Visibility = Visibility.Visible;
            TakeOwnProgress.Visibility = Visibility.Visible;
            TakeOwnProgress.IsIndeterminate = true;
            TakeOwnProgress.Value = 0;
            TxtTakeOwnProgress.Visibility = Visibility.Visible;
            TxtTakeOwnProgress.Text = "Coletando arquivos...";
            TxtTakeOwnCurrentFile.Visibility = Visibility.Collapsed;
            TxtTakeOwnCurrentFile.Text = "";
            TxtTakeOwnResult.Text = $"👑 Assumindo {(isDir ? "pasta" : "arquivo")} {(recursive && isDir ? "(recursivo)" : "")} {(grantFullControl ? "(completo)" : "(rápido)")}...";
            TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0));
            TxtTakeOwnDetail.Text = $"{path}\nIsso pode levar alguns segundos em pastas grandes.";

            try
            {
                var result = await Task.Run(() => FileTakeOwnership.TakeOwn(path, recursive, (done, total, cur) =>
                {
                    // throttling: Dispatcher a cada 10 ou nos 3 primeiros
                    Dispatcher.Invoke(() =>
                    {
                        TakeOwnProgress.IsIndeterminate = false;
                        TakeOwnProgress.Maximum = Math.Max(1, total);
                        TakeOwnProgress.Value = done;
                        TxtTakeOwnProgress.Text = $"{done} / {total}  ({done * 100 / Math.Max(1, total)}%)";
                        if (!string.IsNullOrEmpty(cur))
                        {
                            TxtTakeOwnCurrentFile.Visibility = Visibility.Visible;
                            TxtTakeOwnCurrentFile.Text = "→ " + Path.GetFileName(cur);
                        }
                    });
                }, grantFullControl));
                TakeOwnProgress.Visibility = Visibility.Collapsed;

                if (result.Ok)
                {
                    TxtTakeOwnResult.Text = $"✅ {result.Success}/{result.Total} item(ns) agora são seus!";
                    TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    TxtTakeOwnDetail.Text = isDir && recursive ? "Recursivo — incluiu todas as subpastas e arquivos." : "Pronto para editar/deletar.";
                    if (result.FallbackUsed)
                        TxtTakeOwnDetail.Text += "\n" + result.FallbackMessage;
                    TxtTakeOwnProgress.Visibility = Visibility.Collapsed;
                    TxtTakeOwnCurrentFile.Visibility = Visibility.Collapsed;

                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("TAKE OWNERSHIP", $"✅ {Path.GetFileName(path)}: {result.Success} item(ns) assumidos.");
                }
                else
                {
                    TxtTakeOwnResult.Text = $"⚠️ {result.Success}/{result.Total} ok, {result.Failed} falha(s)";
                    TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                    TxtTakeOwnDetail.Text = string.Join("\n", result.Errors.Take(5));
                    if (result.FallbackUsed)
                        TxtTakeOwnDetail.Text += "\n" + result.FallbackMessage;
                    TxtTakeOwnProgress.Visibility = Visibility.Collapsed;
                    TxtTakeOwnCurrentFile.Visibility = Visibility.Collapsed;

                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowError("TAKE OWNERSHIP", $"{Path.GetFileName(path)}: {result.Failed} falha(s).\n" + string.Join("\n", result.Errors.Take(3)));
                }
            }
            catch (Exception ex)
            {
                TakeOwnProgress.Visibility = Visibility.Collapsed;
                TxtTakeOwnResult.Text = $"❌ Erro: {ex.Message}";
                TxtTakeOwnResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120));
                TxtTakeOwnDetail.Text = "";
            }
            finally
            {
                BtnTakeOwnExecute.IsEnabled = true;
                BtnTakeOwnExecute.Content = "👑 Assumir";
                // mantém progresso visível no sucesso/erro, esconde só no catch
            }
        }

        private static (bool Exists, string Name, string Owner, string Detail) GetAclSummary(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    // .NET Exists retorna FALSE em caminhos com ACL negada (Windows.old) —
                    // o probe nativo distingue "negado" de "não existe".
                    int probe = FileTakeOwnership.ProbePath(path, out bool pExists, out bool pIsDir, out int errCode);
                    if (!pExists && probe != 5 && probe != 21)
                        return (false, "", "", "");
                    return (true,
                        Path.GetFileName(path.TrimEnd('\\')) ?? path,
                        "(acesso negado)",
                        $"A pasta/arquivo EXISTE mas a ACL nega leitura (erro {errCode}) — dono provável: TrustedInstaller/System (ex: Windows.old).\nClique em 'Assumir': o Kit usa SeTakeOwnershipPrivilege + fallback clássico takeown/icacls.");
                }

                string name = Path.GetFileName(path.TrimEnd('\\')) ?? path;
                string owner = "?";
                string detail = "";

                try
                {
                    if (Directory.Exists(path))
                    {
                        var di = new DirectoryInfo(path);
                        var sec = di.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access | AccessControlSections.Group);
                        owner = sec.GetOwner(typeof(NTAccount))?.ToString() ?? "?";
                        var rules = sec.GetAccessRules(true, false, typeof(NTAccount));
                        detail = $"Tipo: Pasta  |  Regras: {rules.Count}  |  Dono: {owner}";
                    }
                    else
                    {
                        var fi = new FileInfo(path);
                        var sec = fi.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access | AccessControlSections.Group);
                        owner = sec.GetOwner(typeof(NTAccount))?.ToString() ?? "?";
                        var rules = sec.GetAccessRules(true, false, typeof(NTAccount));
                        detail = $"Tipo: Arquivo  |  Tamanho: {fi.Length} bytes  |  Regras: {rules.Count}  |  Dono: {owner}";
                    }
                }
                catch (Exception ex)
                {
                    detail = $"Não foi possível ler ACL: {ex.Message}";
                }

                return (true, name, owner, detail);
            }
            catch { return (false, "", "", ""); }
        }

        private List<KitLugia.Core.BlockingProcessInfo> _quickResults = new();

        private async void BtnQuickAnalyze_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtQuickPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path)) path = TxtTakeOwnPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            // sync
            if (TxtTakeOwnPath != null) TxtTakeOwnPath.Text = path;

            Logger.Log($"[FORCE STOP UI] === Analisar clicado para: {path}");
            Logger.Log($"[FORCE STOP UI] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            string folderContents = ListFolderContents(path);
            Logger.Log($"[FORCE STOP UI] Conteudo do caminho:\n{folderContents}");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                int probe = FileTakeOwnership.ProbePath(path, out bool pExists, out bool pIsDir, out int errCode);
                if (!pExists && probe != 5 && probe != 21)
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
                if (pExists)
                    Logger.Log($"[FORCE STOP UI] Caminho existe mas ACL nega leitura (erro {errCode}) — continuando scan nativo.");
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
            if (string.IsNullOrEmpty(path)) path = TxtTakeOwnPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path)) return;

            Logger.Log($"[FORCE STOP UI] === Tentar Deletar clicado para: {path}");
            Logger.Log($"[FORCE STOP UI] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            if (!EnsureElevatedForFileOp(path, isTakeOwn: false)) return;

            string folderContents = ListFolderContents(path);
            Logger.Log($"[FORCE STOP UI] Conteudo do caminho:\n{folderContents}");

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                int probe = FileTakeOwnership.ProbePath(path, out bool pExists, out bool pIsDir, out int errCode);
                if (!pExists && probe != 5 && probe != 21)
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
                if (pExists)
                    Logger.Log($"[FORCE STOP UI] Caminho existe mas ACL nega leitura (erro {errCode}) — continuando.");
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
                Logger.Log($"[FORCE STOP UI] Executando ForceDeleteViaCmd...");
                var (deleted, errorMsg) = await Task.Run(() => ForceDeleteViaCmd(path));
                Logger.Log($"[FORCE STOP UI] Resultado do delete: Success={deleted}, Error={errorMsg}");

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

                TxtQuickResult.Text = "\U0001f512 Arquivo bloqueado - identificando bloqueadores...";
                TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 100));
                TxtQuickDetail.Text = $"Windows nao conseguiu deletar: {errorMsg}";
                await Task.Delay(300);
                Logger.Log($"[FORCE STOP UI] Chamando FindBlockingProcesses...");
                _quickResults = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));
                Logger.Log($"[FORCE STOP UI] FindBlockingProcesses retornou: {_quickResults.Count} resultado(s)");

                if (_quickResults.Count == 0)
                {
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

        private static (bool Success, string Error) ForceDeleteViaCmd(string path)
        {
            Logger.Log($"[FORCE DELETE] Iniciado para: {path}");
            Logger.Log($"[FORCE DELETE] Admin: {SystemUtils.IsRunningAsAdministrator()}");

            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Logger.Log($"[FORCE DELETE] Arquivo/pasta ja nao existe.");
                    return (true, "");
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

                proc.WaitForExit(10000);
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                Logger.Log($"[FORCE DELETE] cmd.exe exit code: {proc.ExitCode}");
                if (!string.IsNullOrEmpty(stdout))
                    Logger.Log($"[FORCE DELETE] stdout: {stdout.Trim()}");
                if (!string.IsNullOrEmpty(stderr))
                    Logger.Log($"[FORCE DELETE] stderr: {stderr.Trim()}");

                bool gone = !File.Exists(path) && !Directory.Exists(path);
                Logger.Log($"[FORCE DELETE] Arquivo existe apos comando: {!gone}");
                if (gone) return (true, "");

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

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        sb.AppendLine($"[DIR]  {dirInfo.Name}/");
                    }
                }
                catch (Exception ex) { sb.AppendLine($"Erro ao listar pastas: {ex.Message}"); }

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
            if (string.IsNullOrEmpty(path)) path = TxtTakeOwnPath?.Text?.Trim();
            if (string.IsNullOrEmpty(path) || _quickResults.Count == 0) return;

            var selected = _quickResults.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtQuickDetail.Text = "Nenhum processo selecionado.";
                return;
            }

            if (!EnsureElevatedForFileOp(path, isTakeOwn: false)) return;

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

                await Task.Delay(500);
                _quickResults = await Task.Run(() => ForceStopUnlockService.FindBlockingProcesses(path));

                if (_quickResults.Count == 0)
                {
                    TxtQuickResult.Text = "✅ Liberado com sucesso!";
                    TxtQuickResult.Foreground = new SolidColorBrush(Color.FromRgb(100, 220, 100));
                    QuickProcessList.ItemsSource = null;
                    BtnQuickRelease.Visibility = Visibility.Collapsed;

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
                string path = TxtQuickPath?.Text?.Trim();
                if (string.IsNullOrEmpty(path)) path = TxtTakeOwnPath?.Text?.Trim();
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
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
