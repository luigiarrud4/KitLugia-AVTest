using KitLugia.Core;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;

namespace KitLugia.GUI.Windows
{
    public partial class DeepCleanupDialog : Window
    {
        public class CleanupItem : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string DisplayName { get; set; } = "";
            public string FullPath { get; set; } = "";
            public string IconChar { get; set; } = "\U0001F4C1";
            public string Group { get; set; } = "";

            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(); }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged([CallerMemberName] string? n = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private readonly ObservableCollection<CleanupItem> _files = new();
        private readonly ObservableCollection<CleanupItem> _registry = new();
        private readonly DeepUninstaller.UninstallResult _scanResult;
        private readonly string _appName;
        private bool _suppressSelectAll;
        private int _totalDeleted;

        public List<string> SelectedFiles { get; private set; } = new();
        public List<string> SelectedRegistry { get; private set; } = new();
        public bool HasConfirmed { get; private set; }

        public DeepCleanupDialog(string appName, DeepUninstaller.UninstallResult scanResult)
        {
            InitializeComponent();
            _appName = appName;
            _scanResult = scanResult;

            TitleText.Text = $"Deep Cleanup — {appName}";
            SubtitleText.Text = $"Revise os resíduos encontrados após a desinstalação";

            PopulateItems();
            UpdateCounts();
            UpdateStatus();
        }

        private void PopulateItems()
        {
            _files.Clear();
            _registry.Clear();

            // File leftovers
            foreach (var path in _scanResult.LeftoverFiles)
            {
                string name = path.TrimEnd('\\').Split('\\').LastOrDefault() ?? path;
                _files.Add(new CleanupItem
                {
                    DisplayName = name,
                    FullPath = path,
                    IconChar = "\U0001F4C1",
                    Group = "File"
                });
            }

            // Registry leftovers
            foreach (var reg in _scanResult.LeftoverRegistry)
            {
                string name = reg.Split('\\').LastOrDefault() ?? reg;
                _registry.Add(new CleanupItem
                {
                    DisplayName = name,
                    FullPath = reg,
                    IconChar = "\U0001F4D1",
                    Group = "Registry"
                });
            }

            FileList.ItemsSource = _files;
            RegList.ItemsSource = _registry;

            InfoBarText.Text = _scanResult.UninstallSuccess
                ? "Desinstalação concluída. Resíduos encontrados:"
                : "Desinstalação pode não ter sido totalmente bem-sucedida. Resíduos encontrados:";

            InfoBarCount.Text = $"{_files.Count} arquivo(s), {_registry.Count} registro(s)";
        }

        private void ChkSelectAllFiles_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSelectAll) return;
            bool val = ChkSelectAllFiles.IsChecked == true;
            foreach (var item in _files)
                item.IsSelected = val;
            UpdateCounts();
            UpdateStatus();
        }

        private void ChkSelectAllReg_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressSelectAll) return;
            bool val = ChkSelectAllReg.IsChecked == true;
            foreach (var item in _registry)
                item.IsSelected = val;
            UpdateCounts();
            UpdateStatus();
        }

        private void UpdateCounts()
        {
            int fSel = _files.Count(f => f.IsSelected);
            int rSel = _registry.Count(f => f.IsSelected);

            TxtFileCount.Text = $"{_files.Count} item(ns) — {fSel} selecionado(s)";
            TxtRegCount.Text = $"{_registry.Count} item(ns) — {rSel} selecionado(s)";

            BtnDeleteFiles.Content = $"\U0001F5D1 Remover Selecionados ({fSel})";
            BtnDeleteReg.Content = $"\U0001F5D1 Remover Selecionados ({rSel})";

            BtnDeleteFiles.IsEnabled = fSel > 0;
            BtnDeleteReg.IsEnabled = rSel > 0;

            // Update SelectAll check state without re-triggering
            _suppressSelectAll = true;
            ChkSelectAllFiles.IsChecked = _files.Count > 0 && _files.All(f => f.IsSelected);
            ChkSelectAllReg.IsChecked = _registry.Count > 0 && _registry.All(r => r.IsSelected);
            _suppressSelectAll = false;
        }

        private void UpdateStatus()
        {
            int totalSel = _files.Count(f => f.IsSelected) + _registry.Count(f => f.IsSelected);
            StatusText.Text = totalSel > 0
                ? $"{totalSel} item(ns) selecionado(s) — clique em Remover para limpar"
                : "Nenhum item selecionado";
        }

        private async void BtnDeleteFiles_Click(object sender, RoutedEventArgs e)
        {
            var selected = _files.Where(f => f.IsSelected).ToList();
            if (selected.Count == 0) return;

            if (MessageBox.Show(
                $"Excluir permanentemente {selected.Count} arquivo(s)/pasta(s)?\n\nEsta ação não pode ser desfeita.",
                "Confirmar Exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            this.Cursor = System.Windows.Input.Cursors.Wait;
            BtnDeleteFiles.IsEnabled = false;

            var result = new DeepUninstaller.UninstallResult();
            await Task.Run(() => DeepUninstaller.PerformCleanup(
                selected.Select(f => f.FullPath).ToList(),
                new List<string>(),
                result));

            int count = selected.Count;
            _totalDeleted += count;
            var remaining = _files.Where(f => !selected.Contains(f)).ToList();
            _files.Clear();
            foreach (var item in remaining)
                _files.Add(item);

            UpdateCounts();
            UpdateStatus();
            this.Cursor = System.Windows.Input.Cursors.Arrow;
            BtnDeleteFiles.IsEnabled = _files.Any(f => f.IsSelected);

            if (result.Errors.Count > 0)
                MessageBox.Show($"Erros ao excluir:\n{string.Join("\n", result.Errors.Take(5))}",
                    "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show($"{count} arquivo(s)/pasta(s) removidos com sucesso.",
                    "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnDeleteReg_Click(object sender, RoutedEventArgs e)
        {
            var selected = _registry.Where(r => r.IsSelected).ToList();
            if (selected.Count == 0) return;

            if (MessageBox.Show(
                $"Excluir permanentemente {selected.Count} chave(s) de registro?\n\nEsta ação não pode ser desfeita.",
                "Confirmar Exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            this.Cursor = System.Windows.Input.Cursors.Wait;
            BtnDeleteReg.IsEnabled = false;

            var result = new DeepUninstaller.UninstallResult();
            await Task.Run(() => DeepUninstaller.PerformCleanup(
                new List<string>(),
                selected.Select(r => r.FullPath).ToList(),
                result));

            int count = selected.Count;
            _totalDeleted += count;
            var remaining = _registry.Where(r => !selected.Contains(r)).ToList();
            _registry.Clear();
            foreach (var item in remaining)
                _registry.Add(item);

            UpdateCounts();
            UpdateStatus();
            this.Cursor = System.Windows.Input.Cursors.Arrow;
            BtnDeleteReg.IsEnabled = _registry.Any(r => r.IsSelected);

            if (result.Errors.Count > 0)
                MessageBox.Show($"Erros ao excluir:\n{string.Join("\n", result.Errors.Take(5))}",
                    "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show($"{count} chave(s) de registro removidas com sucesso.",
                    "Concluído", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            SelectedFiles = _files.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
            SelectedRegistry = _registry.Where(r => r.IsSelected).Select(r => r.FullPath).ToList();
            HasConfirmed = true;
            DialogResult = true;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!HasConfirmed)
            {
                SelectedFiles = _files.Where(f => f.IsSelected).Select(f => f.FullPath).ToList();
                SelectedRegistry = _registry.Where(r => r.IsSelected).Select(r => r.FullPath).ToList();
                HasConfirmed = true;
            }
            base.OnClosing(e);
        }
    }
}
