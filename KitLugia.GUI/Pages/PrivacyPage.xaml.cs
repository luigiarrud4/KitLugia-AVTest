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
            this.Unloaded -= PrivacyPage_Unloaded;

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
            _refreshTimer.Start();
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

                // Aplicar na UI thread
                foreach (var (setting, state) in states)
                {
                    if (setting.IsEnabled != state)
                        setting.IsEnabled = state;
                }

                foreach (var cat in Categories)
                    cat.RefreshAllEnabled();

                int secured = allSettings.Count(s => s.IsEnabled);
                int total = allSettings.Count;

                TxtSecureCount.Text = secured.ToString();
                TxtVulnerableCount.Text = (total - secured).ToString();
                int percent = total > 0 ? (int)((double)secured / total * 100) : 0;
                TxtPrivacyScore.Text = $"{percent}% Protegido";
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

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)
        {
            // Na nova UI, tudo já está expandido por padrão neste design simplificado.
            // Poderíamos adicionar lógica de expandir/colapsar nos ViewModels se necessário.
            LoadData();
        }

        private async Task ApplyPreset(OOShutUpManager.PrivacyLevel level)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                string taskId = Guid.NewGuid().ToString();
                BackgroundTaskTracker.Instance.RegisterTask(taskId, $"Preset {level}", "Privacy");
                mw.ShowInfo("PRIVACIDADE", $"Aplicando preset {level}...");
                await Task.Run(() => OOShutUpManager.ApplyPreset(level));
                BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
                mw.ShowSuccess("SUCESSO", "Configurações aplicadas.");
                RefreshStatus();
            }
        }

        private async void BtnApplyRecommended_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                await ApplyPreset(OOShutUpManager.PrivacyLevel.Recommended);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnApplyRecommended_Click", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        private async void BtnApplyLimited_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                await ApplyPreset(OOShutUpManager.PrivacyLevel.Limited);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnApplyLimited_Click", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        private async void BtnApplyNotRecommended_Click(object sender, RoutedEventArgs e)
        {
            if (_isPrivacyOperation) return;
            _isPrivacyOperation = true;
            try
            {
                await ApplyPreset(OOShutUpManager.PrivacyLevel.NotRecommended);
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnApplyNotRecommended_Click", ex.Message);
            }
            finally
            {
                _isPrivacyOperation = false;
            }
        }

        private async void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                if (await mw.ShowConfirmationDialog("Isso reativará toda a telemetria e coleta de dados padrão do Windows.\nDeseja continuar?"))
                {
                    await Task.Run(() => OOShutUpManager.RestoreDefaults());
                    mw.ShowSuccess("RESTAURADO", "Padrões do Windows restaurados.");
                    RefreshStatus();
                }
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

        private void BtnGenerateReport_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Funcionalidade em desenvolvimento.");
        private void BtnExportConfig_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Funcionalidade em desenvolvimento.");
        private void BtnSnapshot_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Funcionalidade em desenvolvimento.");

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

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));

                    // Aplica mudança em background
                    var model = _model;
                    if (value)
                        Task.Run(() => { try { OOShutUpManager.ApplyPrivacySetting(model); } catch { } });
                    else
                        Task.Run(() => { try { OOShutUpManager.RevertPrivacySetting(model); } catch { } });

                    _refreshCallback?.Invoke();
                    _categoryRefreshCallback?.Invoke();
                }
            }
        }

        // Comando para checkbox (opcional, já que usamos TwoWay binding no IsEnabled)
        public ICommand ToggleCommand => new RelayCommand(_ => { });

        public bool CheckRegistryState()
        {
            try { return OOShutUpManager.IsPrivacySettingApplied(_model); }
            catch { return _isEnabled; }
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
