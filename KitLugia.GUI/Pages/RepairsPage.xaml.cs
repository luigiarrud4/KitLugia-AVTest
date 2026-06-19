using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Services;

// Resolve conflito se houver (WPF vs Forms)
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class RepairsPage : Page
    {
        // Cache da lista completa para filtros rápidos
        private ObservableCollection<RepairAction> _allRepairs = new ObservableCollection<RepairAction>();
        private ObservableCollection<RepairAction> _filteredRepairs = new ObservableCollection<RepairAction>();

        // CancellationTokenSource para tasks em background
        private CancellationTokenSource? _cts;

        private ValorantDiagnosticOverlay? _currentOverlay;
        private bool _isRunningRepair;

        public RepairsPage()
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();
            LoadData();

            this.Unloaded += RepairsPage_Unloaded;
        }


        public void Cleanup()
        {

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;


            // As tarefas em segundo plano devem continuar executando mesmo após o usuário sair da página


            _allRepairs?.Clear();
            _allRepairs = null!;

            if (ItemsRepairs != null)
            {
                ItemsRepairs.ItemsSource = null;
                ItemsRepairs.Items.Clear();
            }

            if (LstCategories != null)
            {
                LstCategories.ItemsSource = null;
                LstCategories.Items.Clear();
            }


            this.DataContext = null;




            if (TxtFilter != null)
                TxtFilter.Text = string.Empty;

            if (_currentOverlay != null)
                _currentOverlay.Closed -= OnDiagnosticOverlayClosed;

            this.Unloaded -= RepairsPage_Unloaded;
        }

        private void RepairsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void LoadData()
        {
            // 1. Pega TUDO do Core (GeneralRepairManager)
            var repairs = GeneralRepairManager.GetAllRepairs();
            _allRepairs = new ObservableCollection<RepairAction>(repairs);

            // 2. Extrai categorias únicas dinamicamente
            // Isso significa que se você adicionar uma categoria nova no Core,
            // ela aparece aqui no menu sem mexer no XAML.
            var categories = _allRepairs.Select(x => x.Category).Distinct().OrderBy(c => c).ToList();

            // Adiciona a opção de ver todos no topo
            categories.Insert(0, "Todos");

            // Liga os dados à lista da esquerda
            LstCategories.ItemsSource = categories;

            // Seleciona o primeiro por padrão
            LstCategories.SelectedIndex = 0;
        }

        private void ApplyFilters()
        {
            // Se a lista ainda não carregou, sai

            if (_allRepairs == null || _allRepairs.Count == 0) return;

            string searchText = TxtFilter.Text.ToLower().Trim();
            string selectedCat = LstCategories.SelectedItem as string ?? "Todos";

            // LINQ: O filtro mágico que atualiza a tela
            var filtered = _allRepairs.Where(r =>
            {
                // Verifica Categoria
                bool catMatch = selectedCat == "Todos" || r.Category == selectedCat;

                // Verifica Texto (Busca no Nome e na Descrição)
                bool textMatch = string.IsNullOrEmpty(searchText) ||
                                 r.Name.ToLower().Contains(searchText) ||
                                 r.Description.ToLower().Contains(searchText);

                return catMatch && textMatch;
            }).ToList();

            // Atualiza a visualização (o ItemsControl recria os botões automaticamente)
            ItemsRepairs.ItemsSource = filtered;
        }

        // --- EVENTOS DE INTERFACE ---

        private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void LstCategories_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        // --- LÓGICA DE CLIQUE NO BOTÃO (Executar Reparo) ---
        private async void BtnRunRepair_Click(object sender, RoutedEventArgs e)
        {
            // O objeto "RepairAction" está escondido na propriedade Tag do botão
            if (sender is Button btn && btn.Tag is RepairAction action)
            {
                if (_isRunningRepair) return;
                _isRunningRepair = true;

                var mainWindow = Application.Current.MainWindow as MainWindow;


                if (action.Name == "Correção VALORANT (VAN9005)")
                {
                    // Mostra o painel de diagnóstico integrado
                    ShowValorantDiagnosticPanel();
                    return;
                }

                // 1. CHECAGEM DE SEGURANÇA
                // Se o reparo for marcado como 'IsDangerous' no Core, pede confirmação
                if (action.IsDangerous && mainWindow != null)
                {
                    bool confirm = await mainWindow.ShowConfirmationDialog(
                        $"ATENÇÃO: A ação '{action.Name}' altera configurações críticas.\nIsso pode afetar a conectividade ou sistema.\n\nDeseja realmente continuar?");

                    if (!confirm) return;
                }


                string taskId = Guid.NewGuid().ToString();
                Services.BackgroundTaskTracker.Instance.RegisterTask(taskId, action.Name, "Repairs");

                // 3. FEEDBACK VISUAL (Início)
                btn.IsEnabled = false; // Trava o botão para não clicar 2x
                btn.Opacity = 0.6;
                if (mainWindow != null) mainWindow.ShowInfo("PROCESSANDO", $"Executando: {action.Name}...");

                try
                {
                    // 4. EXECUÇÃO EM BACKGROUND (Thread Separada)
                    // Isso impede que a janela congele enquanto roda o comando
                    await Task.Run(() =>
                    {
                        if (_cts?.IsCancellationRequested == true) return;

                        // Roda o código Action definido no Core
                        action.Execute?.Invoke();
                    });

                    // 5. SUCESSO
                    if (mainWindow != null)
                    {
                        if (action.IsSlow)
                            mainWindow.ShowSuccess("INICIADO", $"{action.Name} foi iniciado em uma nova janela.");
                        else
                            mainWindow.ShowSuccess("CONCLUÍDO", $"{action.Name} finalizado com sucesso.");
                    }


                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true);
                }
                catch (Exception ex)
                {
                    // 6. ERRO
                    if (mainWindow != null) mainWindow.ShowError("FALHA", $"Erro ao executar: {ex.Message}");


                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
                }
                finally
                {
                    // 7. LIMPEZA (Destrava o botão)
                    btn.IsEnabled = true;
                    btn.Opacity = 1.0;
                    _isRunningRepair = false;
                }
            }
        }

        // --- DIAGNÓSTICO DO VALORANT (OVERLAY) ---

        private void OnDiagnosticOverlayClosed(object? s, EventArgs e)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            var overlayContainer = mainWindow.FindName("OverlayContainer") as Grid;
            if (overlayContainer == null) return;

            if (_currentOverlay != null)
            {
                overlayContainer.Children.Remove(_currentOverlay);
                _currentOverlay.Closed -= OnDiagnosticOverlayClosed;
                _currentOverlay = null;
            }

            if (overlayContainer.Children.Count == 0)
                overlayContainer.Visibility = Visibility.Collapsed;
        }

        private void ShowValorantDiagnosticPanel()
        {
            // Obtém o OverlayContainer do MainWindow
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            var overlayContainer = mainWindow.FindName("OverlayContainer") as Grid;
            if (overlayContainer == null) return;

            // Cria o overlay de diagnóstico
            var overlay = new ValorantDiagnosticOverlay();
            _currentOverlay = overlay;
            overlay.Closed += OnDiagnosticOverlayClosed;

            // Adiciona ao OverlayContainer e mostra
            overlayContainer.Children.Add(overlay);
            overlayContainer.Visibility = Visibility.Visible;
        }
    }
}