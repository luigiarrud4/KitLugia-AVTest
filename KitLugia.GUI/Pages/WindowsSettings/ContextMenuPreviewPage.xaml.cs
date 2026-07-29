using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class ContextMenuPreviewPage : Page
    {
        private List<SystemTweaks.ContextMenuEntry> _entries = new();
        private bool _isLoading;

        private static readonly (string label, string emoji, string[] prefixes)[] ContextModes = {
            ("Arquivo (qualquer tipo)", "\U0001F4C4", new[] { @"*\shell", @"*\shellex" }),
            ("Pasta", "\U0001F4C1", new[] { @"Directory\shell", @"Directory\shellex", @"Folder\shell", @"Folder\shellex" }),
            ("Fundo / Desktop", "\U0001F5A5\uFE0F", new[] { @"Directory\Background\", @"DesktopBackground\" }),
            ("Unidade (C:, D:)", "\U0001F4BE", new[] { @"Drive\" }),
            (".exe", "\u2699\uFE0F", new[] { @"exefile\" }),
            (".bat / .cmd", "\U0001F4DD", new[] { @"batfile\", @"cmdfile\" }),
            (".reg", "\U0001F4DD", new[] { @"regfile\" }),
            ("Imagem (.png, .jpg...)", "\U0001F5BC\uFE0F", new[] { @"SystemFileAssociations\image\" }),
            ("V\u00EDdeo (.mp4, .avi...)", "\U0001F3AC", new[] { @"SystemFileAssociations\video\" }),
            ("\u00C1udio (.mp3, .wav...)", "\U0001F3B5", new[] { @"SystemFileAssociations\audio\" }),
            ("Biblioteca", "\U0001F4DA", new[] { @"LibraryFolder\" }),
            ("Todos objetos", "\U0001F5C2\uFE0F", new[] { @"AllFileSystemObjects\" }),
        };

        public ContextMenuPreviewPage()
        {
            InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                await Task.Run(() =>
                {
                    var entries = SystemTweaks.GetAllUserContextMenuEntries();
                    Dispatcher.Invoke(() =>
                    {
                        _entries = entries;
                        BuildSelector();
                    });
                });
            };
        }

        private void BuildSelector()
        {
            CtxSelector.Items.Clear();
            foreach (var mode in ContextModes)
            {
                CtxSelector.Items.Add(new ComboBoxItem
                {
                    Content = $"{mode.emoji}  {mode.label}",
                    Tag = mode,
                    Foreground = System.Windows.Media.Brushes.White,
                    Background = System.Windows.Media.Brushes.Transparent,
                });
            }
            if (CtxSelector.Items.Count > 0)
            {
                CtxSelector.SelectedIndex = 0;
                RebuildMenu();
            }
        }

        private void CtxSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            RebuildMenu();
        }

        private void RebuildMenu()
        {
            MenuItemsPanel.Children.Clear();

            if (_entries.Count == 0 || CtxSelector.SelectedIndex < 0)
            {
                SimulatedMenu.Visibility = Visibility.Collapsed;
                return;
            }

            var mode = ContextModes[CtxSelector.SelectedIndex];
            var matched = _entries
                .Where(e => mode.prefixes.Any(p =>
                    e.Location.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();

            if (matched.Count == 0)
            {
                SimulatedMenu.Visibility = Visibility.Collapsed;
                EntriesEmpty.Visibility = Visibility.Visible;
                return;
            }

            SimulatedMenu.Visibility = Visibility.Visible;
            EntriesEmpty.Visibility = Visibility.Collapsed;

            var byName = matched.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            var shell = byName.Where(e => e.Type == "shell").ToList();
            var shellex = byName.Where(e => e.Type == "shellex").ToList();

            bool hasShell = shell.Count > 0;
            bool hasShellex = shellex.Count > 0;

            foreach (var e in shell)
                MenuItemsPanel.Children.Add(MakeItem(e));

            if (hasShell && hasShellex)
                MenuItemsPanel.Children.Add(Separator());

            foreach (var e in shellex)
                MenuItemsPanel.Children.Add(MakeItem(e));

            MenuItemsPanel.Children.Add(Separator());
            MenuItemsPanel.Children.Add(Footer());
        }

        private static Border Separator()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
                Margin = new Thickness(12, 3, 12, 3),
            };
        }

        private static Border Footer()
        {
            var b = new Border { Padding = new Thickness(14, 11, 14, 11) };
            var s = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            s.Children.Add(new TextBlock
            {
                Text = "\u229E",
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 170)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            s.Children.Add(new TextBlock
            {
                Text = "  Mostrar mais opções",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 170)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            b.Child = s;
            return b;
        }

        private Border MakeItem(SystemTweaks.ContextMenuEntry entry)
        {
            bool isHKLM = entry.Source == "HKLM";

            var it = new Border
            {
                Padding = new Thickness(12, 8, 8, 8),
                MinHeight = 34,
                CornerRadius = new CornerRadius(4),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = isHKLM ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                Tag = entry,
            };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tb = new TextBlock
            {
                Text = entry.DisplayName,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(tb, 0);
            g.Children.Add(tb);

            var btn = new System.Windows.Controls.Button
            {
                Content = isHKLM ? "\U0001F512" : "\u2716",
                FontSize = 11,
                Padding = new Thickness(5, 1, 5, 1),
                MinWidth = 22,
                MinHeight = 22,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = entry,
                IsEnabled = !isHKLM,
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(isHKLM ? Color.FromRgb(50, 50, 55) : Color.FromRgb(70, 25, 25)),
                Foreground = new SolidColorBrush(isHKLM ? Color.FromRgb(140, 140, 140) : Color.FromRgb(221, 80, 80)),
                BorderBrush = new SolidColorBrush(isHKLM ? Color.FromRgb(70, 70, 75) : Color.FromRgb(100, 35, 35)),
                BorderThickness = new Thickness(1),
                ToolTip = isHKLM ? "Sistema" : "Remover",
            };
            if (!isHKLM)
                btn.Click += BtnRemove_Click;
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);

            it.Child = g;

            var defBg = System.Windows.Media.Brushes.Transparent;
            var hovBg = new SolidColorBrush(Color.FromRgb(50, 50, 65));

            it.MouseEnter += (s, e) => { it.Background = hovBg; btn.Visibility = Visibility.Visible; };
            it.MouseLeave += (s, e) => { it.Background = defBg; btn.Visibility = Visibility.Collapsed; };

            return it;
        }

        private async void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is SystemTweaks.ContextMenuEntry entry)
            {
                if (_isLoading) return;
                _isLoading = true;
                try
                {
                    bool removed = false;
                    await Task.Run(() => { removed = SystemTweaks.RemoveUserContextMenuEntry(entry.Location, entry.Name); });

                    if (removed)
                    {
                        _entries.Remove(entry);
                        RebuildMenu();
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                finally { _isLoading = false; }
            }
        }
    }
}
