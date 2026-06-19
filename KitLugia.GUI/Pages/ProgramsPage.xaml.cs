using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using KitLugia.Core.UninstallTools;
using KitLugia.Core;
using KitLugia.GUI.Helpers;

// --- CORREÇÃO DOS CONFLITOS DE AMBIGUIDADE ---
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace KitLugia.GUI.Pages
{
    public partial class ProgramsPage : Page
    {
        private ObservableCollection<ProgramViewModel>? ProgramsCollection;
        private ObservableCollection<ProgramViewModel>? FilteredProgramsCollection;
        private CancellationTokenSource? _iconLoadCts;
        private bool _isProgramOperation;

        public ProgramsPage()
        {
            InitializeComponent();
            LoadPrograms();
            this.Unloaded += ProgramsPage_Unloaded;
        }

        private void ProgramsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async void LoadPrograms()
        {
            if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Visible;
            if (ProgramsList != null) ProgramsList.ItemsSource = null;

            try
            {
                // Cancela carregamento anterior se existir
                _iconLoadCts?.Cancel();
                _iconLoadCts = new CancellationTokenSource();

                // Carrega programas do Registry
                var programs = await Task.Run(() => RegistryProgramFactory.GetInstalledPrograms());
                
                // Converte para ViewModel
                ProgramsCollection = new ObservableCollection<ProgramViewModel>(
                    programs.Select(p => new ProgramViewModel(p)));
                
                // Atualiza contador
                if (ProgramCountText != null)
                    ProgramCountText.Text = $"Programas: {ProgramsCollection.Count}";

                // Mostra a lista
                FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(ProgramsCollection);
                if (ProgramsList != null) ProgramsList.ItemsSource = FilteredProgramsCollection;

                // Carrega ícones em paralelo
                await LoadIconsAsync(_iconLoadCts.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erro ao carregar programas: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (LoadingPanel != null) LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadIconsAsync(CancellationToken cancellationToken)
        {
            if (ProgramsCollection == null) return;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Carregando Ícones de Programas", "Programs");

            var dispatcher = Dispatcher;
            var tasks = ProgramsCollection.Select(async program =>
            {
                if (cancellationToken.IsCancellationRequested) return;

                BitmapSource? icon = null;

                try
                {
                    // 1) Caminho do desinstalador
                    if (!string.IsNullOrEmpty(program.UninstallString))
                        icon = ProgramIconHelper.GetIconFromFile(ExtractPathFromUninstallString(program.UninstallString));

                    // 2) Busca recursiva no diretório de instalação
                    if (icon == null && !string.IsNullOrEmpty(program.InstallLocation))
                        icon = ProgramIconHelper.GetIconFromDirectory(program.InstallLocation);

                    // 3) Genérico
                    icon ??= ProgramIconHelper.GetGenericIcon();
                }
                catch
                {
                    icon = ProgramIconHelper.GetGenericIcon();
                }

                if (icon != null && !cancellationToken.IsCancellationRequested)
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        program.Icon = icon;
                        if (ProgramsList != null) ProgramsList.Items.Refresh();
                    });
                }
            });

            await Task.WhenAll(tasks);

            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Ícones de programas carregados");
        }

        private string? ExtractPathFromUninstallString(string uninstallString)
        {
            try
            {
                // Remove aspas se existirem
                uninstallString = uninstallString.Trim('"');

                // Se já é um caminho de arquivo
                if (uninstallString.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(uninstallString))
                {
                    return uninstallString;
                }

                // Se é um comando com parâmetros, extrai o executável
                if (uninstallString.Contains(" "))
                {
                    var parts = uninstallString.Split(new[] { ' ' }, 2);
                    if (parts.Length > 0 && File.Exists(parts[0]))
                    {
                        return parts[0];
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && ProgramsCollection != null)
            {
                string searchText = textBox.Text.ToLower();

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    // Mostra todos os programas
                    FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(ProgramsCollection);
                }
                else
                {
                    // Filtra programas por DisplayName ou Publisher
                    var filtered = ProgramsCollection.Where(p => 
                        p.DisplayName.ToLower().Contains(searchText) || 
                        p.Publisher.ToLower().Contains(searchText)).ToList();
                    FilteredProgramsCollection = new ObservableCollection<ProgramViewModel>(filtered);
                }

                if (ProgramsList != null)
                {
                    ProgramsList.ItemsSource = FilteredProgramsCollection;
                }
            }
        }

        private async void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            if (_isProgramOperation) return;
            _isProgramOperation = true;
            try
            {
                if (sender is Button btn && btn.Tag is ProgramViewModel program)
                {
                    if (MessageBox.Show($"Remover {program.DisplayName}?\n\nIsso irá desinstalar o programa usando o uninstaller original.", "Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    {
                        // Estado inicial de desinstalação
                        btn.Content = "⏳";
                        btn.IsEnabled = false;

                        // Executa desinstalação
                        bool success = await UninstallProgram(program);

                        if (success)
                        {
                            btn.Content = "✅ REMOVIDO";
                            btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0));
                            
                            // Remove da lista após 1 segundo
                            await Task.Delay(1000);
                            if (FilteredProgramsCollection != null)
                            {
                                FilteredProgramsCollection.Remove(program);
                            }
                            if (ProgramsCollection != null)
                            {
                                ProgramsCollection.Remove(program);
                            }
                            if (ProgramCountText != null)
                                ProgramCountText.Text = $"Programas: {ProgramsCollection?.Count ?? 0}";
                        }
                        else
                        {
                            btn.Content = "✅- ERRO";
                            btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(192, 0, 0));
                            MessageBox.Show($"Erro ao remover {program.DisplayName}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                            
                            // Reabilita o botão após erro
                            await Task.Delay(2000);
                            btn.Content = "REMOVER";
                            btn.IsEnabled = true;
                            btn.Background = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemove_Click", ex.Message);
            }
            finally
            {
                _isProgramOperation = false;
            }
        }

        private async void BtnRemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_isProgramOperation) return;
            _isProgramOperation = true;
            try
            {
                if (FilteredProgramsCollection == null) return;

                var selectedPrograms = FilteredProgramsCollection.Where(p => p.IsSelected).ToList();

                if (selectedPrograms.Count == 0)
                {
                    MessageBox.Show("Nenhum programa selecionado para remoção.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string programNames = string.Join("\n", selectedPrograms.Select(p => p.DisplayName));
                if (MessageBox.Show($"Remover {selectedPrograms.Count} programa(s) selecionado(s)?\n\n{programNames}\n\nIsso irá desinstalar os programas usando os uninstallers originais.", "Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    int successCount = 0;
                    int failCount = 0;

                    foreach (var program in selectedPrograms)
                    {
                        bool success = await UninstallProgram(program);
                        
                        if (success)
                        {
                            successCount++;
                            if (FilteredProgramsCollection.Contains(program))
                            {
                                FilteredProgramsCollection.Remove(program);
                            }
                            if (ProgramsCollection != null && ProgramsCollection.Contains(program))
                            {
                                ProgramsCollection.Remove(program);
                            }
                        }
                        else
                        {
                            failCount++;
                        }
                    }

                    string message = $"Remoção concluída:\n\n✅ {successCount} programa(s) removido(s) com sucesso";
                    if (failCount > 0)
                    {
                        message += $"\n✅- {failCount} programa(s) falharam na remoção";
                    }
                    
                    MessageBox.Show(message, "Resultado", MessageBoxButton.OK, MessageBoxImage.Information);
                    
                    if (ProgramCountText != null)
                        ProgramCountText.Text = $"Programas: {ProgramsCollection?.Count ?? 0}";
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnRemoveSelected_Click", ex.Message);
            }
            finally
            {
                _isProgramOperation = false;
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadPrograms();
        }

        private async Task<bool> UninstallProgram(ProgramViewModel program)
        {
            try
            {
                string uninstallString = program.UninstallString;

                if (string.IsNullOrEmpty(uninstallString))
                {
                    return false;
                }

                // Executa o uninstaller
                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{uninstallString}\"",
                    UseShellExecute = true,
                    Verb = "runas" // Executa como administrador
                };

                using (var process = Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        return process.ExitCode == 0;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao desinstalar {program.DisplayName}: {ex.Message}");
                return false;
            }
        }

        public void Cleanup()
        {
            _iconLoadCts?.Cancel();
            _iconLoadCts?.Dispose();
            ProgramsCollection?.Clear();
            FilteredProgramsCollection?.Clear();
            ProgramsCollection = null;
            FilteredProgramsCollection = null;
            ProgramsList.ItemsSource = null;
            ProgramsList.Items.Clear();
            this.Unloaded -= ProgramsPage_Unloaded;
            this.DataContext = null;
        }
    }
}
