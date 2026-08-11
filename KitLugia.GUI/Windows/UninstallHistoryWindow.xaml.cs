using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using KitLugia.Core;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Windows
{
    /// <summary>Wrapper de exibicao: adiciona Summary calculado ao entry do historico.</summary>
    public class HistoryItemViewModel
    {
        public UninstallHistoryEntry Entry { get; }
        public HistoryItemViewModel(UninstallHistoryEntry e) => Entry = e;

        public DateTime Timestamp => Entry.Timestamp;
        public string AppName => string.IsNullOrEmpty(Entry.AppName) ? "(sem nome)" : Entry.AppName;
        public string Summary
        {
            get
            {
                var parts = new List<string>
                {
                    $"{Entry.FilesDeleted} arquivo(s)/pasta(s)",
                    $"{Entry.RegistryDeleted} chave(s)"
                };
                if (Entry.FilesBackedUp.Count + Entry.RegistryBackups.Count > 0)
                    parts.Add($"{Entry.FilesBackedUp.Count + Entry.RegistryBackups.Count} backup(s) restauráveis");
                return string.Join(" · ", parts);
            }
        }
    }

    public partial class UninstallHistoryWindow : Window
    {
        private List<UninstallHistoryEntry>? _entries;

        public UninstallHistoryWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
            HistoryList.SelectionChanged += (_, _) =>
            {
                bool has = HistoryList.SelectedItem is HistoryItemViewModel;
                BtnRestore.IsEnabled = has;
                BtnDelete.IsEnabled = has;
                TxtDetail.Text = has
                    ? "Backups em %LOCALAPPDATA%\\KitLugia — restauram arquivos e chaves de registro (.reg) mesmo após reiniciar o PC."
                    : "";
            };
        }

        private void Refresh()
        {
            _entries = UninstallHistory.Load();
            HistoryList.ItemsSource = _entries
                .Select(e => new HistoryItemViewModel(e)).ToList();
            TxtCount.Text = $"{_entries.Count} registro(s)";
        }

        private async void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is not HistoryItemViewModel vm) return;
            var entry = UninstallHistory.Find(vm.Entry.Id);
            if (entry == null) return;

            var msg = $"? Restaurar backup de \"{entry.AppName}\" do dia {entry.Timestamp:dd/MM/yyyy HH:mm}?\n\n" +
                      $"{entry.FilesBackedUp.Count} arquivo(s)/pasta(s)\n" +
                      $"{entry.RegistryBackups.Count} backup(s) de registro (.reg)";
            if (MessageBox.Show(msg, "Restaurar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            this.Cursor = System.Windows.Input.Cursors.Wait;
            int filesOk = 0, regOk = 0;
            var errors = new List<string>();
            await Task.Run(() =>
            {
                foreach (var fb in entry.FilesBackedUp)
                {
                    var parts = fb.Split('|', 2);
                    if (parts.Length != 2) continue;
                    try { DeepUninstaller.RestoreFileBackup(parts[1], parts[0]); filesOk++; }
                    catch (Exception ex) { errors.Add(parts[0] + " -> " + ex.Message); }
                }
                foreach (var regFile in entry.RegistryBackups)
                {
                    try { DeepUninstaller.RestoreRegistryBackup(regFile); regOk++; }
                    catch (Exception ex) { errors.Add(regFile + " -> " + ex.Message); }
                }
            });
            this.Cursor = System.Windows.Input.Cursors.Arrow;

            string info = $"Restaurados: {filesOk} arquivo(s)/pasta(s), {regOk} chave(s).\n\n" +
                          "Se o app original ainda estiver instalado, os arquivos antigos podem ter sido sobrescritos.";
            if (errors.Count > 0)
                info += $"\n\nFalhas:\n{string.Join("\n", errors.Take(5))}";
            MessageBox.Show(info, "Restauração concluída", MessageBoxButton.OK,
                errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Refresh();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryList.SelectedItem is not HistoryItemViewModel vm) return;
            if (MessageBox.Show($"Excluir \"{vm.AppName}\" do histórico? Os backups em disco também serão apagados.",
                "Excluir", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            UninstallHistory.Remove(vm.Entry.Id);
            Refresh();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

        private void BtnClose_MouseDown(object sender, MouseButtonEventArgs e) => Close();
    }
}