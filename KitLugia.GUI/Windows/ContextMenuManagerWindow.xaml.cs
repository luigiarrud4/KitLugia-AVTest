using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Windows
{
    public partial class ContextMenuManagerWindow : Window
    {
        private List<ForceStopUnlockService.ContextMenuEntry> _allEntries = new();

        public ContextMenuManagerWindow()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadEntries();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private async System.Threading.Tasks.Task LoadEntries()
        {
            TxtStatus.Text = "Escaneando menu de contexto...";

            try
            {
                _allEntries = await System.Threading.Tasks.Task.Run(() =>
                    ForceStopUnlockService.ScanContextMenuEntries());

                ApplyFilter();
                TxtEntryCount.Text = $"{_allEntries.Count} entradas encontradas";
                TxtStatus.Text = $"Carregado: {_allEntries.Count} entradas em {_allEntries.Select(e => e.Root).Distinct().Count()} categorias";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro ao escanear: {ex.Message}";
            }
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var filtered = _allEntries.AsEnumerable();

            if (RdKit?.IsChecked == true)
                filtered = filtered.Where(e => e.IsKitEntry);
            else if (RdThirdParty?.IsChecked == true)
                filtered = filtered.Where(e => !e.IsKitEntry);
            else if (RdSystem?.IsChecked == true)
                filtered = filtered.Where(e => e.Name.Contains("open", StringComparison.OrdinalIgnoreCase) ||
                                               e.Name.Contains("explore", StringComparison.OrdinalIgnoreCase));

            EntryList.ItemsSource = filtered.ToList();
            TxtEntryCount.Text = $"{filtered.Count()} entradas visíveis";
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadEntries();
        }

        private async void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allEntries.Where(e => e.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TxtStatus.Text = "Nenhuma entrada selecionada para remover.";
                return;
            }

            // Confirm
            var names = string.Join("\n", selected.Take(10).Select(s => $"• {s.Label} ({s.Root})"));
            if (selected.Count > 10)
                names += $"\n... e mais {selected.Count - 10}";

            var confirm = MessageBox.Show(
                $"Remover {selected.Count} entrada(s) do menu de contexto?\n\n{names}",
                "Gerenciador de Menu de Contexto",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                var (removed, failed) = await System.Threading.Tasks.Task.Run(() =>
                    ForceStopUnlockService.RemoveSelectedEntries(selected));

                TxtStatus.Text = removed > 0
                    ? $"Removido(s): {removed} entrada(s). {(failed > 0 ? $"{failed} falhou." : "")}"
                    : "Nenhuma entrada foi removida.";

                await LoadEntries();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro ao remover: {ex.Message}";
            }
        }
    }
}
