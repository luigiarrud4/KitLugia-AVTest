using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
}
