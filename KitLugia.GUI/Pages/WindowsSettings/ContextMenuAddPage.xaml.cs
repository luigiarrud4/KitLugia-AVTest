using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

namespace KitLugia.GUI.Pages.WindowsSettings;

public partial class ContextMenuAddPage : Page
{
    private List<ContextMenuQuickAdd.QuickItem> _items = new();
    private bool _loaded;

    public ContextMenuAddPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                await Task.Run(() => { }); // yield
                BuildCards();
            }
        };
        Unloaded += (_, _) => Cleanup();
    }

    public void Cleanup()
    {
        QuickAddGrid.Children.Clear();
        _items.Clear();
        _loaded = false;
    }

    private void BuildCards()
    {
        try
        {
            _items = ContextMenuQuickAdd.GetItems();
            Dispatcher.Invoke(() =>
            {
                QuickAddGrid.Children.Clear();
                foreach (var item in _items)
                {
                    item.IsAdded = SafeCheck(item);
                    QuickAddGrid.Children.Add(BuildCard(item));
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Unknown", $"AddPage cards: {ex.Message}");
        }
    }

    private bool SafeCheck(ContextMenuQuickAdd.QuickItem item)
    {
        try { return item.Check(); } catch { return false; }
    }

    private Border BuildCard(ContextMenuQuickAdd.QuickItem item)
    {
        var added = item.IsAdded;

        var icon = new TextBlock
        {
            Text = item.Emoji,
            FontSize = 30,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var title = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = System.Windows.Media.Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };

        var desc = new TextBlock
        {
            Text = item.Description,
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xAA)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 10),
        };

        var noteHeader = new TextBlock
        {
            Text = "⚡ VERSÃO SUPER:",
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xB8, 0x4E)),
            Margin = new Thickness(0, 0, 0, 4),
        };

        var note = new TextBlock
        {
            Text = item.SuperNote,
            FontSize = 11,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x88, 0x66)),
            TextWrapping = TextWrapping.Wrap,
        };

        var statusBadge = new Border
        {
            Background = added
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x3A, 0x24))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            BorderBrush = added
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0xAA, 0x55))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = added ? "✅ ATIVO NO MENU" : "○ não adicionado",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = added
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0xDD, 0x99))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
            },
            Margin = new Thickness(0, 10, 0, 10),
        };

        var btn = new System.Windows.Controls.Button
        {
            Content = added ? "Remover do menu" : "➕ Adicionar ao menu",
            Height = 36,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = added
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x2A, 0x1E))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x4A, 0x2A)),
            Foreground = added
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x99))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0xEE, 0xAA)),
        };
        btn.Click += async (_, _) => await ToggleAsync(item);

        var stack = new StackPanel();
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(desc);
        stack.Children.Add(noteHeader);
        stack.Children.Add(note);
        stack.Children.Add(statusBadge);
        stack.Children.Add(btn);

        return new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x14)),
            BorderBrush = added
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x6A, 0x3E))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x3A, 0x2E)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Margin = new Thickness(0, 0, 14, 14),
            Child = stack,
        };
    }

    private async Task ToggleAsync(ContextMenuQuickAdd.QuickItem item)
    {
        try
        {
            if (item.IsAdded)
                await Task.Run(() =>
                {
                    item.Remove();
                    SystemTweaks.SaveContextMenuPref(item.Id, false);
                });
            else
                await Task.Run(() =>
                {
                    item.Add();
                    SystemTweaks.SaveContextMenuPref(item.Id, true);
                });

            if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo("SUPER COMANDOS",
                    $"{item.DisplayName}: {(item.IsAdded ? "removido" : "adicionado")}.\n\n" +
                    "O menu real atualiza quando o Explorer recarregar (abra uma pasta nova ou reinicie o Explorer: Ctrl+Shift+ESC → Windows Explorer → Reiniciar).");

            BuildCards(); // re-renderiza todos os cards com o estado novo
        }
        catch (Exception ex)
        {
            if (System.Windows.Application.Current.MainWindow is MainWindow mw)
                mw.ShowError("MENU DE CONTEXTO", ex.Message);
        }
    }
}
