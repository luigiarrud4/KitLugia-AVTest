using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using MenuItem = System.Windows.Controls.MenuItem;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KitLugia.Core;
using KitLugia.Core.TaskManager;
using KitLugia.GUI.Helpers;

namespace KitLugia.GUI.Windows.TaskManager
{
    public partial class KitTaskManagerWindow : Window
    {
        // ══════════════════════════════════════════════
        //  STATE
        // ══════════════════════════════════════════════
        private List<ProcessRow> _allRows = new();
        private List<ProcessRow> _filteredRows = new();
        private Dictionary<int, TimeSpan> _prevCpu = new(); // mantido para fallback .NET
        private DateTime _prevTime = DateTime.UtcNow;
        private readonly KitLugia.Core.TaskManager.NativeMetricsHelper.CpuDeltaTracker _cpuTracker = new(); // TMOG-style
        private bool _useNativeProcPath = true; // auto-desativa se o caminho nativo falhar
        private readonly object _lock = new();
        private bool _isClosed; // FIX crash em máquinas lentas: async continuations pós-Close

        /// <summary>
        /// A janela já ficou oculta (minimizada para a bandeja / escondida pelo dono). Serve
        /// para distinguir a PRIMEIRA exibição (que não precisa de refresh extra — o Loaded
        /// cuida dela) do retorno da bandeja (que precisa, senão a tela mostra dado velho).
        /// </summary>
        private bool _windowWasHidden;
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private CancellationTokenSource? _refreshCts;

        // Performance counters
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _memAvailable;
        private long _totalMemBytes;
        // Icon cache: path → BitmapSource
        private readonly Dictionary<string, BitmapSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _iconLock = new();
        private static BitmapSource? _genericIcon;

        /// <summary>
        /// Ícone genérico do shell (imageres.dll), criado SOB DEMANDA.
        /// Estava no construtor: SHGetFileInfo + leitura do imageres.dll NA THREAD DA UI em
        /// toda abertura, mesmo quando nenhuma linha precisava dele (quase todo processo tem
        /// ícone próprio e ele nem era usado no Resumo — a aba que abre por padrão).
        /// O helper já devolve o BitmapSource CONGELADO, então criar num worker é seguro.
        /// </summary>
        private static BitmapSource? GenericIcon => _genericIcon ??= ProgramIconHelper.GetGenericIcon();

        // Search
        private DispatcherTimer? _searchDebounce;
        private string _lastSearchQuery = "";

        // Refresh
        private DispatcherTimer? _refreshTimer;
        private int _refreshSeconds = 1;

        // Network cache
        private Dictionary<uint, int> _networkConnections = new();

        // Per-process network speed cache: pid → (readBytes, writeBytes, timestamp)
        private readonly Dictionary<int, (double readBytes, double writeBytes, DateTime time)> _netSpeedCache = new();

        // Disk read+write perf counter
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;

        // Performance graphs (histórico por dispositivo — ver região PERFORMANCE TAB)
        private DispatcherTimer? _graphTimer;

        // Process I/O cache (from last refresh cycle)
        private Dictionary<int, ProcessIoHelper.ProcessIoRate> _ioCache = new();

        // Mini CPU graph for detail panel
        private readonly Queue<float> _miniCpuHistory = new(31);

        // Services & Startup
        private List<ServiceInfo> _allServices = new();
        private List<StartupAppDetails> _allStartupApps = new();

        // Sorting (Win11: usuário escolhe e mantém)
        private string _currentSortColumn = "CpuValue";
        private ListSortDirection _currentSortDirection = ListSortDirection.Descending;
        private readonly ObservableCollection<ProcessRow> _groupedLive = new();
        private CollectionViewSource? _groupedCvs;
        private bool _cvsInitialized = false;
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("kernel32.dll")]
        private static extern void GetPhysicallyInstalledSystemMemory(out long totalMemoryInKb);

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>Milissegundos desde o último input do usuário (teclado/mouse). uint.MaxValue = não mediu.</summary>
        private static uint MillisecondsSinceLastInput()
        {
            try
            {
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (GetLastInputInfo(ref lii)) return unchecked((uint)Environment.TickCount) - lii.dwTime;
            }
            catch { }
            return uint.MaxValue;
        }

        // ══════════════════════════════════════════════
        //  SINGLETON + ABERTURA ÚNICA (compatibilidade multi-sistema)
        //  Cada nova instância criava OUTRO conjunto de timers de 1s + contadores
        //  PerformanceCounter. Em máquinas mais fracas, abrir várias vezes a janela
        //  empilhava refreshes e o app congelava/crashava. Reusa a instância viva.
        // ══════════════════════════════════════════════
        private static KitTaskManagerWindow? _sharedInstance;
        private static readonly object _instanceLock = new();

        public static KitTaskManagerWindow OpenOrActivate(System.Windows.Window? owner)
        {
            lock (_instanceLock)
            {
                var existing = _sharedInstance;
                if (existing != null && !existing._isClosed)
                {
                    try
                    {
                        if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                        existing.Show();
                        existing.Activate();
                        // Janela pré-aquecida nasce SEM dono (o Prewarm roda em idle, sem owner).
                        // Sem isto, a instância adotada ficaria fora do ciclo de vida da janela
                        // principal — o caminho frio sempre cria com Owner.
                        if (existing.Owner == null && owner != null && !ReferenceEquals(owner, existing))
                            existing.Owner = owner;
                    }
                    catch { }
                    return existing;
                }
                KitTaskManagerWindow w;
                try
                {
                    w = new KitTaskManagerWindow { Owner = owner };
                }
                catch (Exception ctorEx)
                {
                    // Ctor falhou: limpa referência para o próximo clique tentar de novo
                    // do zero (sem singleton “zumbi” que re-lança a mesma exceção).
                    try { KitLugia.Core.Logger.Log($"[KIT TASK MANAGER] Ctor falhou: {ctorEx.Message}"); } catch { }
                    throw;
                }
                _sharedInstance = w;
                w.Closed += (_, __) =>
                {
                    lock (_instanceLock)
                    {
                        if (ReferenceEquals(_sharedInstance, w)) _sharedInstance = null;
                        // Libera o próximo prewarm: quem fecha e reabre o gerenciador era
                        // premiado com um cold-start (o XAML é reprocessado a cada construção).
                        _prewarmed = false;
                    }
                    ScheduleReprewarm(w.Dispatcher);
                };
                return w;
            }
        }

        /// <summary>
        /// Reconstrói a janela oculta em idle DEPOIS que a anterior fechou, para a próxima
        /// abertura voltar a ser Show()+Activate(). O piso de 15 s existe de propósito: quem
        /// fechou pode estar justamente querendo a memória de volta — esperamos um tempo
        /// humano antes de reconstruir, e se o usuário abrir antes disso o Prewarm não faz nada.
        /// </summary>
        private static void ScheduleReprewarm(System.Windows.Threading.Dispatcher dispatcher)
            => ScheduleReprewarm(dispatcher, 15);

        private static void ScheduleReprewarm(System.Windows.Threading.Dispatcher dispatcher, int seconds)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    await dispatcher.InvokeAsync(Prewarm, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                catch { }
            });
        }

        /// <summary>
        /// Abre (ou traz para frente) o gerenciador já numa aba específica. Usado pelo
        /// atalho "Central de Diagnóstico" do painel inicial.
        /// </summary>
        public static void OpenOnTab(System.Windows.Window? owner, string tabTag)
        {
            var w = OpenOrActivate(owner);
            w.Show();
            // Depois do layout: a troca de aba mexe em visibilidade e constrói conteúdo.
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { w.SwitchTabByTag(tabTag); } catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Descarta a instância quebrada (após falha de abertura) para permitir nova tentativa limpa.</summary>
        public static void DiscardBrokenInstance()
        {
            lock (_instanceLock)
            {
                var broken = _sharedInstance;
                _sharedInstance = null;
                if (broken != null)
                {
                    try { broken.Close(); } catch { }
                }
            }
        }

        private static bool _prewarmed;

        /// <summary>
        /// Pré-aquece a janela FORA do clique. O custo dominante da 1ª abertura é
        /// InitializeComponent (XAML de ~2300 linhas) + JIT de todos os partials —
        /// 1-3s em Debug/máquinas fracas, tudo NA UI THREAD. Criando a janela oculta
        /// em idle (30s após o startup), o clique vira apenas Show()+Activate().
        /// O singleton do OpenOrActivate ADOTA esta instância — o trabalho nunca é
        /// jogado fora; se o usuário clicar antes, o prewarm simplesmente não roda.
        /// </summary>
        public static void Prewarm()
        {
            // NÃO construir a janela com o usuário digitando/clicando AGORA: o custo é todo na
            // UI thread e apareceria como engasgo no meio de uma clique. Adia e re-tenta quando
            // houver 3 s de ociosidade — o pior caso é um cold-start (o que já acontecia antes).
            try
            {
                if (MillisecondsSinceLastInput() < 3000)
                {
                    var d = System.Windows.Application.Current?.Dispatcher;
                    if (d != null) ScheduleReprewarm(d, 8);
                    return;
                }
            }
            catch { }

            lock (_instanceLock)
            {
                if (_prewarmed) return;
                var existing = _sharedInstance;
                if (existing != null && !existing._isClosed) { _prewarmed = true; return; }
                try
                {
                    // Deve rodar na UI THREAD (elementos WPF têm afinidade de thread).
                    // Janela construída mas nunca "Showned" é invisível — sem flash na tela.
                    var w = new KitTaskManagerWindow();
                    _sharedInstance = w;
                    w.Closed += (_, __) => { lock (_instanceLock) { if (ReferenceEquals(_sharedInstance, w)) _sharedInstance = null; } };
                    _prewarmed = true;
                    try { KitLugia.Core.Logger.Log("[KIT TASK MANAGER] Janela pré-aquecida em idle — clique abrirá instantâneo."); } catch { }
                }
                catch (Exception ex)
                {
                    // Otimização best-effort: se falhar aqui, o clique tenta do zero (comportamento antigo).
                    try { KitLugia.Core.Logger.Log($"[KIT TASK MANAGER] Prewarm falhou (clique fará cold-start): {ex.Message}"); } catch { }
                }
            }
        }

        // ══════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════
        public KitTaskManagerWindow()
        {
            InitializeComponent();

            // FIX minimizar: janela tem ShowInTaskbar=False (tool window filha).
            // Minimizar criava janela órfã invisível sem botão na taskbar — com explorer.exe
            // fechado aparecia como "vários gerenciadores abertos" impossível de restaurar.
            // Remove botão minimizar do XAML e bloqueia minimize via sistema (Win+Down, Alt+Space).
            StateChanged += (_, __) =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            };

            // FIX HARD ERROR: suprime o box "Exception Processing Message 0xC0000005" que
            // aparece em máquinas com drives de rede/USB ausentes quando shell32 tenta
            // carregar ícones. Com SEM_FAILCRITICALERRORS as APIs falham silenciosamente.
            ProgramIconHelper.SuppressHardErrorDialogs();

            // Nota: handlers globais (DispatcherUnhandledException / UnobservedTaskException)
            // foram movidos para App.RegisterGlobalExceptionHandlers — registrados UMA vez no
            // startup. Aqui NÃO registramos mais por instância (vazava handlers a cada open).

            InitFrameEngine();   // animação das linhas (esmaecer verde/vermelho) — ligada SOB DEMANDA
            // Nota: o motor fluido de apresentação (hook de CompositionTarget.Rendering que
            // animava gráficos/barras/números entre amostras) foi removido — os gráficos
            // voltaram a ser redesenhados por amostra, no tick fixo de ~1s.

            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += SearchDebounce_Tick;

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_refreshSeconds) };
            // async void → QUALQUER exceção que escape do tick cairia no dispatcher e derrubaria o
            // app inteiro. Try/catch extra garante que um tick falho nunca sai daqui sem log.
            _refreshTimer.Tick += async (_, __) =>
            {
                // Janela oculta (kit minimizado para a bandeja com o gerenciador aberto):
                // NENHUMA aba está na tela. Enumerar ~400 processos por segundo para ninguém
                // ver era o maior consumo contínuo do kit — e é o que fazia o app "pesar".
                // O IsVisibleChanged religa com um refresh imediato ao reaparecer.
                if (!IsVisible) return;
                try { await RefreshAsync(); }
                catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Refresh tick: {ex.Message}"); } catch { } }
                // Aba Usuários aberta: mantém CPU/memória por usuário vivos no mesmo ritmo
                try { if (TabUsers.Visibility == Visibility.Visible) _ = LoadUsersSafeAsync(); } catch { }
            };

            _graphTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            // Mesmo gate do refresh: escondida, a coleta de PDX/nativo + o desenho dos gráficos
            // (o trabalho mais caro do tick) não tem quem olhe.
            _graphTimer.Tick += (_, __) => { if (IsVisible) UpdatePerformanceGraphsSafe(); };

            // Voltou a aparecer (kit restaurado da bandeja, TM reexibido): não esperar o próximo
            // tick — o dado na tela é de quando a janela foi escondida.
            IsVisibleChanged += (_, __) =>
            {
                try
                {
                    if (!IsVisible) { _windowWasHidden = true; return; }
                    // 1ª exibição não conta: o Loaded já dispara o refresh inicial.
                    if (!_windowWasHidden) return;
                    _windowWasHidden = false;
                    _ = RefreshAsync();
                    UpdatePerformanceGraphsSafe();
                }
                catch { }
            };

            // (o ícone genérico do shell passou a ser criado sob demanda — ver GenericIcon:
            //  era I/O de shell na thread da UI em TODA abertura, para nada.)

            Loaded += (_, __) =>
            {
                try
                {
                    // Inicializa CollectionViewSource live uma única vez (evita recriar e perder SortDescriptions)
                    _groupedCvs = new CollectionViewSource { Source = _groupedLive };
                    _groupedCvs.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
                    DgProcesses.ItemsSource = _groupedCvs.View;
                    _cvsInitialized = true;
                    // Marca CPU como ordenação inicial (setinha ↓)
                    foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                    var cpuCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == "CpuValue");
                    if (cpuCol != null) cpuCol.SortDirection = ListSortDirection.Descending;
                    ApplySorting();

                    // ── Abertura INSTANTÂNEA (fire-and-forget) ──
                    // NUNCA await aqui — a janela aparece em <50ms, dados populam depois.
                    // Marca a aba inicial (Resumo) na sidebar: o Indicator novo é uma pílula
                    // sem default visível no XAML — sem isso a sidebar inicia toda apagada.
                    ActivateSidebarButton(BtnTabSummary);
                    // Véu da 1ª carga: a janela já está na tela e os dados ainda vêm —
                    // mostra spinner + etapa + tempo decorrido em vez da casca vazia.
                    BeginLoadingOverlay();
                    TxtStatus.Text = "Primeira coleta em andamento: enumerando processos e contadores do sistema.";
                    // Contadores são lentos (PerformanceCounter cria registry + NtQuery) — off UI
                    _ = Task.Run(() => InitCountersSafe());
                    // Primeiro refresh sem bloquear UI; timers já iniciam para não perder tick
                    _ = RefreshAsync();
                    _refreshTimer.Start();
                    _graphTimer.Start();
                    // Primeiro tick de desempenho JÁ (antes só acontecia 1 s depois, e o Resumo
                    // — a aba que abre — ficava sem número nenhum nesse segundo inteiro; em
                    // máquina fraca a coleta ainda somava mais alguns segundos em cima disso).
                    UpdatePerformanceGraphsSafe();

                    // FIX abertura lenta: aqui rodavam TRÊS varreduras pesadas para abas que o
                    // usuário talvez nunca abra — Win32_Service (Serviços), registro + tarefas
                    // agendadas (Inicialização) e PerformanceCounterCategory + DXGI + CIM
                    // (Dispositivos de desempenho). A aba Resumo (a que abre) não usa NENHUMA
                    // delas. Agora cada uma só roda quando a aba dela é aberta — o próprio
                    // SwitchTabByTag já as dispara, então não se perde nada.
                }
                catch (Exception ex)
                {
                    try { Logger.Log($"[KIT TASK MANAGER] Erro no Loaded: {ex.Message}"); } catch { }
                }
            };

            Closing += (_, __) =>
            {
                // Teardown 100% defensivo: exceção aqui deixa a janela "zumbi" (não fecha,
                // timers continuam, kit congela — relato histórico de force-stop).
                try
                {
                    _isClosed = true;
                    try { _refreshCts?.Cancel(); } catch { }
                    _refreshTimer?.Stop();
                    _graphTimer?.Stop();
                    _searchDebounce?.Stop();
                    // A escuta de áudio é um singleton com thread própria: sem isso a
                    // thread ficaria lendo o buffer em loopback para sempre (100 Hz) mesmo
                    // com a janela fechada — overhead medindo um problema que não está mais na tela.
                    try { if (AudioGlitchMonitor.Instance.IsRunning) AudioGlitchMonitor.Instance.Stop(); } catch { }
                    StopLoadingOverlay();
                    DisposeCounters();
                    try { ProcessIoHelper.ResetAll(); } catch { }
                    try { _refreshGate.Dispose(); } catch { }
                    try { _refreshCts?.Dispose(); } catch { }
                    try { GpuMonitor.Shutdown(); } catch { }
                    // Motor de animação: sai do CompositionTarget.Rendering ao fechar (antes
                    // ficava assinado até o Closed, animando uma janela que já não existe).
                    ReleaseFrameAnimation();
                }
                catch (Exception closeEx)
                {
                    try { Logger.Log($"[KIT TASK MANAGER] Closing: {closeEx.Message}"); } catch { }
                }
            };
        }

        // ══════════════════════════════════════════════
        //  COUNTERS
        // ══════════════════════════════════════════════
        private void InitCounters()
        {
            try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuCounter.NextValue(); } catch { }
            try { _memAvailable = new PerformanceCounter("Memory", "Available MBytes"); } catch { }
            try { _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total"); _diskReadCounter.NextValue(); } catch { }
            try { _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total"); _diskWriteCounter.NextValue(); } catch { }
            _totalMemBytes = GetTotalPhysicalMemory();
        }

        /// <summary>Versão blindada: nunca lança (PerformanceCounter pode falhar em ambientes restritos).</summary>
        private void InitCountersSafe()
        {
            try { InitCounters(); }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] InitCounters: {ex.Message}"); } catch { } }
            // Sucesso ou falha, esta etapa terminou: o véu não pode depender dela para sair.
            try { Dispatcher.BeginInvoke(new Action(NotifyLoadCountersReady), DispatcherPriority.Background); } catch { }
        }

        /// <summary>
        /// Tick de gráficos blindado: pula se o tick anterior ainda não terminou (máquinas lentas
        /// acumulavam renders e congelavam), e captura qualquer exceção para não derrubar o app.
        /// </summary>
        private int _graphTickRunning;
        private async void UpdatePerformanceGraphsSafe()
        {
            if (Interlocked.CompareExchange(ref _graphTickRunning, 1, 0) != 0) return; // tick anterior ainda rodando
            try
            {
                // FIX congelamento: a coleta (PDH + queries nativas) roda em WORKER;
                // a UI thread só desenha o snapshot. Se a coleta anterior ainda não
                // terminou, este tick é pulado (história perde 1 batida — melhor que stall).
                var sample = await CollectSamplesAsync();
                if (sample == null || _isClosed) return;
                RenderPerfSample(sample);
                try { UpdateSummaryTick(); }
                catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Summary: {ex.Message}"); } catch { } }
                NotifyLoadGraphicsRendered();
            }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Graphs: {ex.Message}"); } catch { } }
            finally { Interlocked.Exchange(ref _graphTickRunning, 0); }
        }

        // ── Lazy loading: Serviços/Inicialização só quando o usuário abre a aba ──
        private bool _servicesLazyStarted;
        private Task LoadServicesWhenNeededAsync()
        {
            if (_servicesLazyStarted) return Task.CompletedTask;
            _servicesLazyStarted = true;
            return LoadServicesAsync();
        }

        private bool _startupLazyStarted;
        private Task LoadStartupWhenNeededAsync()
        {
            if (_startupLazyStarted) return Task.CompletedTask;
            _startupLazyStarted = true;
            return LoadStartupAppsAsync();
        }

        // Versões blindadas p/ fire-and-forget do Loaded/SwitchTab: nunca deixam exceção escapar.
        private async Task LoadServicesWhenNeededSafeAsync()
        {
            try { await LoadServicesWhenNeededAsync(); }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Services: {ex.Message}"); } catch { } }
        }

        private async Task LoadStartupWhenNeededSafeAsync()
        {
            try { await LoadStartupWhenNeededAsync(); }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Startup: {ex.Message}"); } catch { } }
        }

        // ======================================================
        //  ABA USUÁRIOS — paridade TMOG: cada usuário com CPU%, memória,
        //  nº de processos e a LISTA dos processos dele (expansível), como no TMOG.
        // ======================================================
        public sealed class UserProcRow : INotifyPropertyChanged
        {
            private string _name = "", _cpu = "0%", _memMB = "";
            private double _cpuValue, _memValue;

            public int Pid { get; set; }

            // Notifica: o detalhe do usuário ABERTO atualiza os valores em LUGAR
            // (sem re-bind da lista inteira a cada segundo).
            public string Name { get => _name; set { if (_name == value) return; _name = value; Raise(nameof(Name)); } }
            public string Cpu { get => _cpu; set { if (_cpu == value) return; _cpu = value; Raise(nameof(Cpu)); } }
            public double CpuValue { get => _cpuValue; set { if (_cpuValue == value) return; _cpuValue = value; Raise(nameof(CpuValue)); } }
            public string MemMB { get => _memMB; set { if (_memMB == value) return; _memMB = value; Raise(nameof(MemMB)); } }
            public double MemValue { get => _memValue; set { if (_memValue == value) return; _memValue = value; Raise(nameof(MemValue)); } }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        public sealed class UserRow : INotifyPropertyChanged
        {
            private bool _isExpanded;

            private string _status = "ativa", _cpu = "0%", _memMB = "";
            private double _cpuValue, _memValue;
            private int _processCount;
            private List<UserProcRow> _processes = new();

            public string UserName { get; set; } = "";

            // Propriedades NOTIFICAM: sem isso o refresh por segundo so conseguia atualizar os
            // numeros TROCANDO a linha inteira (o que derrubava selecao e expansao).
            public string Status { get => _status; set { if (_status == value) return; _status = value; Raise(nameof(Status)); } }
            public string Cpu { get => _cpu; set { if (_cpu == value) return; _cpu = value; Raise(nameof(Cpu)); } }
            public double CpuValue { get => _cpuValue; set { if (_cpuValue == value) return; _cpuValue = value; Raise(nameof(CpuValue)); Raise(nameof(CpuCellBackground)); } }
            public string MemMB { get => _memMB; set { if (_memMB == value) return; _memMB = value; Raise(nameof(MemMB)); } }
            public double MemValue { get => _memValue; set { if (_memValue == value) return; _memValue = value; Raise(nameof(MemValue)); } }
            public int ProcessCount { get => _processCount; set { if (_processCount == value) return; _processCount = value; Raise(nameof(ProcessCount)); } }

            public List<UserProcRow> Processes
            {
                get => _processes;
                set
                {
                    _processes = value ?? new();
                    Raise(nameof(Processes));
                    Raise(nameof(ExpandIcon));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

            /// <summary>Processos do usuário aparecem/somem ao clicar na setinha (RowDetails).</summary>
            public bool IsExpanded
            {
                get => _isExpanded;
                set
                {
                    if (_isExpanded == value) return;
                    _isExpanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetailsVisibility)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandIcon)));
                }
            }

            public Visibility DetailsVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;
            public string ExpandIcon => Processes.Count > 1 ? (IsExpanded ? "▼" : "▶") : "";

            /// <summary>
            /// Atualiza a lista de processos do usuário SEM trocar a instância a cada
            /// segundo: os PIDs que continuam vivos têm os valores atualizados em lugar
            /// (as linhas notificam) e só quando o CONJUNTO/ordem muda é que a lista é
            /// reatribuída — antes, 182 processos eram recriados 1x/s dentro do detalhe.
            /// </summary>
            public void MergeProcesses(List<UserProcRow> src)
            {
                var byPid = new Dictionary<int, UserProcRow>(_processes.Count);
                foreach (var p in _processes) byPid[p.Pid] = p;

                var merged = new List<UserProcRow>(src.Count);
                foreach (var s in src)
                {
                    if (byPid.TryGetValue(s.Pid, out var keep))
                    {
                        keep.Name = s.Name;
                        keep.Cpu = s.Cpu; keep.CpuValue = s.CpuValue;
                        keep.MemMB = s.MemMB; keep.MemValue = s.MemValue;
                        merged.Add(keep);
                    }
                    else merged.Add(s);
                }

                bool sameShape = merged.Count == _processes.Count;
                if (sameShape)
                    for (int i = 0; i < merged.Count; i++)
                        if (!ReferenceEquals(merged[i], _processes[i])) { sameShape = false; break; }
                if (sameShape) return; // só os valores mudaram — já notificaram sozinhos

                Processes = merged;
            }

            /// <summary>Heatmap da célula de CPU (mesmas faixas da aba Processos).</summary>
            public SolidColorBrush CpuCellBackground =>
                CpuValue >= 60 ? FreezeCellBrush(new SolidColorBrush(Color.FromArgb(40, 0xE8, 0x11, 0x23)))
                : CpuValue >= 25 ? FreezeCellBrush(new SolidColorBrush(Color.FromArgb(30, 0xFF, 0x98, 0x00)))
                : CpuValue >= 5 ? FreezeCellBrush(new SolidColorBrush(Color.FromArgb(20, 0xFF, 0xD7, 0x00)))
                : Brushes.Transparent;
        }

        private static SolidColorBrush FreezeCellBrush(SolidColorBrush b) { if (!b.IsFrozen) b.Freeze(); return b; }

        private int _usersLoadRunning;
        private bool _didInitialSelect; // auto-seleciona a 1ª linha 1x (painel de detalhes não abre vazio)
        private readonly KitLugia.Core.TaskManager.NativeMetricsHelper.CpuDeltaTracker _usersCpuTracker = new();

        // Ordenação da aba Usuários (antes o clique no cabeçalho ia para o sort default
        // do DataGrid, que o refresh de 1 s desfazia — parecia "não funciona").
        private string _usersSortColumn = "CpuValue";
        private ListSortDirection _usersSortDirection = ListSortDirection.Descending;

        private void BtnUserExpand_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if ((sender as FrameworkElement)?.DataContext is not UserRow r) return;
                r.IsExpanded = !r.IsExpanded;
                // A lista de processos só é preenchida para quem está ABERTO — sem este
                // empurrão o detalhe abria VAZIO por até 1 s (seta ▼ sem nada listado).
                if (r.IsExpanded && r.Processes.Count == 0) _ = LoadUsersSafeAsync();
            }
            catch { }
        }

        private void DgUsers_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string prop = e.Column?.SortMemberPath ?? "";
            if (string.IsNullOrEmpty(prop)) return;
            if (_usersSortColumn == prop)
                _usersSortDirection = _usersSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _usersSortColumn = prop;
                _usersSortDirection = prop == "UserName" ? ListSortDirection.Ascending : ListSortDirection.Descending;
            }
            ApplyUsersSort();
        }

        /// <summary>Ordena a coleção VIVA da aba Usuários pelo critério escolhido (fica aplicado no refresh).</summary>
        private void ApplyUsersSort()
        {
            try
            {
                if (DgUsers.ItemsSource is not ObservableCollection<UserRow> live || live.Count == 0) return;
                bool asc = _usersSortDirection == ListSortDirection.Ascending;
                Func<UserRow, IComparable> key = _usersSortColumn switch
                {
                    "UserName" => r => r.UserName,
                    "ProcessCount" => r => r.ProcessCount,
                    "MemValue" => r => r.MemValue,
                    _ => r => r.CpuValue,
                };
                var sorted = (asc ? live.OrderBy(key) : live.OrderByDescending(key)).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    int cur = live.IndexOf(sorted[i]);
                    if (cur >= 0 && cur != i) live.Move(cur, i);
                }
                foreach (var c in DgUsers.Columns) c.SortDirection = null;
                var active = DgUsers.Columns.FirstOrDefault(c => c.SortMemberPath == _usersSortColumn);
                if (active != null) active.SortDirection = _usersSortDirection;
            }
            catch { }
        }

        /// <summary>
        /// Monta a aba Usuários: para cada usuário soma CPU% (delta real por processo via
        /// CpuDeltaTracker — antes a coluna CPU ficava sempre 0%), memória e processos, e
        /// guarda a lista dos processos filhos para o detalhe expansível.
        /// </summary>
        private async Task LoadUsersSafeAsync()
        {
            if (Interlocked.CompareExchange(ref _usersLoadRunning, 1, 0) != 0) return;
            try
            {
                var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (DgUsers.ItemsSource is IEnumerable<UserRow> cur)
                    foreach (var r in cur) if (r.IsExpanded) expanded.Add(r.UserName);

                var rows = await Task.Run(() =>
                {
                    var users = KitLugia.Core.TaskManager.NativeMetricsHelper.GetUserNames();
                    var procs = KitLugia.Core.TaskManager.NativeMetricsHelper.EnumerateProcesses()
                                ?? new List<KitLugia.Core.TaskManager.NativeMetricsHelper.ProcMetrics>();
                    long now100 = DateTime.UtcNow.Ticks;
                    _usersCpuTracker.SetCoreCount(Environment.ProcessorCount);

                    var agg = new Dictionary<string, UserRow>(StringComparer.OrdinalIgnoreCase);
                    var alive = new List<int>(procs.Count);
                    foreach (var p in procs)
                    {
                        alive.Add(p.Pid);
                        string acct = users.TryGetValue(p.Pid, out var a) ? a : "";
                        string shortName = KitLugia.Core.TaskManager.NativeMetricsHelper.ShortenUserName(acct);
                        if (string.IsNullOrEmpty(shortName)) shortName = "SYSTEM"; // sem SID = pseudo-processo do kernel (System, Registry, Memory Compression...) — sempre SYSTEM

                        if (!agg.TryGetValue(shortName, out var row))
                            agg[shortName] = row = new UserRow { UserName = shortName };

                        double cpu = _usersCpuTracker.Compute(p.Pid, p.KernelTime100ns, p.UserTime100ns, now100);
                        double memMb = p.WorkingSetBytes / 1048576.0;

                        row.ProcessCount++;
                        row.MemValue += memMb;
                        row.CpuValue += cpu;
                        row.Processes.Add(new UserProcRow
                        {
                            Name = p.Name,
                            Pid = p.Pid,
                            CpuValue = cpu,
                            Cpu = cpu > 0.05 ? $"{cpu:F1}%" : "0%",
                            MemValue = memMb,
                            MemMB = memMb >= 1024 ? $"{memMb / 1024.0:F1} GB" : $"{memMb:F0} MB",
                        });
                    }
                    _usersCpuTracker.RemoveDead(alive.ToArray());

                    foreach (var row in agg.Values)
                    {
                        row.CpuValue = Math.Min(100, row.CpuValue);
                        row.Cpu = row.CpuValue > 0.05 ? $"{row.CpuValue:F1}%" : "0%";
                        row.MemMB = row.MemValue >= 1024 ? $"{row.MemValue / 1024.0:F1} GB" : $"{row.MemValue:F0} MB";
                        row.Status = row.Processes.Count > 0 ? "ativa" : "inativa";
                        // Processos mais "pesados" primeiro, como o TMOG. Ordena pelos
                        // VALORES numéricos — antes usava x.MemMB.Length, ou seja, o
                        // COMPRIMENTO DO TEXTO ("9 MB" e "1,2 GB" como strings!).
                        row.Processes = row.Processes.OrderByDescending(x => x.CpuValue)
                                                     .ThenByDescending(x => x.MemValue)
                                                     .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                    }
                    return agg.Values.OrderByDescending(r => r.CpuValue).ThenByDescending(r => r.MemValue).ToList();
                });

                // Preserva o que o usuário tinha aberto (o refresh é a cada 1 s)
                foreach (var r in rows) if (expanded.Contains(r.UserName)) r.IsExpanded = true;

                if (_isClosed) return;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_isClosed) return;
                    int totalProcs = rows.Sum(r => r.ProcessCount);
                    TxtUserCount.Text = $"- {rows.Count} usuários · {totalProcs} processos";

                    // MERGE por usuario em vez de trocar o ItemsSource: reatribuir a lista a cada
                    // segundo RECRIAVA as linhas e derrubava selecao/expansao — a causa de
                    // "cliquei na seta e o processo sumiu". Agora as instancias sao reaproveitadas.
                    if (DgUsers.ItemsSource is not ObservableCollection<UserRow> live)
                    {
                        live = new ObservableCollection<UserRow>();
                        DgUsers.ItemsSource = live;
                    }

                    var porNome = new Dictionary<string, UserRow>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in live) porNome[r.UserName] = r;

                    var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var src = rows[i];
                        vistos.Add(src.UserName);

                        if (porNome.TryGetValue(src.UserName, out var dst))
                        {
                            dst.Status = src.Status;
                            dst.Cpu = src.Cpu;
                            dst.CpuValue = src.CpuValue;
                            dst.MemMB = src.MemMB;
                            dst.MemValue = src.MemValue;
                            dst.ProcessCount = src.ProcessCount;
                            // Lista de processos: merge por PID de quem esta ABERTO (mantem a
                            // lista pronta para quem esta fechado — abrir nunca mostra vazio).
                            if (expanded.Contains(src.UserName)) dst.MergeProcesses(src.Processes);
                        }
                        else
                        {
                            live.Insert(Math.Min(i, live.Count), src);
                            porNome[src.UserName] = src;
                        }
                    }
                    for (int i = live.Count - 1; i >= 0; i--)
                        if (!vistos.Contains(live[i].UserName)) live.RemoveAt(i);

                    // Mantem a ordenacao escolhida pelo usuario (a colecao e reordenada
                    // por CPU ao ser montada; sem isto o refresh desfazia o clique).
                    ApplyUsersSort();
                });
            }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] Users: {ex.Message}"); } catch { } }
            finally { Interlocked.Exchange(ref _usersLoadRunning, 0); }
        }

        private void DisposeCounters()
        {
            try { _cpuCounter?.Dispose(); } catch { }
            try { _memAvailable?.Dispose(); } catch { }
            try { _diskReadCounter?.Dispose(); } catch { }
            try { _diskWriteCounter?.Dispose(); } catch { }
        }

        private static long GetTotalPhysicalMemory()
        {
            try { GetPhysicallyInstalledSystemMemory(out long kb); return kb * 1024; }
            catch { return 0; }
        }

        // ══════════════════════════════════════════════
        //  DRAG / SEARCH BAR
        // ══════════════════════════════════════════════
        private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { }
        }

        private void SearchBar_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void SearchBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            TxtSearch.Focus();
            e.Handled = true;
        }

        private void WindowControls_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // ══════════════════════════════════════════════
        //  WINDOW CONTROLS
        // ══════════════════════════════════════════════
        private void BtnToggleMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // Remove WS_MINIMIZEBOX do system menu (Alt+Space / Win+Down)
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, -16);
                style &= ~0x00020000; // WS_MINIMIZEBOX
                SetWindowLong(hwnd, -16, style);
            }
            catch { }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // ══════════════════════════════════════════════
        //  TAB SWITCHING
        // ══════════════════════════════════════════════
        private void SwitchTab(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            SwitchTabByTag(tag);
        }

        /// <summary>
        /// Troca de aba pela tag. Público porque a janela pode abrir JÁ numa aba
        /// (o atalho da Central de Diagnóstico no painel inicial usa isso) — antes o
        /// clique e a abertura programada eram caminhos separados.
        /// </summary>
        public void SwitchTabByTag(string tag)
        {
            // Hide all tabs
            TabSummary.Visibility = Visibility.Collapsed;
            TabProcesses.Visibility = Visibility.Collapsed;
            TabPerformance.Visibility = Visibility.Collapsed;
            TabServices.Visibility = Visibility.Collapsed;
            TabStartup.Visibility = Visibility.Collapsed;
            TabUsers.Visibility = Visibility.Collapsed;
            TabConnections.Visibility = Visibility.Collapsed;
            TabLatency.Visibility = Visibility.Collapsed;
            TabStorage.Visibility = Visibility.Collapsed;
            TabDiagnostic.Visibility = Visibility.Collapsed;

            // Reset all sidebar buttons to inactive
            ResetSidebarButton(BtnTabSummary);
            ResetSidebarButton(BtnTabProcesses);
            ResetSidebarButton(BtnTabPerformance);
            ResetSidebarButton(BtnTabServices);
            ResetSidebarButton(BtnTabStartup);
            ResetSidebarButton(BtnTabUsers);
            ResetSidebarButton(BtnTabConnections);
            ResetSidebarButton(BtnTabLatency);
            ResetSidebarButton(BtnTabStorage);
            ResetSidebarButton(BtnTabDiagnostic);

            // Activate selected tab + sidebar button
            switch (tag)
            {
                case "Summary":
                    TabSummary.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabSummary);
                    EnsureSummaryBuilt();
                    break;
                case "Connections":
                    TabConnections.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabConnections);
                    _ = RefreshConnectionsAsync();
                    break;
                case "Processes":
                    TabProcesses.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabProcesses);
                    break;
                case "Performance":
                    TabPerformance.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabPerformance);
                    _ = Task.Run(async () => { try { await BuildPerfDevicesAsync(); } catch { } });
                    break;
                case "Users":
                    TabUsers.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabUsers);
                    _ = LoadUsersSafeAsync();
                    break;
                case "Services":
                    TabServices.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabServices);
                    if (_allServices.Count == 0) _ = LoadServicesWhenNeededSafeAsync();
                    break;
                case "Startup":
                    TabStartup.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabStartup);
                    if (_allStartupApps.Count == 0) _ = LoadStartupWhenNeededSafeAsync();
                    break;
                case "Latency":
                    TabLatency.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabLatency);
                    EnsureLatencyBuilt();
                    break;
                case "Storage":
                    TabStorage.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabStorage);
                    EnsureStorageBuilt();
                    break;
                case "Diagnostic":
                    TabDiagnostic.Visibility = Visibility.Visible;
                    ActivateSidebarButton(BtnTabDiagnostic);
                    EnsureDiagnosticBuilt();
                    break;
            }

            // A aba Processos pode ter esmaecimentos congelados (o motor se desliga fora dela):
            // ao voltar, ele é religado se ainda houver algo a esmaecer.
            ResumeFrameAnimationsIfNeeded();
        }

        private static readonly SolidColorBrush _accentBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7))); // ciano — identidade própria do TM
        private static readonly SolidColorBrush _grayBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly SolidColorBrush _bgActive = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0x28, 0x28)));
        private static readonly SolidColorBrush _bgInactive = Freeze(new SolidColorBrush(Colors.Transparent));

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            if (!b.IsFrozen) b.Freeze();
            return b;
        }

        private static void ResetSidebarButton(Button btn)
        {
            if (btn.Template == null) return;
            // Background
            if (btn.Template.FindName("bdr", btn) is System.Windows.Controls.Border bdr)
                bdr.Background = _bgInactive;
            // Indicator
            if (btn.Template.FindName("Indicator", btn) is System.Windows.Controls.Border ind)
                ind.Visibility = Visibility.Collapsed;
            // Icon
            if (btn.Template.FindName("Icon", btn) is TextBlock icon)
                icon.Foreground = _grayBrush;
            // Label
            if (btn.Template.FindName("Label", btn) is TextBlock label)
                label.Foreground = _grayBrush;
        }

        private static void ActivateSidebarButton(Button btn)
        {
            if (btn.Template == null) return;
            // Background
            if (btn.Template.FindName("bdr", btn) is System.Windows.Controls.Border bdr)
                bdr.Background = _bgActive;
            // Indicator
            if (btn.Template.FindName("Indicator", btn) is System.Windows.Controls.Border ind)
                ind.Visibility = Visibility.Visible;
            // Icon
            if (btn.Template.FindName("Icon", btn) is TextBlock icon)
                icon.Foreground = _accentBrush;
            // Label
            if (btn.Template.FindName("Label", btn) is TextBlock label)
                label.Foreground = _accentBrush;
        }

        // ══════════════════════════════════════════════
        //  SEARCH
        // ══════════════════════════════════════════════
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounce?.Stop();
            _searchDebounce?.Start();
        }

        private void SearchDebounce_Tick(object? sender, EventArgs e)
        {
            try
            {
                _searchDebounce?.Stop();
                if (_isClosed) return;
                var query = TxtSearch.Text?.Trim() ?? "";
                if (query == _lastSearchQuery) return;
                _lastSearchQuery = query;
                ApplyFilter(query);
                // Barra de busca GLOBAL: aplica o mesmo filtro nas outras abas
                ApplyServiceFilter(GetServiceFilter());
                ApplyStartupFilter();
            }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] SearchDebounce: {ex.Message}"); } catch { } }
        }

        private string GetServiceFilter() =>
            (CmbServiceFilter?.SelectedItem as ComboBoxItem)?.Content as string ?? "Todos";

        // ══════════════════════════════════════════════
        //  REFRESH INTERVAL
        // ══════════════════════════════════════════════
        private void CmbRefreshInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbRefreshInterval.SelectedItem is ComboBoxItem item && item.Content is string text)
            {
                if (int.TryParse(text.Replace("s", ""), out int sec))
                {
                    _refreshSeconds = sec;
                    if (_refreshTimer != null)
                        _refreshTimer.Interval = TimeSpan.FromSeconds(sec);
                    // Gráficos na MESMA cadência da lista: com 5s na lista e 1s no gráfico,
                    // os números das duas áreas andavam dessincronizados (pisca-pisca cruzado).
                    if (_graphTimer != null)
                        _graphTimer.Interval = TimeSpan.FromSeconds(sec);
                    // Mantém o combo do Resumo em sincronia
                    if (CmbSummaryInterval != null)
                    {
                        foreach (ComboBoxItem ci in CmbSummaryInterval.Items)
                            if (ci.Content as string == text) { CmbSummaryInterval.SelectedItem = ci; break; }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════
        //  PROCESS REFRESH — zero-freeze pipeline (native batch + single-flight + off-UI)
        // ══════════════════════════════════════════════

        // ══════════════════════════════════════════════
        //  COLETA AUXILIAR EM BACKGROUND (GPU por processo, TCP por PID, usuários)
        // ══════════════════════════════════════════════
        // São os coletores MAIS LENTOS do pipeline: na 1ª chamada o perflib está frio
        // (PerformanceCounter/PDH) e a leitura do \GPU Engine(*) pode levar segundos em
        // máquina fraca; WTS + tradução de SID somam mais algumas centenas de ms.
        //
        // Eles rodam DESACOPLADOS do desenho: cada coleta publica um OBJETO NOVO
        // (nunca altera o dicionário que a UI está lendo), e o refresh só usa o ÚLTIMO
        // valor publicado. Se a coleta ainda não terminou, a tela mostra o valor anterior
        // — em vez de esperar parada por um coletor que não é essencial para a lista.
        private volatile Dictionary<uint, int> _auxNetConnections = new();
        private volatile Dictionary<uint, double> _auxGpuPerPid = new();
        private volatile Dictionary<int, string> _auxUserNames = new();
        private float _auxGpuTotal = -1f;
        private int _auxCollecting;      // 1 = uma coleta auxiliar em andamento (sem sobreposição)
        private int _firstPaintWaited;   // 1 = a janela curta da 1ª pintura já foi consumida

        /// <summary>
        /// Dispara a coleta auxiliar se não houver nenhuma em andamento. Devolve a Task
        /// (para o primeiro refresh dar uma janela curta a ela) ou null quando uma coleta
        /// anterior ainda está rodando — nesse caso o valor anterior segue valendo.
        /// </summary>
        private Task? KickAuxCollectors()
        {
            if (Interlocked.CompareExchange(ref _auxCollecting, 1, 0) != 0) return null;
            return Task.Run(() =>
            {
                try
                {
                    try { _auxNetConnections = NetworkTrafficMonitor.GetActiveTcpConnectionsPerPid(); } catch { }
                    try { _auxGpuTotal = (float)GpuMonitor.GetTotalGpuUtilization(); } catch { }
                    try { _auxGpuPerPid = GpuMonitor.GetGpuUtilizationPerPid(); } catch { }
                    try { _auxUserNames = KitLugia.Core.TaskManager.NativeMetricsHelper.GetUserNames(); } catch { }
                }
                finally { Interlocked.Exchange(ref _auxCollecting, 0); }
            });
        }

        private async Task RefreshAsync()
        {
            // Single-flight gate: evita sobreposição de refreshes quando UI dispara rápido
            bool entered = false;
            try { entered = await _refreshGate.WaitAsync(0); } catch (ObjectDisposedException) { return; } catch { return; }
            if (!entered) return;
            var sw = Stopwatch.StartNew();
            var now = DateTime.UtcNow;
            var deltaMs = (now - _prevTime).TotalMilliseconds;
            _prevTime = now;
            int cores = Environment.ProcessorCount;
            var cts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _refreshCts, cts);
            try { oldCts?.Cancel(); } catch { }
            try { oldCts?.Dispose(); } catch { }
            var token = cts.Token;
            try
            {
                // Auxiliares (GPU/TCP/usuários) saem em paralelo, mas FORA do caminho
                // crítico: o desenho não espera por eles (ver KickAuxCollectors).
                var auxTask = KickAuxCollectors();

                var snapTask = Task.Run<object?>(() =>
                {
                    try
                    {
                        if (_useNativeProcPath)
                        {
                            var snap = KitLugia.Core.TaskManager.NativeMetricsHelper.EnumerateProcesses();
                            if (snap == null || snap.Count == 0)
                                _useNativeProcPath = false;
                            else
                                return snap;
                        }
                    }
                    catch { _useNativeProcPath = false; }
                    try { return (object?)Process.GetProcesses(); }
                    catch { return (object?)Array.Empty<Process>(); }
                }, token);

                // ── ESSENCIAL: o snapshot nativo dos processos é o que enche a lista ──
                // Só ele é aguardado; tudo o mais que atrasar não segura a primeira pintura.
                var snapProcesses = await snapTask;
                if (token.IsCancellationRequested) return;
                var nativeSnap = snapProcesses as List<KitLugia.Core.TaskManager.NativeMetricsHelper.ProcMetrics>;

                // Primeira pintura: dá uma janela CURTA aos auxiliares para que a 1ª tela já
                // saia com GPU/Rede preenchidos em máquina boa. Da 2ª em diante não espera
                // nada: uma coluna desatualizada por no máximo 1 s é melhor que lista atrasada.
                if (auxTask != null && Interlocked.CompareExchange(ref _firstPaintWaited, 1, 0) == 0)
                {
                    try { await Task.WhenAny(auxTask, Task.Delay(300, token)); } catch { }
                    if (token.IsCancellationRequested) return;
                }

                var netConnections = _auxNetConnections;
                float gpuTotal = _auxGpuTotal;
                var gpuPerPid = _auxGpuPerPid;
                var userNamesNat = _auxUserNames;
                _networkConnections = netConnections;
                // Só sobrescreve com valor REAL: o coletor de desempenho pode ter medido a GPU
                // antes desta coleta auxiliar terminar (evita apagar um número bom com -1).
                if (gpuTotal >= 0) _lastGpuPct = gpuTotal;
                var nowUtc = DateTime.UtcNow;

                var swEnum = Stopwatch.StartNew();
                var rows = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var result = new List<ProcessRow>(320);

                    // ─────────────── CAMINHO NATIVO TMOG (NtQuerySystemInformation) ───────────────
                    // Sem OpenProcess por PID (dados vindos do kernel), CPU/memória/IO de todos
                    // os processos sem admin, threads reais e parentPid nativos.
                    if (nativeSnap != null)
                    {
                        var now100ns = nowUtc.ToFileTime();
                        var windowPids = KitLugia.Core.TaskManager.NativeMetricsHelper.GetPidsWithVisibleWindows();
                        var userNames = KitLugia.Core.TaskManager.NativeMetricsHelper.GetUserNames();
                        var pidListNat = new List<int>(nativeSnap.Count);
                        foreach (var nm in nativeSnap) pidListNat.Add(nm.Pid);
                        var pathMapNat = SafeProcessHelper.GetProcessPathsBatch(pidListNat);

                        foreach (var nm in nativeSnap)
                        {
                            if (token.IsCancellationRequested) break;
                            try
                            {
                                int pid = nm.Pid;
                                string pName = string.IsNullOrEmpty(nm.Name) ? "(" + pid + ")" : nm.Name;
                                string path = pathMapNat.TryGetValue(pid, out var cached) ? cached : "";

                                bool hasWindow = windowPids.Contains(pid);

                                string group;
                                if (hasWindow) group = "Aplicativos";
                                else if (IsWindowsProcess(pName, path)) group = "Processos do Windows";
                                else group = "Processos em segundo plano";

                                string badge = "";
                                bool isProtected = false;
                                if (pid < 100) { badge = "SYSTEM"; isProtected = true; }
                                else if (group == "Processos do Windows" && pid > 4) { badge = "WIN"; isProtected = true; }

                                double cpuVal = _cpuTracker.Compute(pid, nm.KernelTime100ns, nm.UserTime100ns, now100ns);
                                string cpu = cpuVal > 0 ? $"{cpuVal:F1}%" : "0%";

                                double ramVal = nm.WorkingSetBytes / 1024.0 / 1024.0;
                                string ramMb = $"{(long)ramVal} MB";

                                var io = ProcessIoHelper.SampleProcessIoFromTotals(
                                    pid, nm.IoReadBytesTotal, nm.IoWriteBytesTotal,
                                    nm.IoReadOpsTotal, nm.IoWriteOpsTotal, nowUtc);
                                double ioBytes = io.ReadBytesPerSec + io.WriteBytesPerSec;
                                string disk = FormatBytesSpeed(ioBytes);

                                int netConns = netConnections.TryGetValue((uint)pid, out int c) ? c : 0;
                                double netBytesPerSec = netConns > 0 ? ioBytes : 0;
                                string net = FormatBytesSpeed(netBytesPerSec);

                                var iconNow = GetCachedIcon(path, pName);

                                // TMOG: GPU% do processo — engines do PDH trazem pid_NNNN no nome.
                                // Com engine (mesmo ocioso) = valor ("0,3%"/"0,0%"); sem engine = "—".
                                double gpuPct = gpuPerPid.TryGetValue((uint)pid, out var gp) ? gp : -1;
                                string gpuCell = gpuPct >= 0
                                    ? (gpuPct >= 10 ? $"{gpuPct:F0}%" : $"{gpuPct:F1}%")
                                    : (gpuTotal >= 0 ? "—" : "N/A");
                                if (gpuPct < 0) gpuPct = 0;

                                // Paridade TMOG: User name / Peak memory / CPU time / Page faults
                                string user = userNames.TryGetValue(pid, out var un) ? KitLugia.Core.TaskManager.NativeMetricsHelper.ShortenUserName(un) : "";
                                double peakMb = nm.PeakWorkingSetBytes / 1048576.0;
                                string peakCell = peakMb > 0.5 ? FormatBytesSpeed(nm.PeakWorkingSetBytes).Replace("/s", "").Trim() : "—";
                                TimeSpan cpuSpan = TimeSpan.FromTicks((nm.KernelTime100ns + nm.UserTime100ns) * 10);
                                string cpuTimeCell = cpuSpan.TotalSeconds >= 1
                                    ? $"{(int)cpuSpan.TotalHours:00}:{cpuSpan.Minutes:00}:{cpuSpan.Seconds:00}"
                                    : "—";
                                string pfCell = nm.PageFaults > 0 ? nm.PageFaults.ToString("N0") : "—";

                                // IconPath marca "ícone resolvido": loader incremental pula linhas
                                // não-vazias — setar sempre path deixava linhas nativas SEM ícone.
                                string iconPath = iconNow != null ? path : "";

                                result.Add(new ProcessRow
                                {
                                    Name = pName,
                                    DisplayName = pName,
                                    Pid = pid,
                                    Cpu = cpu,
                                    CpuValue = cpuVal,
                                    RamMB = ramMb,
                                    RamValue = ramVal,
                                    Handles = nm.HandleCount.ToString(),
                                    Threads = nm.ThreadCount.ToString(), // REAL (NtQSI) em vez de "—"
                                    Group = group,
                                    Status = hasWindow ? "Executando" : (group == "Processos do Windows" ? "Serviço" : "Segundo plano"),
                                    Disk = disk,
                                    DiskBytesPerSec = ioBytes,
                                    DiskReadBytesPerSec = io.ReadBytesPerSec,
                                    DiskWriteBytesPerSec = io.WriteBytesPerSec,
                                    DiskOpsPerSec = io.ReadOpsPerSec + io.WriteOpsPerSec,
                                    Network = net,
                                    NetworkConnections = netConns,
                                    NetBytesPerSec = netBytesPerSec,
                                    Gpu = gpuCell,
                                    GpuValue = gpuPct,
                                    UserName = user,
                                    PeakMemMB = peakCell,
                                    PeakMemValue = peakMb,
                                    CpuTime = cpuTimeCell,
                                    CpuTimeSec = cpuSpan.TotalSeconds,
                                    PageFaults = pfCell,
                                    PageFaultsValue = nm.PageFaults,
                                    Path = path,
                                    ParentPid = nm.ParentPid,
                                    // Detalhes avançados (TMOG): commit privado, tempos separados, E/S acumulada
                                    CommitMB = nm.PrivateBytes / 1048576.0,
                                    KernelTimeSec = nm.KernelTime100ns / 1e7,
                                    UserTimeSec = nm.UserTime100ns / 1e7,
                                    IoReadTotal = nm.IoReadBytesTotal,
                                    IoWriteTotal = nm.IoWriteBytesTotal,
                                    IoOpsTotal = nm.IoReadOpsTotal + nm.IoWriteOpsTotal,
                                    ProtectedBadge = badge,
                                    IsProtected = isProtected,
                                    ProcessIcon = iconNow,
                                    IconPath = iconPath,
                                });
                            }
                            catch { }
                        }

                        _cpuTracker.RemoveDead(result.Select(r => r.Pid).ToArray());
                        ProcessIoHelper.CleanupStaleSnapshots(new HashSet<int>(result.Select(r => r.Pid)));
                        return result;
                    }

                    // ─────────────── FALLBACK .NET (Process.GetProcesses) — igual ao anterior ───────────────
                    Process[] processes = snapProcesses as Process[] ?? Array.Empty<Process>();
                    if (processes.Length == 0) return result;

                    // Fast path: enumeração nativa (Rust) entrega parentPid + handleCount sem WMI
                    var fastMap = SafeProcessHelper.TryEnumerateFast();
                    Dictionary<int, int>? parentPidDict = null;
                    if (fastMap == null)
                    {
                        // Fallback CIM (WsMan, sucessor WMI) → WMI DCOM via NativeHardware.Cim — nunca trava WinPE
                        try { parentPidDict = NativeHardware.GetParentPidsViaCim(); if (parentPidDict.Count==0) parentPidDict = new Dictionary<int,int>(processes.Length); } catch { parentPidDict = new Dictionary<int,int>(processes.Length); }
                        if (parentPidDict.Count == 0)
                        {
                            try
                            {
                                var cimRows = NativeHardware.Cim.Query("SELECT ProcessId, ParentProcessId FROM Win32_Process");
                                foreach (var r in cimRows)
                                {
                                    try
                                    {
                                        int pid = Convert.ToInt32(r.TryGetValue("ProcessId", out var a) ? a ?? 0 : 0);
                                        int ppid = Convert.ToInt32(r.TryGetValue("ParentProcessId", out var b) ? b ?? 0 : 0);
                                        parentPidDict[pid] = ppid;
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }

                    // Batch de paths: 1 buffer reutilizado para todos os PIDs (evita 300 allocs)
                    var pidList = processes.Select(pr => pr.Id).ToList();
                    var pathMap = SafeProcessHelper.GetProcessPathsBatch(pidList);

                    foreach (var p in processes)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            bool exited = false;
                            try { exited = p.HasExited; } catch { exited = true; }
                            if (exited) { try { p.Dispose(); } catch { } continue; }

                            string pName;
                            try { pName = p.ProcessName; } catch { try { p.Dispose(); } catch { } continue; }
                            int pid = p.Id;

                            string path = pathMap.TryGetValue(pid, out var cached) ? cached : "";
                            bool hasWindow = false;
                            try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch { }
                            // Threads.Count é caro (NtQuerySystemInformation por processo ~0,8ms × 250 = 200ms) — lista mostra "—", detalhe carrega sob demanda
                            string threadsStr = "—";
                            int handles = 0;
                            if (fastMap != null && fastMap.TryGetValue(pid, out var fm))
                                handles = (int)fm.HandleCount;
                            else
                                try { handles = p.HandleCount; } catch { }

                            string group;
                            if (hasWindow) group = "Aplicativos";
                            else if (IsWindowsProcess(pName, path)) group = "Processos do Windows";
                            else group = "Processos em segundo plano";

                            string badge = "";
                            bool isProtected = false;
                            if (pid < 100) { badge = "SYSTEM"; isProtected = true; }
                            else if (group == "Processos do Windows" && pid > 4) { badge = "WIN"; isProtected = true; }

                            double cpuVal = 0;
                            string cpu = "0%";
                            try
                            {
                                var cur = p.TotalProcessorTime;
                                if (_prevCpu.TryGetValue(pid, out var prev) && deltaMs > 50)
                                {
                                    var diff = (cur - prev).TotalMilliseconds;
                                    cpuVal = Math.Clamp(diff / deltaMs / cores * 100.0, 0, 100);
                                    cpu = $"{cpuVal:F1}%";
                                }
                                _prevCpu[pid] = cur;
                            }
                            catch { }

                            long ramBytes = 0;
                            string ramMb = "0 MB";
                            try { ramBytes = p.WorkingSet64; ramMb = $"{ramBytes / 1024 / 1024} MB"; } catch { }
                            double ramVal = ramBytes / 1024.0 / 1024.0;

                            var io = ProcessIoHelper.SampleProcessIo(pid);
                            double ioBytes = io.ReadBytesPerSec + io.WriteBytesPerSec;
                            string disk = FormatBytesSpeed(ioBytes);

                            int netConns = netConnections.TryGetValue((uint)pid, out int c) ? c : 0;
                            double netBytesPerSec = netConns > 0 ? ioBytes : 0;
                            string net = FormatBytesSpeed(netBytesPerSec);

                            double gpuPctFb = gpuPerPid.TryGetValue((uint)pid, out var gpf) ? gpf : -1;
                            string gpu = gpuPctFb >= 0
                                ? (gpuPctFb >= 10 ? $"{gpuPctFb:F0}%" : $"{gpuPctFb:F1}%")
                                : (gpuTotal >= 0 ? "—" : "N/A");
                            if (gpuPctFb < 0) gpuPctFb = 0;
                            // Fallback .NET não tem user por processo barato (WTS seria chamado
                            // por refresh; reusa o mesmo cache do caminho nativo)
                            string userFb = userNamesNat.TryGetValue(pid, out var unf) ? unf : "";

                            int parentPid = 0;
                            if (fastMap != null)
                            {
                                if (fastMap.TryGetValue(pid, out var f)) parentPid = (int)f.ParentPid;
                            }
                            else if (parentPidDict != null && parentPidDict.TryGetValue(pid, out int pp)) parentPid = pp;

                            // Detalhes avançados no caminho .NET: handle completo pode falhar
                            // em processo de sistema — cada campo é opcional (graceful degradation).
                            double commitMbFb = 0, kernelSecFb = 0, userSecFb = 0;
                            try { commitMbFb = p.PrivateMemorySize64 / 1048576.0; } catch { }
                            try { kernelSecFb = p.PrivilegedProcessorTime.TotalSeconds; } catch { }
                            try { userSecFb = p.UserProcessorTime.TotalSeconds; } catch { }

                            // Ícone instantâneo do cache — evita linhas em branco e "piscada"
                            // a cada refresh (linhas são recriadas, mas o cache persiste).
                            var iconNow = GetCachedIcon(path, pName);

                            result.Add(new ProcessRow
                            {
                                Name = pName,
                                DisplayName = pName,
                                Pid = pid,
                                Cpu = cpu,
                                CpuValue = cpuVal,
                                RamMB = ramMb,
                                RamValue = ramVal,
                                Handles = handles.ToString(),
                                Threads = threadsStr,
                                Group = group,
                                Status = hasWindow ? "Executando" : (group == "Processos do Windows" ? "Serviço" : "Segundo plano"),
                                Disk = disk,
                                DiskBytesPerSec = ioBytes,
                                DiskReadBytesPerSec = io.ReadBytesPerSec,
                                DiskWriteBytesPerSec = io.WriteBytesPerSec,
                                DiskOpsPerSec = io.ReadOpsPerSec + io.WriteOpsPerSec,
                                Network = net,
                                NetworkConnections = netConns,
                                NetBytesPerSec = netBytesPerSec,
                                Gpu = gpu,
                                GpuValue = gpuPctFb,
                                UserName = KitLugia.Core.TaskManager.NativeMetricsHelper.ShortenUserName(userFb),
                                Path = path,
                                ParentPid = parentPid,
                                CommitMB = commitMbFb,
                                KernelTimeSec = kernelSecFb,
                                UserTimeSec = userSecFb,
                                ProtectedBadge = badge,
                                IsProtected = isProtected,
                                ProcessIcon = iconNow,
                                IconPath = string.IsNullOrEmpty(iconNow == null ? "" : path) ? "" : path,
                            });
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }

                    var alive = new HashSet<int>(result.Select(r => r.Pid));
                    ProcessIoHelper.CleanupStaleSnapshots(alive);
                    foreach (var k in _prevCpu.Keys.Where(k => !alive.Contains(k)).ToList()) _prevCpu.Remove(k);
                    return result;
                }, token);

                if (token.IsCancellationRequested) return;
                lock (_lock) { _allRows = rows; }

                // Ícones: carrega incremental e atualiza via INotify (sem recriar ItemsSource).
                // Lote maior (96) + prioridade por CPU (linhas visíveis primeiro).
                // Ícones pendentes: com path (extração direta) OU sem path mas resolvíveis
                // via System32 pelo nome (dwm, conhost...) — antes, linhas nativas sem path
                // nunca eram processadas e ficavam com ícone genérico para sempre.
                _ = LoadIconsIncrementalAsync(rows
                    .Where(r => string.IsNullOrEmpty(r.IconPath) &&
                                (!string.IsNullOrEmpty(r.Path) || !string.IsNullOrEmpty(r.Name)))
                    .OrderByDescending(r => r.CpuValue)
                    .Take(96).ToList());

                ApplyFilter(_lastSearchQuery);
                UpdateSummaryTopCpu();
                MaybeRefreshConnections();

                // Primeira lista na tela: uma das duas condições para o véu de carga sair.
                NotifyLoadRowsPainted();

                sw.Stop();
                if (!_isClosed) _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_isClosed) return;
                        TxtStatus.Text = $"{rows.Count} processos em {sw.ElapsedMilliseconds}ms";
                    }
                    catch { }
                }), DispatcherPriority.Background);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { try { Logger.Log($"[KIT TASK MANAGER] RefreshAsync: {ex.Message}"); } catch { } }
            finally { try { _refreshGate.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } catch { } }
        }

        // ══════════════════════════════════════════════
        //  ICON LOADING — incremental, sem re-criar ItemsSource (INotify atualiza ícone na linha)
        // ══════════════════════════════════════════════
        /// <summary>
        /// Lê SOMENTE do cache (thread-safe). Nunca extrai ícone aqui — extração custa ms
        /// e é feita por LoadIconsIncrementalAsync. Resolve também fallback System32 pelo nome.
        /// </summary>
        private BitmapSource? GetCachedIcon(string path, string name)
        {
            try
            {
                lock (_iconLock)
                {
                    if (!string.IsNullOrEmpty(path) && _iconCache.TryGetValue(path, out var byPath))
                        return byPath ?? GenericIcon;
                    if (_iconCache.TryGetValue(name, out var byName))
                        return byName ?? GenericIcon;
                }
                // Fallback System32 conhecido (dwm, conhost, etc.) já em cache?
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                {
                    string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string cand = Path.Combine(sys, name + ".exe");
                    lock (_iconLock)
                    {
                        if (_iconCache.TryGetValue(cand, out var bySys)) return bySys ?? GenericIcon;
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task LoadIconsAsync(List<ProcessRow> rows) => await LoadIconsIncrementalAsync(rows);

        private static string ExtractPackageName(string path)
        {
            try
            {
                var parts = path.Split('\\');
                int idx = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx + 1 < parts.Length) return parts[idx + 1];
            }
            catch { }
            return "";
        }

        private int _iconsLoadRunning; // guard anti-sobreposição (lote anterior ainda rodando → pula)
        private async Task LoadIconsIncrementalAsync(List<ProcessRow> rows)
        {
            if (rows.Count == 0) return;
            if (Interlocked.CompareExchange(ref _iconsLoadRunning, 1, 0) != 0) return;
            try
            {
                await Task.Run(() =>
                {
                    // FIX CRASH: SHGetFileInfo (shell32) NÃO é thread-safe. O Parallel.ForEach
                    // com 4 threads podia causar AccessViolationException em código nativo,
                    // que derruba o processo inteiro sem possibilidade de catch.
                    // Extração agora é serial + limitada por lote para não saturar a UI.
                    // NOTA: com cache em disco (ProgramIconHelper), a 2ª abertura lê PNG (~1ms/ícone).
                    foreach (var row in rows)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(row.Path))
                            {
                                // Tenta resolver via System32 pelo nome (ex: dwm -> System32\dwm.exe)
                                try
                                {
                                    string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                                    string cand = Path.Combine(sys, row.Name + ".exe");
                                    if (File.Exists(cand))
                                    {
                                        var ic2 = ProgramIconHelper.GetIconFromFile(cand);
                                        if (ic2 != null)
                                        {
                                            lock (_iconLock) { _iconCache[row.Name] = ic2; }
                                            Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = ic2; row.IconPath = cand; }), DispatcherPriority.Background);
                                            continue;
                                        }
                                    }
                                }
                                catch { }
                                Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = GenericIcon; row.IconPath = ""; }), DispatcherPriority.Background);
                                continue;
                            }
                            lock (_iconLock)
                            {
                                if (_iconCache.ContainsKey(row.Path))
                                {
                                    var cached = _iconCache[row.Path] ?? GenericIcon;
                                    Dispatcher.BeginInvoke(new Action(() => { row.ProcessIcon = cached; row.IconPath = row.Path; }), DispatcherPriority.Background);
                                    continue;
                                }
                            }
                            BitmapSource? icon = null;
                            // UWP: tenta AppIconHelper via manifest (evita SHGetFileInfo falhar por permissão)
                            if (row.Path.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try
                                {
                                    string pkg = ExtractPackageName(row.Path);
                                    if (!string.IsNullOrEmpty(pkg))
                                        icon = AppIconHelper.GetAppIcon(pkg, 32);
                                }
                                catch { }
                            }
                            if (icon == null)
                                icon = ProgramIconHelper.GetIconFromFile(row.Path);
                            lock (_iconLock)
                            {
                                _iconCache[row.Path] = icon;
                                // Cache também pela chave de nome: agrupamentos e processos sem
                                // caminho resolvem o ícone instantaneamente no próximo refresh.
                                if (icon != null && !string.IsNullOrEmpty(row.Name))
                                    _iconCache[row.Name] = icon;
                            }
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                row.ProcessIcon = icon ?? GenericIcon;
                                row.IconPath = row.Path;
                            }), DispatcherPriority.Background);
                        }
                        catch { }
                    }
                });
            }
            finally { Interlocked.Exchange(ref _iconsLoadRunning, 0); }
            // Sem ApplyFilter — ProcessIcon notifica via INotify, linha virtualizada atualiza sozinha
        }

        // ══════════════════════════════════════════════
        //  PROCESS CLASSIFICATION
        // ══════════════════════════════════════════════
        private static readonly HashSet<string> WindowsProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "audiodg", "conhost", "csrss", "ctfmon", "dwm", "lsass", "services",
            "smss", "svchost", "wininit", "winlogon", "FontdrvHost",
            "sihost", "RuntimeBroker", "ShellExperienceHost", "SearchUI",
            "TextInputHost", "StartMenuExperienceHost", "ApplicationFrameHost",
            "backgroundTaskHost", "dllhost", "taskhostw", "WerFault",
            "WmiPrvSE", "SearchProtocolHost", "SearchFilterHost",
            "spoolsv", "WUDFHost", "msdtc", "TrustedInstaller",
            "TiWorker", "WaaSMedicSvc", "UsoSvc", "WaaSMedicAgent",
            "SearchIndexer", "SearchApp", "SecHealthUI",
            "wsqmcons", "cbdhsvc", "csrss", "win32k",
            "Memory Compression", "Registry", "Security Health Service",
            "System", "Idle", "[System Process]",
        };

        private static bool IsWindowsProcess(string name, string path)
        {
            if (WindowsProcessNames.Contains(name)) return true;
            if (!string.IsNullOrEmpty(path))
            {
                var lower = path.ToLowerInvariant();
                if (lower.Contains(@"\windows\system32\") || lower.Contains(@"\windows\syswow64\") ||
                    lower.Contains(@"\windows\winsxs\") || lower.Contains(@"\program files\windowsapps\"))
                    return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════
        //  PARENT PID (batched — single WMI query for all processes)
        // ══════════════════════════════════════════════
        private static Dictionary<int, int> GetBatchParentPids(List<ProcessRow> rows)
        {
            if (rows.Count == 0) return new Dictionary<int, int>();
            // Nativo Rust → CIM (WsMan sucessor) → WMI fallback via NativeHardware
            var fast = SafeProcessHelper.TryEnumerateFast();
            if (fast != null)
            {
                var d = new Dictionary<int, int>(fast.Count);
                foreach (var kv in fast) d[kv.Key] = (int)kv.Value.ParentPid;
                return d;
            }
            return NativeHardware.GetParentPidsViaCim();
        }

        // ══════════════════════════════════════════════
        //  FILTER + GROUPING
        // ══════════════════════════════════════════════
        // Win11: clique no cabeçalho preserva ordenação
        private void DgProcesses_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var col = e.Column;
            string prop = col.SortMemberPath;
            if (string.IsNullOrEmpty(prop)) return;
            if (_currentSortColumn == prop)
                _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else
            {
                _currentSortColumn = prop;
                // Métricas numéricas começam do maior
                _currentSortDirection = (prop == "DisplayName" || prop == "Status" || prop == "Name") ? ListSortDirection.Ascending : ListSortDirection.Descending;
            }
            foreach (var c in DgProcesses.Columns) c.SortDirection = null;
            col.SortDirection = _currentSortDirection;
            ApplySorting();
            SyncSortUi();
            // Aplica JA. Antes so as setas eram atualizadas e a ordem real mudava no proximo
            // tick (1s depois) — dava a impressao de que ordenar "nao funcionava".
            _ = RefreshAsync();
        }

        private bool _syncingSortUi;

        /// <summary>
        /// Mantem em sincronia o combo de ordenacao, a seta de sentido e o cabecalho.
        /// Sem isso o usuario clica no cabecalho, a lista reordena e o combo continua dizendo
        /// outro criterio — exatamente a confusao de "nao sei como esta ordenado".
        /// </summary>
        private void SyncSortUi()
        {
            if (_syncingSortUi) return;
            try
            {
                _syncingSortUi = true;
                string tag = $"{_currentSortColumn}|{(_currentSortDirection == ListSortDirection.Ascending ? "Ascending" : "Descending")}";
                if (CmbSortProcesses != null)
                {
                    bool found = false;
                    foreach (var o in CmbSortProcesses.Items)
                    {
                        if (o is ComboBoxItem ci && string.Equals(ci.Tag as string, tag, StringComparison.Ordinal))
                        {
                            if (!ReferenceEquals(CmbSortProcesses.SelectedItem, ci)) CmbSortProcesses.SelectedItem = ci;
                            found = true;
                            break;
                        }
                    }
                    // Coluna ordenada por clique no cabecalho que nao tem item no combo:
                    // marca o combo com o criterio real (senao ele continua exibindo outro
                    // — o usuario jura que esta ordenado por algo que nao esta).
                    if (!found)
                    {
                        var custom = CmbSortProcesses.Items.OfType<ComboBoxItem>()
                                                   .FirstOrDefault(i => (i.Tag as string ?? "").StartsWith("__custom__", StringComparison.Ordinal));
                        if (custom == null)
                        {
                            custom = new ComboBoxItem { Tag = "__custom__" };
                            CmbSortProcesses.Items.Add(custom);
                        }
                        custom.Content = $"{SortColumnLabel(_currentSortColumn)} ({(_currentSortDirection == ListSortDirection.Ascending ? "menor primeiro" : "maior primeiro")})";
                        if (!ReferenceEquals(CmbSortProcesses.SelectedItem, custom)) CmbSortProcesses.SelectedItem = custom;
                    }
                }
                if (BtnSortDirection != null)
                    BtnSortDirection.Content = _currentSortDirection == ListSortDirection.Ascending ? "▲" : "▼";
            }
            catch { }
            finally { _syncingSortUi = false; }
        }

        /// <summary>Nome amigavel da coluna de ordenacao (para o combo quando o criterio vem do cabecalho).</summary>
        private static string SortColumnLabel(string prop) => prop switch
        {
            "CpuValue" => "CPU",
            "RamValue" => "Memória",
            "DiskBytesPerSec" => "Disco",
            "NetBytesPerSec" => "Rede",
            "GpuValue" => "GPU",
            "PeakMemValue" => "Pico de memória",
            "CpuTimeSec" => "Tempo de CPU",
            "PageFaultsValue" => "Page faults",
            "DisplayName" or "Name" => "Nome",
            "UserName" => "Usuário",
            "ProcessCount" => "Processos",
            "Status" => "Status",
            "Threads" => "Threads",
            "Pid" => "PID",
            "CommitMB" => "Commit",
            _ => string.IsNullOrEmpty(prop) ? "CPU" : prop,
        };

        /// <summary>Criterio escolhido explicitamente no combo de ordenacao.</summary>
        private void CmbSortProcesses_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSortUi) return;
            try
            {
                if ((sender as System.Windows.Controls.ComboBox)?.SelectedItem is not ComboBoxItem item) return;
                var parts = (item.Tag as string ?? "").Split('|');
                if (parts.Length != 2) return;

                _currentSortColumn = parts[0];
                _currentSortDirection = parts[1] == "Ascending" ? ListSortDirection.Ascending : ListSortDirection.Descending;

                ApplySorting();
                SyncSortUi();
                _ = RefreshAsync();
            }
            catch { }
        }

        /// <summary>Seta clicavel ao lado do combo: inverte o sentido (▼/▲) e reaplica na hora.</summary>
        private void BtnSortDirection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _currentSortDirection = _currentSortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
                ApplySorting();
                SyncSortUi();
                _ = RefreshAsync();
            }
            catch { }
        }

        /// <summary>Métrica numérica pelo nome da coluna (ordenar colunas novas sem duplicar cases).</summary>
        private static double MetricOf(ProcessRow r, string prop) => prop switch
        {
            "CpuValue" => r.CpuValue,
            "RamValue" => r.RamValue,
            "DiskBytesPerSec" => r.DiskBytesPerSec,
            "DiskReadBytesPerSec" => r.DiskReadBytesPerSec,
            "DiskWriteBytesPerSec" => r.DiskWriteBytesPerSec,
            "DiskOpsPerSec" => r.DiskOpsPerSec,
            "NetBytesPerSec" => r.NetBytesPerSec,
            "GpuValue" => r.GpuValue,
            "PeakMemValue" => r.PeakMemValue,
            "CpuTimeSec" => r.CpuTimeSec,
            "PageFaultsValue" => r.PageFaultsValue,
            "Pid" => r.Pid,
            "Threads" => double.TryParse(r.Threads, out double t) ? t : 0,
            "CommitMB" => r.CommitMB,
            _ => 0,
        };

        /// <summary>
        /// Ordena a lista de processos pelo criterio atual. O rank do grupo vem SEMPRE
        /// primeiro (Aplicativos → 2º plano → Windows, paridade TMOG) e o desempate usa a
        /// posicao ANTERIOR (anti-pisca) e o nome.
        ///
        /// Cobre QUALQUER SortMemberPath: numericas via MetricOf, texto por string e as
        /// colunas Status/Usuario/Threads/PID. Antes, quem nao tinha um case caia no
        /// default (ordem por CPU) — clicar nessas colunas "nao fazia nada".
        /// </summary>
        private List<ProcessRow> OrderRows(IEnumerable<ProcessRow> src, Func<string, int> rank, Func<ProcessRow, int> tieBreaker)
        {
            bool asc = _currentSortDirection == ListSortDirection.Ascending;
            string col = string.IsNullOrEmpty(_currentSortColumn) ? "CpuValue" : _currentSortColumn;

            IOrderedEnumerable<ProcessRow> q = src.OrderBy(r => rank(r.Group));
            switch (col)
            {
                case "DisplayName":
                case "Name":
                    q = asc ? q.ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                            : q.ThenByDescending(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "Status":
                    q = asc ? q.ThenBy(r => r.Status, StringComparer.CurrentCultureIgnoreCase)
                            : q.ThenByDescending(r => r.Status, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case "UserName":
                    q = asc ? q.ThenBy(r => r.UserName, StringComparer.CurrentCultureIgnoreCase)
                            : q.ThenByDescending(r => r.UserName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    // Arredonda em 1 casa: a metrica oscila decimos a cada segundo e cada
                    // micro-troca de ordem movia linhas na tela (a "piscada" da lista).
                    q = asc ? q.ThenBy(r => Math.Round(MetricOf(r, col), 1))
                            : q.ThenByDescending(r => Math.Round(MetricOf(r, col), 1));
                    break;
            }
            return q.ThenBy(tieBreaker).ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private void ApplySorting()
        {
            // IMPORTANTE: a ordenação NÃO usa view.SortDescriptions — ela reordenaria a
            // CollectionView agrupada e jogaria as linhas-filho para longe do pai ao
            // expandir (bug "filhos espalhados"). A ordem real é aplicada manualmente
            // em ApplyFilter (Rank do grupo + métrica), preservando pai→filhos juntos.
            // Aqui só atualizamos as setinhas das colunas.
            try
            {
                foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                var activeCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == _currentSortColumn);
                if (activeCol != null) activeCol.SortDirection = _currentSortDirection;
            }
            catch { }
        }

        // ══════════════════════════════════════════════
        //  EXPAND/COLLAPSE — estilo Gerenciador de Tarefas:
        //  os processos-filhos aparecem SEMPRE imediatamente abaixo do pai.
        // ══════════════════════════════════════════════
        private void ToggleExpand(ProcessRow row)
        {
            if (row == null || row.IsChild || row.RawChildren.Count == 0) return;

            bool expand = !row.IsExpanded;
            row.IsExpanded = expand;
            string key = row.GroupKey;
            if (expand) _expandedGroups.Add(key); else _expandedGroups.Remove(key);

            // Sem SortDescriptions na view, a ordem da ObservableCollection é
            // preservada dentro do grupo — inserir logo após o pai funciona.
            int idx = _groupedLive.IndexOf(row);
            if (idx < 0) return;
            if (expand)
            {
                for (int i = 0; i < row.RawChildren.Count; i++)
                {
                    var child = row.RawChildren[i];
                    child.IsChild = true;
                    // Defensivo: se a instância já está na lista (estado legado de um
                    // refresh interrompido), remove antes de inserir — duplicata de
                    // linha é o bug visual nº 1 deste grid. A remoção desloca índices:
                    // recalcula a posição do pai a cada iteração.
                    int existing = _groupedLive.IndexOf(child);
                    if (existing >= 0) _groupedLive.RemoveAt(existing);
                    int pIdx = _groupedLive.IndexOf(row);
                    if (pIdx < 0) return;
                    _groupedLive.Insert(pIdx + 1 + i, child);
                }
                var needIcons = row.RawChildren.Where(c => c.ProcessIcon == null).ToList();
                if (needIcons.Count > 0) _ = LoadIconsIncrementalAsync(needIcons);
            }
            else
            {
                // Só remove enquanto o GroupKey BATE com o do pai. Antes o loop também
                // exigia IsChild — mas filhos renascidos (ReviveGhost de um filho morto
                // que voltou) perdem IsChild=false e o loop parava no meio, deixando
                // "lixo" do grupo visível após o colapso. O GroupKey já isola o grupo:
                // filhos de outros grupos têm outra chave.
                int rem = idx + 1;
                while (rem < _groupedLive.Count && _groupedLive[rem].GroupKey == key)
                    _groupedLive.RemoveAt(rem);
            }
            try { DgProcesses.UpdateLayout(); } catch { }
        }

        private void ExpandIcon_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ProcessRow row)
            {
                e.Handled = true;
                ToggleExpand(row);
            }
        }

        /// <summary>Número em cultura invariante: aceita "1.5" e "1,5" em qualquer idioma do Windows.</summary>
        private static bool ParseDoubleInv(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string norm = s.Trim().Replace(',', '.');
            return double.TryParse(norm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static bool ParseIntInv(string s, out int v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return int.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        private static int ParseIntLoose(string s)
        {
            if (int.TryParse(s?.Trim(), out int v)) return v;
            return 0;
        }

        /// <summary>
        /// Um token "campo:expressão" aplicado sobre as linhas. Operadores &gt;, &lt;, &gt;=,
        /// &lt;=, = ou : (contém/igual). Retorna false quando o campo é desconhecido ou o
        /// número não parseia — o chamador cai no texto livre em vez de zerar a lista.
        /// </summary>
        private static bool MatchAdvancedToken(List<ProcessRow> rows, string field, string expr, out List<ProcessRow> filtered)
        {
            filtered = rows;
            string op = ":";
            string val = expr ?? "";
            foreach (var cand in new[] { ">=", "<=", ">", "<", "=" })
            {
                if (val.StartsWith(cand, StringComparison.Ordinal))
                {
                    op = cand; val = val.Substring(cand.Length);
                    break;
                }
            }
            bool CmpD(double metric, double threshold) => op switch
            {
                ">" => metric > threshold,
                "<" => metric < threshold,
                ">=" => metric >= threshold,
                "<=" => metric <= threshold,
                "=" => Math.Abs(metric - threshold) < 0.0001,
                _ => Math.Abs(metric - threshold) < 0.0001,
            };
            bool CmpI(double metric, double threshold) => op switch
            {
                ">" => metric > threshold,
                "<" => metric < threshold,
                ">=" => metric >= threshold,
                "<=" => metric <= threshold,
                _ => Math.Abs(metric - threshold) < 0.5,
            };
            switch (field)
            {
                case "name":
                case "nome":
                    filtered = rows.Where(r => r.Name.Contains(val, StringComparison.OrdinalIgnoreCase)).ToList();
                    return true;
                case "pid":
                    if (op == ":")
                    {
                        if (ParseIntInv(val, out int pidEq)) { filtered = rows.Where(r => r.Pid == pidEq).ToList(); return true; }
                        filtered = rows.Where(r => r.Pid.ToString().Contains(val)).ToList();
                        return true;
                    }
                    if (!ParseIntInv(val, out int pid)) return false;
                    filtered = rows.Where(r => CmpI(r.Pid, pid)).ToList();
                    return true;
                case "cpu":
                    if (!ParseDoubleInv(val, out double cpu)) return false;
                    filtered = rows.Where(r => CmpD(r.CpuValue, cpu)).ToList();
                    return true;
                case "ram":
                case "mem":
                case "memoria":
                    if (!ParseDoubleInv(val, out double ram)) return false;
                    filtered = rows.Where(r => CmpD(r.RamValue, ram)).ToList();
                    return true;
                case "threads":
                    if (!ParseDoubleInv(val, out double th)) return false;
                    filtered = rows.Where(r => CmpI(ParseIntLoose(r.Threads), th)).ToList();
                    return true;
                case "handles":
                    if (!ParseDoubleInv(val, out double ha)) return false;
                    filtered = rows.Where(r => CmpI(ParseIntLoose(r.Handles), ha)).ToList();
                    return true;
                case "disco":
                case "disk":
                case "d":
                    if (!ParseDoubleInv(val, out double dk)) return false;
                    filtered = rows.Where(r => CmpD(r.DiskBytesPerSec / 1048576.0, dk)).ToList();
                    return true;
                case "rede":
                case "net":
                case "network":
                    if (!ParseDoubleInv(val, out double nt)) return false;
                    filtered = rows.Where(r => CmpD(r.NetBytesPerSec / 1048576.0, nt)).ToList();
                    return true;
                case "gpu":
                    if (!ParseDoubleInv(val, out double gpu)) return false;
                    filtered = rows.Where(r => CmpD(r.GpuValue, gpu)).ToList();
                    return true;
                case "pico":
                case "peak":
                    if (!ParseDoubleInv(val, out double pk)) return false;
                    filtered = rows.Where(r => CmpD(r.PeakMemValue, pk)).ToList();
                    return true;
                case "tempocpu":
                case "cputime":
                    if (!ParseDoubleInv(val, out double ct)) return false;
                    filtered = rows.Where(r => CmpD(r.CpuTimeSec, ct)).ToList();
                    return true;
                case "usuario":
                case "user":
                    filtered = rows.Where(r => (r.UserName ?? "").Contains(val, StringComparison.OrdinalIgnoreCase)).ToList();
                    return true;
                case "status":
                    filtered = rows.Where(r => (r.Status ?? "").Contains(val, StringComparison.OrdinalIgnoreCase)).ToList();
                    return true;
                case "path":
                case "caminho":
                    filtered = rows.Where(r => (r.Path ?? "").Contains(val, StringComparison.OrdinalIgnoreCase)).ToList();
                    return true;
                default:
                    return false;
            }
        }

private void ApplyFilter(string query)
        {
            List<ProcessRow> rows;
            lock (_lock) { rows = _allRows.ToList(); }
            // snapshot para evitar closure sobre lista mutável
            bool isFirstLoad = false;
            try { isFirstLoad = _groupedLive.Count == 0 && string.IsNullOrEmpty(query); } catch { }

            _ = Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(query))
                {
                    // Multi-termos com AND: "cpu:>10 ram:>500 chrome" filtra quem passa em TODOS.
                    // Campos: name/nome, pid, cpu, ram/mem/memoria, threads, handles,
                    // disco/disk/d, rede/net/network, gpu, pico/peak, tempocpu/cputime,
                    // usuario/user, status, path/caminho. Operadores: >, <, >=, <=, = ou :.
                    // Números em cultura invariante ("1.5" e "1,5" valem o mesmo); ram/pico
                    // em MB, disco/rede em MB/s, tempocpu em segundos, cpu/gpu em %.
                    foreach (var token in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var m = Regex.Match(token, @"^(\w+):(.+)$");
                        if (!m.Success)
                        {
                            string t = token;
                            rows = rows.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().Contains(t) || (r.Path?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                        }
                        else if (!MatchAdvancedToken(rows, m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value, out var filtered))
                        {
                            // Campo desconhecido ou número inválido: cai no texto livre do token.
                            string t = token;
                            rows = rows.Where(r => r.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Pid.ToString().Contains(t) || (r.Path?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                        }
                        else rows = filtered;
                        if (rows.Count == 0) break;
                    }
                }

                // Agrupa por Name+Group (mesma lógica Win11) — RawChildren guarda os PIDs individuais para expansão
                var grouped = rows.GroupBy(r => new { r.Name, r.Group }).Select(g =>
                {
                    var first = g.First();
                    var count = g.Count();
                    var totalRam = g.Sum(r => r.RamValue);
                    var totalCpu = g.Sum(r => r.CpuValue);
                    var members = g.ToList();
                    string gkey = $"{first.Name}|{first.Group}";
                    // Ícone do grupo: primeiro membro que já tenha ícone resolvido
                    // (evita grupo sem ícone quando o 1º processo ainda não carregou).
                    var groupIcon = first.ProcessIcon ?? members.Select(m => m.ProcessIcon).FirstOrDefault(i => i != null);
                    // Usuário do grupo = o dominante entre os membros (paridade TMOG: toda linha mostra usuário)
                    var domUser = members.Where(m => !string.IsNullOrEmpty(m.UserName)).GroupBy(m => m.UserName).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
                    var row = new ProcessRow
                    {
                        Name = first.Name,
                        DisplayName = count > 1 ? $"{first.Name} ({count})" : first.Name,
                        Pid = first.Pid,
                        Cpu = count > 1 ? $"{totalCpu:F1}%" : first.Cpu,
                        CpuValue = totalCpu,
                        RamMB = totalRam > 1024 ? $"{totalRam / 1024:F1} GB" : $"{totalRam:F0} MB",
                        RamValue = totalRam,
                        Handles = first.Handles,
                        Threads = first.Threads,
                        Group = first.Group,
                        UserName = domUser,
                        Status = first.Status,
                        Disk = first.Disk,
                        DiskBytesPerSec = first.DiskBytesPerSec,
                        DiskReadBytesPerSec = g.Sum(r => r.DiskReadBytesPerSec),
                        DiskWriteBytesPerSec = g.Sum(r => r.DiskWriteBytesPerSec),
                        DiskOpsPerSec = g.Sum(r => r.DiskOpsPerSec),
                        Network = first.Network,
                        NetworkConnections = first.NetworkConnections,
                        NetBytesPerSec = g.Sum(r => r.NetBytesPerSec),
                        Gpu = first.Gpu,
                        Path = first.Path,
                        ParentPid = first.ParentPid,
                        ProtectedBadge = first.ProtectedBadge,
                        IsProtected = first.IsProtected,
                        ChildCount = count,
                        ProcessIcon = groupIcon,
                        IconPath = first.IconPath,
                        // Só grupo com 2+ membros pode estar "expandido": grupo que encolheu
                        // (ex.: 2 abas do Opera fecharam) não pode herdar o fundo de expandido
                        // sem ter seta nem filhos — visual de linha "presa".
                        IsExpanded = count > 1 && _expandedGroups.Contains(gkey),
                    };
                    if (count > 1)
                    {
                        row.RawChildren = members.Select(m => new ProcessRow
                        {
                            Name = m.Name,
                            DisplayName = $"{m.Name} — PID {m.Pid}",
                            Pid = m.Pid,
                            Cpu = m.Cpu,
                            CpuValue = m.CpuValue,
                            RamMB = m.RamMB,
                            RamValue = m.RamValue,
                            Handles = m.Handles,
                            Threads = m.Threads,
                            Group = m.Group,
                            UserName = m.UserName,
                            GpuValue = m.GpuValue,
                            PeakMemMB = m.PeakMemMB,
                            PeakMemValue = m.PeakMemValue,
                            CpuTime = m.CpuTime,
                            CpuTimeSec = m.CpuTimeSec,
                            PageFaults = m.PageFaults,
                            PageFaultsValue = m.PageFaultsValue,
                            CommitMB = m.CommitMB,
                            KernelTimeSec = m.KernelTimeSec,
                            UserTimeSec = m.UserTimeSec,
                            IoReadTotal = m.IoReadTotal,
                            IoWriteTotal = m.IoWriteTotal,
                            IoOpsTotal = m.IoOpsTotal,
                            Status = m.Status,
                            Disk = m.Disk,
                            DiskBytesPerSec = m.DiskBytesPerSec,
                            DiskReadBytesPerSec = m.DiskReadBytesPerSec,
                            DiskWriteBytesPerSec = m.DiskWriteBytesPerSec,
                            DiskOpsPerSec = m.DiskOpsPerSec,
                            Network = m.Network,
                            NetworkConnections = m.NetworkConnections,
                            NetBytesPerSec = m.NetBytesPerSec,
                            Gpu = m.Gpu,
                            Path = m.Path,
                            ParentPid = m.ParentPid,
                            ChildCount = 1,
                            IsChild = true,
                            ProcessIcon = m.ProcessIcon,
                            IconPath = m.IconPath,
                        }).OrderByDescending(c => c.CpuValue).ThenByDescending(c => c.RamValue).ToList();
                    }
                    return row;
                }).ToList();

                _filteredRows = grouped;
                int total = rows.Count;
                int apps = rows.Count(r => r.Group == "Aplicativos");
                int bg = rows.Count(r => r.Group == "Processos em segundo plano");
                int win = rows.Count(r => r.Group == "Processos do Windows");

                Dispatcher.InvokeAsync(() =>
                {
                    // Multi-seleção: preserva TODAS as linhas selecionadas no refresh (PID + GroupKey)
                    var selectedPids = new HashSet<int>();
                    var selectedKeys = new HashSet<string>();
                    try
                    {
                        foreach (var s in DgProcesses.SelectedItems.OfType<ProcessRow>())
                        {
                            selectedPids.Add(s.Pid);
                            if (!string.IsNullOrEmpty(s.GroupKey)) selectedKeys.Add(s.GroupKey);
                        }
                    }
                    catch { }
                    if (selectedPids.Count == 0 && DgProcesses.SelectedItem is ProcessRow sel0)
                    {
                        selectedPids.Add(sel0.Pid);
                        if (!string.IsNullOrEmpty(sel0.GroupKey)) selectedKeys.Add(sel0.GroupKey);
                    }
                    double savedOffset = 0;
                    ScrollViewer? sv = null;
                    try { sv = FindVisualChild<ScrollViewer>(DgProcesses); if (sv != null) savedOffset = sv.VerticalOffset; } catch { }

                    if (_cvsInitialized && _groupedCvs != null)
                    {
                        // FAST PATH primeira carga: sem diff O(n²), só Add direto (~30ms vs ~250ms)
                        if (isFirstLoad)
                        {
                            int Rank0(string g) => g == "Aplicativos" ? 0 : g == "Processos em segundo plano" ? 1 : 2;
                            grouped = OrderRows(grouped, Rank0, _ => 0);
                            _filteredRows = grouped;
                            _groupedLive.Clear();
                            foreach (var g in grouped) _groupedLive.Add(g);
                            SetText(TxtProcessCount, $"— {total} processos ({apps} apps, {bg} segundo plano, {win} Windows)");
                            foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                            var activeCol0 = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == _currentSortColumn);
                            if (activeCol0 != null) activeCol0.SortDirection = _currentSortDirection;
                        }
                        else
                        {
                        var childs = _groupedLive.Where(r => r.IsChild).ToList();
                        foreach (var ch in childs) _groupedLive.Remove(ch);
                        int Rank(string g) => g == "Aplicativos" ? 0 : g == "Processos em segundo plano" ? 1 : 2;
                        // Anti-pisca: a métrica oscila décimos a cada segundo e cada micro-troca
                        // de ordem movia linhas na tela. Arredonda em 1 casa e desempatas pela
                        // posição ANTERIOR: só troca de lugar quem realmente passou à frente.
                        var prevPos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int pi = 0; pi < _groupedLive.Count; pi++)
                        {
                            var k = _groupedLive[pi].GroupKey;
                            if (!prevPos.ContainsKey(k)) prevPos[k] = pi;
                        }
                        int PrevPos(ProcessRow r) => prevPos.TryGetValue(r.GroupKey, out int p) ? p : int.MaxValue;
                        grouped = OrderRows(grouped, Rank, PrevPos);
                        _filteredRows = grouped;
                        var existingDict = _groupedLive.GroupBy(r => r.GroupKey).ToDictionary(g => g.Key, g => g.First());
                        var freshDict = grouped.GroupBy(r => r.GroupKey).ToDictionary(g => g.Key, g => g.First());
                        // Processo que fechou NÃO some na hora: vira uma linha FANTASMA
                        // vermelha no lugar onde estava, esmaece (~2,5 s) e o motor de
                        // 60 fps a remove depois — igual ao TMOG.
                        for (int i = _groupedLive.Count - 1; i >= 0; i--)
                        {
                            var stale = _groupedLive[i];
                            if (stale.IsGhost) continue;   // fantasma: quem remove é o ReapGhostRows
                            if (!freshDict.ContainsKey(stale.GroupKey))
                            {
                                // Grupo expandido morreu: recolhe ANTES de virar fantasma,
                                // senão as linhas-filhas obsoletas ficam penduradas no
                                // fantasma até o ReapGhostRows apagá-lo (visual quebrado).
                                if (stale.IsExpanded && stale.RawChildren.Count > 0)
                                {
                                    _expandedGroups.Remove(stale.GroupKey);
                                    stale.IsExpanded = false;
                                    int rem = i + 1;
                                    while (rem < _groupedLive.Count && _groupedLive[rem].IsChild && _groupedLive[rem].GroupKey == stale.GroupKey)
                                        _groupedLive.RemoveAt(rem);
                                }
                                MakeGhost(stale);
                            }
                        }
                        foreach (var fresh in grouped)
                        {
                            // Processo NOVO ganha o brilho verde que esmaece.
                            if (existingDict.TryGetValue(fresh.GroupKey, out var ex) && !ex.IsGhost) ex.UpdateFrom(fresh);
                            else if (existingDict.TryGetValue(fresh.GroupKey, out var gh) && gh.IsGhost) ReviveGhost(gh, fresh); // renasceu: fantasma volta a viver (sem duplicar a chave)
                            else { MarkRowNew(fresh); _groupedLive.Add(fresh); }
                        }
                        // dedupe de segurança: mesma chave só pode existir 1x na lista (as linhas
                        // filhas são re-inseridas DEPOIS, com IsChild, e não passam por aqui).
                        // Remove duplicatas legadas de sessões anteriores a esta correção.
                        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int i = _groupedLive.Count - 1; i >= 0; i--)
                        {
                            if (_groupedLive[i].IsChild) continue;
                            if (!seenKeys.Add(_groupedLive[i].GroupKey)) _groupedLive.RemoveAt(i);
                        }
                        // index map O(1) ao invés de scan O(n²) — corta 150*150 buscas por refresh
                        var indexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int j = 0; j < _groupedLive.Count; j++)
                        {
                            // mesmo nome pode coexistir (sufixados por PID) — a 1ª ocorrência manda
                            if (!indexMap.ContainsKey(_groupedLive[j].GroupKey)) indexMap[_groupedLive[j].GroupKey] = j;
                        }
                        for (int i = 0; i < grouped.Count; i++)
                        {
                            var desired = grouped[i];
                            if (!indexMap.TryGetValue(desired.GroupKey, out int cur)) continue;
                            if (cur != i)
                            {
                                _groupedLive.Move(cur, i);
                                // reindex após Move (só faixa afetada)
                                int lo = Math.Min(cur, i), hi = Math.Max(cur, i);
                                for (int k = lo; k <= hi && k < _groupedLive.Count; k++) indexMap[_groupedLive[k].GroupKey] = k;
                            }
                        }
                        var expanded = _groupedLive.Where(r => !r.IsChild && r.IsExpanded && r.RawChildren.Count > 0).ToList();
                        // Re-inserção dos filhos com dedupe O(n) por PID: o UpdateFrom agora
                        // PRESERVA as instâncias das linhas-filhas (merge por PID), então uma
                        // que já está na lista não pode ser re-inserida — duplicaria a linha.
                        var insertedPids = new HashSet<int>();
                        foreach (var parent in expanded.AsEnumerable().Reverse())
                        {
                            int pIdx = _groupedLive.IndexOf(parent);
                            if (pIdx < 0) continue;
                            for (int c = parent.RawChildren.Count - 1; c >= 0; c--)
                            {
                                var child = parent.RawChildren[c];
                                if (!insertedPids.Add(child.Pid)) continue; // já está na lista (merge preservou)
                                child.IsChild = true;
                                _groupedLive.Insert(pIdx + 1, child);
                            }
                        }
                        var needIcons = expanded.SelectMany(pr => pr.RawChildren).Where(c => c.ProcessIcon == null).ToList();
                        if (needIcons.Count > 0) _ = LoadIconsIncrementalAsync(needIcons);
                        SetText(TxtProcessCount, $"— {total} processos ({apps} apps, {bg} segundo plano, {win} Windows)");
                        if (!_didInitialSelect && DgProcesses.SelectedItem == null && _groupedLive.Count > 0)
                        {
                            _didInitialSelect = true;
                            try { DgProcesses.SelectedItem = _groupedLive[0]; } catch { }
                        }
                        if (selectedPids.Count > 0)
                        {
                            // Multi-seleção: restaura TODAS as linhas (por PID ou GroupKey) em vez de só a primeira.
                            // Anti-pisca: Clear()+Add a cada segundo redisparava SelectionChanged e o
                            // painel de detalhes era reescrito (com fetch em background) sem necessidade.
                            // Só toca na seleção quando o conjunto desejado difere do atual.
                            try
                            {
                                var wanted = new List<ProcessRow>();
                                foreach (var row in _groupedLive)
                                {
                                    if (selectedPids.Contains(row.Pid) ||
                                        (!string.IsNullOrEmpty(row.GroupKey) && selectedKeys.Contains(row.GroupKey)))
                                        wanted.Add(row);
                                }
                                bool same = DgProcesses.SelectedItems.Count == wanted.Count;
                                if (same)
                                {
                                    var cur = new HashSet<ProcessRow>(DgProcesses.SelectedItems.OfType<ProcessRow>());
                                    foreach (var w in wanted) { if (!cur.Contains(w)) { same = false; break; } }
                                }
                                if (!same)
                                {
                                    DgProcesses.SelectedItems.Clear();
                                    foreach (var w in wanted) DgProcesses.SelectedItems.Add(w);
                                }
                            }
                            catch { }
                            if (DgProcesses.SelectedItems.Count == 0)
                            {
                                DgProcesses.SelectedItem = null; DetailName.Text = "Nenhum processo selecionado"; DetailPid.Text = ""; DetailPidValue.Text = "—"; DetailStatus.Text = "—"; DetailUser.Text = "—"; DetailStartTime.Text = "—"; DetailUptime.Text = "—"; DetailHandles.Text = "—"; DetailThreads.Text = "—"; DetailDisk.Text = "—"; DetailNet.Text = "—"; DetailCpu.Text = "—"; DetailRam.Text = "—";
                            }
                        }
                        foreach (var col in DgProcesses.Columns) col.SortDirection = null;
                        var activeCol = DgProcesses.Columns.FirstOrDefault(c => c.SortMemberPath == _currentSortColumn);
                        if (activeCol != null) activeCol.SortDirection = _currentSortDirection;
                        if (sv != null) Dispatcher.BeginInvoke(new Action(() => { try { sv.ScrollToVerticalOffset(savedOffset); } catch { } }), DispatcherPriority.Loaded);
                        }
                    }
                    else
                    {
                        // Fallback (não deveria ocorrer após Loaded)
                        var cvs = new CollectionViewSource { Source = grouped };
                        cvs.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
                        DgProcesses.ItemsSource = cvs.View;
                        TxtProcessCount.Text = $"— {total} processos ({apps} apps, {bg} segundo plano, {win} Windows)";
                    }

                    // Painel de detalhes AO VIVO (CPU/RAM/Handles/Threads/Disco/Rede/Uptime):
                    // sem isto ele só era escrito no SelectionChanged e ficava congelado no
                    // processo selecionado — o "as informações não atualizam" do painel.
                    RefreshLiveDetail();
                }, DispatcherPriority.DataBind);
            });
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        // ══════════════════════════════════════════════
        //  KEYBOARD SHORTCUTS
        // ══════════════════════════════════════════════
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; return; }
            if (e.Key == Key.Delete) { Kill(false); e.Handled = true; return; }
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            { TxtSearch?.Focus(); TxtSearch?.SelectAll(); e.Handled = true; return; }
            // Enter numa linha agrupada expande/recolhe (acessibilidade de teclado)
            if (e.Key == Key.Enter && SelectedRow is ProcessRow pr && !pr.IsChild && pr.RawChildren.Count > 0
                && !ReferenceEquals(Keyboard.FocusedElement, TxtSearch))
            { ToggleExpand(pr); e.Handled = true; return; }
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(TxtSearch?.Text)) { TxtSearch.Text = ""; e.Handled = true; }
                else Close();
                return;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && DgProcesses?.SelectedItem != null) { Kill(false); e.Handled = true; }
        }

        // ══════════════════════════════════════════════
        //  SELECTION + DETAIL PANEL
        // ══════════════════════════════════════════════
        private ProcessRow? SelectedRow => DgProcesses.SelectedItem as ProcessRow;

        /// <summary>Linhas selecionadas (multi-seleção estilo TMOG: ctrl/shift-click).
        /// Exclui linhas-filho para ações em massa não duplicarem (pais já cobrem a árvore).</summary>
        private List<ProcessRow> SelectedRows
        {
            get
            {
                try
                {
                    var rows = DgProcesses.SelectedItems?.OfType<ProcessRow>()
                        .Where(r => !r.IsChild).ToList();
                    if (rows == null || rows.Count == 0)
                    {
                        var one = SelectedRow;
                        return one == null ? new List<ProcessRow>() : new List<ProcessRow> { one };
                    }
                    return rows;
                }
                catch { return new List<ProcessRow>(); }
            }
        }

        /// <summary>Atualiza itens multi do menu de contexto e headers com contagem ("Finalizar 6 tarefas").</summary>
        private void UpdateMultiSelectionUi()
        {
            try
            {
                int n = 0;
                try { n = DgProcesses.SelectedItems?.OfType<ProcessRow>().Count(r => !r.IsChild) ?? 0; } catch { }
                bool multi = n > 1;
                if (MenuKillMulti != null) MenuKillMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (MenuKillTreeMulti != null) MenuKillTreeMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (MenuSuspendMulti != null) MenuSuspendMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (MenuResumeMulti != null) MenuResumeMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (MenuEcoQoSMulti != null) MenuEcoQoSMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (MenuPriorityMulti != null) MenuPriorityMulti.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
                if (multi)
                {
                    MenuKillMulti.Header = $"Finalizar {n} tarefas";
                    MenuKillTreeMulti.Header = $"Finalizar {n} árvores";
                    MenuSuspendMulti.Header = $"Suspender {n}";
                    MenuResumeMulti.Header = $"Retomar {n}";
                    MenuEcoQoSMulti.Header = $"Eficiência EcoQoS ({n})";
                    MenuPriorityMulti.Header = $"Prioridade para {n}";
                }
            }
            catch { }
        }

        private void BtnCloseDetail_Click(object sender, RoutedEventArgs e)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailSplitter.Visibility = Visibility.Collapsed;
            // Set column width to 0 so splitter area collapses
            if (DetailPanel.Parent is Grid parentGrid)
            {
                var col = parentGrid.ColumnDefinitions[2];
                col.Width = new GridLength(0);
                col.MinWidth = 0;
            }
        }

        private CancellationTokenSource? _detailCts;

        private void DgProcesses_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMultiSelectionUi();

            var row = SelectedRow;
            if (row == null) return;

            // Re-show detail panel if it was closed
            if (DetailPanel.Visibility != Visibility.Visible)
            {
                DetailPanel.Visibility = Visibility.Visible;
                DetailSplitter.Visibility = Visibility.Visible;
                if (DetailPanel.Parent is Grid pg)
                {
                    var col = pg.ColumnDefinitions[2];
                    col.Width = new GridLength(340);
                    col.MinWidth = 0;
                    col.MaxWidth = 500;
                }
            }

            // Icon + campos instantâneos (zero bloqueios)
            lock (_iconLock)
            {
                if (_iconCache.TryGetValue(row.Path ?? "", out var icon) && icon != null)
                    DetailIcon.Source = icon;
                else if (GenericIcon != null)
                    DetailIcon.Source = GenericIcon;
            }

            DetailName.Text = row.Name;
            DetailPid.Text = $"PID: {row.Pid}";
            DetailPidValue.Text = row.Pid.ToString();
            DetailStatus.Text = row.Status;
            DetailCpu.Text = row.Cpu;
            DetailCpu.Foreground = GetHeatColor((float)row.CpuValue, 50, 80);
            DetailRam.Text = row.RamMB;
            DetailRam.Foreground = GetHeatColor((float)row.RamValue, 2048, 8192);
            DetailHandles.Text = row.Handles;
            DetailThreads.Text = row.Threads;
            DetailDisk.Text = row.Disk;
            DetailNet.Text = row.Network;
            DetailPath.Text = row.Path ?? "(desconhecido)";

            // Placeholders enquanto carrega off-UI
            DetailUser.Text = "…";
            DetailStartTime.Text = "…";
            DetailUptime.Text = "…";
            // Prioridade — tenta ler rápida do cache, sem WMI
            try { CmbPriority.SelectedIndex = 3; } catch { }

            // Cancela fetch anterior e dispara novo em background (não trava hover)
            try { _detailCts?.Cancel(); } catch { }
            _detailCts = new CancellationTokenSource();
            var pid = row.Pid;
            var ct = _detailCts.Token;

            // Detalhes avançados (TMOG): sessão/arquitetura/linha de comando/IO/tempos.
            // Síncronos da linha (commit, IO acumulada, tempos kernel/user) já vêm do refresh.
            _ = LoadAdvancedDetailsAsync(row);

            _ = Task.Run(() =>
            {
                string user = "—";
                string start = "—";
                string uptime = "—";
                string prioTag = "Normal";
                try
                {
                    // User nativo ultra-rápido (<0.1ms) — sem WMI, sem storm ao navegar com setas
                    try { user = SafeProcessHelper.GetProcessUserFast(pid); } catch { }
                    // StartTime e Priority ainda precisam do handle, mas fora da UI
                    using var proc = Process.GetProcessById(pid);
                    if (proc != null && !proc.HasExited)
                    {
                        try
                        {
                            var st = proc.StartTime;
                            start = st.ToString("dd/MM/yyyy HH:mm");
                            uptime = (DateTime.Now - st).ToString(@"d\.hh\:mm\:ss");
                            // Guardado para o uptime andar de verdade (ver RefreshLiveDetail).
                            _detailStartTime = st; _detailStartPid = pid;
                        }
                        catch { }
                        try
                        {
                            prioTag = proc.PriorityClass switch
                            {
                                ProcessPriorityClass.RealTime => "RealTime",
                                ProcessPriorityClass.High => "High",
                                ProcessPriorityClass.AboveNormal => "AboveNormal",
                                ProcessPriorityClass.Normal => "Normal",
                                ProcessPriorityClass.BelowNormal => "BelowNormal",
                                ProcessPriorityClass.Idle => "Idle",
                                _ => "Normal"
                            };
                        }
                        catch { }
                    }
                }
                catch { }
                if (ct.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SelectedRow?.Pid != pid) return; // seleção mudou
                    DetailUser.Text = KitLugia.Core.TaskManager.NativeMetricsHelper.ShortenUserName(user);
                    DetailStartTime.Text = start;
                    DetailUptime.Text = uptime;
                    foreach (ComboBoxItem item in CmbPriority.Items)
                    {
                        if (item.Tag?.ToString() == prioTag) { CmbPriority.SelectedItem = item; return; }
                    }
                    CmbPriority.SelectedIndex = 3;
                }), DispatcherPriority.Background);
            }, ct);
        }

        // StartTime do processo em exibição — permite o Uptime ANDAR sem refazer o fetch.
        private DateTime? _detailStartTime;
        private int _detailStartPid = -1;

        /// <summary>
        /// Reaplica no painel de detalhes os campos que MUDAM com o tempo, sempre que a
        /// lista é atualizada. Antes o painel só era escrito no SelectionChanged: com um
        /// processo vivo selecionado, CPU/RAM/Handles/Threads/Disco/Rede/Uptime ficavam
        /// CONGELADOS na tela — a queixa "as informações não atualizam".
        /// O que vem do fetch nativo (sessão, arquitetura, linha de comando, Elevated) só
        /// é recarregado ao trocar de seleção: isso praticamente não muda.
        /// </summary>
        private void RefreshLiveDetail()
        {
            try
            {
                if (DetailPanel.Visibility != Visibility.Visible) return;
                if (DgProcesses.SelectedItem is not ProcessRow row) return;

                DetailName.Text = row.Name;
                DetailPid.Text = $"PID: {row.Pid}";
                DetailPidValue.Text = row.Pid.ToString();
                DetailStatus.Text = row.Status;
                DetailCpu.Text = row.Cpu;
                DetailCpu.Foreground = GetHeatColor((float)row.CpuValue, 50, 80);
                DetailRam.Text = row.RamMB;
                DetailRam.Foreground = GetHeatColor((float)row.RamValue, 2048, 8192);
                DetailHandles.Text = row.Handles;
                DetailThreads.Text = row.Threads;
                DetailDisk.Text = row.Disk;
                DetailNet.Text = row.Network;
                if (!string.IsNullOrWhiteSpace(row.UserName)) DetailUser.Text = row.UserName;

                // Métricas que crescem em tempo real (mesmos formatos do load nativo).
                DetailCommit.Text = row.CommitMB > 0 ? FormatTotalBytes((ulong)(row.CommitMB * 1048576.0)) : DetailCommit.Text;
                if (!string.IsNullOrEmpty(row.PeakMemMB)) DetailPeak.Text = row.PeakMemMB;
                if (row.KernelTimeSec > 0) DetailKernelTime.Text = FormatCpuTime(row.KernelTimeSec);
                if (row.UserTimeSec > 0) DetailUserTime.Text = FormatCpuTime(row.UserTimeSec);
                if (row.IoReadTotal > 0) DetailIoRead.Text = FormatTotalBytes(row.IoReadTotal);
                if (row.IoWriteTotal > 0) DetailIoWrite.Text = FormatTotalBytes(row.IoWriteTotal);
                if (row.IoOpsTotal > 0) DetailIoOps.Text = row.IoOpsTotal.ToString("N0");

                if (_detailStartPid == row.Pid && _detailStartTime.HasValue)
                    DetailUptime.Text = (DateTime.Now - _detailStartTime.Value).ToString(@"d\.hh\:mm\:ss");
            }
            catch { }
        }

        private static string GetProcessUser(Process proc)
        {
            try
            {
                if (proc == null || proc.HasExited) return "—";
                // Nativo token (0.1ms, sem WMI) → CIM fallback se nativo falhar
                string fast = SafeProcessHelper.GetProcessUserFast(proc.Id);
                if (!string.IsNullOrEmpty(fast) && fast != "—") return fast;
                var rows = NativeHardware.Cim.Query($"SELECT Owner FROM Win32_Process WHERE ProcessId={proc.Id}");
                if (rows.Count > 0 && rows[0].TryGetValue("Owner", out var o) && o != null) return o.ToString() ?? "—";
            }
            catch { }
            return "—";
        }

        private void DgProcesses_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Estilo Gerenciador de Tarefas: duplo clique num app agrupado expande/recolhe
            // os processos abaixo dele; nos demais abre a pasta do executável.
            if (SelectedRow is ProcessRow row && !row.IsChild && row.RawChildren.Count > 0)
            {
                ToggleExpand(row);
                e.Handled = true;
                return;
            }
            MenuOpenFolder_Click(sender, e);
        }

        // ══════════════════════════════════════════════
        //  KILL ACTIONS (with instant removal)
        // ══════════════════════════════════════════════
        private void BtnKill_Click(object sender, RoutedEventArgs e) => Kill(false);
        private void MenuKill_Click(object sender, RoutedEventArgs e) => Kill(false);
        private void BtnKillTree_Click(object sender, RoutedEventArgs e) => Kill(true);
        private void MenuKillTree_Click(object sender, RoutedEventArgs e) => Kill(true);

        private void Kill(bool tree)
        {
            var targets = SelectedRows;
            if (targets.Count == 0) { TxtStatus.Text = "Selecione um processo primeiro."; return; }

            int ok = 0, fail = 0;
            foreach (var row in targets)
            {
                if (row == null) continue;
                try
                {
                    if (tree)
                    {
                        KillTree(row.Pid);
                        ok++;
                    }
                    else
                    {
                        try
                        {
                            using var p = Process.GetProcessById(row.Pid);
                            if (p == null || p.HasExited) { fail++; continue; }
                            if (!p.CloseMainWindow()) p.Kill(entireProcessTree: true);
                            ok++;
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            // Try Force Stop via our engine
                            try
                            {
                                string target = !string.IsNullOrEmpty(row.Path) && File.Exists(row.Path) ? row.Path : row.Name;
                                ForceStopUnlockService.Unlock(target, new List<BlockingProcessInfo>(), deleteTarget: false);
                                ok++;
                            }
                            catch { fail++; }
                        }
                    }
                }
                catch (InvalidOperationException) { fail++; }
                catch { fail++; }
            }

            TxtStatus.Text = targets.Count == 1
                ? (fail == 0
                    ? (tree ? $"❌ {targets[0].Name} (PID {targets[0].Pid}) + filhos finalizados." : $"❌ {targets[0].Name} (PID {targets[0].Pid}) finalizado.")
                    : $"{targets[0].Name}: acesso negado. Use Force Stop.")
                : $"❌ {ok}/{targets.Count} finalizados" + (fail > 0 ? $" ({fail} com acesso negado/já encerrados)" : "");

            // Instant removal from UI
            lock (_lock)
            {
                var pids = new HashSet<int>(targets.Select(t => t.Pid));
                _allRows.RemoveAll(r => pids.Contains(r.Pid));
            }
            ApplyFilter(_lastSearchQuery);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr a, string? n);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool TerminateJobObject(IntPtr job, uint code);

        private void KillTree(int pid)
        {
            // FIX CRASH: GetChildPids recursivo podia explodir em ciclos de PID (pai↔filho)
            // e o acesso a root.Handle em processo protegido lança Win32Exception/AV.
            try
            {
                var children = GetChildPidsSafe(pid, maxDepth: 6, maxCount: 512);
                foreach (var c in children)
                    try { using var p = Process.GetProcessById(c); p.Kill(entireProcessTree: true); } catch { }

                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    try
                    {
                        using var root = Process.GetProcessById(pid);
                        // Acessa .Handle dentro de try — processo protegido lança aqui
                        AssignProcessToJobObject(job, root.Handle);
                        TerminateJobObject(job, 1);
                    }
                    catch
                    {
                        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
                    }
                    finally { CloseHandle(job); }
                }
                else
                {
                    try { using var root = Process.GetProcessById(pid); root.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
            }

            // Also remove children from UI
            lock (_lock) { _allRows.RemoveAll(r => r.ParentPid == pid || r.Pid == pid); }
        }

        /// <summary>BFS com limites de profundidade e quantidade — imune a ciclos e explosões.</summary>
        private List<int> GetChildPidsSafe(int parentPid, int maxDepth, int maxCount)
        {
            var res = new List<int>();
            var visited = new HashSet<int> { parentPid };
            var frontier = new List<int> { parentPid };
            for (int depth = 0; depth < maxDepth && res.Count < maxCount && frontier.Count > 0; depth++)
            {
                var next = new List<int>();
                foreach (var p in frontier)
                {
                    foreach (var c in GetDirectChildrenPids(p))
                    {
                        if (!visited.Add(c)) continue; // ciclo detectado
                        res.Add(c);
                        if (res.Count >= maxCount) return res;
                        next.Add(c);
                    }
                }
                frontier = next;
            }
            return res;
        }

        private List<int> GetDirectChildrenPids(int parentPid)
        {
            var res = new List<int>();
            try
            {
                // 1) Nativo Rust (NtQuerySystemInformation) — instantâneo
                var fast = SafeProcessHelper.TryEnumerateFast();
                if (fast != null)
                {
                    foreach (var kv in fast) if ((int)kv.Value.ParentPid == parentPid) res.Add(kv.Key);
                    return res;
                }
                // 2) CIM (WsMan sucessor WMI) → WMI fallback via NativeHardware
                var rows = NativeHardware.Cim.Query($"Select ProcessId From Win32_Process Where ParentProcessId={parentPid}");
                foreach (var r in rows)
                    try { res.Add(Convert.ToInt32(r.TryGetValue("ProcessId", out var v) ? v ?? 0 : 0)); } catch { }
            }
            catch { }
            return res;
        }

        private List<int> GetChildPids(int parentPid)
        {
            // Legado recursivo — agora delega ao nativo + CIM, evita WMI DCOM travado
            var res = new List<int>();
            try
            {
                var fast = SafeProcessHelper.TryEnumerateFast();
                if (fast != null)
                {
                    // BFS via nativo já está em GetChildPidsSafe, aqui só retorna filhos diretos recursivo fallback
                    foreach (var kv in fast) if ((int)kv.Value.ParentPid == parentPid) res.Add(kv.Key);
                    var snapshot = res.ToList();
                    foreach (var c in snapshot) res.AddRange(GetChildPids(c));
                    return res;
                }
                var rows = NativeHardware.Cim.Query($"Select ProcessId From Win32_Process Where ParentProcessId={parentPid}");
                foreach (var r in rows) res.Add(Convert.ToInt32(r.TryGetValue("ProcessId", out var v) ? v ?? 0 : 0));
                foreach (var c in res.ToList()) res.AddRange(GetChildPids(c));
            }
            catch { }
            return res;
        }

        // ══════════════════════════════════════════════
        //  FORCE STOP
        // ══════════════════════════════════════════════
        private async void BtnForceStop_Click(object sender, RoutedEventArgs e) => await ForceStopSelectedAsync();
        private async void MenuForceStop_Click(object sender, RoutedEventArgs e) => await ForceStopSelectedAsync();

        private async Task ForceStopSelectedAsync()
        {
            var row = SelectedRow;
            if (row == null) { TxtStatus.Text = "Selecione um processo para Force Stop."; return; }

            TxtStatus.Text = $"Force Stop {row.Name}...";
            await Task.Run(() =>
            {
                try
                {
                    string target = !string.IsNullOrEmpty(row.Path) && (File.Exists(row.Path) || Directory.Exists(row.Path)) ? row.Path : row.Name;
                    var blocking = ForceStopUnlockService.FindBlockingProcesses(target);
                    if (blocking.Count == 0)
                    {
                        try { using var p = Process.GetProcessById(row.Pid); p.Kill(entireProcessTree: true); } catch { }
                    }
                    else ForceStopUnlockService.Unlock(target, blocking, deleteTarget: false);
                }
                catch (Exception ex) { Logger.Log($"[KIT TASK MANAGER] {row.Name}: {ex.Message}"); }
            });

            // Instant removal
            lock (_lock) { _allRows.RemoveAll(r => r.Pid == row.Pid); }
            ApplyFilter(_lastSearchQuery);
            TxtStatus.Text = $"❌ Force Stop {row.Name} concluído.";
        }

        // ══════════════════════════════════════════════
        //  CONTEXT MENU ACTIONS
        // ══════════════════════════════════════════════
        private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null || string.IsNullOrEmpty(r.Path)) return;
            try { Process.Start("explorer.exe", $"/select,\"{r.Path}\""); } catch { }
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null) return;
            try { Clipboard.SetText(r.Path ?? r.Name); TxtStatus.Text = "📋 Caminho copiado."; } catch { }
        }

        private void MenuCopyPid_Click(object sender, RoutedEventArgs e)
        {
            var r = SelectedRow;
            if (r == null) return;
            try { Clipboard.SetText(r.Pid.ToString()); TxtStatus.Text = "📋 PID copiado."; } catch { }
        }

        // ══════════════════════════════════════════════
        //  PROCESS ACTIONS (Detail Panel)
        // ══════════════════════════════════════════════
        private void CmbPriority_Changed(object sender, SelectionChangedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null || CmbPriority.SelectedItem is not ComboBoxItem item) return;

            try
            {
                var priority = item.Tag?.ToString() switch
                {
                    "RealTime" => ProcessPriorityClass.RealTime,
                    "High" => ProcessPriorityClass.High,
                    "AboveNormal" => ProcessPriorityClass.AboveNormal,
                    "Normal" => ProcessPriorityClass.Normal,
                    "BelowNormal" => ProcessPriorityClass.BelowNormal,
                    "Low" => ProcessPriorityClass.Idle,
                    "Idle" => ProcessPriorityClass.Idle,
                    _ => ProcessPriorityClass.Normal
                };
                using var proc = Process.GetProcessById(row.Pid);
                proc.PriorityClass = priority;
                TxtStatus.Text = $"✅ Prioridade de {row.Name} alterada para {priority}";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao alterar prioridade: {ex.Message}"; }
        }

        private void MenuPriority_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi || mi.Tag is not string tag) return;

            // Multi-seleção: aplica a prioridade a TODAS as linhas selecionadas
            var targets = SelectedRows;
            if (targets.Count == 0) return;

            var priority = tag switch
            {
                "RealTime" => ProcessPriorityClass.RealTime,
                "High" => ProcessPriorityClass.High,
                "AboveNormal" => ProcessPriorityClass.AboveNormal,
                "Normal" => ProcessPriorityClass.Normal,
                "BelowNormal" => ProcessPriorityClass.BelowNormal,
                "Low" => ProcessPriorityClass.Idle,
                "Idle" => ProcessPriorityClass.Idle,
                _ => ProcessPriorityClass.Normal
            };

            int ok = 0, denied = 0;
            foreach (var row in targets)
            {
                try
                {
                    using var proc = Process.GetProcessById(row.Pid);
                    proc.PriorityClass = priority;
                    ok++;
                }
                catch { denied++; }
            }

            TxtStatus.Text = targets.Count == 1
                ? (denied == 0
                    ? $"✅ Prioridade de {targets[0].Name} alterada para {priority}"
                    : $"Erro ao alterar prioridade de {targets[0].Name}.")
                : $"✅ Prioridade {priority} em {ok}/{targets.Count}" + (denied > 0 ? $" ({denied} acesso negado)" : "");
        }

        private void BtnClearMemory_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;
            try
            {
                MemoryOptimizer.EmptyProcessWorkingSet(row.Pid);
                TxtStatus.Text = $"🧹 Memória de {row.Name} limpa.";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao limpar memória: {ex.Message}"; }
        }

        private void MenuSuspend_Click(object sender, RoutedEventArgs e) => SuspendResume(true);
        private void MenuResume_Click(object sender, RoutedEventArgs e) => SuspendResume(false);
        private void BtnSuspend_Click(object sender, RoutedEventArgs e)
        {
            var row = SelectedRow;
            if (row == null) return;
            // Check if process is suspended by trying to resume
            SuspendResume(true);
        }

        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandleP(IntPtr handle);

        private const uint PROCESS_SUSPEND_RESUME = 0x0800;

        private void SuspendResume(bool suspend)
        {
            var targets = SelectedRows;
            if (targets.Count == 0) return;
            int ok = 0, denied = 0;
            foreach (var row in targets)
            {
                try
                {
                    IntPtr hProcess = OpenProcess(PROCESS_SUSPEND_RESUME, false, row.Pid);
                    if (hProcess == IntPtr.Zero) { denied++; continue; }
                    try
                    {
                        if (suspend) NtSuspendProcess(hProcess);
                        else NtResumeProcess(hProcess);
                        ok++;
                    }
                    finally { CloseHandleP(hProcess); }
                }
                catch { denied++; }
            }
            string verb = suspend ? "suspenso(s)" : "retomado(s)";
            TxtStatus.Text = targets.Count == 1
                ? (denied == 0 ? $"⏸ {targets[0].Name} {verb}." : "Acesso negado.")
                : $"⏸ {ok}/{targets.Count} {verb}" + (denied > 0 ? $" ({denied} acesso negado)" : "");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass, IntPtr ProcessInformation, uint ProcessInformationSize);

        private const int ProcessPowerThrottling = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        private void MenuEcoQoS_Click(object sender, RoutedEventArgs e) => BtnEcoQos_Click(sender, e);

        private void BtnEcoQos_Click(object sender, RoutedEventArgs e)
        {
            var targets = SelectedRows;
            if (targets.Count == 0) return;
            int ok = 0, denied = 0;
            foreach (var row in targets)
            {
                try
                {
                    using var proc = Process.GetProcessById(row.Pid);
                    var state = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = 1,
                        ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                        StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                    };
                    IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(state));
                    try
                    {
                        Marshal.StructureToPtr(state, ptr, false);
                        if (SetProcessInformation(proc.Handle, ProcessPowerThrottling, ptr, (uint)Marshal.SizeOf(state))) ok++;
                        else denied++;
                    }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                catch { denied++; }
            }
            TxtStatus.Text = targets.Count == 1
                ? (denied == 0 ? $"🌱 EcoQoS ativado para {targets[0].Name}." : "Acesso negado.")
                : $"🌱 EcoQoS em {ok}/{targets.Count}" + (denied > 0 ? $" ({denied} acesso negado)" : "");
        }

        // ══════════════════════════════════════════════
        //  EXPORT CSV
        // ══════════════════════════════════════════════
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV (*.csv)|*.csv",
                    FileName = $"KitLugia_Processos_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (dialog.ShowDialog() != true) return;

                var sb = new StringBuilder();
                sb.AppendLine("Nome,PID,CPU%,RAM,Disco,Rede,GPU,Status,Grupo,Threads,Handles,Caminho");

                foreach (var r in _filteredRows)
                {
                    sb.AppendLine($"\"{r.Name}\",{r.Pid},{r.CpuValue:F1},\"{r.RamMB}\",\"{r.Disk}\",\"{r.Network}\",\"{r.Gpu}\",\"{r.Status}\",\"{r.Group}\",{r.Threads},{r.Handles},\"{r.Path}\"");
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                TxtStatus.Text = $"📄 Exportado {dialog.FileName}";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao exportar: {ex.Message}"; }
        }

        // ══════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════
        private static string FormatBytesSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            if (bytesPerSec < 1024 * 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
            return $"{bytesPerSec / (1024 * 1024 * 1024):F2} GB/s";
        }

        /// <summary>
        /// Escreve um número do resumo superior SOMENTE quando o texto muda — evita o "pisca-pisca"
        /// de reescrever o mesmo valor a cada tick (que força re-render do TextBlock).
        /// </summary>
        private static void SetMetricText(System.Windows.Controls.TextBlock tb, string text, SolidColorBrush? brush)
        {
            if (tb == null) return;
            if (!string.Equals(tb.Text, text, StringComparison.Ordinal))
            {
                tb.Text = text;
                if (brush != null) tb.Foreground = brush;
            }
            else if (brush != null && !Equals(tb.Foreground, brush)) tb.Foreground = brush;
        }

        /// <summary>
        /// Escreve texto SOMENTE quando mudou (foreground opcional). Mesma ideia do
        /// SetMetricText, para os ~20 textos do Resumo/Desempenho que eram reescritos a
        /// cada tick e forçavam re-render — causa do "pisca-pisca" com dado parado.
        /// </summary>
        private static void SetText(System.Windows.Controls.TextBlock tb, string text, SolidColorBrush? brush = null)
        {
            if (tb == null) return;
            if (!string.Equals(tb.Text, text, StringComparison.Ordinal)) tb.Text = text;
            if (brush != null && !Equals(tb.Foreground, brush)) tb.Foreground = brush;
        }

        // Frozen brush cache — created once, shared across all calls (thread-safe, zero GC)
        private static readonly SolidColorBrush _brushRed = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x11, 0x23)));
        private static readonly SolidColorBrush _brushOrange = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x98, 0x00)));
        // Ambar do TM (era #FFD700 = o DOURADO do tema global do Kit, que destoava do azul
        // do Gerenciador de Tarefas em todo valor em atencao: CPU/RAM ~50-60%, temperatura,
        // potencia. Mesmo tom do icone de "fixado" no Resumo.)
        private static readonly SolidColorBrush _brushYellow = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F)));
        private static readonly SolidColorBrush _brushGreen = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)));
        private static readonly SolidColorBrush _brushGray = FreezeBrush(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly SolidColorBrush _brushTransparent = FreezeBrush(Brushes.Transparent);

        private static SolidColorBrush FreezeBrush(SolidColorBrush b)
        {
            if (!b.IsFrozen) b.Freeze();
            return b;
        }

        private static SolidColorBrush GetHeatColor(float value, float warnThreshold, float criticalThreshold)
        {
            if (value >= criticalThreshold) return _brushRed;
            if (value >= warnThreshold) return _brushOrange;
            if (value >= warnThreshold * 0.6f) return _brushYellow;
            return _brushGreen;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        // ══════════════════════════════════════════════
        //  PROCESS ROW MODEL
        // ══════════════════════════════════════════════
        public class ProcessRow : INotifyPropertyChanged
        {
            private BitmapSource? _processIcon;
            private string _displayName = "";
            private string _name = "";
            private int _pid;
            private string _cpu = "0%";
            private double _cpuValue;
            private string _ramMB = "";
            private double _ramValue;
            private string _handles = "0";
            private string _threads = "0";
            private string _group = "";
            private string _status = "";
            private string _disk = "—";
            private double _diskBytesPerSec;
            private double _diskReadBytesPerSec;
            private double _diskWriteBytesPerSec;
            private double _diskOpsPerSec;
            private string _network = "—";
            private int _networkConnections;
            private double _netBytesPerSec;
            private string _gpu = "—";
            private double _gpuValue;
            private string _userName = "";
            private string _peakMemMB = "";
            private double _peakMemValue;
            private string _cpuTime = "";
            private double _cpuTimeSec;
            private string _pageFaults = "";
            private double _pageFaultsValue;
            private string _path = "";
            // ── Paridade TMOG (painel de detalhes): commit privado, tempos separados e E/S acumulada
            private double _commitMB;
            private double _kernelTimeSec;
            private double _userTimeSec;
            private ulong _ioReadTotal;
            private ulong _ioWriteTotal;
            private ulong _ioOpsTotal;
            private int _parentPid;
            private int _childCount = 1;
            private bool _isProtected;
            private string _protectedBadge = "";
            private string _iconPath = "";
            private bool _isExpanded;
            private List<ProcessRow> _rawChildren = new();
            private bool _isChild;

            public event PropertyChangedEventHandler? PropertyChanged;
            private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            private bool Set<T>(ref T f, T v, string n) { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; Raise(n); return true; }

            // ── Motor de render 60 fps (ver KitTaskManager.Render.cs) ──────────────
            // "Target" = valor REAL da última amostra (1x/s); o valor exibido (CpuValue,
            // RamValue...) caminha até ele quadro a quadro, criando o movimento suave.
            // -1 em Disk/Net/GpuTarget significa "não animar esta métrica".
            private double _cpuTarget, _ramTarget;
            private double _diskTarget = -1, _netTarget = -1, _gpuTarget = -1;
            private bool _isGhost;
            private bool _hot = true;

            // CUIDADO com thread: as linhas nascem numa thread de trabalho (enumeração em
            // background) e SolidColorBrush é Freezable com afinidade de thread — criar o
            // brush no construtor gerava "É necessário criar DependencySource no mesmo
            // thread que o DependencyObject" ao montar o template. Por isso é LAZY: nasce
            // na primeira leitura, que acontece na thread da UI (binding/refresh).
            private SolidColorBrush? _highlightBrush;

            /// <summary>Fundo da linha (NÃO congelado: o motor de 60 fps anima a Opacity).</summary>
            public SolidColorBrush HighlightBrush => _highlightBrush ??= new SolidColorBrush(Colors.Transparent);

            /// <summary>0..1 — quanto do brilho verde/vermelho ainda está aceso.</summary>
            public double HighlightLevel { get; private set; }

            internal void SetHighlightColor(Color c)
            {
                try { HighlightBrush.Color = c; } catch { }
            }

            internal void SetHighlightLevel(double v)
            {
                HighlightLevel = v;
                // Mexer na Opacity de um brush re-renderiza a linha SEM disparar
                // PropertyChanged — é o que permite centenas de linhas a 60 fps.
                try { HighlightBrush.Opacity = v; } catch { }
            }

            /// <summary>Linha de processo que acabou de fechar (mostrada em vermelho até apagar).</summary>
            public bool IsGhost { get => _isGhost; set { if (Set(ref _isGhost, value, nameof(IsGhost))) { Raise(nameof(GhostBadge)); Raise(nameof(IsGhostVisible)); } } }
            public string GhostBadge => IsGhost ? "✖" : "";
            public Visibility IsGhostVisible => IsGhost ? Visibility.Visible : Visibility.Collapsed;

            public double CpuTarget { get => _cpuTarget; set => _cpuTarget = value; }
            public double RamTarget { get => _ramTarget; set => _ramTarget = value; }
            public double DiskTarget { get => _diskTarget; set => _diskTarget = value; }
            public double NetTarget { get => _netTarget; set => _netTarget = value; }
            public double GpuTarget { get => _gpuTarget; set => _gpuTarget = value; }

            internal bool IsHot => _hot;
            internal void MarkHot() => _hot = true;
            internal void MarkSettled() => _hot = false;

            public bool IsExpanded { get => _isExpanded; set { if (Set(ref _isExpanded, value, nameof(IsExpanded))) Raise(nameof(ExpandIcon)); } }
            public bool IsChild { get => _isChild; set { if (Set(ref _isChild, value, nameof(IsChild))) { Raise(nameof(NameMargin)); } } }
            // Filho: 4px = mesmo ponto de partida do pai; a seta oculta (14px) alinha o
            // └─ EXATAMENTE sob o início do nome do pai — árvore clássica (TMOG/Win11).
            // Antes era 28px: o nome do filho ficava mais longe que o texto do pai.
            public Thickness NameMargin => IsChild ? new Thickness(4, 0, 0, 0) : new Thickness(4, 0, 0, 0);
            public List<ProcessRow> RawChildren { get => _rawChildren; set { _rawChildren = value; Raise(nameof(RawChildren)); Raise(nameof(HasChildren)); } }
            public bool HasChildren => RawChildren != null && RawChildren.Count > 0;
            public string ExpandIcon => IsChild ? "" : (ChildCount > 1 ? (IsExpanded ? "▼" : "▶") : "");
            public string DisplayName { get => _displayName; set { if (Set(ref _displayName, value, nameof(DisplayName))) Raise(nameof(ExpandIcon)); } }
            public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
            public int Pid { get => _pid; set => Set(ref _pid, value, nameof(Pid)); }
            public string Cpu { get => _cpu; set => Set(ref _cpu, value, nameof(Cpu)); }
            public double CpuValue { get => _cpuValue; set { if (Set(ref _cpuValue, value, nameof(CpuValue))) Raise(nameof(CpuCellBackground)); } }
            public string RamMB { get => _ramMB; set => Set(ref _ramMB, value, nameof(RamMB)); }
            public double RamValue { get => _ramValue; set { if (Set(ref _ramValue, value, nameof(RamValue))) Raise(nameof(RamCellBackground)); } }
            public string Handles { get => _handles; set => Set(ref _handles, value, nameof(Handles)); }
            public string Threads { get => _threads; set => Set(ref _threads, value, nameof(Threads)); }
            public string Group { get => _group; set => Set(ref _group, value, nameof(Group)); }
            public string Status { get => _status; set => Set(ref _status, value, nameof(Status)); }
            public string Disk { get => _disk; set => Set(ref _disk, value, nameof(Disk)); }
            public double DiskBytesPerSec { get => _diskBytesPerSec; set => Set(ref _diskBytesPerSec, value, nameof(DiskBytesPerSec)); }
            // Split leitura/escrita — alimenta a aba Armazenamento (o total sozinho não
            // diz se o processo está lendo (indexação, cache) ou gravando (backup, sync)).
            public double DiskReadBytesPerSec { get => _diskReadBytesPerSec; set => Set(ref _diskReadBytesPerSec, value, nameof(DiskReadBytesPerSec)); }
            public double DiskWriteBytesPerSec { get => _diskWriteBytesPerSec; set => Set(ref _diskWriteBytesPerSec, value, nameof(DiskWriteBytesPerSec)); }
            // Operações/s: revela o caso em que o volume de bytes é baixo mas a
            // quantidade de operações é enorme (E/S aleatória pequena) — que é
            // justamente o que satura um disco sem aparecer no MB/s.
            public double DiskOpsPerSec { get => _diskOpsPerSec; set => Set(ref _diskOpsPerSec, value, nameof(DiskOpsPerSec)); }
            public string Network { get => _network; set => Set(ref _network, value, nameof(Network)); }
            public int NetworkConnections { get => _networkConnections; set => Set(ref _networkConnections, value, nameof(NetworkConnections)); }
            public double NetBytesPerSec { get => _netBytesPerSec; set => Set(ref _netBytesPerSec, value, nameof(NetBytesPerSec)); }
            public string Gpu { get => _gpu; set => Set(ref _gpu, value, nameof(Gpu)); }
            public double GpuValue { get => _gpuValue; set => Set(ref _gpuValue, value, nameof(GpuValue)); }
            // Paridade TMOG: User name / Peak memory / CPU time / Page faults
            public string UserName { get => _userName; set => Set(ref _userName, value, nameof(UserName)); }
            public string PeakMemMB { get => _peakMemMB; set => Set(ref _peakMemMB, value, nameof(PeakMemMB)); }
            public double PeakMemValue { get => _peakMemValue; set => Set(ref _peakMemValue, value, nameof(PeakMemValue)); }
            public string CpuTime { get => _cpuTime; set => Set(ref _cpuTime, value, nameof(CpuTime)); }
            public double CpuTimeSec { get => _cpuTimeSec; set => Set(ref _cpuTimeSec, value, nameof(CpuTimeSec)); }
            public string PageFaults { get => _pageFaults; set => Set(ref _pageFaults, value, nameof(PageFaults)); }
            public double PageFaultsValue { get => _pageFaultsValue; set => Set(ref _pageFaultsValue, value, nameof(PageFaultsValue)); }
            public string Path { get => _path; set => Set(ref _path, value, nameof(Path)); }
            public double CommitMB { get => _commitMB; set => Set(ref _commitMB, value, nameof(CommitMB)); }
            public double KernelTimeSec { get => _kernelTimeSec; set => Set(ref _kernelTimeSec, value, nameof(KernelTimeSec)); }
            public double UserTimeSec { get => _userTimeSec; set => Set(ref _userTimeSec, value, nameof(UserTimeSec)); }
            public ulong IoReadTotal { get => _ioReadTotal; set => Set(ref _ioReadTotal, value, nameof(IoReadTotal)); }
            public ulong IoWriteTotal { get => _ioWriteTotal; set => Set(ref _ioWriteTotal, value, nameof(IoWriteTotal)); }
            public ulong IoOpsTotal { get => _ioOpsTotal; set => Set(ref _ioOpsTotal, value, nameof(IoOpsTotal)); }
            public int ParentPid { get => _parentPid; set => Set(ref _parentPid, value, nameof(ParentPid)); }
            public int ChildCount { get => _childCount; set { if (Set(ref _childCount, value, nameof(ChildCount))) Raise(nameof(ExpandIcon)); } }
            public bool IsProtected { get => _isProtected; set => Set(ref _isProtected, value, nameof(IsProtected)); }
            public string ProtectedBadge { get => _protectedBadge; set { if (Set(ref _protectedBadge, value, nameof(ProtectedBadge))) Raise(nameof(IsProtectedBadgeVisible)); } }
            public Visibility IsProtectedBadgeVisible => string.IsNullOrEmpty(ProtectedBadge) ? Visibility.Collapsed : Visibility.Visible;
            public string IconPath { get => _iconPath; set => Set(ref _iconPath, value, nameof(IconPath)); }

            public BitmapSource? ProcessIcon
            {
                get => _processIcon;
                set { _processIcon = value; Raise(nameof(ProcessIcon)); }
            }

            // Chave estável para diff (Win11: agrupa por nome+grupo)
            public string GroupKey => $"{Name}|{Group}";

            // Atualiza in-place (sem recriar linha — mantém seleção/ordem)
            public void UpdateFrom(ProcessRow src)
            {
                DisplayName = src.DisplayName;
                // CPU/RAM/Disco/Rede/GPU entram como ALVO e o motor de 60 fps leva o
                // valor exibido até ele (texto + barra andam juntos, sem pular).
                CpuTarget = src.CpuValue;
                RamTarget = src.RamValue;
                // Disco/Rede/GPU são TEXTO (sem barra) — esses seguem instantâneos, como antes.
                DiskTarget = -1; NetTarget = -1; GpuTarget = -1;
                // TEXTO atualiza 1x/s AQUI (ritmo TMOG real). A animação por quadro é só da
                // COR da célula (via brush) — texto por quadro saturava a UI.
                CpuValue = src.CpuValue; Cpu = src.Cpu;
                RamValue = src.RamValue; RamMB = src.RamMB;
                Handles = src.Handles; Threads = src.Threads;
                Status = src.Status;
                Disk = src.Disk; DiskBytesPerSec = src.DiskBytesPerSec;
                DiskReadBytesPerSec = src.DiskReadBytesPerSec; DiskWriteBytesPerSec = src.DiskWriteBytesPerSec;
                DiskOpsPerSec = src.DiskOpsPerSec;
                Network = src.Network; NetworkConnections = src.NetworkConnections; NetBytesPerSec = src.NetBytesPerSec;
                Gpu = src.Gpu; GpuValue = src.GpuValue;
                UserName = src.UserName;
                PeakMemMB = src.PeakMemMB; PeakMemValue = src.PeakMemValue;
                CpuTime = src.CpuTime; CpuTimeSec = src.CpuTimeSec;
                PageFaults = src.PageFaults; PageFaultsValue = src.PageFaultsValue;
                Path = src.Path; ParentPid = src.ParentPid;
                CommitMB = src.CommitMB; KernelTimeSec = src.KernelTimeSec; UserTimeSec = src.UserTimeSec;
                IoReadTotal = src.IoReadTotal; IoWriteTotal = src.IoWriteTotal; IoOpsTotal = src.IoOpsTotal;
                ChildCount = src.ChildCount;
                IsProtected = src.IsProtected; ProtectedBadge = src.ProtectedBadge;
                if (src.ProcessIcon != null) ProcessIcon = src.ProcessIcon;
                IconPath = src.IconPath;
                // Uma linha que volta a ser viva (fantasma revivido, ou linha recém-reusada
                // pelo merge de filhos) pode ter ficado marcada como "filha" no ciclo
                // anterior — se o Merge preservou a INSTÂNCIA e re-insere sem resetar
                // IsChild, o produto é um FILHO duplicado (objeto já na lista).
                if (IsChild && src.IsChild == false)
                {
                    IsChild = false;
                }
                // Filhos: atualiza lista (RowDetails mostra os PIDs individuais).
                // MERGE por PID (não substituição): as linhas-filhas INSERIDAS na grid
                // são ESTES objetos — trocar a instância todo refresh fazia o valor
                // mostrado congelar (a grid guarda a instância velha) e a seleção pular.
                var oldByPid = new Dictionary<int, ProcessRow>();
                foreach (var oc in _rawChildren) oldByPid[oc.Pid] = oc;
                var merged = new List<ProcessRow>(src.RawChildren.Count);
                foreach (var nc in src.RawChildren)
                {
                    if (oldByPid.TryGetValue(nc.Pid, out var keep)) { keep.UpdateFrom(nc); merged.Add(keep); }
                    else merged.Add(nc);
                }
                RawChildren = merged;
                // IsExpanded preserva o estado do usuário (não sobrescreve)
            }            // Heatmap CPU/RAM — brushes CONGELADOS (estilo TMOG real: cor e texto atualizam
            // 1x/s no refresh). Medido: animar cor por quadro (brush mutável) custava
            // 1000-2400 ms/s de UI thread — saturava o app mesmo sem PropertyChanged.
            private static readonly SolidColorBrush _cellRed = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(40, 0xE8, 0x11, 0x23)));
            private static readonly SolidColorBrush _cellOrange = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(30, 0xFF, 0x98, 0x00)));
            private static readonly SolidColorBrush _cellYellow = FreezeCellBrush(new SolidColorBrush(Color.FromArgb(20, 0xFF, 0xD7, 0x00)));

            public SolidColorBrush CpuCellBackground => GetCellBackground(CpuValue, 50, 80);
            public SolidColorBrush RamCellBackground => GetCellBackground(RamValue, 40, 80);

            private static SolidColorBrush GetCellBackground(double value, double warn, double critical)
            {
                if (value >= critical) return _cellRed;
                if (value >= warn) return _cellOrange;
                if (value >= warn * 0.5) return _cellYellow;
                return Brushes.Transparent;
            }

        }
    }

    /// <summary>Largura da barra inline de CPU (paridade TMOG): fração de 57px.</summary>
    public sealed class CpuBarWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try { double v = System.Convert.ToDouble(value); return Math.Max(0, Math.Min(1, v / 100.0)) * 57.0; }
            catch { return 0.0; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
    }
}
