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
            // Carrega discos em background para não travar a UI
            _ = Task.Run(() => LoadDisks());

            _realTimeMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10) // Aumentado de 3s para 10s (menos travamentos)
            };
            _realTimeMonitorTimer.Tick += RealTimeMonitorTimer_Tick;
            _realTimeMonitorTimer.Start();


            this.Unloaded += PartitionsPage_Unloaded;
        }


        public void Cleanup()
        {
            _realTimeMonitorTimer?.Stop();
            _realTimeMonitorTimer = null;


            _usageMonitorCts?.Cancel();
            _usageMonitorCts?.Dispose();
            _usageMonitorCts = null;
            _isMonitoringUsage = false;

            this.Unloaded -= PartitionsPage_Unloaded;


            _disks?.Clear();
            _disks = null!;
            _selectedPartition = null;


            this.DataContext = null;


            MemoryHelper.TrimWorkingSet();
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
                            CmbDisk.Items.Add(disk.DisplayName);

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

        private void CmbDisk_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDisk?.SelectedIndex < 0 || CmbDisk?.SelectedIndex >= _disks?.Count) return;
            var disk = _disks[CmbDisk.SelectedIndex];
            TxtDiskInfo.Text = $"{disk.Model} - {disk.SizeString} - {disk.PartitionStyle} - Interface: {disk.Interface}";
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
            } catch { return true; }
        }

        private void GridPartitions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedPartition = GridPartitions.SelectedItem as PartitionInfoEx;
            UpdateButtons();
            if (CmbDisk.SelectedIndex >= 0) RenderPartitionBar(_disks[CmbDisk.SelectedIndex], null, _selectedPartition);
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
            BtnCheckFS.IsEnabled = hasSel && !isUnallocated && !string.IsNullOrEmpty(_selectedPartition?.FileSystem);
            BtnMove.IsEnabled = hasSel && !isUnallocated;

            BtnSetActive.Visibility = disk.PartitionStyle == "MBR" ? Visibility.Visible : Visibility.Collapsed;
            BtnSetActive.IsEnabled = hasSel && !isUnallocated;
            BtnFormat.Content = isUnallocated ? "➕ Criar" : "🔄 Formatar";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Carregando Discos", "Partitions");

            await Task.Run(() => LoadDisks());

            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Discos carregados com sucesso");
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
                    else ShowError("Erro", "Não foi possível criar.");
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
                    else ShowError("Erro", "Não foi possível formatar.");
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
            if (_selectedPartition == null || string.IsNullOrEmpty(_selectedPartition.DriveLetter)) return;
            _maxShrinkMb = (long)(_selectedPartition.FreeSpace / (1024 * 1024));
            TxtShrinkTitle.Text = $"REDUZIR VOLUME [{_selectedPartition.DriveLetter}]";
            TxtMaxShrinkInfo.Text = $"Máximo estimativo: {_maxShrinkMb} MB";
            TxtShrinkMb.Text = (_maxShrinkMb / 2).ToString();


            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.Replace(":", "");
            string selectedDrive = _selectedPartition.DriveLetter.Replace(":", "");
            bool isSystemPartition = selectedDrive.Equals(sysDrive, StringComparison.OrdinalIgnoreCase);


            System.Diagnostics.Debug.WriteLine($"[DEBUG] SystemDirectory: {Environment.SystemDirectory}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG] sysDrive: {sysDrive}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG] selectedDrive: {selectedDrive}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG] isSystemPartition: {isSystemPartition}");

            OverlayShrink.Visibility = Visibility.Visible;
            TxtShrinkMb.Focus();
            string drive = _selectedPartition.DriveLetter;
            _ = Task.Run(async () => {
                long realMax = await PartitionManager.GetMaxShrinkMb(drive);
                Dispatcher.Invoke(() => { if (_selectedPartition?.DriveLetter == drive) { _maxShrinkMb = realMax; TxtMaxShrinkInfo.Text = $"Máximo real (Diskpart): {realMax} MB"; } });
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
                else ShowError("Erro", "Não foi possível reduzir.");
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
        private void BtnCancelShrink_Click(object sender, RoutedEventArgs e) => OverlayShrink.Visibility = Visibility.Collapsed;

        private void BtnExtend_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            var disk = _disks.FirstOrDefault(d => d.Index == _selectedPartition.DiskIndex);
            if (disk == null) return;
            var parts = disk.Partitions.OrderBy(p => p.StartingOffset).ToList();
            int myIdx = parts.IndexOf(_selectedPartition);
            
            _maxExtendMb = 0; 
            _neighborPartition = null; 
            _maxNeighborMb = 0;
            
            RadioUnallocatedRight.IsEnabled = false; 
            RadioNeighborRight.IsEnabled = false;
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
                    
                    ChkMergeMode.Visibility = Visibility.Visible;
                    ChkMergeMode.IsChecked = true; // Always active by default per user request
                    
                    _ = Task.Run(async () => 
                    { 
                        long realMax = await PartitionManager.GetMaxShrinkMb(next.DriveLetter); 
                        Dispatcher.Invoke(() => 
                        { 
                            if (_neighborPartition == next && !ChkMergeMode.IsChecked == true) 
                                _maxNeighborMb = realMax; 
                        }); 
                    }); 
                }
            }

            if (!RadioUnallocatedRight.IsEnabled && !RadioNeighborRight.IsEnabled) 
            { 
                ShowError("Indisponível", "Não há espaço livre à direita."); 
                return; 
            }
            
            OverlayExtend.Visibility = Visibility.Visible;
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
                    SetActionBusy(true, "Estendendo (Atomic Sniper Mode)...", target: _selectedPartition);

                    taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Estendendo {_selectedPartition.DriveLetter}", "Partitions");

                    bool ok = await PartitionManager.ExtendPartition(_selectedPartition!.DriveLetter, extendMb, UpdateProgress);

                    if (!ok)
                    {
                        UpdateProgress(-1, "Diskpart falhou (Limite de 3GB/Imóveis). Iniciando Bypass Atômico (DISM)...");
                        ok = await PartitionManager.AtomicExtendDISM(_selectedPartition.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter, UpdateProgress);
                    }

                    SetActionBusy(false);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, ok, ok ? "Volume estendido com sucesso" : "Falha ao estender volume");

                    if (ok) ShowSuccess("Sucesso", "Volume estendido com Engine Atômica DISM!");
                    else ShowError("Erro", "Falha crítica ao estender mesmo com Engine Atômica.");
                }
                else if (RadioNeighborRight.IsChecked == true && _neighborPartition != null)
                {
                    if (ChkMergeMode.IsChecked == true)
                    {
                        if (!await ShowConfirm("MESCLAR ATÔMICO (DISM)", "Deseja usar a Engine Atômica? Ela é mais lenta, porém IGNRORA o limite de 3GB e arquivos imóveis, movendo TUDO de " + _neighborPartition.DriveLetter + " para 'Arquivos_Mesclados'.")) { EndCriticalOperation(); return; }
                        SetActionBusy(true, "Mesclando (Atomic DISM Engine)...", active: _neighborPartition, target: _selectedPartition);

                        taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Mesclando {_neighborPartition.DriveLetter}", "Partitions");

                        bool mergeOk = await PartitionManager.AtomicMergeDISM(_neighborPartition.DiskIndex, _neighborPartition.Index, _neighborPartition.DriveLetter, _selectedPartition.DriveLetter, UpdateProgress);

                        SetActionBusy(false);

                        Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, mergeOk, mergeOk ? "Mesclagem concluída com sucesso" : "Falha na mesclagem");

                        if (mergeOk) ShowSuccess("Sucesso", "Mesclagem Atômica concluída com sucesso absoluto!");
                        else ShowError("Erro", "Falha na Mesclagem Atômica. Os dados podem estar em uso severo.");
                    }
                    else
                    {
                        SetActionBusy(true, "Transferindo Espaço...", active: _neighborPartition);

                        taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Transferindo Espaço", "Partitions");

                        if (await PartitionManager.ShrinkPartition(_neighborPartition.DiskIndex, _neighborPartition.Index, _neighborPartition.DriveLetter, extendMb, UpdateProgress))
                        {
                            bool extOk = await PartitionManager.ExtendPartition(_selectedPartition!.DriveLetter, extendMb, UpdateProgress);
                            SetActionBusy(false);

                            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, extOk, extOk ? "Espaço transferido com sucesso" : "Falha ao estender partição principal");

                            if (extOk) ShowSuccess("Sucesso", "Espaço transferido entre partições.");
                            else ShowError("Erro", "Vizinho reduzido, mas falha ao estender principal.");
                        }
                        else
                        {
                            UpdateProgress(-1, "Diskpart falhou (Limite de 3GB). Tentando Bypass Atômico (DISM)...");
                            // OBS: Engine atômica recria o volume ocupando TODO o espaço não alocado contíguo.
                            // Para o caso de 'puxar do vizinho' com valor específico, este bypass é melhor usado em modo MESCLAR.
                            bool ok = await PartitionManager.AtomicExtendDISM(_neighborPartition.DiskIndex, _neighborPartition.Index, _neighborPartition.DriveLetter, UpdateProgress);
                            if (ok)
                            {
                                await Task.Delay(2000);
                                await PartitionManager.ExtendPartition(_selectedPartition.DriveLetter, extendMb, UpdateProgress);
                                SetActionBusy(false);

                                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Espaço transferido com Engine Atômica");

                                ShowSuccess("Sucesso", "Espaço transferido com Engine Atômica DISM.");
                            }
                            else
                            {
                                SetActionBusy(false);

                                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, "Falha ao transferir espaço");

                                ShowError("Erro", "Não foi possível reduzir o vizinho nem com Engine Atômica.");
                            }
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
            string tempWim = Path.Combine(Path.GetTempPath(), $"move_{_selectedPartition.Index}.wim");
            try
            {
                if (await PartitionManager.CaptureVolumeImage(_selectedPartition.DriveLetter, tempWim, UpdateProgress))
                {
                    string letter = _selectedPartition.DriveLetter;
                    if (await PartitionManager.DeletePartition(_selectedPartition.DiskIndex, _selectedPartition.Index, letter))
                    {
                        int size = (int)(_selectedPartition.Size / (1024*1024));
                        if (await PartitionManager.CreatePartition(_selectedPartition.DiskIndex, size, "ntfs", "Moved"))
                        {
                            await Task.Delay(2000);
                            await PartitionManager.ApplyVolumeImage(tempWim, $"{letter}\\", UpdateProgress);
                            try { File.Delete(tempWim); } catch { }
                            SetActionBusy(false); ShowSuccess("Sucesso", "Movida.");
                            LoadDisks(); return;
                        }
                    }
                }
                SetActionBusy(false); ShowError("Erro", "Falha ao mover.");
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
            bool ok = await PartitionManager.DeletePartition(_selectedPartition.DiskIndex, _selectedPartition.Index, _selectedPartition.DriveLetter);
            SetActionBusy(false);
            if (ok) ShowSuccess("Removida", "Partição deletada.");
            LoadDisks();
        }

        private async void BtnAssignLetter_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedPartition == null) return;
            string? l = await ShowInputOverlay("LETRA", "Nova letra:", "");
            if (string.IsNullOrEmpty(l)) return;
            SetActionBusy(true, "Alterando...");
            await PartitionManager.ChangeDriveLetter(_selectedPartition.DriveLetter, l.ToUpper());
            SetActionBusy(false); LoadDisks();
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
            await PartitionManager.CleanDisk(_disks[CmbDisk.SelectedIndex].Index);
            SetActionBusy(false); LoadDisks();
        }

        private async void BtnConvert_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDisk.SelectedIndex < 0) return;
            string t = _disks[CmbDisk.SelectedIndex].PartitionStyle == "GPT" ? "MBR" : "GPT";
            if (!await ShowConfirm("CONVERTER", $"Converter para {t}?")) return;
            SetActionBusy(true, "Convertendo...");
            await PartitionManager.ConvertDiskStyle(_disks[CmbDisk.SelectedIndex].Index, t);
            SetActionBusy(false); LoadDisks();
        }

        private async void BtnDiskDetail_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDisk.SelectedIndex < 0) return;
            SetActionBusy(true, "Carregando detalhes...");
            string d = await PartitionManager.GetDiskDetail(_disks[CmbDisk.SelectedIndex].Index);
            TxtOpTitle.Text = "📄 DETALHES DO DISCO";
            TxtOpDesc.Text = d;
            PanelProgress.Visibility = Visibility.Collapsed;
            BdrSafetyWarning.Visibility = Visibility.Collapsed;
            PanelLiveActivity.Visibility = Visibility.Collapsed;
            PanelOpFooter.Visibility = Visibility.Visible;
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
    }
}
