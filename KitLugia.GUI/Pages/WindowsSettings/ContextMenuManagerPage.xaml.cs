using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

namespace KitLugia.GUI.Pages.WindowsSettings;

public partial class ContextMenuManagerPage : Page
{
    private List<ContextMenuEntry> _all = new();
    private ContextMenuEntry? _selected;
    private bool _loaded;

    public ContextMenuManagerPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                await LoadAsync();
            }
        };
        Unloaded += (_, _) => Cleanup();
    }

    public void Cleanup()
    {
        ItemsList.ItemsSource = null;
        _all.Clear();
        _selected = null;
        _loaded = false;
    }

    private async Task LoadAsync()
    {
        if (TxtCount == null) return;
        TxtCount.Text = "Escaneando registro...";
        try
        {
            _all = await Task.Run(() => ContextMenuManager.EnumerateAll());
            ApplyFilter();
            int sys = _all.Count(e => e.IsSystem);
            TxtCount.Text = $"{_all.Count} itens — {sys} do Windows, {_all.Count - sys} de terceiros";
        }
        catch (Exception ex)
        {
            TxtCount.Text = "Erro ao escanear: " + ex.Message;
        }
    }

    private sealed class Vm
    {
        public ContextMenuEntry E = null!;
        public string DisplayName => E.DisplayName;
        public string Scope => E.Scope;
        public string Kind => E.Kind;
        public string DllShort => string.IsNullOrEmpty(E.DllPath) ? "" : "• " + System.IO.Path.GetFileName(E.DllPath);
        public Visibility IsSystemBadgeVisible => E.IsSystem ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IsThirdPartyBadgeVisible => !E.IsSystem ? Visibility.Visible : Visibility.Collapsed;
        public Visibility IsDisabledBadgeVisible => E.IsDisabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        // XAML dispara SelectionChanged durante InitializeComponent (IsSelected=True do 1º item)
        // quando TxtSearch/ItemsList ainda não existem — ignorar.
        if (TxtSearch == null || CmbFilter == null || ItemsList == null || SimulatedMenu == null) return;

        var query = TxtSearch.Text?.Trim() ?? "";
        int f = CmbFilter.SelectedIndex;

        IEnumerable<ContextMenuEntry> q = _all;
        if (!string.IsNullOrEmpty(query))
            q = q.Where(e =>
                e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Scope.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (e.DllPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        q = f switch
        {
            1 => q.Where(e => e.IsSystem),
            2 => q.Where(e => !e.IsSystem),
            3 => q.Where(e => e.IsDisabled),
            4 => q.Where(e => !e.IsDisabled),
            _ => q,
        };

        var list = q.Select(e => new Vm { E = e }).ToList();
        ItemsList.ItemsSource = list;
        UpdateSimulation(list);
    }

    private void UpdateSimulation(List<Vm> items)
    {
        SimulatedMenu.Children.Clear();

        foreach (var vm in items.Take(40))
        {
            var row = new TextBlock
            {
                Text = (vm.E.IsDisabled ? "⊘  " : "") + vm.E.DisplayName,
                FontSize = 12.5,
                Foreground = vm.E.IsDisabled ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66))
                            : vm.E.IsSystem ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xDD, 0xFF))
                            : System.Windows.Media.Brushes.White,
                Padding = new Thickness(10, 5, 8, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = vm.E.IsDisabled ? "DESABILITADO — não aparece no menu real" : null,
            };
            SimulatedMenu.Children.Add(row);

            var next = items.IndexOf(vm) + 1 < items.Count ? items[items.IndexOf(vm) + 1] : null;
            if (next != null && next.E.Scope != vm.E.Scope)
                SimulatedMenu.Children.Add(new Separator
                {
                    Margin = new Thickness(8, 2, 8, 2),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x50, 0x50, 0x50))
                });
        }

        if (items.Count > 40)
            SimulatedMenu.Children.Add(new TextBlock
            {
                Text = $"... +{items.Count - 40} itens",
                FontSize = 11,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                Padding = new Thickness(10, 5, 8, 5)
            });
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Vm vm) return;
        _selected = vm.E;

        TxtSelName.Text = _selected.DisplayName;
        var origin = _selected.IsSystem ? "Componente do Windows" : "Aplicação de terceiros";
        TxtSelDetail.Text = $"{origin}\nEscopo: {_selected.Scope}\nTipo: {_selected.Kind}" +
                            (_selected.Clsid != null ? $"\nCLSID: {_selected.Clsid}" : "") +
                            (_selected.Command != null ? $"\nComando: {_selected.Command}" : "");
        BtnToggle.Content = _selected.IsDisabled ? "Habilitar item" : "Desabilitar item";
        BtnDelete.IsEnabled = true;
        BtnToggle.IsEnabled = true;
        TxtBackupInfo.Text = "";
    }

    private async void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        bool wasDisabled = _selected.IsDisabled;
        var (ok, msg) = wasDisabled
            ? ContextMenuManager.Enable(_selected)
            : ContextMenuManager.Disable(_selected);

        if (!ok)
        {
            ShowError(msg);
            return;
        }

        if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            mw.ShowInfo("MENU DE CONTEXTO", $"{msg}.\n\nSe o menu real não atualizar: Ctrl+Shift+ESC → Windows Explorer → Reiniciar.");

        await LoadAsync();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Deletar \"{_selected.DisplayName}\" do menu de contexto?\n\n" +
            "Um backup .reg será salvo ANTES da deleção.\n" +
            $"Chave: {_selected.KeyPath}",
            "Confirmar Deleção", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var (ok, msg, backupPath) = ContextMenuManager.Delete(_selected);
        TxtBackupInfo.Text = ok ? "✅ Backup: " + backupPath : "";
        if (!ok) ShowError(msg);
        else if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            mw.ShowSuccess("MENU DE CONTEXTO", msg);

        _ = LoadAsync();
    }

    private async void BtnRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "Restaurar TODAS as alterações feitas pelo KitLugia no menu de contexto?\n\n" +
            "Remove todos os bloqueios de handlers COM e sombras LegacyDisable criados por nós.\n" +
            "Itens DELETADOS não voltam (use os backups .reg em KitLugia\\Backups\\ContextMenu).",
            "Restaurar Tudo", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        BtnRestoreAll.IsEnabled = false;
        var (restored, errors) = await Task.Run(() => ContextMenuManager.RestoreAllKitChanges());
        BtnRestoreAll.IsEnabled = true;

        if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            mw.ShowInfo("RESTAURAÇÃO", $"{restored} itens restaurados ({errors} erros).");

        await LoadAsync();
    }

    private void ShowError(string msg)
    {
        if (System.Windows.Application.Current.MainWindow is MainWindow mw)
            mw.ShowError("MENU DE CONTEXTO", msg);
    }

}
