using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KitLugia.Core;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using MainWindow = KitLugia.GUI.MainWindow;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class ContextMenuPage : Page
    {
        // --- ViewModel for the DataGrid ---
        public class ContextMenuEntryView
        {
            public SystemTweaks.ContextMenuEntry Entry { get; }
            public string DisplayName => Entry.DisplayName;
            public string CommandText => string.IsNullOrEmpty(Entry.Command) ? "-" : Entry.Command;
            public string TypeLabel => Entry.Type == "shellex" ? "ShellEx" : "Shell";
            public string OriginLabel => Entry.Source == "HKLM" ? "Sistema" : "Usuário";
            public string LocationPath => Entry.Location;
            public bool IsSystem => Entry.Source == "HKLM";
            public bool CanRemove => Entry.Source != "HKLM";

            public ContextMenuEntryView(SystemTweaks.ContextMenuEntry entry) => Entry = entry;
        }

        private bool _isLoading;
        private List<SystemTweaks.ContextMenuEntry> _allEntries = new();
        private ObservableCollection<ContextMenuEntryView> _filteredEntries = new();
        private int _currentContextIndex;

        private static readonly SolidColorBrush BgTransparent = WpfBrushes.Transparent;
        private static readonly SolidColorBrush BgHover = new(Color.FromRgb(58, 58, 74));
        private static readonly SolidColorBrush TxtWhite = WpfBrushes.White;
        private static readonly SolidColorBrush TxtMuted = new(Color.FromRgb(136, 136, 153));
        private static readonly SolidColorBrush TxtGold = new(Color.FromRgb(255, 215, 0));
        private static readonly SolidColorBrush BrdLock = new(Color.FromRgb(80, 80, 90));
        private static readonly SolidColorBrush BgRemove = new(Color.FromRgb(70, 25, 25));
        private static readonly SolidColorBrush FgRemove = new(Color.FromRgb(221, 80, 80));

        private static readonly (string label, string emoji, string[] prefixes)[] ContextModes = {
            ("Arquivo (qualquer tipo)", "\U0001F4C4", new[] { @"*\shell", @"*\shellex", @"AllFileSystemObjects\shell", @"AllFileSystemObjects\shellex" }),
            ("Pasta", "\U0001F4C1", new[] { @"Directory\shell", @"Directory\shellex", @"Folder\shell", @"Folder\shellex" }),
            ("Fundo / Desktop", "\U0001F5A5\uFE0F", new[] { @"Directory\Background\shell", @"Directory\Background\shellex", @"DesktopBackground\shell", @"DesktopBackground\shellex" }),
            ("Unidade (C:, D:)", "\U0001F4BE", new[] { @"Drive\shell", @"Drive\shellex" }),
            (".exe", "\u2699\uFE0F", new[] { @"exefile\shell", @"exefile\shellex" }),
            ("Biblioteca", "\U0001F4DA", new[] { @"LibraryFolder\shell", @"LibraryFolder\shellex", @"LibraryFolder\Background\shell" }),
            ("Todos objetos", "\U0001F5C2\uFE0F", new[] { @"AllFileSystemObjects\", @"*\shell", @"*\shellex" }),
        };

        private static readonly (string label, string icon)[] NativeEntries = {
            ("Recortar", "\u2702"),
            ("Copiar", "\U0001F4CB"),
            ("Renomear", "\u270E"),
            ("Excluir", "\U0001F5D1"),
            ("Propriedades", "\u2699"),
        };

        private record QuickAddItem(
            string Key,
            string DisplayName,
            string Description,
            string Emoji,
            System.Action Add,
            System.Action Remove,
            Func<bool> IsActive
        );

        private List<QuickAddItem> _quickItems = new();

        public ContextMenuPage()
        {
            InitializeComponent();
            GridEntries.ItemsSource = _filteredEntries;
            this.Loaded += async (s, e) =>
            {
                BuildContextChips();
                BuildQuickAddItems();
                await LoadAll();
            };
            this.Unloaded += ContextMenuPage_Unloaded;
        }

        public void Cleanup()
        {
            this.Unloaded -= ContextMenuPage_Unloaded;
            this.DataContext = null;
            _filteredEntries.Clear();
        }

        private void ContextMenuPage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        // ================================================================
        // BUILD: Context selector chips
        // ================================================================
        private void BuildContextChips()
        {
            ContextChips.Children.Clear();
            for (int i = 0; i < ContextModes.Length; i++)
            {
                var mode = ContextModes[i];
                var chip = new WpfButton
                {
                    Content = $"{mode.emoji}  {mode.label}",
                    Tag = i,
                    Cursor = WpfCursors.Hand,
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(14, 7, 14, 7),
                    FontSize = 13,
                    BorderThickness = new Thickness(1),
                };
                chip.Click += (s, e) =>
                {
                    if (s is WpfButton b && b.Tag is int idx)
                        SelectContext(idx);
                };
                ApplyChipStyle(chip, i == _currentContextIndex);
                ContextChips.Children.Add(chip);
            }
        }

        private void ApplyChipStyle(WpfButton chip, bool selected)
        {
            if (selected)
            {
                chip.Background = new SolidColorBrush(Color.FromRgb(255, 215, 0));
                chip.Foreground = new SolidColorBrush(Color.FromRgb(10, 10, 10));
                chip.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 200, 0));
                chip.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                chip.Background = new SolidColorBrush(Color.FromRgb(30, 30, 38));
                chip.Foreground = WpfBrushes.White;
                chip.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 65));
                chip.FontWeight = FontWeights.Normal;
            }
        }

        private void SelectContext(int idx)
        {
            if (idx < 0 || idx >= ContextModes.Length || idx == _currentContextIndex) return;
            _currentContextIndex = idx;
            for (int i = 0; i < ContextChips.Children.Count; i++)
            {
                if (ContextChips.Children[i] is WpfButton chip)
                    ApplyChipStyle(chip, i == idx);
            }
            RebuildMenu();
        }

        // ================================================================
        // BUILD: Quick-add items (now with 10 items)
        // ================================================================
        private void BuildQuickAddItems()
        {
            _quickItems = new List<QuickAddItem>
            {
                new("TakeOwnership", "Take Ownership", "Assumir controle total de arquivos e pastas", "\U0001F511",
                    SystemTweaks.AddTakeOwnership, SystemTweaks.RemoveTakeOwnership, SystemTweaks.IsTakeOwnershipAdded),
                new("ForceClose", "Force Close", "Forçar encerramento de processos .exe", "\u26D4",
                    SystemTweaks.AddForceClose, SystemTweaks.RemoveForceClose, SystemTweaks.IsForceCloseAdded),
                new("CmdHere", "CMD Here", "Abrir Prompt de Comando na pasta atual", "\U0001F4BB",
                    SystemTweaks.AddCmdHere, SystemTweaks.RemoveCmdHere, SystemTweaks.IsCmdHereAdded),
                new("PsHere", "PowerShell Here", "Abrir PowerShell na pasta atual", "\U0001F9EE",
                    SystemTweaks.AddPowerShellHere, SystemTweaks.RemovePowerShellHere, SystemTweaks.IsPowerShellHereAdded),
                new("CmdAdmin", "CMD as Admin", "CMD como Administrador na pasta", "\U0001F6E1",
                    SystemTweaks.AddCmdAdmin, SystemTweaks.RemoveCmdAdmin, SystemTweaks.IsCmdAdminAdded),
                new("PsAdmin", "PS as Admin", "PowerShell como Admin na pasta", "\U0001F6E1",
                    SystemTweaks.AddPowerShellAdmin, SystemTweaks.RemovePowerShellAdmin, SystemTweaks.IsPowerShellAdminAdded),
                new("Notepad", "Abrir com Bloco de Notas", "Editar qualquer arquivo no Notepad", "\U0001F4DD",
                    SystemTweaks.AddNotepad, SystemTweaks.RemoveNotepad, SystemTweaks.IsNotepadAdded),
                new("CopyAsPath", "Copiar como Caminho", "Copiar caminho completo do arquivo/pasta", "\U0001F4CB",
                    SystemTweaks.AddCopyAsPath, SystemTweaks.RemoveCopyAsPath, SystemTweaks.IsCopyAsPathAdded),
                new("VsCode", "VS Code (Admin)", "Abrir VS Code como Administrador na pasta", "\u2328\uFE0F",
                    SystemTweaks.AddVsCode, SystemTweaks.RemoveVsCode, SystemTweaks.IsVsCodeAdded),
                new("ForceStopUnlock", "Force Stop Unlock", "Desbloquear arquivos pelo menu de contexto", "\U0001F513",
                    SystemTweaks.AddForceStopUnlock, SystemTweaks.RemoveForceStopUnlock, SystemTweaks.IsForceStopUnlockAdded),
            };

            QuickAddGrid.Children.Clear();
            int total = _quickItems.Count;
            int cols = total <= 6 ? 2 : 3;
            QuickAddGrid.Columns = cols;
            foreach (var item in _quickItems)
                QuickAddGrid.Children.Add(BuildQuickAddCard(item));
        }

        private Border BuildQuickAddCard(QuickAddItem item)
        {
            var border = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(14, 10, 10, 10),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 60)),
                BorderThickness = new Thickness(1),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = item.Emoji,
                FontSize = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = TxtWhite,
            });
            stack.Children.Add(new TextBlock
            {
                Text = item.Description,
                FontSize = 11,
                Foreground = TxtMuted,
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var chk = new WpfCheckBox
            {
                Style = (Style)FindResource("ToggleSwitchStyle"),
                Cursor = WpfCursors.Hand,
                Tag = item,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chk.Click += QuickAddToggle_Click;
            Grid.SetColumn(chk, 2);
            grid.Children.Add(chk);

            border.Child = grid;
            border.Tag = (chk, item);
            return border;
        }

        // ================================================================
        // LOAD: Everything
        // ================================================================
        private async Task LoadAll()
        {
            _isLoading = true;
            try
            {
                await Task.Run(() =>
                {
                    _allEntries = SystemTweaks.GetAllUserContextMenuEntries();
                    bool backupExists = SystemTweaks.ContextMenuBackupExists();
                    DateTime? backupTime = SystemTweaks.GetContextMenuBackupTime();

                    Dispatcher.Invoke(() =>
                    {
                        UpdateBackupStatus(backupExists, backupTime);
                        UpdateQuickAddStates();
                        RefreshDataGrid();
                        RebuildMenu();
                    });
                });

                await Task.Run(() => SystemTweaks.BackupUserContextMenu());
                Dispatcher.Invoke(() =>
                {
                    bool bk = SystemTweaks.ContextMenuBackupExists();
                    DateTime? bt = SystemTweaks.GetContextMenuBackupTime();
                    UpdateBackupStatus(bk, bt);
                });
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // ================================================================
        // DATAGRID: Filtering & Refresh
        // ================================================================
        private void RefreshDataGrid()
        {
            string filter = TxtSearch?.Text?.Trim() ?? "";
            _filteredEntries.Clear();

            var query = _allEntries.AsEnumerable();
            if (!string.IsNullOrEmpty(filter))
            {
                var f = filter.ToLowerInvariant();
                query = query.Where(e =>
                    e.DisplayName.ToLowerInvariant().Contains(f) ||
                    e.Location.ToLowerInvariant().Contains(f) ||
                    e.Command.ToLowerInvariant().Contains(f) ||
                    e.Name.ToLowerInvariant().Contains(f));
            }

            foreach (var entry in query.OrderBy(e => e.Source == "HKLM" ? 0 : 1)
                                       .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                _filteredEntries.Add(new ContextMenuEntryView(entry));
            }

            // Atualizar contagem (total + nativas + usuario)
            int total = _allEntries.Count;
            int systemCount = _allEntries.Count(e => e.Source == "HKLM");
            int userCount = total - systemCount;
            TxtEntryCount.Text = $"{_filteredEntries.Count} exibidas | {systemCount} sistema / {userCount} usuário";
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            RefreshDataGrid();
        }

        private async void BtnRefreshTable_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            await LoadAll();
        }

        // ================================================================
        // DATAGRID: Remove entry
        // ================================================================
        private async void BtnGridRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || sender is not WpfButton btn || btn.Tag is not SystemTweaks.ContextMenuEntry entry) return;
            if (entry.Source == "HKLM") return;

            _isLoading = true;
            try
            {
                bool removed = false;
                await Task.Run(() =>
                {
                    removed = SystemTweaks.RemoveUserContextMenuEntry(entry.Location, entry.Name);
                });

                if (removed)
                {
                    SystemTweaks.ClearContextMenuPrefsForEntryName(entry.Name);
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.ShowSuccess("ENTRADA REMOVIDA", $"'{entry.DisplayName}' foi removida do menu de contexto.");
                    await LoadAll();
                    UpdateQuickAddStates();
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // ================================================================
        // UPDATE: Quick-add toggle states
        // ================================================================
        private void UpdateQuickAddStates()
        {
            int activeCount = 0;
            for (int i = 0; i < QuickAddGrid.Children.Count; i++)
            {
                if (QuickAddGrid.Children[i] is Border border && border.Tag is (WpfCheckBox chk, QuickAddItem item))
                {
                    bool isActive = item.IsActive();
                    chk.IsChecked = isActive;
                    chk.Foreground = isActive ? TxtGold : TxtMuted;
                    if (isActive) activeCount++;
                    border.BorderBrush = isActive
                        ? new SolidColorBrush(Color.FromRgb(255, 200, 0))
                        : new SolidColorBrush(Color.FromRgb(50, 50, 60));
                }
            }
            QuickAddActiveCount.Text = $"{activeCount} ativa{(activeCount == 1 ? "" : "s")}";
        }

        // ================================================================
        // REBUILD: Simulated menu 1:1
        // ================================================================
        private void RebuildMenu()
        {
            MenuPanel.Children.Clear();
            EmptyHint.Visibility = Visibility.Collapsed;
            SimulatedMenu.Visibility = Visibility.Collapsed;

            if (_allEntries.Count == 0 || _currentContextIndex < 0 || _currentContextIndex >= ContextModes.Length)
                return;

            var mode = ContextModes[_currentContextIndex];
            var matched = _allEntries
                .Where(e => mode.prefixes.Any(p =>
                    e.Location.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            if (matched.Count == 0)
            {
                EmptyHint.Visibility = Visibility.Visible;
                return;
            }

            SimulatedMenu.Visibility = Visibility.Visible;

            var shell = matched.Where(e => e.Type == "shell").OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            var shellex = matched.Where(e => e.Type == "shellex").OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var e in shell)
                MenuPanel.Children.Add(MakeMenuEntry(e));
            if (shell.Count > 0 && shellex.Count > 0)
                MenuPanel.Children.Add(MakeSeparator());
            foreach (var e in shellex)
                MenuPanel.Children.Add(MakeMenuEntry(e));
            MenuPanel.Children.Add(MakeSeparator());
            foreach (var n in NativeEntries)
                MenuPanel.Children.Add(MakeNativeEntry(n.label, n.icon));
            MenuPanel.Children.Add(MakeSeparator());
            MenuPanel.Children.Add(MakeFooterEntry());
        }

        // ================================================================
        // MENU ITEM BUILDERS
        // ================================================================
        private Border MakeMenuEntry(SystemTweaks.ContextMenuEntry entry)
        {
            bool isHKLM = entry.Source != "HKCU";
            bool isExtended = !string.IsNullOrEmpty(entry.Extended);

            var row = new Border
            {
                Padding = new Thickness(12, 7, 6, 7),
                MinHeight = 32,
                CornerRadius = new CornerRadius(4),
                Background = BgTransparent,
                Cursor = isHKLM ? WpfCursors.Arrow : WpfCursors.Hand,
                Tag = entry,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (isHKLM || isExtended)
            {
                var badgePanel = new StackPanel { Orientation = WpfOrientation.Horizontal };
                badgePanel.Children.Add(new TextBlock
                {
                    Text = entry.DisplayName,
                    Foreground = isExtended ? TxtMuted : TxtWhite,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                if (isExtended)
                {
                    var extBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(40, 40, 50)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 80)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    extBadge.Child = new TextBlock { Text = "EXT", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = TxtMuted };
                    badgePanel.Children.Add(extBadge);
                }

                if (isHKLM)
                {
                    var lckBadge = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(30, 35, 50)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(60, 80, 120)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(6, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    lckBadge.Child = new TextBlock { Text = "HKLM", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(100, 150, 220)) };
                    badgePanel.Children.Add(lckBadge);
                }

                Grid.SetColumn(badgePanel, 0);
                grid.Children.Add(badgePanel);
            }
            else
            {
                var tb = new TextBlock
                {
                    Text = entry.DisplayName,
                    Foreground = TxtWhite,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }

            var btn = new WpfButton
            {
                Content = isHKLM ? "\U0001F512" : "\u2716",
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                MinWidth = 24,
                MinHeight = 24,
                Cursor = WpfCursors.Hand,
                Tag = entry,
                IsEnabled = !isHKLM,
                Visibility = Visibility.Collapsed,
                Background = isHKLM ? new SolidColorBrush(Color.FromRgb(40, 40, 45)) : BgRemove,
                Foreground = isHKLM ? new SolidColorBrush(Color.FromRgb(120, 120, 130)) : FgRemove,
                BorderBrush = isHKLM ? BrdLock : new SolidColorBrush(Color.FromRgb(100, 35, 35)),
                BorderThickness = new Thickness(1),
                ToolTip = isHKLM ? "Entrada do sistema" : "Remover",
            };
            if (!isHKLM)
                btn.Click += BtnRemoveEntry_Click;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            row.Child = grid;
            row.MouseEnter += (s, e) => { row.Background = BgHover; btn.Visibility = Visibility.Visible; };
            row.MouseLeave += (s, e) => { row.Background = BgTransparent; btn.Visibility = Visibility.Collapsed; };

            return row;
        }

        private Border MakeNativeEntry(string label, string iconEmoji)
        {
            var row = new Border
            {
                Padding = new Thickness(12, 7, 12, 7),
                MinHeight = 32,
                CornerRadius = new CornerRadius(4),
                Background = BgTransparent,
                Cursor = WpfCursors.Arrow,
                Opacity = 0.5,
            };

            var sp = new StackPanel { Orientation = WpfOrientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = iconEmoji, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = TxtMuted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            row.Child = sp;
            return row;
        }

        private Border MakeFooterEntry()
        {
            var row = new Border { Padding = new Thickness(12, 7, 12, 7), MinHeight = 32, CornerRadius = new CornerRadius(4), Background = BgTransparent, Cursor = WpfCursors.Arrow };
            var sp = new StackPanel { Orientation = WpfOrientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = "\u229E", FontSize = 14, Foreground = TxtMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            sp.Children.Add(new TextBlock { Text = "Mostrar mais opções", Foreground = TxtMuted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            row.Child = sp;
            return row;
        }

        private Border MakeSeparator() => new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
            Margin = new Thickness(12, 3, 12, 3),
        };

        // ================================================================
        // BACKUP
        // ================================================================
        private void UpdateBackupStatus(bool backupExists, DateTime? backupTime)
        {
            if (backupExists && backupTime.HasValue)
            {
                BackupStatus.Text = $"Backup salvo em: {backupTime.Value:dd/MM/yyyy HH:mm:ss}";
                BtnRestoreBackup.Visibility = Visibility.Visible;
            }
            else
            {
                BackupStatus.Text = "Nenhum backup disponível.";
                BtnRestoreBackup.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnBackupNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                await Task.Run(() => SystemTweaks.BackupUserContextMenu());
                bool bk = SystemTweaks.ContextMenuBackupExists();
                DateTime? bt = SystemTweaks.GetContextMenuBackupTime();
                UpdateBackupStatus(bk, bt);
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowSuccess("BACKUP", "Backup salvo com sucesso.");
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        private async void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                (bool Success, string Message) result = (false, "");
                await Task.Run(() => result = SystemTweaks.RestoreUserContextMenu());

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (result.Success)
                        mw.ShowSuccess("BACKUP RESTAURADO", result.Message);
                    else
                        mw.ShowError("ERRO", result.Message);
                }

                await LoadAll();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }

        // ================================================================
        // REMOVE ENTRY (from simulated menu)
        // ================================================================
        private async void BtnRemoveEntry_Click(object sender, RoutedEventArgs e)
        {
            if (sender is WpfButton btn && btn.Tag is SystemTweaks.ContextMenuEntry entry)
            {
                if (_isLoading) return;
                _isLoading = true;
                try
                {
                    bool removed = false;
                    await Task.Run(() => removed = SystemTweaks.RemoveUserContextMenuEntry(entry.Location, entry.Name));

                    if (removed)
                    {
                        SystemTweaks.ClearContextMenuPrefsForEntryName(entry.Name);
                        if (Application.Current.MainWindow is MainWindow mw)
                            mw.ShowSuccess("ENTRADA REMOVIDA", $"'{entry.DisplayName}' foi removida do menu de contexto.");
                        await LoadAll();
                        UpdateQuickAddStates();
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                finally { _isLoading = false; }
            }
        }

        // ================================================================
        // QUICK-ADD TOGGLE
        // ================================================================
        private async void QuickAddToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is not WpfCheckBox chk || chk.Tag is not QuickAddItem item) return;

            _isLoading = true;
            try
            {
                bool targetActive = chk.IsChecked == true;
                chk.Foreground = targetActive ? TxtGold : TxtMuted;

                await Task.Run(() =>
                {
                    if (targetActive) item.Add();
                    else item.Remove();
                    // Persiste para o startup reaplicar com a config mais recente
                    SystemTweaks.SaveContextMenuPref(item.Key, targetActive);
                });

                await LoadAll();
                UpdateQuickAddStates();
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            finally { _isLoading = false; }
        }
    }
}
