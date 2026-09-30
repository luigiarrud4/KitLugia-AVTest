using KitLugia.Core;
using KitLugia.GUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using MessageBox = System.Windows.MessageBox; // Resolve ambiguidade com WinForms
using Application = System.Windows.Application;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class PrivacyPage : Page
    {
        public ObservableCollection<PrivacyCategoryViewModel> Categories { get; set; } = new ObservableCollection<PrivacyCategoryViewModel>();
        private ObservableCollection<PrivacyCategoryViewModel> _allCategories { get; set; } = new ObservableCollection<PrivacyCategoryViewModel>();

        private string _currentFilter = "All";
        private string _searchText = "";
        private DispatcherTimer? _refreshTimer;
        private bool _isPrivacyOperation;

        public PrivacyPage()
        {
            InitializeComponent();
            DataContext = this;
            LoadData();
            InitializeTimer();
            this.Loaded += PrivacyPage_Loaded;

            // �� LIMPEZA: Para timer ao sair da página
            this.Unloaded += PrivacyPage_Unloaded;

            // �� CORRE�?�fO: Garantir que o filtro não é aplicado durante inicialização
            _currentFilter = "All";
            _searchText = "";
        }

        // �� CORRE�?�fO: Cleanup público para ser chamado via reflection pelo MainWindow
        public void Cleanup()
        {
            if (_refreshTimer != null)
                _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer?.Stop();
            _refreshTimer = null;
            this.Loaded -= PrivacyPage_Loaded;
            this.Unloaded -= PrivacyPage_Unloaded;
            _timersStarted = false;

            // �� LIMPEZA: Limpa DataContext e coleções
            Categories.Clear();
            this.DataContext = null;

            // �� LIMPEZA: Força GC para liberar memória imediatamente

            // �� LIMPEZA: Força Windows a liberar Working Set (reduz RAM no Task Manager)
        }

        private void PrivacyPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void InitializeTimer()
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += OnRefreshTimerTick;
            // só inicia no Loaded — não disputa com o layout da navegação
        }

        private bool _timersStarted;

        private void PrivacyPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_timersStarted) return; // evita timers duplicados se Loaded disparar de novo
            _timersStarted = true;
            _refreshTimer?.Start();
        }

        private void OnRefreshTimerTick(object? s, EventArgs e) => RefreshStatus();

        private void LoadData()
        {
            Categories.Clear();
            _allCategories.Clear();
            var rawCats = OOShutUpManager.GetPrivacyCategories();

            foreach (var cat in rawCats)
            {
                var vm = new PrivacyCategoryViewModel { Name = cat.Key };
                foreach (var setting in cat.Value)
                {
                    vm.Settings.Add(new PrivacySettingViewModel(setting, RefreshStatus, vm.RefreshAllEnabled));
                }
                vm.RefreshAllEnabled(); // Inicializa o estado do checkbox da categoria
                Categories.Add(vm);
                _allCategories.Add(vm);
            }
            RefreshStatus();
        }

        private void ApplyFilter()
        {
            Categories.Clear();

            foreach (var category in _allCategories)
            {
                // Filtra settings da categoria
                var filteredSettings = new ObservableCollection<PrivacySettingViewModel>();

                foreach (var setting in category.Settings)
                {
                    // Filtro por nível
                    bool levelMatch = _currentFilter == "All" || setting.Level.ToString() == _currentFilter;

                    // Filtro por texto
                    bool searchMatch = string.IsNullOrWhiteSpace(_searchText) ||
                                      setting.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                                      setting.Description.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

                    if (levelMatch && searchMatch)
                    {
                        filteredSettings.Add(setting);
                    }
                }

                // Adiciona categoria apenas se tiver settings após filtro
                if (filteredSettings.Count > 0)
                {
                    var filteredCategory = new PrivacyCategoryViewModel
                    {
                        Name = category.Name,
                        Settings = filteredSettings
                    };
                    filteredCategory.RefreshAllEnabled();
                    Categories.Add(filteredCategory);
                }
            }
        }

        private async void RefreshStatus()
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                var allSettings = Categories.SelectMany(c => c.Settings).ToList();

                // Ler registro em background
                var states = await Task.Run(() =>
                    allSettings.Select(s => (s, state: s.CheckRegistryState())).ToList()
                );

                // Aplicar na UI thread (pula settings toggled nos ultimos 3s)
                foreach (var (setting, state) in states)
                {
                    if (PrivacySettingViewModel.IsRecentlyToggled(setting.Name))
                        continue;

                    if (setting.IsEnabled != state)
                        setting.IsEnabled = state;
                }

                foreach (var cat in Categories)
                    cat.RefreshAllEnabled();

                // Protecao POR NIVEL: verdes / amarelos / vermelhos, cada um separado
                int safeTotal = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.Recommended);
                int modTotal = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.Limited);
                int dangerTotal = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.NotRecommended);
                int safeOn = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.Recommended && s.IsEnabled);
                int modOn = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.Limited && s.IsEnabled);
                int dangerOn = allSettings.Count(s => s.Level == OOShutUpManager.PrivacyLevel.NotRecommended && s.IsEnabled);

                TxtProtSafe.Text = $"{safeOn} / {safeTotal}";
                TxtProtModerate.Text = $"{modOn} / {modTotal}";
                TxtProtDanger.Text = $"{dangerOn} / {dangerTotal}";
                PbSafe.Value = safeTotal > 0 ? (double)safeOn / safeTotal * 100 : 0;
                PbModerate.Value = modTotal > 0 ? (double)modOn / modTotal * 100 : 0;
                PbDanger.Value = dangerTotal > 0 ? (double)dangerOn / dangerTotal * 100 : 0;

                int secured = allSettings.Count(s => s.IsEnabled);
                int total = allSettings.Count;
                int percent = total > 0 ? (int)((double)secured / total * 100) : 0;
                TxtPrivacyScore.Text = $"Resultado: {secured} de {total} proteções ativas ({percent}%)";
            }
            catch (Exception ex)
            {
                Logger.LogError("RefreshStatus", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        // --- Event Handlers dos Botões (Mantidos para simplicidade) ---

        private void BtnRefreshStatus_Click(object sender, RoutedEventArgs e) => RefreshStatus();

        // --- "Presets Personalizados": janela própria (padrão PathExplorerWindow) ---
        // A janela mostra o que SERÁ ativado e o que NÃO será em cada item.

        private async void BtnOpenPresets_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            var win = new KitLugia.GUI.Windows.PresetsPersonalizadosWindow
            {
                Owner = Window.GetWindow(this)
            };
            if (win.ShowDialog() != true) return;

            var toApply = win.ToApply;
            var toRevert = win.ToRevert;
            if (toApply.Count == 0 && toRevert.Count == 0) return;

            _isPrivacyOperation = true;
            BtnOpenPresets.IsEnabled = false;
            BtnResetAll.IsEnabled = false;
            string prevScore = TxtPrivacyScore.Text;
            TxtPrivacyScore.Text = $"⏳ Aplicando {toApply.Count + toRevert.Count}...";

            try
            {
                string taskId = Guid.NewGuid().ToString();
                BackgroundTaskTracker.Instance.RegisterTask(taskId, "Presets Personalizados", "Privacy");
                var result = await Task.Run(() => OOShutUpManager.ApplyCustomSelection(toApply, toRevert));
                BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Failed == 0);

                if (result.Failed > 0)
                    mw.ShowError("PRIVACIDADE", $"Concluído com falhas: {result.Applied} aplicadas, {result.Reverted} revertidas, {result.Failed} falharam. Veja o log.");
                else
                    mw.ShowSuccess("PRIVACIDADE", $"Concluído: {result.Applied} aplicadas, {result.Reverted} revertidas ao padrão.");

                RefreshStatus();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnOpenPresets_Click", ex.Message);
                TxtPrivacyScore.Text = prevScore;
                mw.ShowError("ERRO", "Falha ao aplicar: " + ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
                BtnOpenPresets.IsEnabled = true;
                BtnResetAll.IsEnabled = true;
            }
        }

        // --- RESETAR TUDO: desliga TODAS as proteções e reverte ao padrão do Windows ---
        // Substitui o antigo "Restaurar Padrão Windows" (mesma função RestoreDefaults,
        // agora com botão vermelho em destaque no card de presets + feedback completo).
        private async void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw == null) return;

            if (!await mw.ShowConfirmationDialog(
                "RESETAR TUDO?\n\n" +
                "• Todas as proteções de privacidade serão DESLIGADAS.\n" +
                "• Telemetria, diagnóstico e rastreamento voltam ao padrão do Windows.\n" +
                "• Serviços de telemetria (DiagTrack etc.) serão reativados.\n\n" +
                "Deseja continuar?"))
                return;

            _isPrivacyOperation = true;
            BtnResetAll.IsEnabled = false;
            BtnOpenPresets.IsEnabled = false;
            string prevScore = TxtPrivacyScore.Text;
            TxtPrivacyScore.Text = "⏳ Revertendo...";

            try
            {
                string taskId = Guid.NewGuid().ToString();
                BackgroundTaskTracker.Instance.RegisterTask(taskId, "Resetar Privacidade", "Privacy");
                var (success, message) = await Task.Run(() => OOShutUpManager.RestoreDefaults());
                BackgroundTaskTracker.Instance.CompleteTask(taskId, success);

                if (success)
                    mw.ShowSuccess("RESETADO", message);
                else
                    mw.ShowError("ERRO", message);

                RefreshStatus();
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnResetAll_Click", ex.Message);
                TxtPrivacyScore.Text = prevScore;
                mw.ShowError("ERRO", "Falha ao resetar: " + ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
                BtnResetAll.IsEnabled = true;
                BtnOpenPresets.IsEnabled = true;
            }
        }

        private async void BtnSaveUserConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var (success, message) = await Task.Run(() => OOShutUpManager.SaveUserConfig());
                    if (success)
                        mw.ShowSuccess("SALVO", message);
                    else
                        mw.ShowError("ERRO", message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnSaveUserConfig_Click", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        private async void BtnRestoreUserConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (await mw.ShowConfirmationDialog("Isso restaurará todas as configurações salvas anteriormente.\nDeseja continuar?"))
                    {
                        var (success, message) = await Task.Run(() => OOShutUpManager.RestoreUserConfig());
                        if (success)
                        {
                            mw.ShowSuccess("RESTAURADO", message);
                            RefreshStatus();
                        }
                        else
                            mw.ShowError("ERRO", message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRestoreUserConfig_Click", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        private void CategoryCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.Tag is PrivacyCategoryViewModel category)
            {
                category.AllEnabled = true;
                e.Handled = true;
            }
        }

        private void CategoryCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox checkBox && checkBox.Tag is PrivacyCategoryViewModel category)
            {
                category.AllEnabled = false;
                e.Handled = true;
            }
        }

        // Event handlers para pesquisa
        private void TxtSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            if (TxtSearch.Text == "🔍 Pesquisar configurações...")
                TxtSearch.Text = "";
        }

        private void TxtSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtSearch.Text))
                TxtSearch.Text = "🔍 Pesquisar configurações...";
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Não aplicar filtro se for o placeholder
            if (TxtSearch.Text == "🔍 Pesquisar configurações...")
            {
                _searchText = "";
                return;
            }

            _searchText = TxtSearch.Text;
            ApplyFilter();
        }

        // Event handler para abrir menu de filtro
        private void BtnFilterMenu_Click(object sender, RoutedEventArgs e)
        {
            FilterContextMenu.PlacementTarget = BtnFilterMenu;
            FilterContextMenu.IsOpen = true;
        }

        // Event handler para seleção no menu de filtro
        private void MenuItemFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem && menuItem.Tag is string filter)
            {
                _currentFilter = filter;

                // Atualiza cor do indicador visual no botão
                switch (filter)
                {
                    case "All":
                        FilterIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0)); // Amarelo
                        break;
                    case "Recommended":
                        FilterIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Verde
                        break;
                    case "Limited":
                        FilterIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 165, 0)); // Laranja
                        break;
                    case "NotRecommended":
                        FilterIndicator.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 43, 28)); // Vermelho
                        break;
                }

                ApplyFilter();

                // �� ATUALIZA�?�fO: Atualiza contador de "protegido" ao selecionar filtro
                RefreshStatus();
            }
        }
    }

    // --- View Models ---

    public class PrivacyCategoryViewModel : INotifyPropertyChanged
    {
        private bool _allEnabled;

        public string Name { get; set; } = string.Empty;
        public ObservableCollection<PrivacySettingViewModel> Settings { get; set; } = new ObservableCollection<PrivacySettingViewModel>();

        public bool AllEnabled
        {
            get => _allEnabled;
            set
            {
                if (_allEnabled != value)
                {
                    _allEnabled = value;
                    OnPropertyChanged(nameof(AllEnabled));

                    // Ativa/desativa todos os settings da categoria
                    foreach (var setting in Settings)
                    {
                        setting.IsEnabled = value;
                    }
                }
            }
        }

        public ICommand ToggleAllCommand => new RelayCommand(_ =>
        {
            // Inverte o estado atual
            AllEnabled = !AllEnabled;
        });

        public void RefreshAllEnabled()
        {
            bool newState = Settings.All(s => s.IsEnabled);
            if (_allEnabled != newState)
            {
                _allEnabled = newState;
                OnPropertyChanged(nameof(AllEnabled));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class PrivacySettingViewModel : INotifyPropertyChanged
    {
        private readonly OOShutUpManager.PrivacySetting _model;
        private readonly Action _refreshCallback;
        private readonly Action _categoryRefreshCallback;
        private bool _isEnabled;
        private static readonly Dictionary<string, DateTime> _pendingToggles = new();
        public static bool IsRecentlyToggled(string name) =>
            _pendingToggles.TryGetValue(name, out var t) && (DateTime.Now - t).TotalSeconds < 3;

        public PrivacySettingViewModel(OOShutUpManager.PrivacySetting model, Action refreshCallback, Action? categoryRefreshCallback = null)
        {
            _model = model;
            _refreshCallback = refreshCallback;
            _categoryRefreshCallback = categoryRefreshCallback ?? (() => { });
            Refresh(); // Carrega estado inicial
        }

        public string Name => _model.Name;
        public string Description => _model.Description;
        public OOShutUpManager.PrivacyLevel Level => _model.Level;
        public string InfoTooltip => $"{_model.Description}\n\nRegistry: {_model.RegistryPath}\\{_model.ValueName}\nValor seguro: {_model.SafeValue ?? "(nenhum)"}\nNível: {_model.Level}";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));

                    // Registra momento do toggle (previne RefreshStatus de sobrescrever)
                    _pendingToggles[_model.Name] = DateTime.Now;

                    // Aplica mudança em background
                    var model = _model;
                    if (value)
                        Task.Run(() => { try { OOShutUpManager.ApplyPrivacySetting(model); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } });
                    else
                        Task.Run(() => { try { OOShutUpManager.RevertPrivacySetting(model); } catch { Logger.LogWarning("Unknown", "Exception suppressed"); } });

                    // Atualiza checkbox da categoria (sem chamar RefreshStatus — o timer faz isso)
                    _categoryRefreshCallback?.Invoke();
                }
            }
        }

        // Comando para checkbox (opcional, já que usamos TwoWay binding no IsEnabled)
        public ICommand ToggleCommand => new RelayCommand(_ => { });

        public bool CheckRegistryState()
        {
            try { return OOShutUpManager.IsPrivacySettingApplied(_model); }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); return _isEnabled; }
        }

        public void Refresh()
        {
            bool newState = OOShutUpManager.IsPrivacySettingApplied(_model);
            if (_isEnabled != newState)
            {
                _isEnabled = newState;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
