using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.IO;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using System.Windows.Threading;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

// Resolvendo ambiguidades de tipo (WPF vs WinForms)
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;

namespace KitLugia.GUI.Pages
{
    public partial class PartitionsPage : Page
    {
        private List<DiskInfoEx> _disks = new();
        private PartitionInfoEx? _selectedPartition;
        private TaskCompletionSource<string?>? _inputCompletionSource;

        private long _maxExtendMb = 0;
        private PartitionInfoEx? _neighborPartition;
        private long _maxNeighborMb = 0;
        private long _maxShrinkMb = 0;

        private DispatcherTimer? _realTimeMonitorTimer;
        private bool _isUpdatingDisks = false;
        private bool _isCriticalOperationInProgress = false;

        public PartitionsPage()
        {
            InitializeComponent();
            // A enumeracao de discos saiu do construtor: ela compete com o layout da
            // navegacao (anti-pattern 8). Agora roda uma vez no Loaded.
            _realTimeMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10) // Aumentado de 3s para 10s (menos travamentos)
            };
            _realTimeMonitorTimer.Tick += RealTimeMonitorTimer_Tick;
            // Só inicia no Loaded — não disputa com o layout da navegação
            this.Loaded += PartitionsPage_Loaded;


            this.Unloaded += PartitionsPage_Unloaded;
        }

        private bool _timersStarted;

        private void PartitionsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_timersStarted) return; // evita timers duplicados se Loaded disparar de novo
            _timersStarted = true;
            _realTimeMonitorTimer?.Start();

            // Enumera discos no background DEPOIS do primeiro layout da pagina.
            _ = Task.Run(() => LoadDisks());
        }


        public void Cleanup()
        {
            _realTimeMonitorTimer?.Stop();
            _realTimeMonitorTimer = null;
            _usageMonitorCts?.Cancel();
            _usageMonitorCts?.Dispose();
            _usageMonitorCts = null;
            _isMonitoringUsage = false;

            this.Loaded -= PartitionsPage_Loaded;
            this.Unloaded -= PartitionsPage_Unloaded;
            _timersStarted = false;


            _disks?.Clear();
            _disks = null!;
            _selectedPartition = null;


            this.DataContext = null;

            // SEM trim aqui: trim centralizado, adiado e cancelavel no MainWindow.
        }

        private void PartitionsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void LoadDisks()
        {
            try
            {
                var disks = PartitionManager.GetAllDisks();
                Dispatcher.Invoke(() =>
                {
                    _disks = disks;
                    if (CmbDisk != null)
                    {
                        int oldIdx = CmbDisk.SelectedIndex;
                        CmbDisk.Items.Clear();
                        foreach (var disk in _disks)
                            CmbDisk.Items.Add(DiskComboLabel(disk));

                        if (CmbDisk.Items.Count > 0)
                            CmbDisk.SelectedIndex = (oldIdx >= 0 && oldIdx < CmbDisk.Items.Count) ? oldIdx : 0;
                    }
                });
            }
            catch (Exception ex) 
            { 
                Dispatcher.Invoke(() => ShowError("Falha ao carregar discos", ex.Message));
            }
        }

        /// <summary>Rótulo do combo: identifica o disco (tipo de bus + espaço livre é o que diferencia NVMe de USB).
        /// Inclui saúde REAL (SMART via MSFT_PhysicalDisk) e o tipo de mídia (SSD/HDD) quando conhecidos.</summary>
        private static string DiskComboLabel(DiskInfoEx disk)
        {
            decimal unalloc = disk.Partitions.Where(p => p.IsUnallocated).Sum(p => (decimal)p.Size);
            string unallocTxt = unalloc > 0 ? $" • {(double)unalloc / (1024.0 * 1024 * 1024):F1} GB livres" : "";
            string sys = disk.IsSystemDisk ? " • SISTEMA" : "";
            string media = string.IsNullOrWhiteSpace(disk.MediaType) ? "" : $" • {disk.MediaType}";
            // Percentual estilo HDS tem prioridade sobre o veredito grosseiro do Windows
            string health;
            if (disk.HealthPercent.HasValue)
            {
                int hp = disk.HealthPercent.Value;
                health = hp >= 90 ? $" • 🟢 {hp}%"
                       : hp >= 50 ? $" • 🟡 {hp}%"
                       : $" • 🔴 {hp}% SAÚDE BAIXA";
            }
            else
            {
                health = string.IsNullOrWhiteSpace(disk.Health) ? ""
                    : disk.HealthIsBad ? $" • ⚠️ SAÚDE: {disk.Health.ToUpper()}" : " • 🟢 Saudável";
            }
            return $"{disk.DisplayName} • {disk.Interface}{media}{unallocTxt}{sys}{health}";
        }

        private void CmbDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDisk?.SelectedIndex < 0 || CmbDisk?.SelectedIndex >= _disks?.Count) return;
            var disk = _disks[CmbDisk.SelectedIndex];

            decimal unalloc = disk.Partitions.Where(p => p.IsUnallocated).Sum(p => (decimal)p.Size);
            int parts = disk.Partitions.Count(p => !p.IsUnallocated);
            string healthTxt;
            if (disk.HealthPercent.HasValue)
            {
                int hp = disk.HealthPercent.Value;
                string emoji = hp >= 90 ? "🟢" : hp >= 50 ? "🟡" : "🔴";
                healthTxt = $"\n{emoji} Saúde do disco: {hp}% ({disk.HealthSource})";
                if (!string.IsNullOrWhiteSpace(disk.HealthDetail))
                    healthTxt += $"\n   ├─ Problemas: {disk.HealthDetail}";
                if (hp < 90)
                    healthTxt += "\n   └─ Recomendado: backup dos dados importantes e teste de superfície antes de qualquer operação destrutiva.";
            }
            else
            {
                healthTxt = string.IsNullOrWhiteSpace(disk.Health) ? ""
                    : $"\n{(!disk.HealthIsBad ? "🟢" : "🔴")} Saúde do disco: {disk.Health}"
                      + (disk.HealthIsBad ? " — faça backup AGORA e rode um diagnóstico SMART antes de qualquer operação destrutiva neste disco." : " (SMART sem alertas)");
            }
            TxtDiskInfo.Text = $"{disk.Model} - {disk.SizeString} - {disk.PartitionStyle} - Interface: {disk.Interface}"
                             + (string.IsNullOrWhiteSpace(disk.MediaType) ? "" : $" - {disk.MediaType}")
                             + $" • {parts} partição(ões)"
                             + $" • {(double)unalloc / (1024.0 * 1024 * 1024):F1} GB não alocados"
                             + (disk.IsSystemDisk ? "  [DISCO DO SISTEMA — não use Excluir/Limpar]" : "")
                             + healthTxt;

            GridPartitions.ItemsSource = disk.Partitions;
            _selectedPartition = null;
            UpdateButtons();
            RenderPartitionBar(disk, null, null);
        }

        private void RenderPartitionBar(DiskInfoEx disk, PartitionInfoEx? activePart = null, PartitionInfoEx? targetPart = null)
        {
            try
            {
                if (PanelPartBar == null || disk == null) return;
                PanelPartBar.Children.Clear();
                PanelPartBar.ColumnDefinitions.Clear();

                foreach (var part in disk.Partitions)
                {
                    PanelPartBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((double)part.Size, GridUnitType.Star) });
                    int colIndex = PanelPartBar.ColumnDefinitions.Count - 1;

                    var partGrid = new Grid();
                    Grid.SetColumn(partGrid, colIndex);
                    PanelPartBar.Children.Add(partGrid);

                    Color baseColor = (Color)ColorConverter.ConvertFromString(part.BarColor);
                    var glassBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                    glassBrush.GradientStops.Add(new GradientStop(Color.FromArgb(200, baseColor.R, baseColor.G, baseColor.B), 0.0));
                    glassBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255, baseColor.R, baseColor.G, baseColor.B), 0.4));
                    glassBrush.GradientStops.Add(new GradientStop(Color.FromArgb(180, baseColor.R, baseColor.G, baseColor.B), 1.0));

                    var partBorder = new Border
                    {
                        Background = glassBrush,
                        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(colIndex == 0 ? 10 : 0, colIndex == disk.Partitions.Count - 1 ? 10 : 0, colIndex == disk.Partitions.Count - 1 ? 10 : 0, colIndex == 0 ? 10 : 0),
                        ToolTip = $"{part.DisplayName}\n{part.SizeString} | {part.FileSystem}"
                    };
                    partGrid.Children.Add(partBorder);

                    if (!part.IsUnallocated && part.Size > 0 && part.UsedPercent > 0)
                    {
                        var usedGrid = new Grid();
                        usedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, part.UsedPercent), GridUnitType.Star) });
                        usedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, part.FreePercent), GridUnitType.Star) });
                        var freeShadow = new Border { Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), CornerRadius = partBorder.CornerRadius };
                        Grid.SetColumn(freeShadow, 1);
                        usedGrid.Children.Add(freeShadow);
                        partBorder.Child = usedGrid;
                    }

                    if (part == activePart || part == targetPart || part == _selectedPartition)
                    {
                        var glowColor = (part == targetPart || (part == _selectedPartition && targetPart == null)) ? Color.FromRgb(255, 215, 0) : Color.FromRgb(255, 255, 255);
                        partBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = glowColor, BlurRadius = 25, ShadowDepth = 0, Opacity = 0.8 };
                        var glowOverlay = new Border { Background = new SolidColorBrush(Color.FromArgb(50, glowColor.R, glowColor.G, glowColor.B)), CornerRadius = partBorder.CornerRadius, IsHitTestVisible = false };
                        partGrid.Children.Add(glowOverlay);
                        (TryFindResource("GlowPulseStoryboard") as System.Windows.Media.Animation.Storyboard)?.Begin(glowOverlay);
                    }

                    if (part == activePart || part == targetPart)
                    {
                        var flowRect = new System.Windows.Shapes.Rectangle { Stroke = Brushes.White, StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 4, 4 }, RadiusX = partBorder.CornerRadius.TopLeft, RadiusY = partBorder.CornerRadius.TopLeft, Opacity = 0.9, IsHitTestVisible = false };
                        partGrid.Children.Add(flowRect);
                        string sbName = (targetPart == null && activePart != null) ? "MarchingAntsLeftStoryboard" : "MarchingAntsRightStoryboard";
                        (TryFindResource(sbName) as System.Windows.Media.Animation.Storyboard)?.Begin(flowRect);
                    }

                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    double diskShare = (double)part.Size / disk.Size;
                    if (diskShare > 0.04)
                    {
                        stack.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(part.DriveLetter) ? part.Label : $"{part.DriveLetter} {part.Label}", Foreground = IsDarkColor(part.BarColor) ? Brushes.White : Brushes.Black, FontSize = 11, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 180 });
                        if (diskShare > 0.08) stack.Children.Add(new TextBlock { Text = part.SizeString, Foreground = IsDarkColor(part.BarColor) ? Brushes.White : Brushes.Black, FontSize = 9, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center });
                    }
                    else if (diskShare > 0.015)
                    {
                        stack.Children.Add(new TextBlock { Text = !string.IsNullOrEmpty(part.DriveLetter) ? part.DriveLetter : "?", Foreground = IsDarkColor(part.BarColor) ? Brushes.White : Brushes.Black, FontSize = 9, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
                    }
                    var overlayGrid = new Grid { IsHitTestVisible = false };
                    overlayGrid.Children.Add(stack);
                    Grid.SetColumn(overlayGrid, colIndex);
                    PanelPartBar.Children.Add(overlayGrid);

                    partBorder.Cursor = Cursors.Hand;
                    partBorder.MouseDown += (s, e) => { _selectedPartition = part; GridPartitions.SelectedItem = part; UpdateButtons(); };
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Render Error: {ex.Message}"); }
        }

        private bool IsDarkColor(string hex)
        {
            try {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                double lum = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
                return lum < 0.5;
            } catch { Logger.LogWarning("Unknown", "Exception suppressed"); return true; }
        }

        private void GridPartitions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedPartition = GridPartitions.SelectedItem as PartitionInfoEx;
            UpdateButtons();
            if (CmbDisk.SelectedIndex >= 0) RenderPartitionBar(_disks[CmbDisk.SelectedIndex], null, _selectedPartition);
            CheckDirtyBitAsync(_selectedPartition);
        }

        /// <summary>
        /// Checa o dirty bit do volume selecionado (fsutil) e acrescenta o resultado à linha
        /// de info. Antes o Status de toda partição era "Saudável" HARDCODED — nunca dizia
        /// nada real. Volume sujo = Windows detectou inconsistência; chkdsk é recomendado.
        /// </summary>
        private async void CheckDirtyBitAsync(PartitionInfoEx? part)
        {
            if (part == null || part.IsUnallocated || string.IsNullOrEmpty(part.DriveLetter)) return;
            bool? dirty = await PartitionManager.IsVolumeDirtyAsync(part.DriveLetter);
            // O usuário pode ter trocado de seleção enquanto o fsutil rodava
            if (dirty == null || part != _selectedPartition) return;
            if (dirty == true)
            {
                TxtDiskInfo.Text += $"\n⚠️ Volume {part.DriveLetter} está SUJO (dirty bit marcado) — rode 🛡️ VERIFICAR ERROS para reparar.";
            }
        }

        private void UpdateButtons()
        {
            if (CmbDisk == null || CmbDisk.SelectedIndex < 0 || _disks == null || CmbDisk.SelectedIndex >= _disks.Count) return;
            var disk = _disks[CmbDisk.SelectedIndex];
            bool hasSel = _selectedPartition != null;
            bool isUnallocated = hasSel && _selectedPartition!.IsUnallocated;
            bool isProtected = hasSel && _selectedPartition!.IsProtected;

            BtnFormat.IsEnabled = hasSel && !isProtected; 
            BtnResize.IsEnabled = hasSel && !isUnallocated;
            BtnExtend.IsEnabled = hasSel && !isUnallocated;
            BtnDelete.IsEnabled = hasSel && !isProtected && !isUnallocated;
            BtnAssignLetter.IsEnabled = hasSel && !isUnallocated;
            BtnRename.IsEnabled = hasSel && !isUnallocated && !string.IsNullOrEmpty(_selectedPartition?.DriveLetter);
            BtnCheckFS.IsEnabled = hasSel && !isUnallocated && !string.IsNullOrEmpty(_selectedPartition?.FileSystem);
            BtnMove.IsEnabled = hasSel && !isUnallocated;

            BtnSetActive.Visibility = disk.PartitionStyle == "MBR" ? Visibility.Visible : Visibility.Collapsed;
            BtnSetActive.IsEnabled = hasSel && !isUnallocated;
            BtnFormat.Content = isUnallocated ? "➕ Criar" : "🔄 Formatar";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingDisks) return; // single-flight: evita WMI/IOCTL storm de cliques rápidos
            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Carregando Discos", "Partitions");

            try
            {
                _isUpdatingDisks = true;
                await Task.Run(() => LoadDisks());
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Discos carregados com sucesso");
            }
            catch (Exception ex)
            {
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                _isUpdatingDisks = false;
            }
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.NavigateToPage(PageType.AdvancedTools);
        }

        private void StartCriticalOperation()
        {
            _isCriticalOperationInProgress = true;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw != null)
            {
                mw.IsNavigationLocked = true;
                mw.ShowSuccess("AVISO CRÍTICO", "OPERAÇÃO DE DISCO EM CURSO.\nNAVEGAÇÃO BLOQUEADA PARA SUA SEGURANÇA.");
            }
        }

        private void EndCriticalOperation()
        {
            _isCriticalOperationInProgress = false;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw != null)
            {
                mw.IsNavigationLocked = false;
            }
        }

        private void RealTimeMonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (ChkAutoRefresh?.IsChecked != true || _isCriticalOperationInProgress || _isUpdatingDisks) return;
            try
            {
                _isUpdatingDisks = true;
                if (CmbDisk?.SelectedIndex < 0 || CmbDisk?.SelectedIndex >= _disks?.Count) return;
                var selectedDisk = _disks[CmbDisk.SelectedIndex];
                foreach (var partition in selectedDisk.Partitions) PartitionManager.RefreshUsage(partition);
                RenderPartitionBar(selectedDisk, null, _selectedPartition);
                GridPartitions?.Items.Refresh();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Erro timer: {ex.Message}"); }
            finally { _isUpdatingDisks = false; }
        }

        private void ChkAutoRefresh_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (ChkAutoRefresh?.IsChecked == true) _realTimeMonitorTimer?.Start();
            else _realTimeMonitorTimer?.Stop();
        }

        private void SetActionBusy(bool busy, string title = "PROCESSANDO", string desc = "Aguarde...", PartitionInfoEx? active = null, PartitionInfoEx? target = null)
        {
            Dispatcher.Invoke(() =>
            {
                if (OverlayAction == null) return;
                OverlayAction.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                if (busy) { TxtOpTitle.Text = title; TxtOpDesc.Text = desc; PanelProgress.Visibility = Visibility.Visible; PanelOpFooter.Visibility = Visibility.Collapsed; StartUsageMonitor(active, target); }
                else StopUsageMonitor();
            });
            SetGearSpin(busy);
        }

        /// <summary>Liga/desliga a rotação do engrenagem ⚙️ do overlay (economiza CPU quando fechado).</summary>
        private void SetGearSpin(bool running)
        {
            try
            {
                if (RotationTransform is not System.Windows.Media.RotateTransform rt) return;
                if (running)
                {
                    var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(2000)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                    rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
                }
                else
                {
                    rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                    rt.Angle = 0;
                }
            }
            catch { }
        }

        private bool _isMonitoringUsage = false;
        private CancellationTokenSource? _usageMonitorCts;

        private async Task StartUsageMonitor(PartitionInfoEx? active, PartitionInfoEx? target)
        {
            if (_isMonitoringUsage) return;
            _isMonitoringUsage = true;


            // sem esperar os 3 segundos do Task.Delay. Evita NullReferenceException quando
            // _disks é nulificado pelo Cleanup() enquanto o loop ainda está rodando.
            _usageMonitorCts?.Cancel();
            _usageMonitorCts?.Dispose();
            _usageMonitorCts = new CancellationTokenSource();
            var token = _usageMonitorCts.Token;

            Dispatcher.Invoke(() => BdrLiveBadge.Visibility = Visibility.Visible);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(3000, token);
                    if (token.IsCancellationRequested) break;
                    if (active != null) PartitionManager.RefreshUsage(active);
                    if (target != null) PartitionManager.RefreshUsage(target);
                    Dispatcher.Invoke(() =>
                    {
                        if (CmbDisk != null && CmbDisk.SelectedIndex >= 0 && _disks != null && CmbDisk.SelectedIndex < _disks.Count)
                            RenderPartitionBar(_disks[CmbDisk.SelectedIndex], active, target);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelamento esperado — não fazer nada
            }
            finally
            {
                _isMonitoringUsage = false;
            }
        }

        private void StopUsageMonitor()
        {
            _usageMonitorCts?.Cancel();
            _isMonitoringUsage = false;
            Dispatcher.Invoke(() => BdrLiveBadge.Visibility = Visibility.Collapsed);
        }

        private void UpdateProgress(double pct, string? status = null)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateProgress(pct, status)); return; }
            if (ProgOp == null) return;
            if (pct >= 0) { ProgOp.IsIndeterminate = false; ProgOp.Value = pct; }
            if (status != null) { TxtOpStatus.Text = status; TxtStatusBar.Text = status; }
        }

        private async Task<bool> ShowConfirm(string title, string msg)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw != null) return await mw.ShowConfirmationDialog(msg);
            return MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private void ShowSuccess(string t, string m) { var mw = Application.Current.MainWindow as MainWindow; if (mw != null) mw.ShowSuccess(t, m); else MessageBox.Show(m, t, MessageBoxButton.OK, MessageBoxImage.Information); }
        private void ShowError(string t, string m) { var mw = Application.Current.MainWindow as MainWindow; if (mw != null) mw.ShowError(t, m); else MessageBox.Show(m, t, MessageBoxButton.OK, MessageBoxImage.Error); }

        /// <summary>
        /// Erro de operação com CAUSA REAL: usa o LastError do PartitionManager (que explica
        /// espaço não contíguo, limite de redução, BitLocker, VDS...) e cai no texto genérico
        /// apenas quando o Core nãoiebenha nada. Antes o usuário só via "Não foi possível".
        /// </summary>
        private void ShowOpError(string fallback)
        {
            string detail = PartitionManager.LastError;
            ShowError("Operação não concluída", string.IsNullOrWhiteSpace(detail) ? fallback : detail);
        }

        /// <summary>Junta linhas de diagnóstico (bloqueios/avisos) em texto para overlay ou toast.</summary>
        private static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "• " + l.Trim()));

        private async Task<string?> ShowInputOverlay(string title, string msg, string def = "")
        {
            Dispatcher.Invoke(() => {
                TxtInputTitle.Text = title; TxtInputMessage.Text = msg; TxtInputField.Text = def;
                OverlayInput.Visibility = Visibility.Visible; TxtInputField.Focus();
            });
            _inputCompletionSource = new TaskCompletionSource<string?>();
            return await _inputCompletionSource.Task;
        }

        private async void BtnFormat_Click(object sender, RoutedEventArgs e)
        {
            string taskId = null;
            try
            {
                if (_selectedPartition == null) return;
                StartCriticalOperation();
                if (_selectedPartition.IsUnallocated)
                {
                    string? input = await ShowInputOverlay("CRIAR PARTIÇÃO", "Digite o tamanho em MB (deixe vazio para usar tudo):", "");
                    int sizeMb = 0;
                    if (input == null) { EndCriticalOperation(); return; }
                    if (!string.IsNullOrEmpty(input) && !int.TryParse(input, out sizeMb)) { ShowError("Valor Inválido", "Digite um número válido."); EndCriticalOperation(); return; }
                    SetActionBusy(true, "Criando Partição...", target: _selectedPartition);

                    taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Criando Partição", "Partitions");

                    bool ok = await PartitionManager.CreatePartition(_selectedPartition.DiskIndex, sizeMb, "ntfs", "Novo Volume");
                    SetActionBusy(false);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, ok, ok ? "Partição criada com sucesso" : "Falha ao criar partição");

                    if (ok) ShowSuccess("Sucesso", "Partição criada.");
                    else ShowOpError("Não foi possível criar a partição.");
                }
                else
                {
                    if (!await ShowConfirm("FORMATAR", $"⚠️ TODOS OS DADOS em {_selectedPartition.DriveLetter} serão APAGADOS.\nContinuar?")) { EndCriticalOperation(); return; }
                    SetActionBusy(true, "Formatando...");

                    taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Formatando {_selectedPartition.DriveLetter}", "Partitions");

                    bool ok = await PartitionManager.FormatPartition(_selectedPartition.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter, "ntfs", _selectedPartition.Label);
                    SetActionBusy(false);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, ok, ok ? "Partição formatada com sucesso" : "Falha ao formatar partição");

                    if (ok) ShowSuccess("Formatada", "A partição foi limpa.");
                    else ShowOpError("Não foi possível formatar a partição.");
                }
                LoadDisks();
            }
            catch (Exception ex)
            {
                SetActionBusy(false);
                ShowError("Erro", ex.Message);
                if (taskId != null) Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally { EndCriticalOperation(); }
        }

        private void BtnResize_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            if (string.IsNullOrEmpty(_selectedPartition.DriveLetter))
            {
                ShowError("Sem letra", $"{(_selectedPartition.Label ?? "Esta partição")} não tem letra de unidade. Atribua uma letra antes de reduzir.");
                return;
            }

            // Diagnóstico ANTES: pagefile/hiberfil/BitLocker são as causas reais de "reduz só X MB".
            var (blockers, warnings) = PartitionManager.GetResizeDiagnostics(_selectedPartition, forExtend: false);
            if (blockers.Count > 0) { ShowError("Não é possível reduzir", JoinLines(blockers)); return; }

            _maxShrinkMb = (long)(_selectedPartition.FreeSpace / (1024 * 1024));
            TxtShrinkTitle.Text = $"REDUZIR VOLUME [{_selectedPartition.DriveLetter}]";
            TxtMaxShrinkInfo.Text = $"Máximo estimativo: {_maxShrinkMb} MB";
            TxtShrinkMb.Text = (_maxShrinkMb / 2).ToString();

            TxtShrinkWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (warnings.Count > 0) TxtShrinkWarning.Text = "⚠️ " + JoinLines(warnings);

            OverlayShrink.Visibility = Visibility.Visible;
            TxtShrinkMb.Focus();

            // Limite REAL (Storage API: Size - SizeMin) — instantâneo e exato, sem parsing de diskpart.
            string drive = _selectedPartition.DriveLetter;
            _ = Task.Run(async () => {
                long realMax = await PartitionManager.GetMaxShrinkMb(drive);
                Dispatcher.Invoke(() => {
                    if (_selectedPartition?.DriveLetter == drive) {
                        _maxShrinkMb = realMax;
                        TxtMaxShrinkInfo.Text = $"Máximo real: {realMax} MB";
                    }
                });
            });
        }

        private async void BtnConfirmShrink_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtShrinkMb.Text, out int shrinkMb) || shrinkMb <= 0) { ShowError("Inválido", "Digite um valor positivo em MB."); return; }
            if (ChkEmergencyPreBoot.IsChecked != true && shrinkMb > _maxShrinkMb) { ShowError("Inválido", $"Máximo para redução normal: {_maxShrinkMb} MB (use Emergency para bypass)."); return; }
            OverlayShrink.Visibility = Visibility.Collapsed;

            if (ChkEmergencyPreBoot.IsChecked == true)
            {
                var result = System.Windows.MessageBox.Show(
                    $"🚨 EMERGENCY PRE-BOOT (WinRE)\n\n" +
                    $"O KitLugia vai:\n" +
                    $"1. Modificar o Windows RE via DISM ({shrinkMb}MB de shrink)\n" +
                    $"2. Configurar boot automatico no WinRE\n" +
                    $"3. REINICIAR o sistema\n\n" +
                    $"Apos o boot, o WinRE executara o diskpart, reduzira {_selectedPartition.DriveLetter}: em {shrinkMb}MB e criara a particao KITLUGIA.\n" +
                    $"Quando terminar, o Windows reiniciara normalmente.\n\n" +
                    $"Deseja continuar?",
                    "Emergency Pre-Boot — KitLugia",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                StartCriticalOperation();
                SetActionBusy(true, "Implantando Emergency WinRE...", active: _selectedPartition);

                string epbTaskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Emergency Pre-Boot (WinRE)", "Partitions");
                try
                {
                    (bool ok, string msg) = await EmergencyUEFIManager.DeployAsync(
                        (int)_selectedPartition!.DiskIndex,
                        (int)_selectedPartition.Index,
                        (long)_selectedPartition.Size,
                        _selectedPartition.DriveLetter,
                        shrinkMb,
                        WinbootManager.WINBOOT_LABEL,
                        UpdateProgress
                    );

                    if (!ok) throw new Exception(msg);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(epbTaskId, true, "UEFI Recovery implantado");
                    ShowSuccess("Sucesso", msg + "\n\nO sistema sera reiniciado AGORA.");

                    await EmergencyUEFIManager.TriggerReboot();
                }
                catch (Exception ex)
                {
                    Services.BackgroundTaskTracker.Instance.CompleteTask(epbTaskId, false, ex.Message);
                    ShowError("Erro", ex.Message);
                }
                finally
                {
                    SetActionBusy(false);
                    EndCriticalOperation();
                    LoadDisks();
                }
                return;
            }

            SetActionBusy(true, "Redimensionando...", active: _selectedPartition);

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Reduzindo {_selectedPartition.DriveLetter}", "Partitions");

            try
            {
                bool ok = await PartitionManager.ShrinkPartition(_selectedPartition!.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter, shrinkMb, UpdateProgress);
                SetActionBusy(false);
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, ok, ok ? "Volume reduzido com sucesso" : "Falha ao reduzir volume");
                if (ok) ShowSuccess("Sucesso", "Volume reduzido.");
                else ShowOpError($"Não foi possível reduzir {_selectedPartition.DriveLetter} em {shrinkMb} MB.");
            }
            catch (Exception ex)
            {
                SetActionBusy(false);
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
                ShowError("Erro", ex.Message);
            }
            LoadDisks();
        }

        private void ChkEmergencyPreBoot_Checked(object sender, RoutedEventArgs e) { TxtEmergencyWarning.Visibility = Visibility.Visible; }
        private void ChkEmergencyPreBoot_Unchecked(object sender, RoutedEventArgs e) { TxtEmergencyWarning.Visibility = Visibility.Collapsed; }

        private void BtnShrinkPercent_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int percent)) { long targetMb = (_maxShrinkMb * percent) / 100; TxtShrinkMb.Text = targetMb.ToString(); } }
        private void BtnShrinkMax_Click(object sender, RoutedEventArgs e) => TxtShrinkMb.Text = _maxShrinkMb.ToString();
        private void BtnCancelShrink_Click(object sender, RoutedEventArgs e) => OverlayShrink.Visibility = Visibility.Collapsed;private void BtnExtend_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            if (string.IsNullOrEmpty(_selectedPartition.DriveLetter))
            {
                ShowError("Sem letra", $"{(_selectedPartition.Label ?? "Esta partição")} não tem letra de unidade — atribua uma letra antes de estender.");
                return;
            }

            var disk = _disks.FirstOrDefault(d => d.Index == _selectedPartition.DiskIndex);
            if (disk == null) return;

            // Diagnóstico ANTES: é aqui que se descobre o motivo real de "estender falhou"
            // (FAT32, espaço não contíguo, limite MBR 2 TB...).
            var (blockers, warnings) = PartitionManager.GetResizeDiagnostics(_selectedPartition, forExtend: true);
            if (blockers.Count > 0) { ShowError("Não é possível estender", JoinLines(blockers)); return; }

            var parts = disk.Partitions.OrderBy(p => p.StartingOffset).ToList();
            int myIdx = parts.IndexOf(_selectedPartition);

            _maxExtendMb = 0;
            _neighborPartition = null;
            _maxNeighborMb = 0;

            RadioUnallocatedRight.IsEnabled = false;
            RadioNeighborRight.IsEnabled = false;
            ChkMergeMode.Visibility = Visibility.Collapsed;
            ChkMergeMode.IsChecked = false;
            TxtExtendWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (warnings.Count > 0) TxtExtendWarning.Text = "⚠️ " + JoinLines(warnings);
            TxtExtendMb.Text = "0";

            if (myIdx >= 0 && myIdx < parts.Count - 1)
            {
                var next = parts[myIdx + 1];
                if (next.IsUnallocated)
                {
                    _maxExtendMb = (long)(next.Size / (1024 * 1024));
                    RadioUnallocatedRight.Content = $"Espaço Não Alocado ({next.SizeString})";
                    RadioUnallocatedRight.IsEnabled = true;
                    RadioUnallocatedRight.IsChecked = true;
                    TxtExtendMb.Text = _maxExtendMb.ToString();
                }
                else
                {
                    _neighborPartition = next;
                    _maxNeighborMb = (long)Math.Max(1, (next.Size / (1024 * 1024)));
                    RadioNeighborRight.Content = $"Vizinha ({next.DriveLetter} {next.Label})";
                    RadioNeighborRight.IsEnabled = true;

                    if (!RadioUnallocatedRight.IsEnabled)
                    {
                        RadioNeighborRight.IsChecked = true;
                        TxtExtendMb.Text = _maxNeighborMb.ToString();
                    }

                    // Mesclar é DESTRUTIVO (copia tudo da vizinha para cá): fica opt-in.
                    ChkMergeMode.Visibility = Visibility.Visible;
                    ChkMergeMode.IsChecked = false;

                    if (!string.IsNullOrEmpty(next.DriveLetter))
                    {
                        _ = Task.Run(async () =>
                        {
                            long realMax = await PartitionManager.GetMaxShrinkMb(next.DriveLetter);
                            Dispatcher.Invoke(() =>
                            {
                                // Em modo merge (DISM) a vizinha inteira é movida — não usa o shrink máximo.
                                if (_neighborPartition == next && ChkMergeMode.IsChecked != true)
                                    _maxNeighborMb = realMax;
                            });
                        });
                    }
                }
            }

            if (!RadioUnallocatedRight.IsEnabled && !RadioNeighborRight.IsEnabled)
            {
                ShowError("Indisponível", "Não há espaço livre contíguo à direita.\n\n" +
                    "O Windows só estende uma partição para o lado DIREITO. Se o espaço não alocado estiver no meio " +
                    "ou à esquerda, é preciso mexer antes na partição que está entre as duas.");
                return;
            }

            OverlayExtend.Visibility = Visibility.Visible;

            // Máximo real pela Storage API (instantâneo): agrupa blocos não alocados contíguos
            // que a barra pode mostrar como entradas separadas.
            string letter = _selectedPartition.DriveLetter;
            _ = Task.Run(async () =>
            {
                long realMax = await PartitionManager.GetMaxExtendMb(letter);
                Dispatcher.Invoke(() =>
                {
                    if (RadioUnallocatedRight.IsChecked == true && realMax > _maxExtendMb)
                    {
                        _maxExtendMb = realMax;
                        TxtExtendMb.Text = realMax.ToString();
                    }
                });
            });
        }


        private async void BtnConfirmExtend_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtExtendMb?.Text, out int extendMb)) return;
            OverlayExtend.Visibility = Visibility.Collapsed;
            StartCriticalOperation();
            string taskId = null;
            try {
                if (RadioUnallocatedRight.IsChecked == true)
                {
                    SetActionBusy(true, "Estendendo volume...", target: _selectedPartition);

                    taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Estendendo {_selectedPartition.DriveLetter}", "Partitions");

                    // Escada do Core: Storage API -> IOCTL nativo -> diskpart (com verificação real).
                    // Task.Run OBRIGATÓRIO (03/10/2026): ExtendPartition é async, mas o caminho
                    // rápido (Storage API via WMI: GetPartitionSizeLimits + ResizeStoragePartition)
                    // roda 100% SÍNCRONO antes do primeiro await. Chamado direto do handler ele
                    // executava inteiro na thread de UI -> [UI-FREEZE] de ~5,5 s e janela "Nao
                    // Respondendo". UpdateProgress ja e thread-safe (faz Dispatcher.Invoke).
                    bool ok = await Task.Run(() => PartitionManager.ExtendPartition(
                        _selectedPartition!.DriveLetter, extendMb, UpdateProgress,
                        _selectedPartition.DiskIndex, _selectedPartition.Index));

                    if (!ok)
                    {
                        UpdateProgress(-1, "Extensão nativa recusou. Tentando modo atômico (wimlib: snapshot + recria)...");
                        ok = await Task.Run(() => PartitionManager.AtomicExtendDISM(
                            _selectedPartition.DiskIndex, _selectedPartition.Index,
                            _selectedPartition.DriveLetter, UpdateProgress));
                    }

                    SetActionBusy(false);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, ok, ok ? "Volume estendido com sucesso" : "Falha ao estender volume");

                    if (ok) ShowSuccess("Sucesso", "Volume estendido com sucesso.");
                    else ShowOpError($"Não foi possível estender {_selectedPartition.DriveLetter}:.");
                }
                else if (RadioNeighborRight.IsChecked == true && _neighborPartition != null)
                {
                    if (ChkMergeMode.IsChecked == true)
                    {
                        if (!await ShowConfirm("MESCLAR VIZINHA",
                            $"A partição {_neighborPartition.DriveLetter} será EXCLUÍDA e o conteúdo dela copiado para " +
                            $"{_selectedPartition.DriveLetter}:\\Arquivos_Mesclados.\n\n" +
                            "A Engine Atômica ignora o limite de redução do Windows, mas é mais lenta (imagem WIM temporária).\n" +
                            $"Deseja continuar?")) { EndCriticalOperation(); return; }

                        SetActionBusy(true, "Mesclando (Engine Atômica)...", active: _neighborPartition, target: _selectedPartition);

                        taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Mesclando {_neighborPartition.DriveLetter}", "Partitions");

                        bool mergeOk = await Task.Run(() => PartitionManager.AtomicMergeDISM(
                            _neighborPartition.DiskIndex, _neighborPartition.Index,
                            _neighborPartition.DriveLetter, _selectedPartition.DriveLetter, UpdateProgress));

                        SetActionBusy(false);

                        Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, mergeOk, mergeOk ? "Mesclagem concluída com sucesso" : "Falha na mesclagem");

                        if (mergeOk) ShowSuccess("Sucesso", "Mesclagem concluída com sucesso!");
                        else ShowOpError("Falha na mesclagem atômica. Os dados podem estar em uso severo.");
                    }
                    else
                    {
                        SetActionBusy(true, "Transferindo Espaço...", active: _neighborPartition);

                        taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Transferindo Espaço", "Partitions");

                        if (await Task.Run(() => PartitionManager.ShrinkPartition(_neighborPartition.DiskIndex, _neighborPartition.Index, _neighborPartition.DriveLetter, extendMb, UpdateProgress)))
                        {
                            bool extOk = await Task.Run(() => PartitionManager.ExtendPartition(
                                _selectedPartition!.DriveLetter, extendMb, UpdateProgress,
                                _selectedPartition.DiskIndex, _selectedPartition.Index));
                            SetActionBusy(false);

                            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, extOk, extOk ? "Espaço transferido com sucesso" : "Falha ao estender partição principal");

                            if (extOk) ShowSuccess("Sucesso", "Espaço transferido entre partições.");
                            else ShowOpError("A partição vizinha foi reduzida, mas a principal não estendeu até o fim.");
                        }
                        else
                        {
                            UpdateProgress(-1, "A vizinha não pode ser reduzida além do limite do Windows. Sugestão: marque 'Mesclar vizinha (DISM)' ou use Emergency Pre-Boot.");
                            SetActionBusy(false);

                            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, "Falha ao transferir espaço");

                            ShowOpError($"Não foi possível reduzir {_neighborPartition.DriveLetter} em {extendMb} MB.");
                        }
                    }
                }
                LoadDisks();
            }
            catch (Exception ex)
            {
                SetActionBusy(false);
                ShowError("Erro", ex.Message);
                if (taskId != null) Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally {
                EndCriticalOperation();
            }
        }

        private void BtnExtendPercent_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int percent)) { long max = RadioUnallocatedRight.IsChecked == true ? _maxExtendMb : _maxNeighborMb; TxtExtendMb.Text = ((max * percent) / 100).ToString(); } }
        private void BtnExtendMax_Click(object sender, RoutedEventArgs e) => TxtExtendMb.Text = (RadioUnallocatedRight.IsChecked == true ? _maxExtendMb : _maxNeighborMb).ToString();
        private void BtnCancelExtend_Click(object sender, RoutedEventArgs e) => OverlayExtend.Visibility = Visibility.Collapsed;

        private async void BtnMove_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null || !await ShowConfirm("MOVER", "Mover partição via Imaging?")) return;
            SetActionBusy(true, "Movendo...", active: _selectedPartition);
            try
            {
                bool ok = await PartitionManager.MovePartition(_selectedPartition.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter, UpdateProgress);
                SetActionBusy(false);
                if (ok) ShowSuccess("Sucesso", "Movida.");
                else ShowOpError("Falha ao mover a partição (veja o log do terminal: [MOVE] nn.Falha).");
                LoadDisks();
            }
            catch (Exception ex)
            {
                SetActionBusy(false);
                ShowError("Erro", ex.Message);
            }
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null || !await ShowConfirm("DELETAR", "Confirmar exclusão?")) return;
            SetActionBusy(true, "Deletando...");
            try
            {
                bool ok = await PartitionManager.DeletePartition(_selectedPartition.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter);
                if (ok) ShowSuccess("Removida", "Partição deletada.");
                else ShowOpError("Falha ao deletar partição.");
            }
            catch (Exception ex)
            {
                ShowError("Erro", ex.Message);
            }
            finally
            {
                SetActionBusy(false); // garante overlay fechado mesmo se diskpart explodir
                LoadDisks();
            }
        }

        private async void BtnAssignLetter_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            string? l = await ShowInputOverlay("LETRA", "Nova letra:", "");
            if (string.IsNullOrEmpty(l)) return;
            l = l.Trim().ToUpperInvariant().Replace(":", "");
            if (l.Length != 1 || l[0] < 'A' || l[0] > 'Z') { ShowError("Letra Inválida", "Digite uma única letra de A a Z."); return; }
            if (l == "C") { ShowError("Protegido", "C: é a partição do sistema. Não é possível reatribuí-la."); return; }
            SetActionBusy(true, "Alterando...");
            bool ok = await PartitionManager.ChangeDriveLetter(_selectedPartition.DriveLetter, l, _selectedPartition.DiskIndex, _selectedPartition.Index);
            SetActionBusy(false);
            if (ok) ShowSuccess("Sucesso", $"Letra alterada para {l}:.");
            else ShowOpError($"Não foi possível alterar a letra de {_selectedPartition?.DriveLetter}.");
            LoadDisks();
        }

        private async void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null || string.IsNullOrEmpty(_selectedPartition.DriveLetter)) return;
            string? newName = await ShowInputOverlay("RENOMEAR VOLUME", $"Novo nome do volume {_selectedPartition.DriveLetter}:", _selectedPartition.Label);
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = newName.Trim();
            if (newName.Length > 32) { ShowError("Nome Muito Longo", "O rótulo do volume aceita no máximo 32 caracteres."); return; }
            SetActionBusy(true, "Renomeando...");
            bool ok = await PartitionManager.SetVolumeLabelAsync(_selectedPartition.DriveLetter, newName);
            SetActionBusy(false);
            if (ok) { ShowSuccess("Sucesso", $"Volume {_selectedPartition.DriveLetter} renomeado para '{newName}'."); LoadDisks(); }
            else ShowOpError($"Não foi possível renomear {_selectedPartition?.DriveLetter}.");
        }

        private async void BtnCheckFS_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            SetActionBusy(true, "Verificando...");
            var (success, output) = await PartitionManager.CheckFileSystem(_selectedPartition.DriveLetter);
            TxtOpTitle.Text = success ? "OK" : "ERROS"; TxtOpDesc.Text = "Resultado:"; TxtOpStatus.Text = output;
            PanelProgress.Visibility = Visibility.Collapsed; PanelOpFooter.Visibility = Visibility.Visible;
        }

        private async void BtnSetActive_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null || !await ShowConfirm("ATIVAR", "Marcar como ativa?")) return;
            SetActionBusy(true, "Ativando...");
            await PartitionManager.SetActivePartition(_selectedPartition.DiskIndex, _selectedPartition.Index);
            SetActionBusy(false); LoadDisks();
        }

        private async void BtnCleanDisk_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDisk.SelectedIndex < 0) return;
            if (!await ShowConfirm("LIMPAR DISCO", "APAGAR TUDO?")) return;
            SetActionBusy(true, "Limpando...");
            try
            {
                bool ok = await PartitionManager.CleanDisk(_disks[CmbDisk.SelectedIndex].Index);
                if (ok) ShowSuccess("Limpo", "Disco limpo com sucesso.");
                else ShowError("Erro", "Falha ao limpar o disco.");
            }
            catch (Exception ex)
            {
                ShowError("Erro", ex.Message);
            }
            finally
            {
                SetActionBusy(false); // nunca deixa overlay preso
                LoadDisks();
            }
        }

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDisk.SelectedIndex < 0) return;
            string t = _disks[CmbDisk.SelectedIndex].PartitionStyle == "GPT" ? "MBR" : "GPT";
            if (!await ShowConfirm("CONVERTER", $"Converter para {t}?")) return;
            SetActionBusy(true, "Convertendo...");
            try
            {
                await PartitionManager.ConvertDiskStyle(_disks[CmbDisk.SelectedIndex].Index, t);
            }
            catch (Exception ex)
            {
                ShowError("Erro", ex.Message);
            }
            finally
            {
                SetActionBusy(false);
                LoadDisks();
            }
        }

        private async void BtnDiskDetail_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDisk.SelectedIndex < 0) return;
            SetActionBusy(true, "Carregando detalhes...");
            try
            {
                string d = await PartitionManager.GetDiskDetail(_disks[CmbDisk.SelectedIndex].Index);
                TxtOpTitle.Text = "📄 DETALHES DO DISCO";
                TxtOpDesc.Text = d;
                PanelProgress.Visibility = Visibility.Collapsed;
                BdrSafetyWarning.Visibility = Visibility.Collapsed;
                PanelLiveActivity.Visibility = Visibility.Collapsed;
                PanelOpFooter.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ShowError("Erro", ex.Message);
                SetActionBusy(false);
            }
        }

        private void BtnCloseOverlay_Click(object sender, RoutedEventArgs e)
        {
            // Fecha e reseta completamente o overlay para o estado padrão
            OverlayAction.Visibility = Visibility.Collapsed;
            PanelOpFooter.Visibility = Visibility.Collapsed;
            PanelProgress.Visibility = Visibility.Visible;
            BdrSafetyWarning.Visibility = Visibility.Visible;
            PanelLiveActivity.Visibility = Visibility.Visible;
            TxtOpTitle.Text = "EXECUTANDO OPERACAO";
            TxtOpDesc.Text = "Aguarde enquanto as alteracoes sao aplicadas...";
            TxtOpStatus.Text = "Status: Processando...";
        }

        private void BtnConfirmInput_Click(object sender, RoutedEventArgs e) { if (_inputCompletionSource != null) { _inputCompletionSource.SetResult(TxtInputField.Text); _inputCompletionSource = null; } OverlayInput.Visibility = Visibility.Collapsed; }
        private void BtnCancelInput_Click(object sender, RoutedEventArgs e) { if (_inputCompletionSource != null) { _inputCompletionSource.SetResult(null); _inputCompletionSource = null; } OverlayInput.Visibility = Visibility.Collapsed; }
        private void TxtInputField_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) BtnConfirmInput_Click(sender, e); else if (e.Key == Key.Escape) BtnCancelInput_Click(sender, e); }

        // ══════════════════════════════════════════════════════════════════
        // ANÁLISE DE SHRINK — somente leitura (técnica do evento 259)
        // Não altera nada: só consulta o limite e lê o Event Log.
        // ══════════════════════════════════════════════════════════════════
        private string _lastShrinkReport = "";
        private char _shrinkDiagDrive = 'C';
        private bool _shrinkDiagBusy;

        private async void BtnShrinkDiag_Click(object sender, RoutedEventArgs e)
        {
            if (_shrinkDiagBusy) return;

            // Usa a partição selecionada se for um volume com letra
            char drive = 'C';
            if (GridPartitions.SelectedItem != null)
            {
                var prop = GridPartitions.SelectedItem.GetType().GetProperty("DriveLetter");
                var raw = prop?.GetValue(GridPartitions.SelectedItem)?.ToString() ?? "";
                raw = raw.Trim().TrimEnd(':');
                if (raw.Length == 1 && char.IsLetter(raw[0])) drive = char.ToUpperInvariant(raw[0]);
            }
            _shrinkDiagDrive = drive;

            _shrinkDiagBusy = true;
            BtnShrinkDiag.IsEnabled = false;
            BtnShrinkDiag.Content = "🔍 ANALISANDO...";
            TxtShrinkBlockerLabel.Text = "Bloqueador: analisando...";
            TxtShrinkMaxLabel.Text = "Máx. encolhível: analisando...";
            TxtStatusBar.Text = $"Analisando bloqueios do shrink em {drive}: (somente leitura)...";

            try
            {
                var rep = await Task.Run(() => ShrinkDiagnostics.AnalyzeAsync(drive));
                _lastShrinkReport = rep.ToText();

                if (!rep.Ok)
                {
                    TxtShrinkBlockerLabel.Text = "Bloqueador: análise falhou";
                    TxtShrinkMaxLabel.Text = rep.Error.Length > 60 ? rep.Error.Substring(0, 60) + "..." : rep.Error;
                    TxtStatusBar.Text = $"Análise falhou: {rep.Error}";
                    return;
                }

                string blk = rep.Blocker == null || string.IsNullOrEmpty(rep.Blocker.FilePath)
                    ? "nenhum arquivo bloqueador reportado"
                    : $"{rep.Blocker.FilePath}  ({rep.Blocker.Kind}{(rep.Blocker.ClearableOnline ? "" : " — só offline")})";
                TxtShrinkBlockerLabel.Text = "Bloqueador: " + blk;
                TxtShrinkMaxLabel.Text = $"Máx. encolhível: {rep.MaxShrinkMb:N0} MB   |   Livre: {rep.FreePercent}";

                BtnSaveShrinkReport.IsEnabled = true;
                BtnCopyShrinkReport.IsEnabled = true;
                TxtStatusBar.Text = $"Análise de {drive}: concluída — máx. encolhível {rep.MaxShrinkMb:N0} MB.";
                Logger.Log($"[SHRINK-DIAG] {drive}: bloqueador={blk} maxMb={rep.MaxShrinkMb} " +
                           $"hiberfil={rep.HiberfilBytes} pagefile={rep.PagefileBytes}");
            }
            catch (Exception ex)
            {
                TxtShrinkBlockerLabel.Text = "Bloqueador: erro";
                TxtShrinkMaxLabel.Text = ex.Message.Length > 60 ? ex.Message.Substring(0, 60) + "..." : ex.Message;
                Logger.Log($"[SHRINK-DIAG] Exceção: {ex}");
            }
            finally
            {
                _shrinkDiagBusy = false;
                BtnShrinkDiag.IsEnabled = true;
                BtnShrinkDiag.Content = "🔍 DIAGNOSTICAR BLOQUEIO";
            }
        }

        private void BtnSaveShrinkReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastShrinkReport)) return;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(desktop,
                    $"diagnostico-shrink-{_shrinkDiagDrive}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(file, _lastShrinkReport, new System.Text.UTF8Encoding(true));
                TxtStatusBar.Text = $"Relatório salvo em: {file}";
                Logger.Log($"[SHRINK-DIAG] Relatório salvo: {file}");
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Não foi possível salvar: {ex.Message}";
            }
        }

        private void BtnCopyShrinkReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastShrinkReport)) return;
            try
            {
                System.Windows.Clipboard.SetText(_lastShrinkReport);
                TxtStatusBar.Text = "Relatório copiado para a área de transferência.";
            }
            catch (Exception ex)
            {
                TxtStatusBar.Text = $"Não foi possível copiar: {ex.Message}";
            }
        }


        // ══════════════════════════════════════════════════════════════════
        // DEEP SHRINK — método do hop (defrag + retry, ou cascata completa)
        // ⚠️ O modo completo ALTERA o sistema. O modo "só defrag" não apaga nada.
        // ══════════════════════════════════════════════════════════════════
        private string _lastDeepReport = "";
        private bool _deepBusy;

        private async void BtnDeepShrink_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "DEEP SHRINK (método hop)\n\n" +
                "Este fluxo ALTERA o sistema:\n" +
                "  • desliga a hibernação (powercfg /h off)\n" +
                "  • remove o pagefile da configuração\n" +
                "  • PODE APAGAR os pontos de restauração (vssadmin delete shadows)\n" +
                "  • remove o journal USN\n" +
                "  • roda defrag com até 4 tentativas (15 min)\n\n" +
                "Os pontos de restauração NÃO voltam. Use numa VM com snapshot.\n\n" +
                "Continuar?",
                "Deep Shrink — confirmação",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
            await RunDeepShrinkAsync(allowDestructive: true, defragOnly: false);
        }

        private async void BtnDeepShrinkDefragOnly_Click(object sender, RoutedEventArgs e)
        {
            await RunDeepShrinkAsync(allowDestructive: false, defragOnly: true);
        }

        private async Task RunDeepShrinkAsync(bool allowDestructive, bool defragOnly)
        {
            if (_deepBusy) return;
            _deepBusy = true;
            BtnDeepShrink.IsEnabled = false;
            BtnDeepShrinkDefragOnly.IsEnabled = false;
            TxtDeepLog.Text = "";
            char drive = _shrinkDiagDrive;

            IProgress<string> progress = new Progress<string>(msg =>
            {
                TxtDeepLog.Text += msg + "\n";
                TxtDeepLog.ScrollToEnd();
                TxtStatusBar.Text = msg;
            });

            try
            {
                DeepShrink.RunResult result;
                if (defragOnly)
                {
                    progress.Report($"Só defrag em {drive}: (não apaga nada)...");
                    var (ok, before) = await DeepShrink.TryGetMaxShrinkMbAsync(drive);
                    var step = await DeepShrink.InvokeDefragAsync(drive, before, progress);
                    result = new DeepShrink.RunResult
                    {
                        Drive = drive,
                        MaxShrinkMbStart = before,
                        MaxShrinkMbEnd = step.MaxShrinkMbAfter
                    };
                    result.Steps.Add(step);
                    result.BlockerAtEnd = (await ShrinkDiagnostics.AnalyzeAsync(drive)).Blocker?.FilePath ?? "";
                    progress.Report(step.ToString());
                }
                else
                {
                    result = await DeepShrink.RunAsync(drive, allowDestructive, progress);
                }

                _lastDeepReport = result.ToText();
                TxtDeepLog.Text += "\n" + result.ToText();
                TxtDeepLog.ScrollToEnd();
                BtnSaveDeepReport.IsEnabled = true;
                TxtStatusBar.Text = $"Deep Shrink concluído: {result.MaxShrinkMbStart:N0} → {result.MaxShrinkMbEnd:N0} MB";
                Logger.Log($"[DEEP-SHRINK] {drive}: {result.MaxShrinkMbStart} -> {result.MaxShrinkMbEnd} MB, " +
                           $"bloqueador final={result.BlockerAtEnd}, offline={result.NeedsOffline}");
            }
            catch (Exception ex)
            {
                TxtDeepLog.Text += $"\nERRO: {ex.Message}\n";
                TxtStatusBar.Text = $"Deep Shrink falhou: {ex.Message}";
                Logger.Log($"[DEEP-SHRINK] Exceção: {ex}");
            }
            finally
            {
                _deepBusy = false;
                BtnDeepShrink.IsEnabled = true;
                BtnDeepShrinkDefragOnly.IsEnabled = true;
            }
        }

        private void BtnSaveDeepReport_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lastDeepReport)) return;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(desktop, $"deep-shrink-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(file, _lastDeepReport, new System.Text.UTF8Encoding(true));
                TxtStatusBar.Text = $"Resultado salvo em: {file}";
            }
            catch (Exception ex) { TxtStatusBar.Text = $"Não foi possível salvar: {ex.Message}"; }
        }

        // ================================================================
        // LAB HOP - metodos do repo hop (github.com/Arjun-Ingole/hop, MIT)
        // Operam na PARTICAO SELECIONADA. Testar em VM com snapshot.
        // ================================================================
        private readonly System.Text.StringBuilder _hopLog = new();
        private bool _hopBusy;

        /// <summary>Letra do volume da particao selecionada; cai para o drive do sistema.</summary>
        private char HopDrive()
        {
            if (GridPartitions.SelectedItem != null)
            {
                var raw = GridPartitions.SelectedItem.GetType()
                    .GetProperty("DriveLetter")?.GetValue(GridPartitions.SelectedItem)?.ToString() ?? "";
                raw = raw.Trim().TrimEnd(':');
                if (raw.Length == 1 && char.IsLetter(raw[0])) return char.ToUpperInvariant(raw[0]);
            }
            var sys = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\', ':');
            return string.IsNullOrEmpty(sys) ? 'C' : sys[0];
        }

        private void HopLog(string msg)
        {
            _hopLog.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
            TxtHopLog.Text = _hopLog.ToString();
            TxtHopLog.ScrollToEnd();
        }

        private async void BtnHopDiag_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            _hopBusy = true;
            char d = HopDrive();
            HopLog("Diagnostico (so leitura) em " + d + ": ...");
            try { HopLog((await Task.Run(() => ShrinkDiagnostics.AnalyzeAsync(d))).ToText()); }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        private async void BtnHopDefrag_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            _hopBusy = true;
            char d = HopDrive();
            HopLog("DEFRAG + RETRY em " + d + ": (nao apaga nada; ate 4 tentativas em 900s)...");
            try
            {
                var (_, before) = await DeepShrink.TryGetMaxShrinkMbAsync(d);
                HopLog("Limite antes: " + before.ToString("N0") + " MB");
                var step = await DeepShrink.InvokeDefragAsync(d, before, new Progress<string>(HopLog));
                HopLog(step.ToString());
                var diag = await ShrinkDiagnostics.AnalyzeAsync(d);
                HopLog("Bloqueador depois: " + (diag.Blocker?.FilePath ?? "(nenhum)"));
            }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        private async void BtnHopHiber_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            if (System.Windows.MessageBox.Show("Desligar a hibernacao? (reversivel com powercfg /h on)",
                "Hibernacao", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
            _hopBusy = true;
            try
            {
                var (rc, outp) = await Task.Run(() => RunHopCmd("powercfg.exe", "/h off"));
                HopLog("powercfg /h off -> rc=" + rc + " " + outp.Trim());
            }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        private async void BtnHopPagefile_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            char d = HopDrive();
            if (System.Windows.MessageBox.Show("Remover o pagefile de " + d + ": do registro PagingFiles?\n" +
                "(efetivo apos reboot; reversivel - o valor ANTES fica no log)",
                "Pagefile", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
            _hopBusy = true;
            try
            {
                var raw = typeof(OnlineShrinkManager)
                    .GetMethod("ReadPagingFilesRaw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
                    .Invoke(null, null) as List<string>;
                HopLog("PagingFiles ANTES: " + string.Join(" | ", raw ?? new List<string>()));
                var ok = typeof(OnlineShrinkManager)
                    .GetMethod("DisablePagefile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
                    .Invoke(null, new object[] { d.ToString() });
                HopLog(ok is true ? "Pagefile removido do registro." : "Falhou - ver o log do Kit.");
            }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        private async void BtnHopVss_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            char d = HopDrive();
            if (System.Windows.MessageBox.Show(
                "APAGAR TODOS os pontos de restauracao de " + d + ":?\n\n" +
                "ISSO E IRREVERSIVEL. So continue numa VM com snapshot.",
                "VSS - irreversivel", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Stop, System.Windows.MessageBoxResult.No)
                != System.Windows.MessageBoxResult.Yes) return;
            _hopBusy = true;
            try
            {
                var (rc, outp) = await Task.Run(() => RunHopCmd("vssadmin.exe", "delete shadows /for=" + d + ": /all /quiet"));
                HopLog("vssadmin delete shadows -> rc=" + rc + "\n" + outp.Trim());
            }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        private async void BtnHopUsn_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            char d = HopDrive();
            if (System.Windows.MessageBox.Show("Remover o journal USN de " + d + ":? (o Windows recria sozinho)",
                "USN Journal", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
            _hopBusy = true;
            try
            {
                var (rc, outp) = await Task.Run(() => RunHopCmd("fsutil.exe", "usn deletejournal /d " + d + ":"));
                HopLog("fsutil usn deletejournal -> rc=" + rc + " " + outp.Trim());
            }
            catch (Exception ex) { HopLog("ERRO: " + ex.Message); }
            finally { _hopBusy = false; }
        }

        // NUCLEAR: NtfsMover.MoveDown (FSCTL_MOVE_FILE). Risco de volume inmontavel.
        private async void BtnHopNuclear_Click(object sender, RoutedEventArgs e)
        {
            if (_hopBusy) { HopLog("ocupado"); return; }
            char d = HopDrive();
            if (System.Windows.MessageBox.Show(
                "METODO NUCLEAR - NtfsMove::MoveDown\n\n" +
                "Move os clusters de $Mft::$BITMAP (metadado NTFS) do fim do disco\n" +
                "para o inicio, via FSCTL_MOVE_FILE - escrita direta em clusters.\n\n" +
                "SE FALHAR NO MEIO, O VOLUME PODE FICAR INMONTAVEL.\n" +
                "Nao ha chkdsk que garanta recuperacao.\n\n" +
                "Confirme que esta numa VM com snapshot tirado AGORA.",
                "NUCLEAR - confirmacao 1/2", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Stop, System.Windows.MessageBoxResult.No)
                != System.Windows.MessageBoxResult.Yes) return;
            if (System.Windows.MessageBox.Show("Ultima chance.\n\nExecutar mesmo assim?",
                "NUCLEAR - confirmacao 2/2", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Stop, System.Windows.MessageBoxResult.No)
                != System.Windows.MessageBoxResult.Yes) return;

            _hopBusy = true;
            BtnHopNuclear.IsEnabled = false;
            try
            {
                var (_, before) = await DeepShrink.TryGetMaxShrinkMbAsync(d);
                HopLog("NUCLEAR em " + d + ": - limite antes: " + before.ToString("N0") + " MB");
                string stream = "\\$Mft::$BITMAP";
                HopLog("Movendo " + stream + " para o inicio do disco...");
                long moved = await Task.Run(() => NtfsMover.MoveDown(d + ":", stream, 0.98, 600));
                HopLog("Clusters movidos: " + moved);
                if (!string.IsNullOrEmpty(NtfsMover.LastFailure))
                    HopLog("Falhas parciais: " + NtfsMover.LastFailure);
                var (_, after) = await DeepShrink.TryGetMaxShrinkMbAsync(d);
                HopLog("Limite depois: " + after.ToString("N0") + " MB");
            }
            catch (Exception ex)
            {
                HopLog("ERRO NUCLEAR: " + ex.Message);
                Logger.Log("[HOP-NUCLEAR] " + ex);
            }
            finally
            {
                _hopBusy = false;
                BtnHopNuclear.IsEnabled = true;
            }
        }

        private void BtnHopSave_Click(object sender, RoutedEventArgs e)
        {
            if (_hopLog.Length == 0) { HopLog("nada para salvar"); return; }
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string file = Path.Combine(desktop, "hop-lab-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(file, _hopLog.ToString(), new System.Text.UTF8Encoding(true));
                HopLog("Salvo em: " + file);
            }
            catch (Exception ex) { HopLog("Nao foi possivel salvar: " + ex.Message); }
        }

        private static (int rc, string output) RunHopCmd(string file, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (-1, "");
            string o = p.StandardOutput.ReadToEnd(), er = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return (p.ExitCode, o + (string.IsNullOrWhiteSpace(er) ? "" : "\n" + er));
        }

    }
}
