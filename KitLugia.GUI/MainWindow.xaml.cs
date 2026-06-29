using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Navigation;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Pages;
using KitLugia.GUI.Services;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

// --- CORREÇÃO DOS ERROS DE AMBIGUIDADE ---
// Estas linhas forçam o código a usar os componentes do WPF
using RadioButton = System.Windows.Controls.RadioButton;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Logger = KitLugia.Core.Logger;

namespace KitLugia.GUI
{
    /// <summary>
    /// Converter para traduzir status de tarefas para português
    /// </summary>
    public class TaskStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Services.TaskStatus status)
            {
                return status switch
                {
                    Services.TaskStatus.Running => "Em Execução",
                    Services.TaskStatus.ProgressUpdate => "Em Progresso",
                    Services.TaskStatus.Completed => "Concluído",
                    Services.TaskStatus.Failed => "Falhou",
                    Services.TaskStatus.Cancelled => "Cancelado",
                    _ => status.ToString()
                };
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter para formatar duração em segundos
    /// </summary>
    public class DurationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is TimeSpan duration)
            {
                int totalSeconds = (int)duration.TotalSeconds;
                if (totalSeconds < 60)
                {
                    return $"{totalSeconds}s";
                }
                else if (totalSeconds < 3600)
                {
                    int minutes = totalSeconds / 60;
                    int seconds = totalSeconds % 60;
                    return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
                }
                else
                {
                    int hours = totalSeconds / 3600;
                    int minutes = (totalSeconds % 3600) / 60;
                    return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
                }
            }
            return "0s";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter para mostrar badge de stack apenas quando count > 1
    /// </summary>
    public class StackCountVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int stackCount)
            {
                return stackCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Enum para identificação de páginas (sem dependência de emojis)
    public enum PageType
    {
        Dashboard,
        Tweaks,
        Screen,
        Apps,
        Storage,
        Network,
        Games,
        Tools,
        GameBoost,
        Services,
        Repairs,
        Drivers,
        Partitions,
        Winboot,
        AdvancedTools,
        Integrity,
        Security,
        Privacy,
        Activation,
        TraySettings,
        Update,
        Diagnostic,
        IsoEditor,
        Server,
        Rufus,
        AdvancedRamCleanSettings,
        AllTweaks,
        ExmTweaks,
        Stutter,
        WinTune,
        QuickInstall,
        Shrink,
    }

    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private const int MaxVisibleToasts = 6;

        // Típico: 1-6 toasts visíveis simultaneamente
        private Dictionary<string, LugiaToast> _activeToasts = new Dictionary<string, LugiaToast>(6, StringComparer.OrdinalIgnoreCase);
        // ⬇️ NOVO: Rastreia toasts de progresso que podem transicionar para Success/Error
        private Dictionary<string, LugiaToast> _progressToasts = new Dictionary<string, LugiaToast>(4, StringComparer.OrdinalIgnoreCase);
        private TaskCompletionSource<bool>? _confirmCompletionSource;


        private Action<string>? _logHandler;
        private Action? _notificationCountHandler;

        // Tray Icon RAM Monitor
        private TrayIconService? _trayService;
        public TrayIconService? TrayService => _trayService;

        // Timer para o Debounce da pesquisa
        private DispatcherTimer _searchDebounceTimer;


        private DispatcherTimer? _healthCheckTimer;

        // Single-instance show window signaling
        private System.Threading.EventWaitHandle? _showWindowEvent;
        private System.Threading.Thread? _showWindowMonitor;


        private CancellationTokenSource? _backgroundTasksCts;

        // GoodbyeDPI
        private Process? _goodbyeDpiProcess;
        private DispatcherTimer? _goodbyeDpiStatusTimer;
        private string _goodbyeDpiMode = "-2"; // -2 = qualquer país, -1 = Rússia, -3 = EUA
        private bool _goodbyeDpiFragmentation = true; // -a flag
        private bool _goodbyeDpiDNS = false; // -d flag
        private bool _goodbyeDpiAutoStart = false; // Iniciar automaticamente
        private readonly string _goodbyeDpiConfigPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "goodbyedpi_config.json");
        public bool GoodbyeDPIActive
        {
            get
            {
                // Verifica se o processo está rodando
                if (_goodbyeDpiProcess != null && !_goodbyeDpiProcess.HasExited)
                    return true;

                // Verifica se há processo goodbyedpi.exe rodando (caso tenha sido iniciado externamente)
                try
                {

                    var processes = Process.GetProcessesByName("goodbyedpi");
                    try
                    {
                        if (processes.Length > 0)
                        {
                            // Se encontrou processo, atualiza referência
                            _goodbyeDpiProcess = processes[0];
                            // Descarta os demais (se houver mais de um)
                            for (int i = 1; i < processes.Length; i++)
                                processes[i].Dispose();
                            return true;
                        }
                    }
                    finally
                    {
                        // Se não encontrou nenhum, descarta todos
                        if (processes.Length == 0)
                        {
                            foreach (var p in processes) p.Dispose();
                        }
                    }
                }
                catch
                {
                    // Ignora erros ao verificar processos
                }

                return false;
            }
        }

        // Verifica status detalhado do GoodbyeDPI
        private string GetGoodbyeDPIStatus()
        {
            try
            {
                var processes = Process.GetProcessesByName("goodbyedpi");
                if (processes.Length == 0)
                    return "Não está rodando";

                var process = processes[0];
                return $"Rodando (PID: {process.Id}, RAM: {process.WorkingSet64 / 1024 / 1024}MB, Tempo: {process.StartTime:HH:mm:ss})";
            }
            catch (Exception ex)
            {
                return $"Erro ao verificar: {ex.Message}";
            }
        }

        // Constrói argumentos do GoodbyeDPI baseados nas configurações
        private string BuildGoodbyeDPIArguments()
        {
            var args = _goodbyeDpiMode;
            if (_goodbyeDpiFragmentation) args += " -a";
            if (_goodbyeDpiDNS) args += " -d";
            return args;
        }

        // Atualiza menu de contexto com configurações atuais
        private void UpdateGoodbyeDPIMenu()
        {
            // Modo
            MenuModeAny.IsChecked = (_goodbyeDpiMode == "-2");
            MenuModeRussia.IsChecked = (_goodbyeDpiMode == "-1");
            MenuModeUSA.IsChecked = (_goodbyeDpiMode == "-3");

            // Fragmentação
            MenuFragmentationOn.IsChecked = _goodbyeDpiFragmentation;
            MenuFragmentationOff.IsChecked = !_goodbyeDpiFragmentation;

            // DNS
            MenuDNSOn.IsChecked = _goodbyeDpiDNS;
            MenuDNSOff.IsChecked = !_goodbyeDpiDNS;

            // Auto Start
            MenuAutoStart.IsChecked = _goodbyeDpiAutoStart;
        }

        // Salva configurações do GoodbyeDPI em JSON
        private void SaveGoodbyeDPIConfig()
        {
            try
            {
                var config = new
                {
                    Mode = _goodbyeDpiMode,
                    Fragmentation = _goodbyeDpiFragmentation,
                    DNS = _goodbyeDpiDNS,
                    AutoStart = _goodbyeDpiAutoStart
                };

                var directory = System.IO.Path.GetDirectoryName(_goodbyeDpiConfigPath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory!);

                System.IO.File.WriteAllText(_goodbyeDpiConfigPath, System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.LogError("GoodbyeDPI", $"Erro ao salvar configurações: {ex.Message}");
            }
        }

        // Carrega configurações do GoodbyeDPI de JSON
        private void LoadGoodbyeDPIConfig()
        {
            try
            {
                if (System.IO.File.Exists(_goodbyeDpiConfigPath))
                {
                    var json = System.IO.File.ReadAllText(_goodbyeDpiConfigPath);
                    var config = System.Text.Json.JsonSerializer.Deserialize<GoodbyeDPIConfig>(json);
                    if (config != null)
                    {
                        _goodbyeDpiMode = config.Mode ?? "-2";
                        _goodbyeDpiFragmentation = config.Fragmentation ?? true;
                        _goodbyeDpiDNS = config.DNS ?? false;
                        _goodbyeDpiAutoStart = config.AutoStart ?? false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("GoodbyeDPI", $"Erro ao carregar configurações: {ex.Message}");
            }
        }

        // Classe para desserialização do JSON
        private class GoodbyeDPIConfig
        {
            public string? Mode { get; set; }
            public bool? Fragmentation { get; set; }
            public bool? DNS { get; set; }
            public bool? AutoStart { get; set; }
        }

        // Botão direito - Abre menu de contexto
        private void BtnGoodbyeDPI_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            UpdateGoodbyeDPIMenu();
            GoodbyeDPIContextMenu.IsOpen = true;
            e.Handled = true;
        }

        // Reinicia GoodbyeDPI com novas configurações
        private async void RestartGoodbyeDPIWithNewSettings()
        {
            if (GoodbyeDPIActive)
            {
                // Encerra processo atual
                if (_goodbyeDpiProcess != null && !_goodbyeDpiProcess.HasExited)
                {
                    _goodbyeDpiProcess.Kill();

                    await Task.Run(() => _goodbyeDpiProcess.WaitForExit(5000));
                    _goodbyeDpiProcess = null;
                }


                await Task.Delay(500);
                ActivateGoodbyeDPI();
            }
            else
            {
                ShowInfo("📶 GoodbyeDPI", "Ative o GoodbyeDPI para aplicar as novas configurações.");
            }
        }

        // Ativa GoodbyeDPI (método auxiliar)
        private void ActivateGoodbyeDPI()
        {
            string goodbyeDpiPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Tools",
                "GoodbyeDPI",
                "goodbyedpi.exe");

            if (!System.IO.File.Exists(goodbyeDpiPath))
            {
                MessageBox.Show(
                    $"❌ Erro: Arquivo goodbyedpi.exe não encontrado!\n\nCaminho: {goodbyeDpiPath}",
                    "Erro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _goodbyeDpiProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = goodbyeDpiPath,
                    Arguments = BuildGoodbyeDPIArguments(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };

            _goodbyeDpiProcess.Start();
            Logger.Log($"GOODBYEDPI: Reiniciado com novas configurações (PID: {_goodbyeDpiProcess.Id})");
        }

        // Handlers do menu de contexto
        private void GoodbyeDPI_ModeAny_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiMode = "-2";
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 Modo Alterado", "Modo: Qualquer País (-2)");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_ModeRussia_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiMode = "-1";
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 Modo Alterado", "Modo: Rússia (-1)");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_ModeUSA_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiMode = "-3";
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 Modo Alterado", "Modo: EUA (-3)");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_FragmentationOn_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiFragmentation = true;
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 Fragmentação", "Fragmentação: Ativada (-a)");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_FragmentationOff_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiFragmentation = false;
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 Fragmentação", "Fragmentação: Desativada");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_DNSOn_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiDNS = true;
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 DNS Redirection", "DNS Redirection: Ativado (-d)");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_DNSOff_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiDNS = false;
            SaveGoodbyeDPIConfig();
            ShowInfo("📶 DNS Redirection", "DNS Redirection: Desativado");
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "GoodbyeDPI - Deep Packet Inspection Circumvention Utility\n\n" +
                "Versão: 0.2.3rc3\n" +
                "Autor: ValdikSS\n\n" +
                "GoodbyeDPI é uma ferramenta de código aberto que contorna\n" +
                "sistemas de DPI (Deep Packet Inspection) para desbloquear\n" +
                "acesso a sites e serviços restritos.\n\n" +
                "Configurações Atuais:\n" +
                $"Modo: {_goodbyeDpiMode}\n" +
                $"Fragmentação: {(_goodbyeDpiFragmentation ? "Ativada (-a)" : "Desativada")}\n" +
                $"DNS Redirection: {(_goodbyeDpiDNS ? "Ativado (-d)" : "Desativado")}\n\n" +
                "GitHub: https://github.com/ValdikSS/GoodbyeDPI",
                "Sobre GoodbyeDPI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void GoodbyeDPI_RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiMode = "-2"; // Qualquer país
            _goodbyeDpiFragmentation = true; // Ativada
            _goodbyeDpiDNS = false; // Desativado
            _goodbyeDpiAutoStart = false; // Desativado

            ShowSuccess("📶 Padrões Restaurados", "Configurações restauradas para os valores padrão:\n\nModo: Qualquer País (-2)\nFragmentação: Ativada (-a)\nDNS Redirection: Desativado\nIniciar Automaticamente: Desativado");
            SaveGoodbyeDPIConfig();
            RestartGoodbyeDPIWithNewSettings();
        }

        private void GoodbyeDPI_AutoStart_Click(object sender, RoutedEventArgs e)
        {
            _goodbyeDpiAutoStart = MenuAutoStart.IsChecked;
            SaveGoodbyeDPIConfig();
            
            if (_goodbyeDpiAutoStart)
            {
                ShowInfo("📶 Auto-Start Ativado", "O GoodbyeDPI será iniciado automaticamente ao abrir o KitLugia.");
            }
            else
            {
                ShowInfo("📶 Auto-Start Desativado", "O GoodbyeDPI não será iniciado automaticamente.");
            }
        }

        public bool StartMinimized => (Application.Current as App)?.StartMinimized ?? false;
        private bool _uiDeferredInit = false;

        private void EnsureUIIinitialized()
        {
            if (_uiDeferredInit) return;
            _uiDeferredInit = true;

            SearchEngine.Initialize();

            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchDebounceTimer.Tick += SearchDebounce_Tick;

            _goodbyeDpiStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _goodbyeDpiStatusTimer.Tick += (s, e) =>
            {
                OnPropertyChanged(nameof(GoodbyeDPIActive));
                if (BtnGoodbyeDPI != null)
                {
                    string status = GoodbyeDPIActive ? $"Ativado - {GetGoodbyeDPIStatus()}" : "Desativado";
                    BtnGoodbyeDPI.ToolTip = $"GoodbyeDPI - {status}";
                }
            };
            // Se OnIntroFinished já foi executado (ex: StartMinimized), inicia o timer agora
            if (_introCompleted) _goodbyeDpiStatusTimer.Start();

            MainFrame.Navigate(new DashboardPage());
            if (BtnDashboard != null) BtnDashboard.IsChecked = true;
            while (MainFrame.NavigationService.CanGoBack)
                MainFrame.NavigationService.RemoveBackEntry();

            if (GlobalConsolePanel != null)
                GlobalConsolePanel.RequestClose += (s, e) => { GlobalConsolePanel.Visibility = Visibility.Collapsed; };
            if (LegacyTerminalPanel != null)
                LegacyTerminalPanel.RequestClose += (s, e) => { LegacyTerminalPanel.Visibility = Visibility.Collapsed; };

            UpdateDebugMenuVisibility(false);
        }

        public MainWindow()
        {
            InitializeComponent();


            _backgroundTasksCts = new CancellationTokenSource();

            // Carrega configurações do GoodbyeDPI
            LoadGoodbyeDPIConfig();

            // Conecta o Logger do Core ao Console da GUI
            _logHandler = (msg) => ConsoleManager.WriteLine(msg);
            KitLugia.Core.Logger.OnLogReceived += _logHandler;

            // Conecta o contador de notificações
            _notificationCountHandler = UpdateNotificationBadge;
            NotificationHistoryManager.OnCountChanged += _notificationCountHandler;


            Services.BackgroundTaskTracker.Instance.TaskStatusChanged += BackgroundTaskTracker_TaskStatusChanged;
            Services.BackgroundTaskTracker.Instance.PropertyChanged += BackgroundTaskTracker_PropertyChanged;

            if (!StartMinimized)
            {
                // Configura o fechamento do painel de console (Rodapé)
                if (GlobalConsolePanel != null)
                    GlobalConsolePanel.RequestClose += (s, e) => { GlobalConsolePanel.Visibility = Visibility.Collapsed; };

                // --- CORREÇÃO: Configura o fechamento do Terminal Legacy (Tela Cheia) ---
                if (LegacyTerminalPanel != null)
                    LegacyTerminalPanel.RequestClose += (s, e) => { LegacyTerminalPanel.Visibility = Visibility.Collapsed; };
            }

            // --- TRAY ICON: Inicializa o Monitor de RAM ---
            _trayService = new TrayIconService();
            _trayService.OnOpenMainWindow += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    EnsureUIIinitialized();
                    // Garante MainFrame visível antes de Show() - mesmo que Window_Loaded
                    // ainda não tenha sido chamado (MainFrame começa Opacity=0 no XAML)
                    if (MainFrame != null)
                    {
                        MainFrame.BeginAnimation(Frame.OpacityProperty, null);
                        MainFrame.Opacity = 1;
                    }
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    Focus();
                });
            };
            _trayService.OnOpenSettings += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    EnsureUIIinitialized();
                    if (MainFrame != null)
                    {
                        MainFrame.BeginAnimation(Frame.OpacityProperty, null);
                        MainFrame.Opacity = 1;
                    }
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                    Focus();
                    NavigateToPage(PageType.TraySettings);
                });
            };
            _trayService.Initialize();


            // O GameBoost continua funcionando mesmo com a janela minimizada
            // Logs rotativos de foreground foram removidos para evitar acumulo

            // --- AUTO-START: Garante que o app inicie com o Windows se o Tray estiver ativo ---

            // IsAutoStartEnabled() detecta o mismatch e retorna false. Nesse caso, re-registramos
            // automaticamente com o caminho atual para que o auto-start não suma silenciosamente.
            if (TrayIconService.IsTrayEnabledStatic())
            {
                var currentPath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                Logger.Log($"KitLugia iniciado: {currentPath}");
                Logger.Log($"Tray ativo: true");

                // Auto-start registration via Task Scheduler COM — defer off UI thread
                _ = Task.Run(() => TrayIconService.SetAutoStart(true));
                

                _healthCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _healthCheckTimer.Tick += (s, e) =>
                {
                    _healthCheckTimer?.Stop();
                    if (_trayService != null && !_trayService.IsTrayIconHealthy())
                    {
                        Logger.Log("❌ Tray Icon não está saudável, tentando recuperar...");
                        if (_trayService.RecoverTrayIcon())
                        {
                            Logger.Log("✅ Tray Icon recuperado com sucesso");
                        }
                        else
                        {
                            Logger.Log("❌ Falha na recuperação do Tray Icon");
                        }
                    }
                    else
                    {
                        Logger.Log("✅ Tray Icon está saudável");
                    }
                };
                _healthCheckTimer.Start();
            }


            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), _backgroundTasksCts.Token); // Espera 10s para iniciar
                    if (!_backgroundTasksCts.Token.IsCancellationRequested)
                    {
                        await GitHubUpdater.StartAutoUpdateCheck();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Task foi cancelada, não fazer nada
                }
            }, _backgroundTasksCts.Token);

            // --- INTELLIGENT MEMORY CLEANER: Limpeza baseada em limite de memória ---
            AggressiveMemoryCleaner.StartIntelligentMonitoring(30, 200); // Verifica a cada 30s, limpa quando o processo ultrapassa 200MB
            Logger.Log("🧹 MemoryCleaner inteligente iniciado - Limite: 200MB, Verificação: 30s");

            if (!StartMinimized)
            {
                // --- MODO DESENVOLVEDOR: Inicializa com menu de debug OCULTO por padrão ---
                UpdateDebugMenuVisibility(false);
            }

            // --- NAMED EVENT: Permite que uma segunda instância sinalize para mostrar a janela ---
            _showWindowEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "KitLugia_ShowWindow");
            _showWindowMonitor = new System.Threading.Thread(() =>
            {
                while (!_backgroundTasksCts.Token.IsCancellationRequested)
                {
                    if (_showWindowEvent.WaitOne(1000))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            EnsureUIIinitialized();
                            Show();
                            WindowState = WindowState.Normal;
                            Activate();
                            Focus();
                        });
                    }
                }
            }) { IsBackground = true, Name = "ShowWindowMonitor" };
            _showWindowMonitor.Start();
        }

        // =========================================================
        // MÉTODO PÚBLICO PARA ABRIR O TERMINAL LEGACY
        // (Chamado pela Dashboard e pela página Sobre)
        // =========================================================
        public void OpenLegacyTerminal()
        {
            if (LegacyTerminalPanel != null)
            {
                LegacyTerminalPanel.Visibility = Visibility.Visible;
                // Opcional: Focar no input se o controle suportar
            }
        }

        #region LIVE SEARCH (PESQUISA GLOBAL)

        private void TxtGlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void SearchDebounce_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();
            PerformLiveSearch(TxtGlobalSearch.Text);
        }

        private void PerformLiveSearch(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                if (SearchPopup != null) SearchPopup.IsOpen = false;
                return;
            }

            if (MainFrame.Content is GlobalSearchPage searchPage)
            {
                searchPage.UpdateSearch(query);
                return;
            }

            UncheckAllNavButtons();
            CleanupAndNavigate(new GlobalSearchPage(query));
        }

        private void TxtGlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Navegação por setas no popup de pesquisa
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (SearchPopup == null || !SearchPopup.IsOpen || LstSearchResults == null)
                    return;

                e.Handled = true;
                int count = LstSearchResults.Items.Count;
                if (count == 0) return;

                int current = LstSearchResults.SelectedIndex;
                int next = e.Key == Key.Down
                    ? (current < count - 1 ? current + 1 : 0)
                    : (current > 0 ? current - 1 : count - 1);

                LstSearchResults.SelectedIndex = next;
                LstSearchResults.ScrollIntoView(LstSearchResults.SelectedItem);
                return;
            }

            if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(TxtGlobalSearch.Text))
                return;

            e.Handled = true;
            if (SearchPopup != null) SearchPopup.IsOpen = false;

            if (MainFrame.Content is GlobalSearchPage searchPage)
                searchPage.UpdateSearch(TxtGlobalSearch.Text);
            else
            {
                UncheckAllNavButtons();
                CleanupAndNavigate(new GlobalSearchPage(TxtGlobalSearch.Text));
            }
        }

        // 🎮 Atalhos de teclado globais
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F: focar barra de pesquisa
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                TxtGlobalSearch?.Focus();
                TxtGlobalSearch?.SelectAll();
                return;
            }

            // Esc: voltar ao Dashboard (ou fechar popup se aberto)
            if (e.Key == Key.Escape)
            {
                if (SearchPopup != null && SearchPopup.IsOpen)
                {
                    SearchPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }

                if (MainFrame.Content is not DashboardPage)
                {
                    e.Handled = true;
                    UncheckAllNavButtons();
                    NavigateToPage(PageType.Dashboard);
                }
                return;
            }

            // F5: recarregar página atual
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                if (MainFrame.Content is Page currentPage)
                {
                    var pageType = GetPageTypeFromContent(currentPage);
                    if (pageType.HasValue)
                    {
                        CleanupAndNavigate(GetPageInstance(pageType.Value));
                    }
                }
                return;
            }

            // Ctrl+R: recarregar (alias para F5)
            if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                var pageType = MainFrame.Content is Page p ? GetPageTypeFromContent(p) : null;
                if (pageType.HasValue)
                {
                    CleanupAndNavigate(GetPageInstance(pageType.Value));
                }
                return;
            }
        }

        private static PageType? GetPageTypeFromContent(Page page)
        {
            return page switch
            {
                DashboardPage => PageType.Dashboard,
                TweaksPage => PageType.Tweaks,
                AppsPage => PageType.Apps,
                CleanupPage => PageType.Storage,
                NetworkPage => PageType.Network,
                GamesPage => PageType.Games,
                ServicesPage => PageType.Services,
                RepairsPage => PageType.Repairs,
                DriversPage => PageType.Drivers,
                PartitionsPage => PageType.Partitions,
                TraySettingsPage => PageType.TraySettings,
                IntegrityPage => PageType.Integrity,
                GameBoostPage => PageType.GameBoost,
                DiagnosticPage => PageType.Diagnostic,
                _ => null
            };
        }

        private static Page GetPageInstance(PageType type)
        {
            return type switch
            {
                PageType.Dashboard => new DashboardPage(),
                PageType.Tweaks => new TweaksPage(),
                PageType.Apps => new AppsPage(),
                PageType.Storage => new CleanupPage(),
                PageType.Network => new NetworkPage(),
                PageType.Games => new GamesPage(),
                PageType.Services => new ServicesPage(),
                PageType.Repairs => new RepairsPage(),
                PageType.Drivers => new DriversPage(),
                PageType.Partitions => new PartitionsPage(),
                PageType.TraySettings => new TraySettingsPage(),
                PageType.Integrity => new IntegrityPage(),
                PageType.GameBoost => new GameBoostPage(),
                PageType.Diagnostic => new DiagnosticPage(),
                _ => new DashboardPage()
            };
        }

        private async void LstSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstSearchResults.SelectedItem is not GlobalSearchResult result)
                return;

            LstSearchResults.SelectedItem = null;
            TxtGlobalSearch.Text = "";
            if (SearchPopup != null) SearchPopup.IsOpen = false;

            await ExecuteGlobalSearchResultAsync(result);
        }

        /// <summary>
        /// Executa um resultado da pesquisa global (navegação, tweak ou ação).
        /// </summary>
        public async Task ExecuteGlobalSearchResultAsync(GlobalSearchResult item)
        {
            if (item.Type == SearchResultType.Navigation)
            {
                if (!string.IsNullOrEmpty(item.NavigationTag))
                    NavigateToPage(item.NavigationTag);
                return;
            }

            try
            {
                if (item.Title.Contains("Reiniciar", StringComparison.OrdinalIgnoreCase) ||
                    item.Title.Contains("MPO", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await ShowConfirmationDialog($"Executar '{item.Title}'?"))
                        return;
                }

                string taskId = BackgroundTaskTracker.Instance.RegisterTask($"Executando: {item.Title}", "GlobalSearch");

                (bool success, string message) result = (false, "");

                await Task.Run(() =>
                {
                    if (item.ExecuteAction != null)
                        result = item.ExecuteAction.Invoke();

                    if (item.IsToggle && item.CheckState != null)
                    {
                        try { item.IsActive = item.CheckState.Invoke(); }
                        catch { }
                    }
                });

                BackgroundTaskTracker.Instance.CompleteTask(taskId, result.success, result.message);

                if (result.success)
                    ShowSuccess("CONCLUÍDO", result.message);
                else
                    ShowInfo("ATENÇÃO", result.message);
            }
            catch (Exception ex)
            {
                ShowError("ERRO CRÍTICO", ex.Message);
            }
        }
        #endregion

        #region SISTEMA DE NAVEGAÇÃO

        public bool IsNavigationLocked { get; set; } = false;

        private static readonly Dictionary<string, PageType> NavTagMap = new()
        {
            ["🏠"] = PageType.Dashboard,
            ["⚡"] = PageType.Tweaks,
            ["📱"] = PageType.Apps,
            ["💿"] = PageType.Storage,
            ["🌐"] = PageType.Network,
            ["🎮"] = PageType.Games,
            ["🛡️"] = PageType.Services,
            ["🔧"] = PageType.Repairs,
            ["💾"] = PageType.Drivers,
            ["💽"] = PageType.Partitions,
            ["🧰"] = PageType.Integrity,
            ["🔔"] = PageType.TraySettings,
            ["🚀"] = PageType.GameBoost,
            ["🔬"] = PageType.Diagnostic,
        };

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsNavigationLocked)
            {
                if (sender is RadioButton rb)
                {
                    rb.IsChecked = false;
                    UpdateNavButtonsSelection();
                }
                ShowError("NAVEGAÇÃO BLOQUEADA", "Uma operação crítica de disco está em curso. Aguarde a conclusão para trocar de página.");
                return;
            }

            if (sender is RadioButton btn && btn.Tag is string tag)
            {
                if (TxtGlobalSearch != null) TxtGlobalSearch.Text = "";
                if (NavTagMap.TryGetValue(tag, out var pageType))
                    NavigateToPage(pageType);
                else
                    ShowInfo("EM BREVE", "Página em desenvolvimento.");
            }
        }

        // 📍 ANIMAÇÃO 5: Fade-in ao navegar entre páginas (0.25s) - MainWindow.xaml.cs linha ~403
        private void MainFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (MainFrame.Content is Page page)
            {
                // Durante o carregamento inicial (antes da intro terminar),
                // não animar escala para não conflitar com a transição do splash.
                if (!_introCompleted)
                {
                    page.RenderTransform = null;
                    return;
                }

                // Animação de fade-in com scale leve
                page.RenderTransform = new ScaleTransform(0.98, 0.98, 0.5, 0.5);

                var scaleIn = new DoubleAnimation
                {
                    From = 0.98,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                if (page.RenderTransform is ScaleTransform scaleTrans)
                {
                    scaleTrans.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
                    scaleTrans.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
                }
            }
        }

        private void UpdateNavButtonsSelection()
        {
            if (MainFrame.Content is DashboardPage) BtnDashboard.IsChecked = true;
            else if (MainFrame.Content is TweaksPage) BtnTweaks.IsChecked = true;
            else if (MainFrame.Content is AppsPage) BtnApps.IsChecked = true;
            else if (MainFrame.Content is CleanupPage) BtnStorage.IsChecked = true;
            else if (MainFrame.Content is NetworkPage) BtnNetwork.IsChecked = true;
            else if (MainFrame.Content is GamesPage) BtnGames.IsChecked = true;
            else if (MainFrame.Content is ServicesPage) BtnServices.IsChecked = true;
            else if (MainFrame.Content is RepairsPage) BtnRepairs.IsChecked = true;
            else if (MainFrame.Content is DriversPage) BtnDrivers.IsChecked = true;
            else if (MainFrame.Content is PartitionsPage) BtnPartitions.IsChecked = true;
            else if (MainFrame.Content is TraySettingsPage) { if (BtnTray != null) BtnTray.IsChecked = true; }
            else if (MainFrame.Content is IntegrityPage) { if (BtnIntegrity != null) BtnIntegrity.IsChecked = true; }
            else if (MainFrame.Content is GameBoostPage) { if (BtnGameBoost != null) BtnGameBoost.IsChecked = true; }
            else if (MainFrame.Content is ToolsPage) { }
            else if (MainFrame.Content is WinbootPage) { }
            else if (MainFrame.Content is AdvancedToolsPage) { }
            else if (MainFrame.Content is SecurityPage) { }
            else if (MainFrame.Content is PrivacyPage) { }
            else if (MainFrame.Content is ActivationPage) { }
            else if (MainFrame.Content is UpdatePage) { }
            else if (MainFrame.Content is DiagnosticPage) { if (BtnDiagnostic != null) BtnDiagnostic.IsChecked = true; }
            else if (MainFrame.Content is WinTunePage) { }
            else if (MainFrame.Content is AllTweaksPage) { }
            else if (MainFrame.Content is StutterPage) { }
        }

        private void UncheckAllNavButtons()
        {
            if (BtnDashboard != null) BtnDashboard.IsChecked = false;
            if (BtnTweaks != null) BtnTweaks.IsChecked = false;
                        if (BtnApps != null) BtnApps.IsChecked = false;
            if (BtnStorage != null) BtnStorage.IsChecked = false;
            if (BtnNetwork != null) BtnNetwork.IsChecked = false;
            if (BtnGames != null) BtnGames.IsChecked = false;
            if (BtnServices != null) BtnServices.IsChecked = false;
            if (BtnRepairs != null) BtnRepairs.IsChecked = false;
            if (BtnDrivers != null) BtnDrivers.IsChecked = false;
            if (BtnPartitions != null) BtnPartitions.IsChecked = false;

            // CORREÇÃO: Garante que o botão de integridade no topo também seja desmarcado
            if (BtnIntegrity != null) BtnIntegrity.IsChecked = false;
            if (BtnTray != null) BtnTray.IsChecked = false;
            if (BtnDiagnostic != null) BtnDiagnostic.IsChecked = false;
        }

        public void NavigateToPage(string? pageTag, object? senderButton = null)
        {
            if (pageTag == null) return;

            int tabIndex = 0;
            if (pageTag.Contains(":"))
            {
                var parts = pageTag.Split(':');
                if (parts.Length > 1) int.TryParse(parts[1], out tabIndex);
                pageTag = parts[0];
            }

            if (Enum.TryParse<PageType>(pageTag, true, out var pageType))
                NavigateToPage(pageType, tabIndex);
            else if (NavTagMap.TryGetValue(pageTag, out var mappedType))
                NavigateToPage(mappedType, tabIndex);
            else
                ShowInfo("EM BREVE", "Página em desenvolvimento.");
        }

        /// <summary>
        /// Navegação usando PageType enum (sem dependência de emojis)
        /// </summary>
        public void NavigateToPage(PageType pageType, int tabIndex = 0)
        {
            Page? newPage = pageType switch
            {
                PageType.Dashboard => new DashboardPage(),
                PageType.Tweaks => new TweaksPage(),
                PageType.Screen => new ScreenPage(),
                PageType.Apps => new AppsPage(),
                PageType.Storage => new CleanupPage(),
                PageType.Network => new NetworkPage(),
                PageType.Games => new GamesPage(),
                PageType.Tools => new ToolsPage(tabIndex),
                PageType.GameBoost => new GameBoostPage(),
                PageType.Services => new ServicesPage(tabIndex),
                PageType.Repairs => new RepairsPage(),
                PageType.Drivers => new DriversPage(),
                PageType.Partitions => new PartitionsPage(),
                PageType.Winboot => new WinbootPage(),
                PageType.AdvancedTools => new AdvancedToolsPage(),
                PageType.IsoEditor => new IsoEditorPage(),
                PageType.Integrity => new IntegrityPage(),
                PageType.Security => new SecurityPage(),
                PageType.Privacy => new PrivacyPage(),
                PageType.Activation => new ActivationPage(),
                PageType.TraySettings => new TraySettingsPage(),
                PageType.Update => new UpdatePage(),
                PageType.Diagnostic => new DiagnosticPage(),
                PageType.Server => new ServerPage(),
                PageType.Rufus => new RufusPage(),
                PageType.AdvancedRamCleanSettings => new AdvancedRamCleanSettingsPage(),
                PageType.AllTweaks => new AllTweaksPage(),
                PageType.ExmTweaks => new ExmTweaksPage(),
                PageType.Stutter => new StutterPage(),
                PageType.WinTune => new WinTunePage(),
                PageType.QuickInstall => new QuickInstallPage(),
                PageType.Shrink => new ShrinkPage(),
                _ => null
            };

            if (newPage != null)
            {
                CleanupAndNavigate(newPage);
            }
            else
            {
                ShowInfo("EM BREVE", "Página em desenvolvimento.");
            }
        }

        /// <summary>
        /// Navegação simples - apenas navega para a nova página.
        /// </summary>
        private void CleanupAndNavigate(Page newPage)
        {
            try
            {

                if (MainFrame.Content is Page previousPage)
                {
                    // Usa reflection para chamar o método Cleanup() se existir
                    var cleanupMethod = previousPage.GetType().GetMethod("Cleanup", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (cleanupMethod != null)
                    {
                        try
                        {
                            cleanupMethod.Invoke(previousPage, null);
                        }
                        catch { }
                    }
                }

                MainFrame.Navigate(newPage);

                // Remove histórico de navegação em background (sem forçar GC)
                // O GC do .NET gerencia a memória de forma muito mais eficiente que chamadas manuais.
                // GC.Collect() forçado causa micro-freezes e compete com a renderização do WPF.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        while (MainFrame.NavigationService.CanGoBack)
                            MainFrame.NavigationService.RemoveBackEntry();
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Erro na navegação: {ex.Message}");
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            // Minimiza para a bandeja em vez de fechar
            Hide();
            _trayService?.ShowMinimizedNotification();
        }
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = (WindowState == WindowState.Normal) ? WindowState.Maximized : WindowState.Normal;
        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        #endregion

        #region EVENTOS DO HEADER (Console e Notificações)

        private void BtnNotifications_Click(object sender, RoutedEventArgs e)
        {
            if (NotifPanel != null) NotifPanel.Toggle();
        }

        
        private void BtnConsole_Click(object sender, RoutedEventArgs e)
        {
            // Alterna a visibilidade do console
            if (GlobalConsolePanel != null)
            {
                GlobalConsolePanel.Visibility = GlobalConsolePanel.Visibility == Visibility.Collapsed 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
        }

        // ⚙️ CONFIGURAÇÕES: Abre página de configurações
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Logger.Log("⚙️ Abrindo configurações...");
                CleanupAndNavigate(new SettingsPage());
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnSettings_Click", $"Erro: {ex.Message}");
                ShowError("❌ Erro", "Não foi possível abrir configurações");
            }
        }

        // � MODO DESENVOLVEDOR: Atualiza visibilidade do menu de debug
        public void UpdateDebugMenuVisibility(bool isDeveloperMode)
        {
            try
            {
                // Mostrar/esconder botão de diagnóstico
                if (BtnDiagnostic != null)
                {
                    BtnDiagnostic.Visibility = isDeveloperMode ? Visibility.Visible : Visibility.Collapsed;
                }

                // Mostrar/esconder console global
                if (GlobalConsolePanel != null)
                {
                    GlobalConsolePanel.Visibility = isDeveloperMode ? Visibility.Visible : Visibility.Collapsed;
                }

                // Logger.Log($"🐛 Modo desenvolvedor: {(isDeveloperMode ? "ATIVADO" : "DESATIVADO")}");
            }
            catch (Exception ex)
            {
                Logger.LogError("UpdateDebugMenuVisibility", $"Erro: {ex.Message}");
            }
        }

        // �🔥 LIMPEZA MANUAL: Botão para forçar limpeza de memory leaks
        private async void BtnCleanMemory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Logger.Log("🧹 Iniciando limpeza manual de memory leaks...");
                
                var result = await AggressiveMemoryCleaner.PerformAggressiveCleanup();
                
                var message = $"""
                    ✅ Limpeza Concluída!
                    
                    Memória antes: {result.MemoryBefore / 1024 / 1024:F1} MB
                    Memória depois: {result.MemoryAfter / 1024 / 1024:F1} MB
                    Liberado: {result.Freed / 1024 / 1024:F1} MB
                    
                    Total acumulado liberado: {AggressiveMemoryCleaner.TotalMemoryFreed / 1024 / 1024:F1} MB
                    Limpezas realizadas: {AggressiveMemoryCleaner.CleanupCount}
                    """;
                
                Logger.Log(message);
                
                // Mostra notificação de confirmação
                ShowSuccess("🧹 Limpeza Concluída", $"Liberados {result.Freed / 1024 / 1024:F1} MB");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnCleanMemory", $"Erro na limpeza: {ex.Message}");
                ShowError("❌ Erro", "Falha na limpeza de memória");
            }
        }

        // --- ATUALIZAÇÃO: Botão de Atualizações ---
        private void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {

                if (sender is Button btn)
                {
                    // Animação simples sem complexidade
                    var storyboard = new Storyboard();
                    var animation = new DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = TimeSpan.FromSeconds(1)
                    };
                    
                    Storyboard.SetTarget(animation, btn);
                    Storyboard.SetTargetProperty(animation, new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
                    
                    btn.RenderTransform = new RotateTransform(18);
                    storyboard.Children.Add(animation);
                    storyboard.Begin();
                }


                KitLugia.Core.Logger.Log("🔄 Abrindo página de atualizações...");
                NavigateToPage(PageType.Update);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro ao abrir atualizações: {ex.Message}");
                MessageBox.Show(
                    $"Erro ao abrir página de atualizações:\n{ex.Message}",
                    "Erro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // --- MONITOR DE SEGUNDO PLANO: Botão de Monitoramento ---
        private void BtnBackgroundMonitor_Click(object sender, RoutedEventArgs e)
        {
            BackgroundMonitorPanel.Visibility = Visibility.Visible;
            RefreshBackgroundTasksList();
        }

        // --- GOODBYEDPI: Botão para ativar/desativar ---
        private async void BtnGoodbyeDPI_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (GoodbyeDPIActive)
                {
                    // Desativar GoodbyeDPI
                    if (_goodbyeDpiProcess != null && !_goodbyeDpiProcess.HasExited)
                    {
                        _goodbyeDpiProcess.Kill();
                        await Task.Run(() => _goodbyeDpiProcess.WaitForExit(5000));
                        _goodbyeDpiProcess = null;
                        Logger.Log("GOODBYEDPI: Desativado");
                        ShowInfo("📶 GoodbyeDPI Desativado", $"Status: {GetGoodbyeDPIStatus()}");
                    }
                    else
                    {
                        // Processo externo detectado
                        var result = MessageBox.Show(
                            "GoodbyeDPI está rodando externamente.\n\nDeseja encerrar todos os processos goodbyedpi.exe?",
                            "GoodbyeDPI Externo Detectado",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            var processes = Process.GetProcessesByName("goodbyedpi");
                            foreach (var proc in processes)
                            {
                                try
                                {
                                    proc.Kill();
                                    proc.WaitForExit(5000);
                                }
                                catch { }
                            }
                            _goodbyeDpiProcess = null;
                            Logger.Log("GOODBYEDPI: Processos externos encerrados");
                            ShowInfo("📶 GoodbyeDPI Encerrado", "Todos os processos goodbyedpi.exe foram encerrados.");
                        }
                    }
                }
                else
                {
                    // Verificar se ESET está instalado (incompatível)
                    string esetPath = @"C:\Program Files\ESET\ESET Security\ekrn.exe";
                    if (System.IO.File.Exists(esetPath))
                    {
                        var result = MessageBox.Show(
                            "⚠️ AVISO: ESET Antivirus detectado!\n\n" +
                            "O ESET Antivirus é incompatível com o GoodbyeDPI (WinDivert driver).\n" +
                            "Isso pode causar conflitos ou o GoodbyeDPI não funcionar corretamente.\n\n" +
                            "Deseja continuar mesmo assim?",
                            "Aviso de Compatibilidade",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.No)
                        {
                            return;
                        }
                    }

                    // Ativar GoodbyeDPI
                    string goodbyeDpiPath = System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Tools",
                        "GoodbyeDPI",
                        "goodbyedpi.exe");

                    if (!System.IO.File.Exists(goodbyeDpiPath))
                    {
                        MessageBox.Show(
                            $"❌ Erro: Arquivo goodbyedpi.exe não encontrado!\n\n" +
                            $"Caminho: {goodbyeDpiPath}",
                            "Erro",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    // Iniciar GoodbyeDPI com argumentos baseados nas configurações
                    _goodbyeDpiProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = goodbyeDpiPath,
                            Arguments = BuildGoodbyeDPIArguments(),
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        }
                    };

                    _goodbyeDpiProcess.Start();
                    Logger.Log($"GOODBYEDPI: Ativado (PID: {_goodbyeDpiProcess.Id})");


                    await Task.Delay(500);

                    // Verifica se o processo ainda está rodando
                    if (!_goodbyeDpiProcess.HasExited)
                    {
                        string modeName = _goodbyeDpiMode switch
                        {
                            "-1" => "Rússia",
                            "-2" => "Qualquer País",
                            "-3" => "EUA",
                            _ => "Desconhecido"
                        };
                        string fragText = _goodbyeDpiFragmentation ? "Ativada" : "Desativada";
                        string dnsText = _goodbyeDpiDNS ? "Ativado" : "Desativado";
                        ShowSuccess("📶 GoodbyeDPI Ativado", $"Status: {GetGoodbyeDPIStatus()}\n\nModo: {modeName} ({_goodbyeDpiMode})\nFragmentação: {fragText}\nDNS Redirection: {dnsText}");
                    }
                    else
                    {
                        ShowError("📶 Erro ao Ativar", "O GoodbyeDPI iniciou mas encerrou imediatamente.\n\nVerifique se há conflitos com antivírus ou firewall.");
                        _goodbyeDpiProcess = null;
                        return;
                    }

                    // Notificar mudança de propriedade
                    OnPropertyChanged(nameof(GoodbyeDPIActive));
                }

                // Atualizar UI
                OnPropertyChanged(nameof(GoodbyeDPIActive));
            }
            catch (Exception ex)
            {
                Logger.LogError("GoodbyeDPI", $"Erro ao controlar GoodbyeDPI: {ex.Message}");
                MessageBox.Show(
                    $"❌ Erro ao controlar GoodbyeDPI: {ex.Message}",
                    "Erro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // Evento PropertyChanged para atualizar UI
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void CloseBackgroundMonitor_Click(object sender, RoutedEventArgs e)
        {
            BackgroundMonitorPanel.Visibility = Visibility.Collapsed;
        }

        private void ClearCompletedTasks_Click(object sender, RoutedEventArgs e)
        {
            Services.BackgroundTaskTracker.Instance.ClearCompletedHistory();
            RefreshBackgroundTasksList();
        }

        private void RefreshBackgroundTasksList()
        {
            var tasks = Services.BackgroundTaskTracker.Instance.GetAllTasks();
            Dispatcher.Invoke(() =>
            {
                if (BackgroundTasksList != null)
                {
                    BackgroundTasksList.ItemsSource = null;
                    BackgroundTasksList.ItemsSource = tasks;
                }

                if (MonitorSubtitle != null)
                {
                    int runningCount = tasks.Count(t => t.Status == Services.TaskStatus.Running);
                    MonitorSubtitle.Text = runningCount > 0
                        ? $"{runningCount} tarefa(s) em execução"
                        : "Nenhuma tarefa em execução";

                    // Atualiza badge no botão
                    UpdateBackgroundTaskBadge(runningCount);
                }
            });
        }

        private void UpdateBackgroundTaskBadge(int count)
        {
            if (BtnBackgroundMonitor.Template.FindName("TaskBadge", BtnBackgroundMonitor) is Border badge &&
                BtnBackgroundMonitor.Template.FindName("TxtTaskCount", BtnBackgroundMonitor) is TextBlock txtCount)
            {
                if (count > 0)
                {
                    badge.Visibility = Visibility.Visible;
                    txtCount.Text = count.ToString();

                    // Animação de pulso quando contador muda
                    var pulseStoryboard = new Storyboard();

                    var scaleUp = new DoubleAnimation
                    {
                        From = 1,
                        To = 1.3,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    var scaleDown = new DoubleAnimation
                    {
                        From = 1.3,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(150),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                        BeginTime = TimeSpan.FromMilliseconds(150)
                    };

                    if (badge.RenderTransform == null || !(badge.RenderTransform is ScaleTransform))
                    {
                        badge.RenderTransform = new ScaleTransform { CenterX = 7, CenterY = 7 };
                    }

                    var scaleTransform = (ScaleTransform)badge.RenderTransform;

                    Storyboard.SetTarget(scaleUp, scaleTransform);
                    Storyboard.SetTargetProperty(scaleUp, new PropertyPath(ScaleTransform.ScaleXProperty));

                    Storyboard.SetTarget(scaleDown, scaleTransform);
                    Storyboard.SetTargetProperty(scaleDown, new PropertyPath(ScaleTransform.ScaleXProperty));

                    pulseStoryboard.Children.Add(scaleUp);
                    pulseStoryboard.Children.Add(scaleDown);

                    pulseStoryboard.Begin();
                }
                else
                {
                    badge.Visibility = Visibility.Collapsed;
                }
            }
        }

        #endregion

        #region CONSOLE E NOTIFICAÇÕES (Lógica)

        private void UpdateNotificationBadge()
        {
            Dispatcher.Invoke(() =>
            {
                int count = NotificationHistoryManager.History.Count;
                if (BtnNotifications.Template.FindName("Badge", BtnNotifications) is Border badge &&
                    BtnNotifications.Template.FindName("TxtBadgeCount", BtnNotifications) is TextBlock txtCount)
                {
                    if (count > 0)
                    {
                        badge.Visibility = Visibility.Visible;
                        txtCount.Text = count > 99 ? "99+" : count.ToString();
                    }
                    else
                    {
                        badge.Visibility = Visibility.Collapsed;
                    }
                }
            });
        }


        private void BackgroundTaskTracker_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Services.BackgroundTaskTracker.HasActiveTasks) ||
                e.PropertyName == nameof(Services.BackgroundTaskTracker.ActiveTaskCount))
            {
                UpdateBackgroundTaskIndicator();
                RefreshBackgroundTasksList();
            }
        }

        private bool _backgroundTasksHadFailure = false;

        private void BackgroundTaskTracker_TaskStatusChanged(object? sender, Services.TaskStatusChangedEventArgs e)
        {
            if (e.Status == Services.TaskStatus.Running)
                _backgroundTasksHadFailure = false;
            else if (e.Status == Services.TaskStatus.Failed)
                _backgroundTasksHadFailure = true;

            UpdateBackgroundTaskIndicator();
            RefreshBackgroundTasksList();
        }

        private bool IsTaskForCurrentPage(string pageName)
        {
            if (string.IsNullOrEmpty(pageName))
                return true; // se não especificou página, mostra sempre

            try
            {
                if (MainFrame?.Content is Page currentPage)
                {
                    var currentName = currentPage.GetType().Name.Replace("Page", "", StringComparison.OrdinalIgnoreCase);
                    return currentName.Equals(pageName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return true; // em caso de erro, mostra o toast
        }

        // Dicionário para rastrear toasts ativos com stack
        private Dictionary<string, Border> _activeBackgroundToasts = new Dictionary<string, Border>();
        private Dictionary<string, int> _backgroundToastStackCount = new Dictionary<string, int>();
        private long _lastToastAnimationTick = 0;

        private void ShowBackgroundTaskToast(Services.BackgroundTaskInfo taskInfo, string icon)
        {
            Dispatcher.Invoke(() =>
            {
                string toastKey = $"{taskInfo.TaskName}|{taskInfo.PageName}";

                // Verifica se já existe um toast com a mesma chave
                if (_activeBackgroundToasts.TryGetValue(toastKey, out var existingToast))
                {
                    // Incrementa o contador de stack
                    _backgroundToastStackCount[toastKey]++;
                    int stackCount = _backgroundToastStackCount[toastKey];

                    // Atualiza o badge do contador
                    if (existingToast.Child is Grid existingGrid && existingGrid.Children.Count > 1)
                    {
                        // Procura pelo badge de stack
                        var badge = existingGrid.Children.OfType<Border>().FirstOrDefault(b => b.Name == "StackBadge");
                        if (badge != null)
                        {
                            var countText = badge.Child as TextBlock;
                            if (countText != null)
                            {
                                countText.Text = $"x{stackCount}";
                                badge.Visibility = Visibility.Visible;

                                // Animação de "pop" no badge
                                long currentTick = DateTime.Now.Ticks;
                                if (currentTick - _lastToastAnimationTick > 1000000) // 100ms
                                {
                                    _lastToastAnimationTick = currentTick;
                                    var scaleTrans = new ScaleTransform(1, 1);
                                    badge.RenderTransform = scaleTrans;
                                    badge.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

                                    var anim = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(150));
                                    scaleTrans.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                                    scaleTrans.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                                }
                            }
                        }
                    }

                    // Reinicia o timer para o toast ficar mais tempo na tela
                    var existingTimer = existingToast.Tag as DispatcherTimer;
                    if (existingTimer != null)
                    {
                        existingTimer.Stop();
                        existingTimer.Start();
                    }

                    return;
                }

                // Cria novo toast
                var toast = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 42, 42, 42)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 51, 51, 51)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(15, 10, 15, 10),
                    Margin = new Thickness(0, 0, 0, 10),
                    Width = 300,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = System.Windows.Media.Colors.Black,
                        BlurRadius = 15,
                        ShadowDepth = 5,
                        Opacity = 0.5
                    }
                };

                var toastGrid = new Grid();
                toastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                toastGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Container para ícone e badge
                var iconContainer = new Grid();
                iconContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                iconContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var iconText = new TextBlock
                {
                    Text = icon,
                    FontSize = 18,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 0)),
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(iconText, 0);

                // Badge de stack
                var stackBadge = new Border
                {
                    Name = "StackBadge",
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(64, 255, 255, 255)),
                    CornerRadius = new CornerRadius(10),
                    MinWidth = 20,
                    Height = 20,
                    Margin = new Thickness(0, -6, -6, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Visibility = Visibility.Collapsed
                };
                Grid.SetColumn(stackBadge, 1);

                var stackCountText = new TextBlock
                {
                    Text = "x1",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(5, 0, 5, 0)
                };
                stackBadge.Child = stackCountText;

                iconContainer.Children.Add(iconText);
                iconContainer.Children.Add(stackBadge);
                Grid.SetColumn(iconContainer, 0);

                var stackPanel = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center
                };

                var titleText = new TextBlock
                {
                    Text = taskInfo.TaskName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White)
                };

                var pageText = new TextBlock
                {
                    Text = taskInfo.PageName,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)),
                    Margin = new Thickness(0, 2, 0, 0)
                };

                stackPanel.Children.Add(titleText);
                stackPanel.Children.Add(pageText);
                Grid.SetColumn(stackPanel, 1);

                toastGrid.Children.Add(iconContainer);
                toastGrid.Children.Add(stackPanel);
                toast.Child = toastGrid;

                // Animação de entrada
                toast.Opacity = 0;
                toast.RenderTransform = new TranslateTransform { X = 50 };

                var storyboard = new Storyboard();

                var fadeIn = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                var slideIn = new DoubleAnimation
                {
                    From = 50,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(fadeIn, toast);
                Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));

                Storyboard.SetTarget(slideIn, toast.RenderTransform);
                Storyboard.SetTargetProperty(slideIn, new PropertyPath("X"));

                storyboard.Children.Add(fadeIn);
                storyboard.Children.Add(slideIn);

                // Adiciona ao dicionário
                _activeBackgroundToasts[toastKey] = toast;
                _backgroundToastStackCount[toastKey] = 1;

                BackgroundTaskToasts?.Children.Add(toast);
                storyboard.Begin();

                // Remove após 3 segundos
                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };

                timer.Tick += (s, args) =>
                {
                    timer.Stop();

                    var fadeOut = new DoubleAnimation
                    {
                        From = 1,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(300),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };

                    var slideOut = new DoubleAnimation
                    {
                        From = 0,
                        To = 50,
                        Duration = TimeSpan.FromMilliseconds(300),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };

                    Storyboard.SetTarget(fadeOut, toast);
                    Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));

                    Storyboard.SetTarget(slideOut, toast.RenderTransform);
                    Storyboard.SetTargetProperty(slideOut, new PropertyPath("X"));

                    var exitStoryboard = new Storyboard();
                    exitStoryboard.Children.Add(fadeOut);
                    exitStoryboard.Children.Add(slideOut);

                    exitStoryboard.Completed += (es, ea) =>
                    {
                        BackgroundTaskToasts?.Children.Remove(toast);
                        _activeBackgroundToasts.Remove(toastKey);
                        _backgroundToastStackCount.Remove(toastKey);
                    };

                    exitStoryboard.Begin();
                };

                toast.Tag = timer;
                timer.Start();
            });
        }

        private void UpdateBackgroundTaskIndicator()
        {
            Dispatcher.Invoke(() =>
            {
                var tracker = Services.BackgroundTaskTracker.Instance;
                if (tracker.HasActiveTasks)
                {
                    BackgroundTaskIndicator.Visibility = Visibility.Visible;
                    BackgroundTaskIndicator.BorderThickness = new Thickness(0);
                    BackgroundTaskIndicator.BorderBrush = System.Windows.Media.Brushes.Transparent;
                    TaskStatusIcon.StartSpinning();
                    BackgroundTaskCount.Text = $"{tracker.ActiveTaskCount} {(tracker.ActiveTaskCount == 1 ? "tarefa" : "tarefas")} em execução";

                    // ⬇️ NOVO: Mostra nome da tarefa com progresso + calcula média
                    double totalProgress = 0;
                    int progressCount = 0;
                    string? activeTaskName = null;
                    foreach (var task in tracker.GetAllTasks())
                    {
                        if (task.Status == Services.TaskStatus.Running || task.Status == Services.TaskStatus.ProgressUpdate)
                        {
                            var progressStr = task.Progress?.Trim() ?? "";
                            if (!string.IsNullOrEmpty(progressStr))
                            {
                                var numStr = System.Text.RegularExpressions.Regex.Replace(progressStr, @"[^0-9.]", "");
                                if (double.TryParse(numStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double pct))
                                {
                                    totalProgress += pct;
                                    progressCount++;
                                    if (activeTaskName == null) activeTaskName = task.TaskName;
                                }
                            }
                        }
                    }

                    // Mostra nome da tarefa ativa (ou lista de páginas se não houver progresso)
                    if (activeTaskName != null)
                    {
                        BackgroundTaskPages.Text = activeTaskName;
                    }
                    else
                    {
                        var pages = new System.Collections.Generic.HashSet<string>();
                        foreach (var task in tracker.GetAllTasks())
                            pages.Add(task.PageName);
                        BackgroundTaskPages.Text = string.Join(", ", pages);
                    }

                    // Barra de progresso: visível se QUALQUER tarefa tiver progresso reportado
                    if (progressCount > 0 && BackgroundTaskProgressBar != null)
                    {
                        double avg = totalProgress / progressCount;
                        BackgroundTaskProgressBar.Value = avg;
                        BackgroundTaskProgressBar.Visibility = Visibility.Visible;
                    }
                    else if (BackgroundTaskProgressBar != null)
                    {
                        BackgroundTaskProgressBar.Value = 0;
                        BackgroundTaskProgressBar.Visibility = Visibility.Collapsed;
                    }

                    // Animação de "pop" no badge e ícone do botão de monitor
                    if (BtnBackgroundMonitor != null && BtnBackgroundMonitor.Template != null)
                    {
                        BtnBackgroundMonitor.ApplyTemplate();

                        // Animação no badge
                        if (BtnBackgroundMonitor.Template.FindName("TaskBadge", BtnBackgroundMonitor) is Border badge &&
                            badge.RenderTransform is ScaleTransform badgeScale)
                        {
                            var anim = new System.Windows.Media.Animation.DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(150));
                            badgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                            badgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                        }

                        // Animação no ícone da engrenagem
                        if (BtnBackgroundMonitor.Template.FindName("monitorIcon", BtnBackgroundMonitor) is TextBlock icon &&
                            icon.RenderTransform is ScaleTransform iconScale)
                        {
                            var iconAnim = new System.Windows.Media.Animation.DoubleAnimation(1.2, 1.0, TimeSpan.FromMilliseconds(150));
                            iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconAnim);
                            iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconAnim);
                        }
                    }
                }
                else
                {
                    // Mostra ícone de resultado (✓ ou ✗) por alguns segundos antes de recolher
                    bool allOk = !_backgroundTasksHadFailure;
                    TaskStatusIcon.Complete(allOk);

                    // Borda colorida ao redor da notificação
                    BackgroundTaskIndicator.BorderBrush = allOk
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0x4C, 0xAF, 0x50))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x99, 0xF4, 0x43, 0x36));
                    BackgroundTaskIndicator.BorderThickness = new Thickness(1);

                    if (BackgroundTaskProgressBar != null)
                    {
                        BackgroundTaskProgressBar.Value = allOk ? 100 : 0;
                        BackgroundTaskProgressBar.Visibility = Visibility.Collapsed;
                    }

                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5)
                    };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();
                        TaskStatusIcon.Reset();
                        BackgroundTaskIndicator.BorderThickness = new Thickness(0);
                        BackgroundTaskIndicator.BorderBrush = System.Windows.Media.Brushes.Transparent;
                        BackgroundTaskIndicator.Visibility = Visibility.Collapsed;
                    };
                    timer.Start();
                }
            });
        }

        private void CloseBackgroundTaskIndicator_Click(object sender, RoutedEventArgs e)
        {
            BackgroundTaskIndicator.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region OVERLAYS E TOASTS (Helpers)

        public async Task<bool> ShowConfirmationDialog(string message)
        {
            TxtConfirmMessage.Text = message;
            OverlayContainer.Visibility = Visibility.Visible;
            OverlayConfirm.Visibility = Visibility.Visible;
            _confirmCompletionSource = new TaskCompletionSource<bool>();
            return await _confirmCompletionSource.Task;
        }

        private void BtnConfirmYes_Click(object sender, RoutedEventArgs e)
        {
            OverlayConfirm.Visibility = Visibility.Collapsed;
            OverlayContainer.Visibility = Visibility.Collapsed;
            _confirmCompletionSource?.SetResult(true);
        }

        private void BtnConfirmNo_Click(object sender, RoutedEventArgs e)
        {
            OverlayConfirm.Visibility = Visibility.Collapsed;
            OverlayContainer.Visibility = Visibility.Collapsed;
            _confirmCompletionSource?.SetResult(false);
        }

        public void ShowSuccess(string title, string message) => ShowNotification(title, message, NotificationType.Success);
        public void ShowError(string title, string message) => ShowNotification(title, message, NotificationType.Error);
        public void ShowInfo(string title, string message) => ShowNotification(title, message, NotificationType.Info);

        public void ShowActionNotification(string title, string message, Action? onAction = null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var notif = new Controls.UpdateNotification();
                notif.SetContent(title, message, onAction);
                notif.Dismissed += (n) => ActionNotificationContainer.Children.Remove(n);
                ActionNotificationContainer.Children.Clear();
                ActionNotificationContainer.Children.Add(notif);
            });
        }

        private int GetPriorityValue(NotificationType type)
        {
            return type switch { NotificationType.Error => 3, NotificationType.Info => 2, NotificationType.Success => 1, _ => 0 };
        }

        private void ShowNotification(string title, string message, NotificationType type)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ConsoleManager.WriteLine($"NOTIFICAÇÃO: [{title}] {message}");

                if (title != "AGUARDE" && title != "PROCESSANDO")
                    NotificationHistoryManager.Add(title, message, type);

                string searchId = (type == NotificationType.Info) ? "GENERIC_INFO" : $"{type}|{title}|{message}";

                if (_activeToasts.TryGetValue(searchId, out LugiaToast? existingToast))
                {
                    if (type == NotificationType.Info) existingToast.UpdateMessage(message);
                    else existingToast.IncrementCounter();
                    return;
                }

                var toast = new LugiaToast();
                toast.SetContent(title, message, type);
                _activeToasts[searchId] = toast;

                if (ToastContainer.Children.Count >= MaxVisibleToasts)
                {
                    if (ToastContainer.Children[ToastContainer.Children.Count - 1] is LugiaToast last) last.Dismiss();
                }

                ToastContainer.Children.Insert(0, toast);

                void OnToastDismissed(LugiaToast t)
                {
                    t.Dismissed -= OnToastDismissed;
                    if (_activeToasts.ContainsKey(searchId)) _activeToasts.Remove(searchId);
                    ToastContainer.Children.Remove(t);
                }
                toast.Dismissed += OnToastDismissed;
            });
        }

        // ⬇️ NOVO: Toast de progresso que transiciona de Info (amarelo) → Success (verde) ou Error (vermelho)
        public void ShowProgressToast(string taskId, string title, string initialMessage)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Se já existe um toast para essa tarefa, só atualiza
                if (_progressToasts.TryGetValue(taskId, out var existing))
                {
                    existing.UpdateMessage(initialMessage);
                    return;
                }

                // Cria novo toast do tipo Info (amarelo)
                var toast = new LugiaToast();
                toast.SetContent(title, initialMessage, NotificationType.Info);
                toast.NotificationId = $"PROGRESS|{taskId}";

                _progressToasts[taskId] = toast;

                // Insere no container
                if (ToastContainer.Children.Count >= MaxVisibleToasts)
                {
                    if (ToastContainer.Children[ToastContainer.Children.Count - 1] is LugiaToast last)
                        last.Dismiss();
                }
                ToastContainer.Children.Insert(0, toast);

                toast.Dismissed += (t) =>
                {
                    _progressToasts.Remove(taskId);
                    ToastContainer.Children.Remove(t);
                };
            });
        }

        public void UpdateProgressToast(string taskId, string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_progressToasts.TryGetValue(taskId, out var toast))
                {
                    toast.UpdateMessage(message);
                }
            });
        }

        public void CompleteProgressToast(string taskId, bool success, string finalMessage)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!_progressToasts.TryGetValue(taskId, out var toast)) return;

                // ⭐ MÁGICA: transiciona o toast para o tipo final com animação
                var newType = success ? NotificationType.Success : NotificationType.Error;
                string finalTitle = success ? "CONCLUÍDO" : "FALHA";
                toast.TransitionToType(newType, finalTitle, finalMessage);

                // Adiciona ao histórico de notificações
                NotificationHistoryManager.Add(finalTitle, finalMessage, newType);

                // Remove do dicionário de progresso (agora é um toast normal com seu próprio ciclo de vida)
                _progressToasts.Remove(taskId);
            });
        }
        #endregion

        #region Loading Overlay Methods

        /// <summary>
        /// Mostra o overlay de carregamento com mensagem personalizada
        /// </summary>
        public void ShowLoading(string message = "Processando...")
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.Message = message;
                LoadingOverlay.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Esconde o overlay de carregamento
        /// </summary>
        public void HideLoading()
        {
            if (LoadingOverlay != null)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Executa uma operação pesada em background mostrando loading
        /// </summary>
        public async Task<T> ExecuteWithLoadingAsync<T>(string message, Func<T> operation)
        {
            ShowLoading(message);
            try
            {
                return await Task.Run(() => operation());
            }
            finally
            {
                HideLoading();
            }
        }

        /// <summary>
        /// Executa uma operação pesada em background mostrando loading (void)
        /// </summary>
        public async Task ExecuteWithLoadingAsync(string message, Action operation)
        {
            ShowLoading(message);
            try
            {
                await Task.Run(() => operation());
            }
            finally
            {
                HideLoading();
            }
        }

        /// <summary>
        /// Cleanup de recursos e handlers para evitar memory leaks
        /// </summary>
        private void Cleanup()
        {
            try
            {

                if (_backgroundTasksCts != null)
                {
                    _backgroundTasksCts.Cancel();
                    _backgroundTasksCts.Dispose();
                    _backgroundTasksCts = null;
                }


                AggressiveMemoryCleaner.StopIntelligentMonitoring();


                if (_logHandler != null)
                {
                    KitLugia.Core.Logger.OnLogReceived -= _logHandler;
                    _logHandler = null;
                }
                if (_notificationCountHandler != null)
                {
                    NotificationHistoryManager.OnCountChanged -= _notificationCountHandler;
                    _notificationCountHandler = null;
                }

                Services.BackgroundTaskTracker.Instance.TaskStatusChanged -= BackgroundTaskTracker_TaskStatusChanged;
                Services.BackgroundTaskTracker.Instance.PropertyChanged -= BackgroundTaskTracker_PropertyChanged;

                // Limpa o timer de debounce
                _searchDebounceTimer?.Stop();


                _healthCheckTimer?.Stop();

                // Parar timer de status do GoodbyeDPI
                _goodbyeDpiStatusTimer?.Stop();


                _trayService?.SaveProcessLimits();
                
                // Limpa o serviço de tray
                _trayService?.Dispose();

                // Limpa GoodbyeDPI
                if (_goodbyeDpiProcess != null && !_goodbyeDpiProcess.HasExited)
                {
                    try
                    {
                        _goodbyeDpiProcess.Kill();
                        _goodbyeDpiProcess.WaitForExit(5000);
                        Logger.Log("GOODBYEDPI: Processo encerrado no cleanup");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("GoodbyeDPI.Cleanup", ex.Message);
                    }
                    _goodbyeDpiProcess = null;
                }

                // Limpa toasts ativos
                _activeToasts.Clear();


                _showWindowEvent?.Dispose();
                _showWindowEvent = null;
            }
            catch (Exception ex)
            {
                Logger.LogError("MainWindow.Cleanup", ex.Message);
            }
        }

        // 📍 ANIMAÇÃO 21: Splash Screen de Intro (controlada por configuração) - MainWindow.xaml.cs linha ~1451-1650
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (StartMinimized)
            {
                // Tray mode: skip intro animation, finish init immediately
                if (SplashScreen != null) SplashScreen.Visibility = Visibility.Collapsed;
                OnIntroFinished();
            }
            else
            {
                // Splash visível (Opacity=1, Z=99999) cobre MainFrame + sidebar.
                // MainFrame já fica pronto atrás; OnIntroFinished só ativa a sidebar.
                if (SplashScreen != null) SplashScreen.Opacity = 1;
                EnsureUIIinitialized();
                _ = LoadIntroSettingsAndPlay();
            }
            _ = CheckForUpdateNotificationAsync();
        }

        private async Task CheckForUpdateNotificationAsync()
        {
            var notificationFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UPDATE_COMPLETE.txt");
            if (System.IO.File.Exists(notificationFile))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(notificationFile);
                    var info = System.Text.Json.JsonSerializer.Deserialize<UpdateCompleteInfo>(json);
                    if (info != null)
                    {
                        bool isReinstall = info.OldVersion == info.NewVersion;
                        string versionLine = $"{info.OldVersion}  --->  {info.NewVersion}";
                        if (isReinstall)
                            versionLine += "  Reinstalado";
                        string message = $"{versionLine}\n\nO KitLugia foi {(isReinstall ? "reinstalado" : "atualizado")} com sucesso.";

                        await Dispatcher.InvokeAsync(() =>
                        {
                            var notif = new Controls.UpdateNotification();
                            notif.SetUpdateContent("Atualização Concluída", message, info.NewVersion, info.OldVersion);
                            notif.Dismissed += (n) => ActionNotificationContainer.Children.Remove(n);
                            ActionNotificationContainer.Children.Clear();
                            ActionNotificationContainer.Children.Add(notif);
                        });
                    }
                }
                catch { }
                System.IO.File.Delete(notificationFile);
            }
        }

        private class UpdateCompleteInfo
        {
            public string OldVersion { get; set; } = "";
            public string NewVersion { get; set; } = "";
        }

        private async Task LoadIntroSettingsAndPlay()
        {
            // Garante que o layout WPF terminou de renderizar antes de iniciar qualquer animação
            await Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { }));

            bool introEnabled = true;
            double introDuration = 2.2;

            try
            {
                (introEnabled, introDuration) = await Task.Run(() => ReadIntroSettingsWithTimeout())
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (true, 2.2));
            }
            catch (Exception ex)
            {
                Logger.Log($"WinTune: Erro ao carregar configurações de intro: {ex.Message}, usando valores padrão");
            }

            if (introEnabled)
            {
                PlayIntroAnimation(introDuration);
                // Safety: garante OnIntroFinished mesmo se o Storyboard Completed falhar
                var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(introDuration + 1.5) };
                safetyTimer.Tick += (s, args) => { safetyTimer.Stop(); if (!_introCompleted) OnIntroFinished(); };
                safetyTimer.Start();
            }
            else
            {
                SkipIntroAnimation();
            }
        }

        private (bool enabled, double duration) ReadIntroSettingsWithTimeout()
        {
            try
            {
                string configDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KitLugia");
                string configPath = System.IO.Path.Combine(configDir, "settings.json");

                if (!System.IO.Directory.Exists(configDir))
                    System.IO.Directory.CreateDirectory(configDir);

                if (!System.IO.File.Exists(configPath))
                {
                    var defaultSettings = new AppSettings();
                    string json = System.Text.Json.JsonSerializer.Serialize(defaultSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(configPath, json);
                    return (defaultSettings.IntroAnimationEnabled, defaultSettings.IntroDuration);
                }

                string existingJson = System.IO.File.ReadAllText(configPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(existingJson);
                if (settings != null)
                    return (settings.IntroAnimationEnabled, settings.IntroDuration);
            }
            catch (Exception ex)
            {
                Logger.LogError("MainWindow.ReadIntroSettings", ex.Message);
            }

            return (true, 2.2);
        }

        // Classe auxiliar local para ler settings.json (apenas propriedades necessárias para intro)
        private class AppSettings
        {
            public bool IntroAnimationEnabled { get; set; } = true;
            public double IntroDuration { get; set; } = 3.0;
        }

        // Flag para garantir que SkipIntroAnimation só seja chamado uma vez
        private bool _introCompleted = false;

        // Intro — restaurada com fade-in, scale, glow, translate, fade-out (animação completa)
        private void PlayIntroAnimation(double duration)
        {
            double fadeInT = duration * 0.23;
            double delayT = duration * 0.23;
            double moveT = duration * 0.27;
            double fadeOutT = duration * 0.09;
            double menuT = duration * 0.14;
            double moveStart = fadeInT + delayT;
            double fadeOutStart = moveStart + moveT;
            double menuStart = fadeOutStart + fadeOutT;

            var sb = new Storyboard();
            if (SplashScreen != null)
            {
                // Sem fade-in de opacidade: o splash já está em Opacity=1 para cobrir
                // o MainFrame desde o primeiro frame (setado em Window_Loaded).
                A(sb, SplashScale, "ScaleX", 0.5, 1.0, fadeInT, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
                A(sb, SplashScale, "ScaleY", 0.5, 1.0, fadeInT, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
                if (SplashGlow != null) A(sb, SplashGlow, "BlurRadius", 30, 50, fadeInT * 0.5, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
                if (SplashSubtext != null) A(sb, SplashSubtext, "Opacity", 0, 1, fadeInT, fadeInT * 0.6, new CubicEase { EasingMode = EasingMode.EaseOut });
                P(sb, fadeInT);
                A(sb, SplashTranslate, "X", 0, -400, moveT, moveStart, new CubicEase { EasingMode = EasingMode.EaseInOut });
                A(sb, SplashTranslate, "Y", 0, -300, moveT, moveStart, new CubicEase { EasingMode = EasingMode.EaseInOut });
                A(sb, SplashScale, "ScaleX", 1.0, 0.25, moveT, moveStart, new CubicEase { EasingMode = EasingMode.EaseInOut });
                A(sb, SplashScale, "ScaleY", 1.0, 0.25, moveT, moveStart, new CubicEase { EasingMode = EasingMode.EaseInOut });
                A(sb, SplashScreen, "Opacity", 1, 0, fadeOutT, fadeOutStart, new CubicEase { EasingMode = EasingMode.EaseIn });
            }
            if (SidebarPanel != null) A(sb, SidebarPanel, "Opacity", 0, 1, menuT, menuStart, new CubicEase { EasingMode = EasingMode.EaseOut });
            // MainFrame já está visível (Opacity=1) atrás do splash.
            // Apenas a sidebar é animada aqui; aparece suave quando o splash sumir.
            sb.Completed += (_, _) => { OnIntroFinished(); };
            sb.Begin();
        }

        private static void A(Storyboard sb, DependencyObject t, string p, double from, double to, double dur, double begin, IEasingFunction? easing = null)
        {
            var anim = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromSeconds(dur), BeginTime = TimeSpan.FromSeconds(begin), EasingFunction = easing };
            Storyboard.SetTarget(anim, t); Storyboard.SetTargetProperty(anim, new PropertyPath(p)); sb.Children.Add(anim);
        }

        private void P(Storyboard sb, double fadeInTime)
        {
            var ps = new[] { Particle1, Particle2, Particle3, Particle4, Particle5, Particle6 };
            var rng = new Random();
            foreach (var p in ps)
            {
                if (p == null) continue;
                double d = rng.NextDouble() * 0.3;
                A(sb, p, "Opacity", 0, 0.6, fadeInTime, d, new CubicEase { EasingMode = EasingMode.EaseOut });
                A(sb, p, "(UIElement.RenderTransform).(TranslateTransform.X)", 0, rng.Next(-50, 50), fadeInTime, d, new CubicEase { EasingMode = EasingMode.EaseOut });
                A(sb, p, "(UIElement.RenderTransform).(TranslateTransform.Y)", 0, rng.Next(-50, 50), fadeInTime, d, new CubicEase { EasingMode = EasingMode.EaseOut });
            }
        }



        // Pular animação de intro e mostrar menu diretamente
        private void SkipIntroAnimation()
        {
            if (_introCompleted) return;

            if (SplashScreen != null)
            {
                SplashScreen.Visibility = Visibility.Collapsed;
            }

            // Fade-in do menu lateral
            var menuFadeInStoryboard = new Storyboard();

            if (SidebarPanel != null)
            {
                var sidebarOpacityAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = TimeSpan.FromSeconds(0.3),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(sidebarOpacityAnimation, SidebarPanel);
                Storyboard.SetTargetProperty(sidebarOpacityAnimation, new PropertyPath("Opacity"));
                menuFadeInStoryboard.Children.Add(sidebarOpacityAnimation);
            }

            // MainFrame já está visível atrás do splash. Só a sidebar é animada.

            menuFadeInStoryboard.Completed += (_, _) => { OnIntroFinished(); };
            menuFadeInStoryboard.Begin();

            // Safety: garante OnIntroFinished mesmo se o Storyboard Completed falhar
            var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
            safetyTimer.Tick += (s, args) => { safetyTimer.Stop(); if (!_introCompleted) OnIntroFinished(); };
            safetyTimer.Start();
        }

        private void OnIntroFinished()
        {
            if (_introCompleted) return;
            _introCompleted = true;

            // Garante que a Sidebar fique visível (fallback caso a storyboard não tenha completado)
            // MainFrame já está em Opacity=1, coberto pelo splash até agora.
            if (SidebarPanel != null)
            {
                SidebarPanel.BeginAnimation(FrameworkElement.OpacityProperty, null);
                SidebarPanel.Opacity = 1;
            }

            if (SplashScreen != null) SplashScreen.Visibility = Visibility.Collapsed;

            // GoodbyeDPI timer só inicia agora (não compete com a intro)
            _goodbyeDpiStatusTimer?.Start();

            // Auto-start do GoodbyeDPI (adiado para não competir com animações)
            if (_goodbyeDpiAutoStart && !GoodbyeDPIActive)
            {
                DispatcherTimer autoStartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                autoStartTimer.Tick += (s, args) =>
                {
                    autoStartTimer.Stop();
                    ActivateGoodbyeDPI();
                    Logger.Log("GOODBYEDPI: Auto-start ativado");
                };
                autoStartTimer.Start();
            }

            // Dashboard já carregou via Loaded com delay de 80ms; não precisa fazer nada aqui
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Verificar se deve minimizar para tray em vez de fechar
            if (TrayService != null && TrayService.CloseToTray && TrayService.IsTrayEnabled)
            {
                e.Cancel = true; // Cancelar o fechamento
                this.Hide(); // Minimizar para tray
                KitLugia.Core.Logger.Log("🔔 Janela minimizada para Tray (Close to Tray ativado)");
            }
            else
            {
                KitLugia.Core.Logger.Log("👋 Fechando aplicação (Close to Tray desativado ou Tray desativado)");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Cleanup();
            base.OnClosed(e);
        }

        #endregion
    }
}