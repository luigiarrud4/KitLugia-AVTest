using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using KitLugia.GUI.Extensions;
using KitLugia.GUI.Helpers;

using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    public partial class CleanupPage : Page
    {
        private bool _isCleaning = false;
        private bool _isCleanupOperation;
        private List<KitLugia.Core.RegistryCleaner.RegistryIssue>? _registryIssues;
        private List<RegistryIssueViewModel> _registryIssueViewModels = new();

        public CleanupPage()
        {
            InitializeComponent();
            this.Unloaded += CleanupPage_Unloaded;
            this.Loaded += CleanupPage_Loaded;
            InitEvidenceList();
        }

        public void Cleanup()
        {
            TxtLog?.Clear();
            this.Unloaded -= CleanupPage_Unloaded;
            this.Loaded -= CleanupPage_Loaded;
            this.DataContext = null;
        }

        private void CleanupPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        private async void CleanupPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Detecta navegadores automaticamente ao abrir a página
            await DetectBrowsersAsync();
        }

        private void AddLog(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtLog.AppendText($"\n[{time}] {message}");
            LogScroller.ScrollToBottom();
        }

        // ─── Limpeza de Arquivos Temporários ────────────────────────────────────

        private async void BtnCleanTemp_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpeza de Temporários...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Temporários", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => Toolbox.CleanTemporaryFiles(buf.OnNext));
                buf.Flush();
                foreach (var line in result.Log) AddLog(line);
                AddLog($"✅ Concluído. {result.TotalBytesFreed / 1024 / 1024:N1} MB liberados.");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCleanUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpeza de Windows Update...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Windows Update", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => Toolbox.CleanWindowsUpdateCache(buf.OnNext));
                buf.Flush();
                foreach (var line in result.Log) AddLog(line);
                AddLog($"✅ Concluído. {result.TotalBytesFreed / 1024 / 1024:N1} MB liberados.");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCleanShaders_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpeza de Cache GPU...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Cache GPU", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => Toolbox.CleanShaderCaches(buf.OnNext));
                buf.Flush();
                foreach (var line in result.Log) AddLog(line);
                AddLog($"✅ Concluído. {result.TotalBytesFreed / 1024 / 1024:N1} MB liberados.");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnFullClean_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "=== INICIANDO LIMPEZA COMPLETA ===";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza Completa", "Cleanup");
            try
            {
                // Limpeza padrão
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => Toolbox.RunFullCleanup(buf.OnNext));
                buf.Flush();
                foreach (var line in result.Log) AddLog(line);

                AddLog("Cache de navegadores não entra na limpeza total. Use a seção opcional se quiser limpar.");

                long totalBytes = result.TotalBytesFreed;
                AddLog("==================================");
                AddLog($"✅ TOTAL LIBERADO: {totalBytes / 1024 / 1024:N1} MB");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, $"{totalBytes / 1024 / 1024:N1} MB liberados");
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCompactOS_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                var mw = System.Windows.Application.Current.MainWindow as MainWindow;
                if (mw != null && !await mw.ShowConfirmationDialog(
                    "CompactOS comprime arquivos do Windows e pode demorar bastante.\n\nDeseja iniciar agora?"))
                {
                    return;
                }

                await Task.Run(() => Toolbox.CompactOS());
                AddLog("Iniciado processo de CompactOS em janela externa.");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnCompactOS_Click", ex.Message);
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        // ─── Limpeza de Lixeira, Prefetch, Thumbnails ───────────────────────────

        private async void BtnCleanRecycleBin_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpando Lixeira...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Lixeira", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => Toolbox.CleanRecycleBin(buf.OnNext));
                buf.Flush();
                AddLog($"✅ {result.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, result.Message);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCleanPrefetch_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpando Prefetch...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Prefetch", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() =>
                {
                    string prefetchPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                    return Toolbox.CleanDirectory(prefetchPath, "Prefetch", buf.OnNext);
                });
                buf.Flush();
                AddLog($"✅ Prefetch: {result.BytesFreed / 1024 / 1024:N1} MB liberados ({result.FilesDeleted} arquivos).");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCleanThumbnails_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpando Cache de Thumbnails...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Thumbnails", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() =>
                {
                    string thumbPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft", "Windows", "Explorer");
                    return Toolbox.CleanDirectory(thumbPath, "Thumbnails", buf.OnNext);
                });
                buf.Flush();
                AddLog($"✅ Thumbnails: {result.BytesFreed / 1024 / 1024:N1} MB liberados.");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        // ─── Cache de Navegadores ────────────────────────────────────────────────

        private async void BtnDetectBrowsers_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                await DetectBrowsersAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDetectBrowsers_Click", ex.Message);
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        private async Task DetectBrowsersAsync()
        {
            TxtBrowserCacheTotal.Text = "Detectando navegadores...";
            TxtNoBrowsers.Visibility = Visibility.Collapsed;

            var browsers = await Task.Run(() => BrowserCacheManager.GetDetectedBrowsers());

            if (browsers.Count == 0)
            {
                BrowserList.ItemsSource = null;
                TxtNoBrowsers.Visibility = Visibility.Visible;
                TxtBrowserCacheTotal.Text = "Nenhum navegador detectado.";
                return;
            }

            BrowserList.ItemsSource = browsers;
            TxtNoBrowsers.Visibility = Visibility.Collapsed;

            long total = browsers.Sum(b => b.CacheSizeBytes);
            TxtBrowserCacheTotal.Text = $"{browsers.Count} navegador(es) detectado(s) — {FormatBytes(total)} de cache";
        }

        private async void BtnCleanAllBrowsers_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = "[Iniciando] Limpeza de cache de navegadores...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpeza de Navegadores", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => BrowserCacheManager.CleanAllBrowserCaches(buf.OnNext));
                buf.Flush();

                foreach (var br in result.Results)
                    AddLog($"  {br.BrowserName}: {br.BytesFreed / 1024 / 1024:N1} MB liberados");

                AddLog($"✅ Total: {result.TotalBytes / 1024 / 1024:N1} MB liberados de {result.Results.Count} navegador(es).");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);

                // Atualiza a lista
                await DetectBrowsersAsync();
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        private async void BtnCleanSingleBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? browserName = btn.Tag?.ToString();
            if (string.IsNullOrEmpty(browserName)) return;

            if (_isCleaning) return;
            _isCleaning = true;
            TxtLog.Text = $"[Iniciando] Limpando cache do {browserName}...";

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Limpeza do {browserName}", "Cleanup");
            try
            {
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() => BrowserCacheManager.CleanBrowserCache(browserName, buf.OnNext));
                buf.Flush();
                AddLog($"✅ {result.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Found, result.Message);

                await DetectBrowsersAsync();
            }
            catch (Exception ex)
            {
                AddLog($"❌ ERRO: {ex.Message}");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isCleaning = false;
            }
        }

        // ─── Limpador de Registro ────────────────────────────────────────────────

        private async void BtnScanRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                TxtRegistryScanResult.Text = "Escaneando registro...";
                TxtRegistryScanResult.Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                BtnCleanRegistry.IsEnabled = false;
                RegistryIssuesBorder.Visibility = Visibility.Collapsed;

                var buf = new LogBuffer(AddLog);
                _registryIssues = await Task.Run(() =>
                    KitLugia.Core.RegistryCleaner.ScanForIssues(buf.OnNext));
                buf.Flush();

                int cleanableCount = _registryIssues.Count(KitLugia.Core.RegistryCleaner.CanCleanIssue);
                int blockedCount = _registryIssues.Count - cleanableCount;
                int safeCount = cleanableCount;
                int unsafeCount = blockedCount;

                TxtRegistryScanResult.Text = $"{_registryIssues.Count} item(ns) encontrado(s) - {safeCount} automático(s), {unsafeCount} para revisão manual.";
                TxtRegistryScanResult.Text = $"{_registryIssues.Count} item(ns) encontrado(s) - {cleanableCount} pode(m) ser selecionado(s), {blockedCount} bloqueado(s)/auditoria.";
                TxtRegistryScanResult.Foreground = _registryIssues.Count > 0
                    ? new SolidColorBrush(Color.FromRgb(255, 165, 0))
                    : new SolidColorBrush(Color.FromRgb(76, 175, 80));

                // Exibe lista de problemas
                _registryIssueViewModels = _registryIssues.Select(i => new RegistryIssueViewModel(i)).ToList();
                RegistryIssuesList.ItemsSource = _registryIssueViewModels;
                RegistryIssuesBorder.Visibility = _registryIssueViewModels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                BtnCleanRegistry.IsEnabled = false;
                BtnCopyRegistryIssues.IsEnabled = _registryIssues.Count > 0;
                AddLog($"Scan de registro: {_registryIssues.Count} itens encontrados ({cleanableCount} selecionaveis).");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnScanRegistry_Click", ex.Message);
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        private async void BtnCleanRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (_registryIssues == null || _registryIssues.Count == 0) return;
            var selectedIssues = _registryIssueViewModels
                .Where(i => i.IsSelected && i.CanClean)
                .Select(i => i.Issue)
                .ToList();

            if (selectedIssues.Count == 0)
            {
                AddLog("Selecione pelo menos um item permitido antes de limpar.");
                return;
            }

            var mw = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mw != null)
            {
                int safeCount = selectedIssues.Count;
                if (!await mw.ShowConfirmationDialog($"Remover {safeCount} entrada(s) segura(s) do registro?\n\nEntradas marcadas como 'não seguras' serão ignoradas."))
                    return;
            }

            BtnCleanRegistry.IsEnabled = false;
            TxtLog.Text = "[Iniciando] Limpeza de registro...";

            var buf = new LogBuffer(AddLog);
            var result = await Task.Run(() =>
                KitLugia.Core.RegistryCleaner.CleanSelectedIssues(selectedIssues, buf.OnNext));
            buf.Flush();

            foreach (var line in result.Log) AddLog(line);
            AddLog($"✅ Registro: {result.IssuesCleaned} entradas removidas, {result.IssuesSkipped} ignoradas.");

            TxtRegistryScanResult.Text = $"Limpeza concluída: {result.IssuesCleaned} removidas.";
            TxtRegistryScanResult.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            _registryIssues = null;
            RegistryIssuesBorder.Visibility = Visibility.Collapsed;
            BtnCopyRegistryIssues.IsEnabled = false;
        }

        private void BtnCopyRegistryIssues_Click(object sender, RoutedEventArgs e)
        {
            if (_registryIssues == null || _registryIssues.Count == 0)
            {
                AddLog("Nenhum item de registro para copiar.");
                return;
            }

            string report = KitLugia.Core.RegistryCleaner.FormatIssuesForResearch(_registryIssues);
            System.Windows.Clipboard.SetText(report);
            AddLog($"📋 {_registryIssues.Count} item(ns) de registro copiado(s) para a área de transferência.");
        }

        private RegistryIssueViewModel? GetIssueFromSender(object sender)
        {
            if (sender is System.Windows.Controls.MenuItem mi && mi.DataContext is RegistryIssueViewModel vm)
                return vm;
            return null;
        }

        private async void MenuRemoveSingleIssue_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                var vm = GetIssueFromSender(sender);
                if (vm == null) return;

                var mw = Application.Current.MainWindow as MainWindow;
                if (mw == null) return;

                var issue = vm.Issue;
                bool canClean = KitLugia.Core.RegistryCleaner.CanCleanIssue(issue);

                if (!canClean && !await mw.ShowConfirmationDialog(
                    $"⚠️ Este item está marcado como BLOQUEADO (não seguro).\n\n{issue.Description}\n\n{issue.RegistryPath}\n\nTem certeza que deseja remover manualmente?"))
                    return;

                if (canClean && !await mw.ShowConfirmationDialog($"Remover:\n{issue.Description}?"))
                    return;

                AddLog($"Removendo manualmente: {issue.Description}...");
                var buf = new LogBuffer(AddLog);
                var result = await Task.Run(() =>
                    KitLugia.Core.RegistryCleaner.CleanSelectedIssues(new[] { issue }, buf.OnNext));
                buf.Flush();

                foreach (var line in result.Log) AddLog(line);
                AddLog($"✅ {result.IssuesCleaned} removido(s).");

                // Re-scan
                BtnScanRegistry_Click(null!, null!);
            }
            catch (Exception ex)
            {
                Logger.LogError("MenuRemoveSingleIssue_Click", ex.Message);
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        private void MenuCopyIssuePath_Click(object sender, RoutedEventArgs e)
        {
            var vm = GetIssueFromSender(sender);
            if (vm == null) return;

            string text = $"{vm.RegistryPath}\n{vm.ValueName}\n{vm.CurrentValue}";
            System.Windows.Clipboard.SetText(text);
            AddLog($"📋 Caminho copiado: {vm.RegistryPath}");
        }

        private void MenuSearchIssue_Click(object sender, RoutedEventArgs e)
        {
            var vm = GetIssueFromSender(sender);
            if (vm == null) return;

            string query = Uri.EscapeDataString($"{vm.Description} registry Windows");
            try
            {
                System.Diagnostics.Process.Start($"https://www.google.com/search?q={query}");
            }
            catch
            {
                AddLog("Não foi possível abrir o navegador.");
            }
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private void RegistryIssueSelection_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCleanButton();
        }

        private void ChkIssue_Click(object sender, RoutedEventArgs e)
        {
            UpdateCleanButton();
        }

        private void UpdateCleanButton()
        {
            bool hasSelected = _registryIssueViewModels.Any(i => i.IsSelected && i.CanClean);
            BtnCleanRegistry.IsEnabled = hasSelected;
            BtnSelectAuto.IsEnabled = _registryIssueViewModels.Any(i => i.CanClean && !i.IsSelected);
        }

        private void IssueRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.CheckBox) return;
            if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
            if (sender is System.Windows.FrameworkElement fe && fe.DataContext is RegistryIssueViewModel vm)
            {
                if (vm.CanClean)
                {
                    vm.IsSelected = !vm.IsSelected;
                    UpdateCleanButton();
                }
            }
        }

        private void BtnSelectAuto_Click(object sender, RoutedEventArgs e)
        {
            foreach (var vm in _registryIssueViewModels)
            {
                if (vm.CanClean) vm.IsSelected = true;
            }
            UpdateCleanButton();
            AddLog($"✅ Selecionados todos os {_registryIssueViewModels.Count(v => v.CanClean)} itens automáticos.");
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:N1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:N1} MB",
            >= 1_024 => $"{bytes / 1_024.0:N1} KB",
            _ => $"{bytes} B"
        };

        private void InitEvidenceList()
        {
            var items = new ObservableCollection<EvidenceItemViewModel>
            {
                new("Recent Documents (MRU)", "Documentos abertos recentemente via File Explorer"),
                new("RunMRU (Executar)", "Comandos digitados no Executar (Win+R)"),
                new("Typed URLs (IE/Edge)", "URLs digitadas no Internet Explorer e Edge"),
                new("UserAssist", "Rastro de programas executados via Explorer"),
                new("BagMRU (Pastas)", "Histórico de navegação em pastas"),
                new("Jump Lists", "Listas de atalhos recentes na taskbar"),
                new("Windows Timeline", "Banco de dados de atividades (ActivitiesCache.db)"),
                new("Clipboard History", "Histórico da área de transferência"),
                new("Prefetch", "Arquivos .pf de pré-carregamento de apps"),
                new("Office MRU", "Documentos recentes do Microsoft Office"),
                new("Visual Studio MRU", "Projetos recentes do Visual Studio")
            };
            EvidenceList.ItemsSource = items;
        }

        private void BtnSelectAllEvidence_Click(object sender, RoutedEventArgs e)
        {
            if (EvidenceList.ItemsSource is ObservableCollection<EvidenceItemViewModel> items)
            {
                foreach (var item in items)
                    item.IsSelected = true;
                BtnCleanEvidence.IsEnabled = true;
            }
        }

        private async void BtnCleanEvidence_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                if (EvidenceList.ItemsSource is not ObservableCollection<EvidenceItemViewModel> items) return;

                var selected = items.Where(i => i.IsSelected).ToList();
                if (selected.Count == 0)
                {
                System.Windows.MessageBox.Show("Nenhuma categoria selecionada.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (System.Windows.MessageBox.Show($"Limpar {selected.Count} categora(s) de evidncias?\n\nIsso remover rastros de uso do sistema.",
                "Limpeza de Evidncias", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                BtnCleanEvidence.IsEnabled = false;
                BtnCleanEvidence.Content = "⏳";

                int totalItems = 0;
                int totalErrors = 0;

                await Task.Run(() =>
                {
                    var results = EvidenceCleaner.CleanAll();
                    foreach (var (cat, (items, errors)) in results)
                    {
                        var match = selected.FirstOrDefault(s => s.Name == cat);
                        if (match != null)
                        {
                            match.Status = $"{items} itens limpos";
                            totalItems += items;
                            totalErrors += errors.Count;
                        }
                    }
                });

                string msg = $"✅ Limpeza concluída!\n\nTotal: {totalItems} itens removidos";
                if (totalErrors > 0) msg += $"\n⚠️ {totalErrors} erros (itens em uso ignorados)";
                System.Windows.MessageBox.Show(msg, "Limpeza de Evidncias", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnCleanEvidence_Click", ex.Message);
            }
            finally
            {
                BtnCleanEvidence.Content = "🧹 Limpar Selecionados";
                BtnCleanEvidence.IsEnabled = true;
                _isCleanupOperation = false;
            }
        }

        // ─── Portable Apps Scanner ────────────────────────────────────────────

        private List<PortableAppEntry>? _portableApps;

        private async void BtnScanPortable_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                TxtPortableScanResult.Text = "Escaneando...";
                TxtNoPortable.Visibility = Visibility.Collapsed;
                BtnDeletePortable.IsEnabled = false;

                var apps = await Task.Run(() => PortableAppScanner.Scan());
                _portableApps = apps;

                var viewModels = apps.Select(a => new PortableAppViewModel(a)).ToList();
                PortableAppList.ItemsSource = viewModels;
                TxtNoPortable.Visibility = viewModels.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

                if (viewModels.Count > 0)
                {
                    int high = viewModels.Count(v => v.Confidence >= 80);
                    int medium = viewModels.Count(v => v.Confidence >= 50 && v.Confidence < 80);
                    string summary = $"{viewModels.Count} port&#xE1;teis encontrados";
                    if (high > 0) summary += $" ({high} alta confian&#xE7;a";
                    if (medium > 0) summary += $", {medium} m&#xE9;dia";
                    if (high > 0 || medium > 0) summary += ")";
                    TxtPortableScanResult.Text = summary;
                    AddLog($"✅ Portable scan: {viewModels.Count} apps encontrados.");
                }
                else
                {
                    TxtPortableScanResult.Text = "Nenhum aplicativo port&#xE1;til encontrado.";
                    AddLog("Portable scan: nenhum encontrado.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnScanPortable_Click", ex.Message);
                AddLog($"❌ Erro no scan portable: {ex.Message}");
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        private async void BtnDeletePortable_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            if (PortableAppList.ItemsSource is not List<PortableAppViewModel> items) return;

            var selected = items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show("Selecione pelo menos um aplicativo port&#xE1;til para remover.",
                    "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            long totalSize = selected.Sum(s => s.TotalSizeBytes);
            string sizeStr = totalSize switch
            {
                >= 1_073_741_824 => $"{totalSize / 1_073_741_824.0:N1} GB",
                >= 1_048_576 => $"{totalSize / 1_048_576.0:N1} MB",
                _ => $"{totalSize / 1_024.0:N1} KB"
            };

            var mw = System.Windows.Application.Current.MainWindow as MainWindow;
            if (mw != null && !await mw.ShowConfirmationDialog(
                $"Remover {selected.Count} aplicativo(s) port&#xE1;teis?\n\n" +
                $"Espa&#xE7;o a ser liberado: {sizeStr}\n\n" +
                $"Isso EXCLUIR&#xC1; as pastas permanentemente."))
                return;

            _isCleanupOperation = true;
            BtnDeletePortable.IsEnabled = false;
            BtnDeletePortable.Content = "⏳";

            try
            {
                int successCount = 0;
                long freedBytes = 0;

                await Task.Run(() =>
                {
                    foreach (var vm in selected)
                    {
                        var result = PortableAppScanner.DeletePortableApp(vm.Entry);
                        if (result.success)
                        {
                            successCount++;
                            freedBytes += vm.TotalSizeBytes;
                            vm.Status = "Removido";
                        }
                        else
                        {
                            vm.Status = $"Erro: {result.message}";
                        }
                    }
                });

                string freedStr = freedBytes switch
                {
                    >= 1_073_741_824 => $"{freedBytes / 1_073_741_824.0:N1} GB",
                    >= 1_048_576 => $"{freedBytes / 1_048_576.0:N1} MB",
                    _ => $"{freedBytes / 1_024.0:N1} KB"
                };

                AddLog($"✅ Portable: {successCount}/{selected.Count} removidos ({freedStr} liberados).");
                System.Windows.MessageBox.Show(
                    $"{successCount} de {selected.Count} aplicativos removidos.\n" +
                    $"Espa&#xE7;o liberado: {freedStr}",
                    "Remo&#xE7;&#xE3;o Conclu&#xED;da", MessageBoxButton.OK, MessageBoxImage.Information);

                BtnScanPortable_Click(null!, null!);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDeletePortable_Click", ex.Message);
                AddLog($"❌ Erro: {ex.Message}");
            }
            finally
            {
                BtnDeletePortable.Content = "🗑️ Remover Selecionados";
                BtnDeletePortable.IsEnabled = true;
                _isCleanupOperation = false;
            }
        }

        private void BtnOpenPortableFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
            {
                string folder = tag;
                if (Directory.Exists(folder))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{folder}\"");
            }
        }

        // ─── Install Monitor ──────────────────────────────────────────────────

        private void BtnToggleMonitor_Click(object sender, RoutedEventArgs e)
        {
            if (InstallMonitor.IsRunning)
            {
                InstallMonitor.Stop();
                MonitorStatusDot.Background = new SolidColorBrush(Color.FromRgb(85, 85, 85));
                BtnToggleMonitor.Content = "▶ Iniciar Monitor";
                BtnSnapshot.IsEnabled = false;
                BtnCompare.IsEnabled = false;
                BtnClearMonitor.IsEnabled = false;
                BtnExportMonitor.IsEnabled = InstallMonitor.ChangeCount > 0;
                TxtMonitorStatus.Text = $"Monitor parado. {InstallMonitor.ChangeCount} alteraçõe(s) registrada(s).";
                AddLog($"Monitor de instalações parado. {InstallMonitor.ChangeCount} alterações detectadas.");
                RefreshMonitorList();
            }
            else
            {
                InstallMonitor.OnChange += OnMonitorChange;
                InstallMonitor.Start();
                MonitorStatusDot.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                BtnToggleMonitor.Content = "⏹ Parar Monitor";
                BtnSnapshot.IsEnabled = true;
                BtnClearMonitor.IsEnabled = true;
                BtnExportMonitor.IsEnabled = true;
                TxtMonitorStatus.Text = "Monitorando alterações em arquivos...";
                AddLog("Monitor de instalações iniciado (FileSystemWatcher).");
                RefreshMonitorList();
            }
        }

        private void OnMonitorChange(InstallMonitorChange change)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshMonitorList();
            });
        }

        private void RefreshMonitorList()
        {
            var changes = InstallMonitor.GetChanges();
            var viewModels = changes.Select(c => new MonitorChangeViewModel(c)).ToList();
            MonitorChangeList.ItemsSource = viewModels;
            TxtNoMonitorChanges.Visibility = viewModels.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            BtnExportMonitor.IsEnabled = viewModels.Count > 0;

            TxtMonitorStatus.Text = InstallMonitor.IsRunning
                ? $"Monitorando... {viewModels.Count} alteraçõe(s) registrada(s). Snapshot: {InstallMonitor.HasRegistrySnapshot}"
                : $"{viewModels.Count} alteraçõe(s) registrada(s).";
        }

        private void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            InstallMonitor.TakeRegistrySnapshot();
            BtnCompare.IsEnabled = true;
            AddLog("📸 Snapshot do registro tirado. Instale/remova programas e clique em 'Comparar'.");
            TxtMonitorStatus.Text = "Snapshot salvo. Clique em 'Comparar' após instalar/remover programas.";
        }

        private async void BtnCompare_Click(object sender, RoutedEventArgs e)
        {
            int count = await Task.Run(() => InstallMonitor.CompareRegistryWithSnapshot());
            RefreshMonitorList();
            if (count > 0)
            {
                AddLog($"🔄 Comparação concluída: {count} alterações no registro detectadas.");
                TxtMonitorStatus.Text = $"{count} alterações no registro encontradas.";
            }
            else
            {
                AddLog("✅ Nenhuma alteração no registro detectada desde o último snapshot.");
                TxtMonitorStatus.Text = "Nenhuma alteração no registro.";
            }
        }

        private void BtnClearMonitor_Click(object sender, RoutedEventArgs e)
        {
            InstallMonitor.ClearChanges();
            RefreshMonitorList();
            TxtNoMonitorChanges.Visibility = Visibility.Visible;
            BtnExportMonitor.IsEnabled = false;
            AddLog("Lista de alterações limpa.");
        }

        private async void BtnAdvancedStartup_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleanupOperation) return;
            _isCleanupOperation = true;
            try
            {
                AddLog("Escaneando locais avançados de inicialização...");

                var result = await Task.Run(() =>
                {
                    var wl = KitLugia.Core.StartupManager.GetWinlogonItems();
                    var ai = KitLugia.Core.StartupManager.GetAppInitDlls();
                    var bho = KitLugia.Core.StartupManager.GetBHOItems();
                    var be = KitLugia.Core.StartupManager.GetBootExecuteItems();
                    return (winlogon: wl, appinit: ai, bho: bho, bootExec: be);
                });

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("=== Locais Avançados de Inicialização ===\n");

                sb.AppendLine($"── Winlogon ({result.winlogon.Count}) ──");
                foreach (var item in result.winlogon)
                    sb.AppendLine($"  {item.Name}: {item.FullCommand}");

                sb.AppendLine($"\n── AppInit_DLLs ({result.appinit.Count}) ──");
                foreach (var item in result.appinit)
                    sb.AppendLine($"  {item.Name}: {item.FullCommand}");

                sb.AppendLine($"\n── BHO ({result.bho.Count}) ──");
                foreach (var item in result.bho)
                    sb.AppendLine($"  {item.Name}: {item.FullCommand}");

                sb.AppendLine($"\n── BootExecute ({result.bootExec.Count}) ──");
                foreach (var item in result.bootExec)
                    sb.AppendLine($"  {item.Name}: {item.FullCommand}");

                AddLog($"✅ Scan concluído: {result.winlogon.Count + result.appinit.Count + result.bho.Count + result.bootExec.Count} itens.");
                System.Windows.Clipboard.SetText(sb.ToString());
                System.Windows.MessageBox.Show(
                    $"Winlogon: {result.winlogon.Count}\n" +
                    $"AppInit_DLLs: {result.appinit.Count}\n" +
                    $"BHO: {result.bho.Count}\n" +
                    $"BootExecute: {result.bootExec.Count}\n\n" +
                    $"Total: {result.winlogon.Count + result.appinit.Count + result.bho.Count + result.bootExec.Count} itens\n\n" +
                    "Resultado copiado para a área de transferência.",
                    "Inicialização Avançada", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAdvancedStartup_Click", ex.Message);
                AddLog($"❌ Erro: {ex.Message}");
            }
            finally
            {
                _isCleanupOperation = false;
            }
        }

        private void BtnExportMonitor_Click(object sender, RoutedEventArgs e)
        {
            var changes = InstallMonitor.GetChanges();
            if (changes.Count == 0) return;

            var lines = changes.Select(c =>
                $"[{c.Timestamp:yyyy-MM-dd HH:mm:ss}] {c.Type} | {c.Category} | {c.Path}");
            string text = $"=== Relatório do Monitor de Instalações ===\n" +
                          $"Gerado em: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"Total de alterações: {changes.Count}\n\n" +
                          string.Join("\n", lines);

            System.Windows.Clipboard.SetText(text);
            AddLog($"📋 {changes.Count} alteraçõe(s) copiada(s) para a área de transferência.");
        }
    }

    public class MonitorChangeViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public InstallMonitorChange Change { get; }
        public string Path => Change.Path;
        public string Type => Change.Type;
        public string Category => Change.Category;
        public string Details => Change.Details;
        public string TimestampFormatted => Change.Timestamp.ToString("HH:mm:ss");

        public string TypeIcon => Change.Type switch
        {
            "Criado" or "Adicionado" => "➕",
            "Removido" => "➖",
            "Modificado" => "✏️",
            "Renomeado" => "🔄",
            _ => "❓"
        };

        public string TypeColor => Change.Type switch
        {
            "Criado" or "Adicionado" => "#4CAF50",
            "Removido" => "#FF6F61",
            "Modificado" => "#FFA500",
            "Renomeado" => "#2196F3",
            _ => "#888"
        };

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public MonitorChangeViewModel(InstallMonitorChange change)
        {
            Change = change;
        }
    }

    public class PortableAppViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public PortableAppEntry Entry { get; }
        public string Name => Entry.Name;
        public string FolderPath => Entry.FolderPath;
        public long TotalSizeBytes => Entry.TotalSizeBytes;
        public string TotalSizeFormatted => Entry.TotalSizeFormatted;
        public int Confidence => Entry.Confidence;
        public string ConfidenceLabel => Entry.ConfidenceLabel;
        public string ConfidenceColor => Entry.Confidence switch
        {
            >= 80 => "#4CAF50",
            >= 50 => "#FFA500",
            _ => "#FF6F61"
        };
        public string LastModifiedFormatted => Entry.LastModified.ToString("dd/MM/yyyy");

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public PortableAppViewModel(PortableAppEntry entry)
        {
            Entry = entry;
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }

        private string _status = "Pronto";
        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
        }
    }

    /// <summary>
    /// ViewModel para exibir problemas de registro na lista.
    /// </summary>
    public class RegistryIssueViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly KitLugia.Core.RegistryCleaner.RegistryIssue _issue;

        public RegistryIssueViewModel(KitLugia.Core.RegistryCleaner.RegistryIssue issue)
        {
            _issue = issue;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Description => _issue.Description;
        public KitLugia.Core.RegistryCleaner.RegistryIssue Issue => _issue;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public bool CanClean => KitLugia.Core.RegistryCleaner.CanCleanIssue(_issue);
        public string Category => _issue.Category;
        public string CategoryIcon => _issue.Category switch
        {
            "Inicialização" => "🚀",
            "Programas" => "📦",
            "Histórico" => "📋",
            "Extensões" => "🔗",
            _ => "🔧"
        };
        public string SafeLabel => KitLugia.Core.RegistryCleaner.GetIssueActionLabel(_issue);
        public string SafeColor => CanClean ? "#4CAF50" : "#FFA500";
        public string RiskWarning => KitLugia.Core.RegistryCleaner.GetIssueRiskWarning(_issue);
        public string RegistryPath => _issue.RegistryPath;
        public string ValueName => _issue.ValueName;
        public string CurrentValue => _issue.CurrentValue;
    }

    public class EvidenceItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public string Name { get; }
        public string Description { get; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public EvidenceItemViewModel(string name, string description)
        {
            Name = name;
            Description = description;
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }

        private string _status = "Pronto";
        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
        }
    }
}
