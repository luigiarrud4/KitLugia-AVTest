using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using KitLugia.Core;

namespace KitLugia.GUI.Windows
{
    /// <summary>
    /// Janela "Perfil de Otimização de Serviços" (estilo PresetsPersonalizados da PrivacyPage):
    /// lista TODOS os serviços que o perfil vai desativar, cada um com checkbox,
    /// bolinha de risco e o 'i' de informação (tooltip) explicando o porquê.
    /// Usuário marca/desmarca; desmarcado volta ao padrão do Windows.
    /// </summary>
    public partial class ServicePresetWindow : Window
    {
        private List<ServicePresetRow> _allItems = new();
        private readonly ObservableCollection<ServicePresetRow> _displayItems = new();

        /// <summary>Itens visíveis (a lista inteira do perfil selecionado).</summary>
        public ObservableCollection<ServicePresetRow> DisplayItems => _displayItems;

        /// <summary>Resultado: itens que serão processados (marcados + desmarcados).</summary>
        public List<BackgroundProcessManager.ServicePresetItem> ResultItems { get; private set; } = new();
        /// <summary>True quando o perfil é "Restaurar" (marcado = voltar ao padrão).</summary>
        public bool IsRestore { get; private set; }

        public ServicePresetWindow(string initialPreset = "Safe")
        {
            InitializeComponent();
            DataContext = this;

            // Seleciona o perfil inicial
            foreach (System.Windows.Controls.ComboBoxItem item in CboPreset.Items)
            {
                if (item.Tag?.ToString() == initialPreset) { CboPreset.SelectedItem = item; break; }
            }
            if (CboPreset.SelectedItem == null) CboPreset.SelectedIndex = 0;
        }

        private void CboPreset_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            string preset = (CboPreset.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Safe";
            LoadPreset(preset);
        }

        private void LoadPreset(string preset)
        {
            IsRestore = preset == "Restore";

            // Texto do perfil (mesma copy do painel da página)
            (string title, string desc) = preset switch
            {
                "Safe" => ("🛡️ Perfil Seguro",
                    "Desativa apenas serviços inúteis: Fax, RetailDemo, Spooler e PrintWorkflow. Ideal se você não usa impressora."),
                "Gamer" => ("🎮 Perfil Gamer",
                    "Desativa telemetria, SysMain, indexação (WSearch), Xbox, Mapas, Geo-loc e diagnósticos — tudo que consome RAM e CPU em background sem necessidade."),
                "GamerPlus" => ("🚀 Perfil Gamer+",
                    "Tudo do Gamer + desativa updaters de terceiros (Adobe, Google, Discord, Punkbuster, Apple...). Máximo de RAM livre."),
                "Restore" => ("⏪ Restaurar Padrão",
                    "Reverte a lista segura para o padrão de fábrica do Windows. Use se algo parar de funcionar. Desmarque o que NÃO quer restaurar."),
                _ => ("Perfil", "")
            };
            TxtPresetTitle.Text = title;
            TxtPresetDescription.Text = desc;

            // Carrega itens do Core (com explicações)
            var items = BackgroundProcessManager.GetServicePresetItems(preset);
            _allItems = items.Select(i => new ServicePresetRow(i)).ToList();
            foreach (var row in _allItems)
                row.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(ServicePresetRow.IsDisabled)) UpdateCount(); };

            _displayItems.Clear();
            foreach (var row in _allItems) _displayItems.Add(row);
            UpdateCount();
        }

        private void BtnQuick_Click(object sender, RoutedEventArgs e)
        {
            string mode = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "All";
            foreach (var row in _allItems)
                row.IsDisabled = mode == "All";
            UpdateCount();
        }

        private int CountSelected() => _allItems.Count(i => i.IsDisabled);

        private void UpdateCount()
        {
            TxtPresetCount.Text = $"{CountSelected()} de {_allItems.Count} marcados";
        }

        private void ItemRow_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Click no CheckBox já altera via binding — não duplique o toggle
            if (FindAncestor<System.Windows.Controls.CheckBox>(e.OriginalSource as DependencyObject) != null) return;
            if (sender is System.Windows.FrameworkElement fe && fe.DataContext is ServicePresetRow item)
                item.IsDisabled = !item.IsDisabled;
        }

        private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
        {
            while (from != null && from is not T)
                from = System.Windows.Media.VisualTreeHelper.GetParent(from);
            return from as T;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            ResultItems = _allItems.Select(r => r.Item).ToList();

            int total = CountSelected();
            string confirmMsg = IsRestore
                ? $"Restaurar {total} serviços ao padrão do Windows?\n\nItens desmarcados não serão tocados.\nContinuar?"
                : $"Desligar {total} serviços?\n\n" +
                  (total == _allItems.Count ? "" : $"{_allItems.Count - total} desmarcados vão voltar ao padrão do Windows.\n") +
                  "Os serviços podem ser reativados aqui mesmo a qualquer momento.\nContinuar?";

            var confirm = System.Windows.MessageBox.Show(this, confirmMsg, "Perfil de Otimização de Serviços",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            DialogResult = true;
            Close();
        }

        private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* janela maximizada etc */ }
            }
        }

        private void BtnClose_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
    }

    /// <summary>Linha da lista: envolve ServicePresetItem com INPC para o checkbox funcionar.</summary>
    public class ServicePresetRow : INotifyPropertyChanged
    {
        private bool _isDisabled;

        public ServicePresetRow(BackgroundProcessManager.ServicePresetItem item) { Item = item; _isDisabled = item.IsDisabled; }

        public BackgroundProcessManager.ServicePresetItem Item { get; }

        public string ServiceName => Item.ServiceName;
        public string Display => string.IsNullOrWhiteSpace(Item.DisplayName) ? Item.ServiceName : Item.DisplayName;
        public string Reason => Item.Reason;
        public string Manufacturer => Item.Manufacturer;

        /// <summary>O 'i' de informação completo: por quê + o que quebra + risco.</summary>
        public string Tooltip =>
            $"ℹ️ {Display} ({ServiceName})\n" +
            $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
            $"POR QUE DESLIGAR:\n{Item.Reason}\n\n" +
            $"⚠ SE VOCÊ USA A FUNÇÃO:\n{Item.Warning}\n\n" +
            $"Risco: {RiskLabel} | Fabricante: {Item.Manufacturer}" +
            (Item.Safety == ServiceSafetyLevel.Dangerous ? "\n🚨 SERVIÇO CRÍTICO: desativar pode desestabilizar o sistema." : "");

        public string RiskLabel => Item.Safety switch
        {
            ServiceSafetyLevel.Safe => "Baixo",
            ServiceSafetyLevel.Caution => "Médio",
            ServiceSafetyLevel.Dangerous => "Alto (crítico)",
            _ => "Desconhecido"
        };

        public bool IsDisabled
        {
            get => _isDisabled;
            set
            {
                if (_isDisabled == value) return;
                _isDisabled = value;
                Item.IsDisabled = value;
                OnPropertyChanged(nameof(IsDisabled));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(RowBrush));
            }
        }

        public string StateText =>
            !IsDisabled ? "— não será tocado"
            : Item.Safety == ServiceSafetyLevel.Dangerous ? "⚠ SERÁ DESLIGADO (CRÍTICO)"
            : "✅ SERÁ DESLIGADO";

        public string StateBrush =>
            !IsDisabled ? "#666666"
            : Item.Safety == ServiceSafetyLevel.Dangerous ? "#EF9A9A"
            : "#7ED07E";

        public string RowBrush => IsDisabled ? "#16E8B84E" : "#00FFFFFF";

        public string LevelBrush => Item.Safety switch
        {
            ServiceSafetyLevel.Safe => "#4CAF50",
            ServiceSafetyLevel.Caution => "#FFA500",
            _ => "#C42B1C"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
