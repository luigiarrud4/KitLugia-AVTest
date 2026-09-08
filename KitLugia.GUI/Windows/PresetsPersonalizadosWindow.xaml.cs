using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    /// <summary>
    /// Janela "Presets Personalizados" da PrivacyPage (estilo O&amp;O ShutUp10):
    /// abas de nivel no topo (Todos/Seguros/Moderados/Perigosos) filtrando uma
    /// lista COMPACTA de 1 linha por item; clique na linha marca/desmarca; cada
    /// item mostra se SERA ativado ou nao. Expose ToApply/ToRevert para a pagina.
    /// </summary>
    public partial class PresetsPersonalizadosWindow : Window
    {
        private List<PresetItem> _allItems = new List<PresetItem>();
        private readonly ObservableCollection<PresetItem> _displayItems = new ObservableCollection<PresetItem>();
        private string _levelFilter = "All";

        /// <summary>Itens visiveis (apos filtro da aba de nivel).</summary>
        public ObservableCollection<PresetItem> DisplayItems => _displayItems;

        /// <summary>Selecao final apos APLICAR (itens marcados).</summary>
        public List<OOShutUpManager.PrivacySetting> ToApply { get; private set; } = new List<OOShutUpManager.PrivacySetting>();
        /// <summary>Itens desmarcados - voltam ao padrao do Windows.</summary>
        public List<OOShutUpManager.PrivacySetting> ToRevert { get; private set; } = new List<OOShutUpManager.PrivacySetting>();

        public PresetsPersonalizadosWindow()
        {
            InitializeComponent();
            DataContext = this;
            Populate();
        }

        private void Populate()
        {
            _allItems = OOShutUpManager.GetPrivacySettings()
                .Select(s => new PresetItem(s))
                .ToList();

            foreach (var item in _allItems)
            {
                // Default seguro: apenas os Recomendados marcados (usuario opta nos outros niveis)
                item.IsSelected = item.Setting.Level == OOShutUpManager.PrivacyLevel.Recommended;
                item.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(PresetItem.IsSelected)) UpdateCount(); };
            }

            TabAll.Content = $"Todos ({_allItems.Count})";
            TabSafe.Content = $"\U0001F7E2 Seguros ({_allItems.Count(i => i.Setting.Level == OOShutUpManager.PrivacyLevel.Recommended)})";
            TabModerate.Content = $"\U0001F7E1 Moderados ({_allItems.Count(i => i.Setting.Level == OOShutUpManager.PrivacyLevel.Limited)})";
            TabDanger.Content = $"\U0001F534 Perigosos ({_allItems.Count(i => i.Setting.Level == OOShutUpManager.PrivacyLevel.NotRecommended)})";

            RebuildDisplay();
            UpdateCount();
        }

        private void TabLevel_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string filter)
            {
                _levelFilter = filter;
                RebuildDisplay();
            }
        }

        private void RebuildDisplay()
        {
            // Organiza: VERDES em cima, AMARELOS logo abaixo, VERMELHOS por ultimo
            // (ordem estavel dentro de cada nivel)
            _displayItems.Clear();
            foreach (var x in _allItems
                .Select((item, idx) => (item, idx))
                .Where(x => FilterMatch(x.item))
                .OrderBy(x => (int)x.item.Setting.Level)
                .ThenBy(x => x.idx))
            {
                _displayItems.Add(x.item);
            }
        }

        private bool FilterMatch(PresetItem item) =>
            _levelFilter == "All" || item.Setting.Level.ToString() == _levelFilter;

        private void ItemRow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Click no CheckBox ja altera via binding - nao duplique o toggle
            if (FindAncestor<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) != null) return;
            if (sender is System.Windows.FrameworkElement fe && fe.DataContext is PresetItem item)
                item.IsSelected = !item.IsSelected;
        }

        private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
        {
            while (from != null && from is not T)
                from = System.Windows.Media.VisualTreeHelper.GetParent(from);
            return from as T;
        }

        private void BtnPresetQuick_Click(object sender, RoutedEventArgs e)
        {
            string mode = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "Safe";
            SetSelection(mode);
        }

        private void SetSelection(string mode)
        {
            foreach (var item in _allItems)
            {
                item.IsSelected = mode switch
                {
                    "Safe" => item.Setting.Level == OOShutUpManager.PrivacyLevel.Recommended,
                    "SafeModerate" => item.Setting.Level != OOShutUpManager.PrivacyLevel.NotRecommended,
                    "All" => true,
                    _ => false
                };
            }
        }

        private int CountSelected() => _allItems.Count(i => i.IsSelected);

        private void UpdateCount()
        {
            TxtPresetCount.Text = $"{CountSelected()} de {_allItems.Count} selecionados";
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            ToApply = _allItems.Where(i => i.IsSelected).Select(i => i.Setting).ToList();
            ToRevert = _allItems.Where(i => !i.IsSelected).Select(i => i.Setting).ToList();

            int dangerous = ToApply.Count(s => s.Level == OOShutUpManager.PrivacyLevel.NotRecommended);
            string confirmMsg = dangerous > 0
                ? $"Aplicar {ToApply.Count} configurações?\n\n" +
                  $"⚠ {dangerous} são PERIGOSAS e podem quebrar recursos (Loja, Cortana, Windows Update).\n\n" +
                  $"Itens desmarcados ({ToRevert.Count}) voltam ao padrão do Windows.\nContinuar?"
                : $"Aplicar {ToApply.Count} configurações?\n\nItens desmarcados ({ToRevert.Count}) voltam ao padrão do Windows.\nContinuar?";

            var confirm = System.Windows.MessageBox.Show(this, confirmMsg, "Presets Personalizados",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            DialogResult = true;
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* janela maximizada etc */ }
            }
        }

        private void BtnClose_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
    }

    /// <summary>Item da lista compacta: checkbox + bolinha do nivel + nome +
    /// descricao + indicador do que acontecera (SERA ATIVADO / nao sera).</summary>
    public class PresetItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public PresetItem(OOShutUpManager.PrivacySetting setting) { Setting = setting; }

        public OOShutUpManager.PrivacySetting Setting { get; }
        public string Name => Setting.Name;
        public string Description => Setting.Description;
        public string Tooltip =>
            (Setting.IsService
                ? $"Serviço: {Setting.ServiceName}"
                : $"Registry: {Setting.RegistryPath}\\{Setting.ValueName}") +
            $"\nValor seguro: {Setting.SafeValue ?? "(nenhum)"} | Padrão Windows: {Setting.UnsafeValue ?? "(nenhum)"}" +
            $"\nNível: {Setting.Level}" +
            (Setting.Level == OOShutUpManager.PrivacyLevel.NotRecommended
                ? "\n⚠ PERIGOSO: pode quebrar recursos do sistema."
                : "");

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(RowBrush));
            }
        }

        public string StateText =>
            !_isSelected ? "— não será ativado"
            : Setting.Level == OOShutUpManager.PrivacyLevel.NotRecommended ? "⚠ SERÁ ATIVADO (PERIGOSO)"
            : "✅ SERÁ ATIVADO";

        public string StateBrush =>
            !_isSelected ? "#666666"
            : Setting.Level == OOShutUpManager.PrivacyLevel.NotRecommended ? "#EF9A9A"
            : "#7ED07E";

        public string RowBrush => _isSelected ? "#16E8B84E" : "#00FFFFFF";

        public string LevelBrush => Setting.Level switch
        {
            OOShutUpManager.PrivacyLevel.Recommended => "#4CAF50",
            OOShutUpManager.PrivacyLevel.Limited => "#FFA500",
            _ => "#C42B1C"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
