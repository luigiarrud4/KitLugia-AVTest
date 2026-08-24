using KitLugia.Core;
using KitLugia.GUI.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace KitLugia.GUI.Pages
{
    public partial class WinpeToolsPage : Page
    {
        private List<DiskInfoEx> _disks = new();
        private PartitionInfoEx _selectedPartition;
        private bool _isLoading;
        private string _customIsoBootWimPath;
        private int _lastProgressPct;

        // Cores para logs
        private static readonly SolidColorBrush _infoBrush = new(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly SolidColorBrush _errorBrush = new(System.Windows.Media.Color.FromRgb(0xFF, 0x69, 0x4B));
        private static readonly SolidColorBrush _successBrush = new(System.Windows.Media.Color.FromRgb(0x32, 0xCD, 0x32));

        // Rastreamento removido: HandleLogReplace encontra o último par dinamicamente

        // Cancelamento de operação em andamento
        private CancellationTokenSource? _cts;

        // Throttle para logs
        private readonly List<string> _logQueue = new();
        private readonly object _logLock = new();
        private System.Windows.Threading.DispatcherTimer _logTimer;

        // Mapa de passos para progresso real
        private static readonly (Regex Pattern, int Pct, string Label)[] _progressSteps =
        {
            (new Regex(@"Resolvendo WinPE base", RegexOptions.IgnoreCase),                                5,  "Baixando WinPE base..."),
            (new Regex(@"Download.*WinPE|Baixando WinPE", RegexOptions.IgnoreCase),                     10, "Baixando WinPE base..."),
            (new Regex(@"WinPE base não encontrado", RegexOptions.IgnoreCase),                           5,  "Baixando WinPE base..."),
            (new Regex(@"Copiando base para", RegexOptions.IgnoreCase),                                 25, "Copiando WinPE base..."),
            (new Regex(@"Resolvendo boot\.sdi", RegexOptions.IgnoreCase),                               30, "Resolvendo boot.sdi..."),
            (new Regex(@"(Customizando|customizar) boot\.wim", RegexOptions.IgnoreCase),                35, "Customizando boot.wim..."),
            (new Regex(@"boot\.wim customizado", RegexOptions.IgnoreCase),                              50, "Customizando boot.wim..."),
            (new Regex(@"boot_base\.wim.*cache", RegexOptions.IgnoreCase),                              55, "Salvando cache..."),
            (new Regex(@"Criando entrada BCD ramdisk", RegexOptions.IgnoreCase),                        60, "Criando entrada BCD..."),
            (new Regex(@"Entrada BCD ramdisk criada|BCD.*criada", RegexOptions.IgnoreCase),             75, "Entrada BCD criada!"),
            (new Regex(@"WinPE ramdisk pronto", RegexOptions.IgnoreCase),                               90, "WinPE pronto!"),
            (new Regex(@"GetDiskPartitionInfo", RegexOptions.IgnoreCase),                               10, "Detectando partição..."),
            (new Regex(@"Marcador escrito", RegexOptions.IgnoreCase),                                   30, "Escrevendo marcador..."),
            (new Regex(@"Config escrito", RegexOptions.IgnoreCase),                                     45, "Configuração salva..."),
            (new Regex(@"Bootsequence configurado", RegexOptions.IgnoreCase),                           65, "Configurando boot..."),
            (new Regex(@"Reiniciando em 10 segundos", RegexOptions.IgnoreCase),                         80, "Reiniciando em 10s..."),
            (new Regex(@"WinPE configurado", RegexOptions.IgnoreCase),                                  90, "Shrink agendado!"),
            (new Regex(@"Removendo entrada BCD", RegexOptions.IgnoreCase),                              30, "Removendo BCD..."),
            (new Regex(@"C:\\KL_WINPE\\ deletado|KL_WINPE deletado", RegexOptions.IgnoreCase),          70, "Removendo arquivos..."),
            (new Regex(@"Remocao WinPE concluida", RegexOptions.IgnoreCase),                            100,"WinPE removido!"),
            (new Regex(@"SUCESSO|sucesso|Sucesso", RegexOptions.IgnoreCase),                            100,"Concluído!"),
            (new Regex(@"ERRO|Falha|falha|Erro", RegexOptions.IgnoreCase),                              -1, "Erro!"),
        };

        public WinpeToolsPage()
        {
            InitializeComponent();
            _logTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _logTimer.Tick += FlushLogQueue;
            Loaded += OnLoaded;
            Unloaded += WinpeToolsPage_Unloaded;
        }

        private async void OnLoaded(object s, RoutedEventArgs e)
        {
            WinbootManager.OnLogUpdate += QueueLogUpdate;
            WinbootManager.OnLogReplace += HandleLogReplace;
            _logTimer.Start();
            await RefreshDisksAsync();
            await CheckWinpeStatusAsync();
        }

        private void QueueLogUpdate(string logLine)
        {
            lock (_logLock)
            {
                _logQueue.Add(logLine);
            }
        }

        private void HandleLogReplace(string logLine)
        {
            Dispatcher.Invoke(() =>
            {
                var inlines = TxtOpDesc.Inlines;
                // Remove o último par LineBreak+Run dinamicamente
                if (inlines.Count >= 2)
                {
                    var last = inlines.LastInline;
                    if (last is Run)
                    {
                        var prev = last.PreviousInline;
                        if (prev is LineBreak)
                        {
                            inlines.Remove(last);
                            inlines.Remove(prev);
                        }
                    }
                }
                var color = IsErrorText(logLine) ? _errorBrush
                    : logLine.Contains("✅") || logLine.Contains("sucesso", StringComparison.OrdinalIgnoreCase)
                        ? _successBrush : _infoBrush;
                inlines.Add(new LineBreak());
                inlines.Add(new Run(logLine) { Foreground = color });
                AtualizarStatus(logLine);
            }, DispatcherPriority.Background);
        }

        private void AtualizarStatus(string logLine)
        {
            foreach (var (pattern, pct, label) in _progressSteps)
            {
                if (pattern.IsMatch(logLine))
                {
                    if (pct >= 0)
                    {
                        _lastProgressPct = Math.Max(_lastProgressPct, pct);
                        UpdateProgressBar(_lastProgressPct, label);
                        TxtProgressStep.Text = label;
                    }
                    else
                    {
                        ProgressFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3D, 0x00));
                        TxtProgressPercent.Text = "ERRO";
                        TxtProgressStep.Text = label;
                    }
                    break;
                }
            }
            var clean = logLine;
            var m = Regex.Match(logLine, @"^\[\d{2}:\d{2}:\d{2}\]\s*(.+)");
            if (m.Success) clean = m.Groups[1].Value;
            if (!string.IsNullOrEmpty(clean) && clean.Length < 120)
                TxtProgressStatus.Text = clean;
        }

        private void FlushLogQueue(object? sender, EventArgs e)
        {
            string[] batch;
            lock (_logLock)
            {
                if (_logQueue.Count == 0) return;
                batch = _logQueue.ToArray();
                _logQueue.Clear();
            }

            foreach (var logLine in batch)
            {
                AppendColoredLog(logLine);
                AtualizarStatus(logLine);
            }

            ScrollOverlayToBottom();
        }

        private void AppendColoredLog(string text)
        {
            var color = IsErrorText(text) ? _errorBrush
                : text.Contains("✅") || text.Contains("sucesso", StringComparison.OrdinalIgnoreCase)
                    ? _successBrush : _infoBrush;
            TxtOpDesc.Inlines.Add(new LineBreak());
            TxtOpDesc.Inlines.Add(new Run(text) { Foreground = color });
        }

        private void ScrollOverlayToBottom()
        {
            if (TxtOpDesc.Parent is ScrollViewer sv)
                sv.ScrollToBottom();
            else if (TxtOpDesc.Parent is Border b && b.Child is ScrollViewer sv2)
                sv2.ScrollToBottom();
        }

        private void UpdateProgressBar(int pct, string label)
        {
            var width = Math.Min(pct / 100.0, 1.0) * 440.0;
            ProgressFill.Width = width;
            TxtProgressPercent.Text = $"{Math.Min(pct, 100)}%";
            TxtProgressStep.Text = label;
        }

        #region Disk Loading

        private async Task RefreshDisksAsync()
        {
            if (_isLoading) return;
            _isLoading = true;

            try
            {
                TxtStatusBar.Text = "Carregando discos...";
                var result = await Task.Run(() => PartitionManager.GetAllDisks());
                _disks = result ?? new();
                PopulatePartitionList();
                TxtStatusBar.Text = $"{_disks.Count} disco(s) carregados.";
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Erro: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void PopulatePartitionList()
        {
            var items = new List<PartitionListItem>();
            foreach (var disk in _disks)
            {
                if (disk.Partitions == null) continue;
                foreach (var part in disk.Partitions)
                {
                    items.Add(new PartitionListItem
                    {
                        DiskLabel = $"DISCO {disk.Index}",
                        DriveLetter = part.DriveLetter ?? "-",
                        Label = part.Label ?? "",
                        FileSystem = part.FileSystem ?? "",
                        SizeString = $"{(part.Size / (1024.0 * 1024 * 1024)):F1} GB",
                        FreeSpaceString = part.IsUnallocated ? "-" : $"{(part.FreeSpace / (1024.0 * 1024 * 1024)):F1} GB",
                        IsUnallocated = part.IsUnallocated,
                        Tag = part
                    });
                }
            }
            ListPartitions.ItemsSource = items;
            TxtPartCount.Text = $"{items.Count} particoes";
        }

        private void ListPartitions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListPartitions.SelectedItem is PartitionListItem item && item.Tag is PartitionInfoEx part)
            {
                _selectedPartition = part;
                TxtTargetInfo.Text = $"{part.DriveLetter}:  {(part.Size / (1024.0 * 1024 * 1024)):F1} GB  {(part.FreeSpace / (1024.0 * 1024 * 1024)):F1} GB livre";
                TxtShrinkReady.Text = $"Sistema de arquivos: {part.FileSystem}";
            }
            else
            {
                _selectedPartition = null;
                TxtTargetInfo.Text = "Nenhuma particao selecionada";
                TxtShrinkReady.Text = "";
            }
            UpdateShrinkButton();
        }

        private void UpdateShrinkButton()
        {
            bool hasPartition = _selectedPartition != null && !string.IsNullOrEmpty(_selectedPartition.DriveLetter);
            // WinPE nao precisa estar pronto: o fluxo prepara automaticamente ao agendar
            BtnShrinkWinpe.IsEnabled = hasPartition;
        }

        #endregion

        #region WinPE Detection

        private async Task CheckWinpeStatusAsync()
        {
            try
            {
                bool found = await Task.Run(() => WinbootManager.IsWinpeReady());

                if (found)
                {
                    BdrWinpeStatus.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#223322");
                    TxtWinpeStatus.Text = "WinPE pronto";
                    TxtWinpeStatus.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#88FF88");
                    BtnRemoveWinpe.Visibility = Visibility.Visible;
                }
                else
                {
                    BdrWinpeStatus.Background = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#222233");
                    TxtWinpeStatus.Text = "WinPE nao preparado";
                    TxtWinpeStatus.Foreground = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString("#8888FF");
                    BtnRemoveWinpe.Visibility = Visibility.Collapsed;
                }

                UpdateShrinkButton();
            }
            catch
            {
                TxtWinpeStatus.Text = "Erro ao verificar";
            }

            UpdateShrinkButton();
        }

        #endregion

        #region Step 1: Preparar WinPE

        private async void BtnPrepareWinpe_Click(object sender, RoutedEventArgs e)
        {
            ShowBusy("PREPARANDO WINPE",
                "Baixando e configurando WinPE no disco local...\n\n" +
                "1. Baixar WinPE base (se necessario)\n" +
                "2. Customizar com script de shrink\n" +
                "3. Configurar entrada de boot RAMDISK\n\n" +
                "O PC NAO sera reiniciado agora.\n" +
                "Nenhuma particao extra sera criada.");

            try
            {
                UpdateStatus("Aguarde...");

                var (ok, msg) = await Task.Run(() =>
                    WinbootManager.PrepareWinpeBoot());

                if (ok)
                {
                    ShowBusyResult($"WinPE preparado com sucesso!\n\n{msg}\n\n" +
                        "O WinPE esta pronto em C:\\Program Files\\KitLugia\\WinPE\\\n" +
                        "Agora selecione a particao alvo e clique em INICIAR SHRINK.");
                }
                else
                {
                    ShowBusyResult($"Falha ao preparar WinPE.\n{msg}");
                }

                await CheckWinpeStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnTestShell_Click(object sender, RoutedEventArgs e)
        {
            ShowBusy("AGENDANDO TESTE DO SHELL (EXPLORER++)",
                "Injetando Explorer++.exe (file manager) no WinPE...\n\n" +
                "1. Resolver Explorer++.exe (raiz do kit ou KitLugia.WinPE\\Explorer++)\n" +
                "2. Injetar no WIM via wimlib\n" +
                "3. Injetar startnet.cmd de teste (shrink pendente roda primeiro)\n" +
                "4. Bootsequence one-time via BCD ramdisk\n\n" +
                "O PC sera reiniciado em 10 segundos.\n" +
                "O WinPE bootara com Explorer++ como file manager (sem kit interno).\n\n" +
                "Se o WinPE nao estiver preparado, sera preparado automaticamente agora.");

            try
            {
                UpdateStatus("Resolvendo Explorer++ e preparando WIM...");

                var token = _cts?.Token ?? CancellationToken.None;
                var (ok, msg) = await Task.Run(() =>
                    WinbootManager.ScheduleTestWinpeShell(), token);

                if (ok)
                {
                    ShowBusyResult($"{msg}\n\n" +
                        "Apos o reboot, o WinPE boota com o Explorer++ como interface.\n" +
                        "Ao voltar ao Windows, clique em VER LOGS.");
                }
                else
                {
                    ShowBusyResult($"Falha: {msg}");
                }
            }
            catch (OperationCanceledException)
            {
                ShowBusyResult("Operacao cancelada pelo usuario.");
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnBootInstaller_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Selecionar ISO do Windows (boot direto no instalador)",
                Filter = "Imagem ISO (*.iso)|*.iso|Todos os arquivos|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            string isoPath = dlg.FileName;

            bool optimizeEsd = false;
            if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                optimizeEsd = await mw.ShowConfirmationDialog(
                    "Otimizar para RAM baixa (ESD solid)?\n\n" +
                    "Converte o install.wim (6,8 GB) para ESD solid (~5 GB, economia de 1,8 GB).\n" +
                    "Necessario para a fonte em RAM rodar com 8 GB de RAM.\n" +
                    "Leva ~8-10 min e precisa de ~6 GB livres extras em C: durante a conversao.\n\n" +
                    "Nao otimizar = fonte em RAM exige ~11 GB de RAM.");

            ShowBusy("AGENDANDO BOOT DIRETO NO INSTALADOR",
                $"ISO: {System.IO.Path.GetFileName(isoPath)}\n\n" +
                "1. Montar a ISO (estilo Titus, sem extrair com 7z)\n" +
                "2. Copiar Sources para C:\\KL_WINPE\\InstallISO (robocopy)\n" +
                "3. Exportar imagem de Setup (index 2) do boot.wim para WIM unico (--boot)\n" +
                (optimizeEsd ? "3.5. Otimizar install.wim para ESD solid (~5 GB, RAM baixa)\n" : "") +
                "4. Injetar startnet.cmd + winpeshl.ini que lanca o shim setup.exe da raiz (fluxo nativo da midia) com /installfrom\n" +
                "5. Bootsequence one-time via BCD ramdisk\n\n" +
                "O PC sera reiniciado em 10 segundos.\n" +
                "O Windows Setup (instalador) abre direto em modo grafico - sem criar particoes.\n\n" +
                (optimizeEsd
                    ? "Requer ~16 GB livres em C: (Sources 7.4 GB + otimizacao ESD ~6 GB temporarios).\n\n" +
                      "FONTE EM RAM: com ESD (~5 GB), o install.wim cabe em 8 GB de RAM - a particao\n" +
                      "de origem pode ser EXCLUIDA na tela do Setup (instalacao limpa)."
                    : "Requer ~10 GB livres em C: (install.wim de 6.8 GB + boot.wim de Setup).\n\n" +
                      "FONTE EM RAM: se o PC tiver RAM suficiente (>= 11 GB livres), o install.wim\n" +
                      "e copiado para o RAMDISK X: do WinPE - ai a particao de origem pode ser\n" +
                      "EXCLUIDA na tela do Setup (instalacao limpa). Sem RAM, o fallback usa a fonte\n" +
                      "do disco e a particao com C:\\KL_WINPE deve permanecer intacta (0x80300001)."));

            try
            {
                UpdateStatus("Montando ISO e preparando instalador...");

                var token = _cts?.Token ?? CancellationToken.None;
                var (ok, msg) = await Task.Run(() =>
                    WinbootManager.ScheduleBootInstallerAsync(isoPath, optimizeEsd), token);

                if (ok)
                {
                    ShowBusyResult($"{msg}\n\n" +
                        "Apos o reboot, o Windows Setup abre direto.\n" +
                        "Ao voltar ao Windows, os arquivos ficam em C:\\KL_WINPE\\InstallISO.");
                }
                else
                {
                    ShowBusyResult($"Falha: {msg}");
                }
            }
            catch (OperationCanceledException)
            {
                ShowBusyResult("Operacao cancelada pelo usuario.");
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnRemoveWinpe_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Remover WinPE?\n\n" +
                "Isso vai:\n" +
                "Remover a entrada de boot RAMDISK do BCD\n" +
                "Deletar C:\\KL_WINPE\\ (boot.wim, boot.sdi)\n" +
                "Limpar arquivos de configuracao\n\n" +
                "O WinPE nao estara mais disponivel para shrink.\n" +
                "Deseja continuar?",
                "Remover WinPE",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            ShowBusy("REMOVENDO WINPE", "Limpando artefatos do WinPE...");
            try
            {
                bool ok = await Task.Run(() => WinbootManager.RemoveWinpeAsync());
                ShowBusyResult(ok
                    ? "WinPE removido com sucesso.\n\n" +
                      "Entrada BCD removida, arquivos deletados."
                    : "Falha ao remover WinPE.");
                await CheckWinpeStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnCleanBcd_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Limpar entradas BCD do KitLugia?\n\n" +
                "Vai remover TODAS as entradas de boot criadas pelo KitLugia\n" +
                "(WinPE, shrink) do Windows Boot Manager.\n\n" +
                "NAO remove arquivos (C:\\KL_WINPE\\ continua intacto).\n" +
                "Deseja continuar?",
                "Limpar BCD",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            ShowBusy("LIMPANDO BCD", "Removendo entradas do Boot Manager...");
            try
            {
                var (ok, msg) = await Task.Run(() => WinbootManager.CleanupAllBcdEntriesAsync());
                ShowBusyResult(ok ? msg : $"Falha: {msg}");
                await CheckWinpeStatusAsync();
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        #endregion

        #region Step 2: Shrink via WinPE (com reboot)

        private void BtnShrinkWinpe_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null)
            {
                ShowToast("Selecione uma particao na lista.");
                return;
            }

            if (string.IsNullOrEmpty(_selectedPartition.DriveLetter))
            {
                ShowToast("Particao sem letra de unidade.");
                return;
            }

            TxtShrinkTargetInfo.Text = $"{_selectedPartition.DriveLetter}:  " +
                $"{(double)_selectedPartition.Size / 1024 / 1024 / 1024:F1} GB  " +
                $"{(double)_selectedPartition.FreeSpace / 1024 / 1024 / 1024:F1} GB livre";

            long maxMB = Math.Max(1024, (long)(_selectedPartition.FreeSpace / (1024L * 1024) * 0.8));
            TxtShrinkMb.Text = Math.Min(maxMB, (long)(_selectedPartition.Size / (1024L * 1024) * 0.5)).ToString();

            OverlayShrink.Visibility = Visibility.Visible;
        }

        private async void BtnConfirmShrinkWinpe_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;

            if (!long.TryParse(TxtShrinkMb.Text, out long shrinkMb) || shrinkMb < 256)
            {
                ShowToast("Digite um valor valido (min. 256 MB).");
                return;
            }

            string drive = _selectedPartition.DriveLetter;
            OverlayShrink.Visibility = Visibility.Collapsed;

            const string osName = "WINPE";
            ShowBusy($"AGENDANDO SHRINK VIA {osName}",
                $"Escrevendo configuracao e agendando reboot...\n\n" +
                $"Alvo: {drive.Replace(":", "").Trim()}: | Reduzir: {shrinkMb} MB\n" +
                $"OS: {osName}\n\n" +
                $"Se o WinPE nao estiver preparado, sera preparado automaticamente agora.\n" +
                $"O PC sera reiniciado em 10 segundos.\n" +
                $"O {osName} executara o shrink automaticamente.");

            try
            {
                UpdateStatus($"Escrevendo config na particao {drive.Replace(":", "").Trim()}...");

                var token = _cts?.Token ?? CancellationToken.None;
                var (ok, msg) = await Task.Run(() =>
                    WinbootManager.ScheduleWinpeShrink(drive, shrinkMb), token);

                if (ok)
                {
                    ShowBusyResult($"{msg}\n\n" +
                        $"Apos o reboot, o {osName} sera executado.\n" +
                        $"Ao voltar ao Windows, clique em VER LOGS.");
                }
                else
                {
                    ShowBusyResult($"Falha: {msg}");
                }
            }
            catch (OperationCanceledException)
            {
                ShowBusyResult("Operacao cancelada pelo usuario.");
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        #endregion

        #region Logs

        private void TxtCopyOpLog_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var inline in TxtOpDesc.Inlines)
                {
                    if (inline is System.Windows.Documents.Run run)
                        sb.Append(run.Text);
                    else if (inline is System.Windows.Documents.LineBreak)
                        sb.AppendLine();
                }
                string text = sb.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    System.Windows.Clipboard.SetText(text);
                    TxtCopyOpLog.Text = "✅ Copiado!";
                    _ = Task.Run(async () => { await Task.Delay(2000); Dispatcher.Invoke(() => TxtCopyOpLog.Text = "📋 Copiar log"); });
                }
            }
            catch { }
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = TxtWinpeLog.Text;
                if (!string.IsNullOrEmpty(text) && text != "Nenhum log.")
                {
                    System.Windows.Clipboard.SetText(text);
                    TxtStatusBar.Text = "Log copiado para a área de transferência.";
                }
                else
                {
                    TxtStatusBar.Text = "Nada para copiar.";
                }
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Erro ao copiar: {ex.Message}";
            }
        }

        private async void BtnCheckWinpeLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtWinpeLog.Text = "Lendo logs...";

                var logs = await Task.Run(() => WinbootManager.ReadAllWinpeLogs());

                if (logs == null || logs.Count == 0)
                {
                    TxtWinpeLog.Text = "Nenhum log encontrado.\n\n" +
                        $"{WinbootManager.WinpePersistentLogPath}\n" +
                        "Execute o shrink via WinPE primeiro.";
                    return;
                }

                TxtWinpeLog.Text = string.Join("\n\n=== PROXIMO ARQUIVO ===\n\n", logs.Values);
                TxtStatusBar.Text = $"{logs.Count} arquivo(s) de log.";
            }
            catch (Exception ex)
            {
                TxtWinpeLog.Text = $"Erro: {ex.Message}";
            }
        }

        #endregion

        #region Shrink Overlay Helpers

        private async void BtnShrinkPercent_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            if (!(sender is FrameworkElement fe && fe.Tag is string pct && int.TryParse(pct, out int pctVal))) return;

            long totalMB = (long)(_selectedPartition.Size / (1024L * 1024));
            long shrinkMB = (long)(totalMB * pctVal / 100.0);
            long maxMB = 1024;

            try { maxMB = await Task.Run(() => PartitionManager.GetMaxShrinkMb(_selectedPartition.DriveLetter)); }
            catch { maxMB = Math.Max(1024, (long)(_selectedPartition.FreeSpace / (1024L * 1024) * 0.8)); }

            TxtShrinkMb.Text = Math.Min(shrinkMB, maxMB).ToString();
        }

        private async void BtnShrinkMax_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;

            long maxMB = 1024;
            try { maxMB = await Task.Run(() => PartitionManager.GetMaxShrinkMb(_selectedPartition.DriveLetter)); }
            catch { maxMB = Math.Max(1024, (long)(_selectedPartition.FreeSpace / (1024L * 1024) * 0.8)); }

            TxtShrinkMb.Text = maxMB.ToString();
        }

        private void BtnCancelShrink_Click(object sender, RoutedEventArgs e)
            => OverlayShrink.Visibility = Visibility.Collapsed;

        #endregion

        #region Busy Overlay (com barra de progresso real)

        private void ShowBusy(string title, string description)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            OverlayBusy.Visibility = Visibility.Visible;
            TxtOpTitle.Text = title;
            TxtOpDesc.Inlines.Clear();
            TxtOpDesc.Inlines.Add(new Run(description) { Foreground = _infoBrush });
            TxtProgressPercent.Text = "0%";
            TxtProgressStep.Text = "Inicializando...";
            TxtProgressStatus.Text = "Aguarde...";
            ProgressFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
            ProgressFill.Width = 0;
            _lastProgressPct = 0;
            PanelOpFooter.Visibility = Visibility.Collapsed;
        }

        private void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                AppendColoredLog(status);
                if (status.Length < 120)
                    TxtProgressStatus.Text = status;
                ScrollOverlayToBottom();
            }, DispatcherPriority.Background);
        }

        private void ShowBusyResult(string result)
        {
            UpdateProgressBar(100, "Concluído");
            TxtProgressStatus.Text = result;
            TxtOpDesc.Inlines.Add(new LineBreak());
            TxtOpDesc.Inlines.Add(new LineBreak());
            var resultColor = IsErrorText(result) ? _errorBrush : _successBrush;
            TxtOpDesc.Inlines.Add(new Run(result) { Foreground = resultColor });
            PanelOpFooter.Visibility = Visibility.Visible;

            if (IsErrorText(result))
            {
                ProgressFill.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3D, 0x00));
                TxtProgressPercent.Text = "FALHA";
                TxtProgressStep.Text = "Erro na operação";
            }
        }

        private static bool IsErrorText(string text)
            => text.Contains("Erro", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Falha", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("❌");

        private void BtnCancelOp_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            ShowToast("Cancelando operacao...");
        }

        private void BtnCloseOverlay_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            OverlayBusy.Visibility = Visibility.Collapsed;
            TxtOpDesc.Inlines.Clear();
            _lastProgressPct = 0;
        }

        #endregion

        #region Navigation & Helpers

        private void BtnBack_Click(object sender, RoutedEventArgs e) => NavigateToPage(PageType.Dashboard);

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            _selectedPartition = null;
            await RefreshDisksAsync();
            await CheckWinpeStatusAsync();
        }

        private void ShowToast(string message) => TxtStatusBar.Text = message;

        private void NavigateToPage(PageType type)
            => (Window.GetWindow(this) as MainWindow)?.NavigateToPage(type);

        private async void BtnLoadCustomIso_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Arquivos ISO|*.iso",
                Title = "Selecione um ISO de WinPE customizado (ex: Sergei Strelec)"
            };
            if (dlg.ShowDialog() != true) return;

            string isoPath = dlg.FileName;
            _customIsoBootWimPath = null;
            TxtCustomIsoPath.Text = $"Extraindo {System.IO.Path.GetFileName(isoPath)}...";
            BtnPrepareCustomWinpe.IsEnabled = false;

            try
            {
                string tempWim = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "KitLugia", "custom_boot.wim");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(tempWim)!);

                string? result = await ExtractBootWimFromIsoAsync(isoPath, tempWim);
                if (result != null)
                {
                    _customIsoBootWimPath = tempWim;
                    TxtCustomIsoPath.Text = System.IO.Path.GetFileName(isoPath);
                    BtnPrepareCustomWinpe.IsEnabled = true;
                    ShowToast($"ISO carregado: {System.IO.Path.GetFileName(isoPath)}. Clique PREPARAR CUSTOM para testar.");
                }
                else
                {
                    TxtCustomIsoPath.Text = $"Falha: {System.IO.Path.GetFileName(isoPath)}";
                    ShowToast("Falha ao extrair boot.wim do ISO.");
                }
            }
            catch (Exception ex)
            {
                TxtCustomIsoPath.Text = $"Erro: {System.IO.Path.GetFileName(isoPath)}";
                ShowToast($"Erro: {ex.Message}");
            }
        }

        private async Task<string?> ExtractBootWimFromIsoAsync(string isoPath, string destPath)
        {
            string mountScript = $"Mount-DiskImage -ImagePath '{isoPath}' -StorageType ISO -Access ReadOnly";
            var (mc, mo) = await RunProcessAsync("powershell.exe",
                $"-NoProfile -Command \"{mountScript}\"", 60000);
            if (mc != 0)
            {
                Debug.WriteLine($"Falha ao montar ISO: {mo}");
                ShowToast("Falha ao montar ISO");
                return null;
            }

            try
            {
                string getLetter = $"(Get-DiskImage -ImagePath '{isoPath}' | Get-Volume).DriveLetter";
                var (lc, lo) = await RunProcessAsync("powershell.exe",
                    $"-NoProfile -Command \"{getLetter}\"", 30000);
                string driveLetter = (lc == 0 ? lo?.Trim() : null) ?? "";
                if (string.IsNullOrEmpty(driveLetter) || driveLetter.Length > 2)
                {
                    Debug.WriteLine($"Falha ao obter letra do drive: {lo}");
                    ShowToast("Falha ao montar ISO");
                    return null;
                }

                string letter = driveLetter[0].ToString();
                string sourcesWim = $@"{letter}:\sources\boot.wim";

                if (!File.Exists(sourcesWim))
                {
                    Debug.WriteLine($"boot.wim nao encontrado em {sourcesWim}");
                    ShowToast("boot.wim nao encontrado no ISO");
                    return null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(sourcesWim, destPath, true);
                Debug.WriteLine($"boot.wim copiado de {sourcesWim} para {destPath}");

                return destPath;
            }
            finally
            {
                await RunProcessAsync("powershell.exe",
                    $"-NoProfile -Command \"Dismount-DiskImage -ImagePath '{isoPath}'\"", 30000);
            }
        }

        private async Task<(int ExitCode, string Output)> RunProcessAsync(string filename, string args, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo(filename, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc == null) return (-1, "");
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            if (timeoutMs > 0)
            {
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                    return (-1, "TIMEOUT");
                }
            }
            else
            {
                proc.WaitForExit();
            }
            return (proc.ExitCode, output + error);
        }

        #endregion

        #region Custom ISO

        private async void BtnPrepareCustomWinpe_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_customIsoBootWimPath) || !File.Exists(_customIsoBootWimPath))
            {
                ShowToast("Carregue um ISO primeiro.");
                return;
            }

            ShowBusy("PREPARANDO CUSTOM WINPE",
                "Copiando boot.wim e configurando entrada BCD...\n\n" +
                "O bootsequence sera configurado para o proximo reboot.\n" +
                "O shrink NAO sera incluido - apenas teste do WinPE.");

            try
            {
                var (ok, msg, guid) = await Task.Run(() =>
                    WinbootManager.PrepareCustomWinpeBoot(_customIsoBootWimPath));

                if (ok && guid != null)
                {
                    ShowBusyResult($"Custom WinPE pronto!\n\n{msg}\n\n" +
                        "O sistema sera reiniciado em 10s para testar o WinPE customizado.\n" +
                        "Apos o teste, use REMOVER para limpar a entrada BCD.");
                }
                else
                {
                    ShowBusyResult($"Falha ao preparar Custom WinPE.\n{msg}");
                }
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        private async void BtnRemoveCustomWinpe_Click(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Remover Custom WinPE?\n\n" +
                "Isso vai:\n" +
                "Remover a entrada de boot Custom WinPE do BCD\n" +
                "Deletar C:\\KL_WINPE\\custom_boot.wim\n\n" +
                "Deseja continuar?",
                "Remover Custom WinPE",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            ShowBusy("REMOVENDO CUSTOM WINPE", "Limpando artefatos...");
            try
            {
                bool ok = await Task.Run(() => WinbootManager.RemoveCustomWinpe());
                ShowBusyResult(ok
                    ? "Custom WinPE removido com sucesso.\nEntrada BCD removida, arquivos deletados."
                    : "Nenhuma entrada BCD custom encontrada para remover.");
                _customIsoBootWimPath = null;
                TxtCustomIsoPath.Text = "Nenhum ISO carregado";
                BtnPrepareCustomWinpe.IsEnabled = false;
            }
            catch (Exception ex)
            {
                ShowBusyResult($"Erro: {ex.Message}");
            }
        }

        #endregion
        public void Cleanup()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _logTimer?.Stop();
            _logTimer = null;
            try { WinbootManager.OnLogUpdate -= QueueLogUpdate; } catch { }
            try { WinbootManager.OnLogReplace -= HandleLogReplace; } catch { }
            Loaded -= OnLoaded;
            this.Unloaded -= WinpeToolsPage_Unloaded;
            this.DataContext = null;
        }

        private void WinpeToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }
    }

    public class PartitionListItem
    {
        public string DiskLabel { get; set; }
        public string DriveLetter { get; set; }
        public string Label { get; set; }
        public string FileSystem { get; set; }
        public string SizeString { get; set; }
        public string FreeSpaceString { get; set; }
        public bool IsUnallocated { get; set; }
        public object Tag { get; set; }
    }
}
