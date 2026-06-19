using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.GUI.Services;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class DiagnosticPage : Page
    {
        private DispatcherTimer? _refreshTimer;
        private List<string> _activityLog = new();
        private long _lastMemoryUsage = 0;
        private DateTime _pageLoadTime;
        private bool _isMeasuringMemory;
        private bool _isCleaningKit;

        public DiagnosticPage()
        {
            InitializeComponent();
            _pageLoadTime = DateTime.Now;
            
            // Timer para atualizar diagnóstico a cada 2 segundos
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
            
            // Log inicial
            LogActivity("🔬 Página de diagnóstico inicializada");
            
            // Primeira atualização
            RefreshDiagnostics();

            // Monitorar mudanças de memória
            _lastMemoryUsage = GC.GetTotalMemory(false);


            this.Unloaded += DiagnosticPage_Unloaded;
        }


        public void Cleanup()
        {
            if (_refreshTimer != null)
                _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer?.Stop();
            _refreshTimer = null;
            this.Unloaded -= DiagnosticPage_Unloaded;


            _activityLog.Clear();
            _activityLog = null!;


            this.DataContext = null;




        }

        private void DiagnosticPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
            LogActivity("⏹️ Timer de diagnóstico parado");
        }

        private void OnRefreshTimerTick(object? s, EventArgs e) => RefreshDiagnostics();


        private int _refreshCounter = 0;
        private int _lastTimerCount = -1;
        private int _lastTaskCount = -1;
        
        // Cache do texto de memória para evitar recriação
        private string _lastMemoryText = "";
        
        private void RefreshDiagnostics()
        {
            try
            {
                // Memória atual
                long currentMemory = GC.GetTotalMemory(false);
                double memoryMB = currentMemory / (1024.0 * 1024.0);
                string memoryText = $"{memoryMB:F1} MB";
                

                if (memoryText != _lastMemoryText)
                {
                    TxtCurrentMemory.Text = memoryText;
                    _lastMemoryText = memoryText;
                }
                
                _lastMemoryUsage = currentMemory;
                

                _refreshCounter++;
                if (_refreshCounter >= 5)
                {
                    _refreshCounter = 0;
                    
                    // Contar timers - só atualizar se mudou
                    int timerCount = CountActiveTimers();
                    if (timerCount != _lastTimerCount)
                    {
                        TxtActiveTimers.Text = timerCount.ToString();
                        _lastTimerCount = timerCount;
                        UpdateTimerList(); // Só atualiza lista quando muda
                    }
                    
                    // Contar tasks - só atualizar se mudou
                    int taskCount = CountBackgroundTasks();
                    if (taskCount != _lastTaskCount)
                    {
                        TxtBackgroundTasks.Text = taskCount.ToString();
                        _lastTaskCount = taskCount;
                    }
                    
                    // Atualizar botões
                    UpdateModuleButtons();
                }
            }
            catch (Exception ex)
            {
                LogActivity($" Erro no diagnóstico: {ex.Message}");
            }
        }

        //  Cache de timers para evitar reflection frequente
        private DispatcherTimer? _cachedTrayTimer;
        private DispatcherTimer? _cachedCleanerTimer;
        private DateTime _lastTimerCacheUpdate = DateTime.MinValue;
        
        private int CountActiveTimers()
        {
            int count = 0;
            
            // Atualizar cache a cada 5 segundos
            bool needCacheUpdate = DateTime.Now - _lastTimerCacheUpdate > TimeSpan.FromSeconds(5);
            
            // Contar timers no TrayIconService
            var trayService = GetTrayService();
            if (trayService != null)
            {
                if (_cachedTrayTimer == null || needCacheUpdate)
                {
                    _cachedTrayTimer = trayService.GetType().GetField("_monitorTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(trayService) as DispatcherTimer;
                }
                if (_cachedTrayTimer?.IsEnabled == true) count++;
            }
            
            // Contar timer no AggressiveMemoryCleaner
            if (_cachedCleanerTimer == null || needCacheUpdate)
            {
                _cachedCleanerTimer = typeof(AggressiveMemoryCleaner).GetField("_cleanupTimer", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DispatcherTimer;
            }
            if (_cachedCleanerTimer?.IsEnabled == true) count++;
            
            // Timer do próprio DiagnosticPage
            if (_refreshTimer?.IsEnabled == true) count++;
            
            if (needCacheUpdate)
                _lastTimerCacheUpdate = DateTime.Now;
            
            return count;
        }

        private int CountBackgroundTasks()
        {
            return ThreadPool.PendingWorkItemCount > 0 ? (int)ThreadPool.PendingWorkItemCount : 0;
        }


        private readonly List<string> _timerListCache = new List<string>(10);
        private bool _lastTrayState = false;
        private bool _lastDiagnosticTimerState = false;
        
        private void UpdateTimerList()
        {
            // Obter estados atuais
            var trayService = GetTrayService();
            bool currentTrayState = trayService?.IsTrayEnabled == true;
            bool currentDiagnosticState = _refreshTimer?.IsEnabled == true;
            

            if (currentTrayState == _lastTrayState && currentDiagnosticState == _lastDiagnosticTimerState)
            {
                return; // Nada mudou, evitar trabalho desnecessário
            }
            
            // Atualizar estados cacheados
            _lastTrayState = currentTrayState;
            _lastDiagnosticTimerState = currentDiagnosticState;
            
            // Limpar e reutilizar lista existente
            _timerListCache.Clear();
            
            // Tray Timer
            if (trayService != null)
            {
                if (_cachedTrayInterval == 0)
                {
                    var interval = trayService.GetType().GetProperty("MonitorIntervalSeconds")?.GetValue(trayService);
                    _cachedTrayInterval = interval != null ? (int)interval : 30;
                }
                _timerListCache.Add($"🌐 TrayIconService._monitorTimer ({_cachedTrayInterval}s)");
            }
            
            // Aggressive Cleaner
            _timerListCache.Add("🧹 AggressiveMemoryCleaner._cleanupTimer (30s)");
            
            // MainWindow search timer
            _timerListCache.Add("🔍 MainWindow._searchDebounceTimer (300ms)");
            
            // Diagnostic timer
            if (currentDiagnosticState)
                _timerListCache.Add("📊 Diagnostic._refreshTimer (2s) [ATIVO]");
            else
                _timerListCache.Add("📊 Diagnostic._refreshTimer (2s) [PAUSADO]");
            
            // Só atualizar ItemsSource se necessário
            ListActiveTimers.ItemsSource = _timerListCache;
        }
        
        private int _cachedTrayInterval = 0;


        private readonly System.Windows.Media.SolidColorBrush _greenBrush = new(System.Windows.Media.Color.FromRgb(0, 128, 0));
        private readonly System.Windows.Media.SolidColorBrush _grayBrush = new(System.Windows.Media.Color.FromRgb(128, 128, 128));
        private bool _lastTrayEnabledState = false;
        
        private void UpdateModuleButtons()
        {
            // Atualizar texto dos botões baseado no estado
            var trayService = GetTrayService();
            bool isEnabled = trayService?.IsTrayEnabled == true;
            

            if (isEnabled != _lastTrayEnabledState)
            {
                BtnToggleTray.Content = isEnabled
                    ? "🌐 Tray Icon Service (ATIVO - Clique para Desligar)" 
                    : "🌐 Tray Icon Service (DESLIGADO - Clique para Ligar)";
                    
                BtnToggleTray.Background = isEnabled ? _greenBrush : _grayBrush;
                _lastTrayEnabledState = isEnabled;
            }
        }

        //  StringBuilder reutilizável para evitar alocações de string
        private readonly System.Text.StringBuilder _logBuilder = new System.Text.StringBuilder(2048);
        private DateTime _lastLogUpdate = DateTime.MinValue;
        
        private void LogActivity(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _activityLog.Add($"[{timestamp}] {message}");
            
            // Limitar log a 50 entradas
            if (_activityLog.Count > 50)
                _activityLog.RemoveAt(0);
            
            //  Só atualizar UI a cada 500ms para evitar alocações excessivas
            if (DateTime.Now - _lastLogUpdate > TimeSpan.FromMilliseconds(500))
            {
                _logBuilder.Clear();
                for (int i = 0; i < _activityLog.Count; i++)
                {
                    if (i > 0) _logBuilder.Append('\n');
                    _logBuilder.Append(_activityLog[i]);
                }
                TxtActivityLog.Text = _logBuilder.ToString();
                TxtActivityLog.ScrollToEnd();
                _lastLogUpdate = DateTime.Now;
            }
        }

        private TrayIconService? GetTrayService()
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
            {
                return mainWindow.TrayService;
            }
            return null;
        }

        // BOTÕES DE CONTROLE
        private void BtnToggleTray_Click(object sender, RoutedEventArgs e)
        {
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.IsTrayEnabled = !tray.IsTrayEnabled;
                LogActivity(tray.IsTrayEnabled ? "🌐 Tray Icon ATIVADO" : "🌐 Tray Icon DESATIVADO");
                RefreshDiagnostics();
            }
        }

        private void BtnToggleGameBoost_Click(object sender, RoutedEventArgs e)
        {
            // Parar/Desligar GameBoost
            try
            {
                // Acessar GameBoost via TrayIconService
                var tray = GetTrayService();
                if (tray != null)
                {
                    tray.GamePriorityEnabled = !tray.GamePriorityEnabled;
                    LogActivity(tray.GamePriorityEnabled ? "🎮 GameBoost ATIVADO" : "🎮 GameBoost DESATIVADO");
                }
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro ao toggle GameBoost: {ex.Message}");
            }
        }

        private void BtnToggleAggressiveCleaner_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var timer = typeof(AggressiveMemoryCleaner).GetField("_cleanupTimer", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DispatcherTimer;
                if (timer != null)
                {
                    if (timer.IsEnabled)
                    {
                        timer.Stop();
                        LogActivity("🧹 AggressiveMemoryCleaner DESATIVADO");
                    }
                    else
                    {
                        timer.Start();
                        LogActivity("🧹 AggressiveMemoryCleaner ATIVADO");
                    }
                }
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro ao toggle cleaner: {ex.Message}");
            }
        }

        private void BtnStopAllTimers_Click(object sender, RoutedEventArgs e)
        {
            LogActivity("⏹️ PARANDO TODOS OS TIMERS...");
            
            try
            {
                // Parar timer do tray
                var tray = GetTrayService();
                if (tray != null)
                {
                    var timer = tray.GetType().GetField("_monitorTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tray) as DispatcherTimer;
                    timer?.Stop();
                }
                
                // Parar AggressiveCleaner
                var cleanerTimer = typeof(AggressiveMemoryCleaner).GetField("_cleanupTimer", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DispatcherTimer;
                cleanerTimer?.Stop();
                
                LogActivity("✅ Todos os timers externos parados");
                LogActivity("💡 Observe a memória nos próximos 10 segundos. Se parar de subir, o leak é em um timer.");
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro: {ex.Message}");
            }
            
            RefreshDiagnostics();
        }

        private async void BtnForceGCCollect_Click(object sender, RoutedEventArgs e)
        {
            if (_isMeasuringMemory) return;
            _isMeasuringMemory = true;
            try
            {
                LogActivity("♻️ Medindo uso de memória...");
                long before = GC.GetTotalMemory(false);

                await Task.Run(() =>
                {
                    // Apenas medir, sem forçar GC
                });

                long after = GC.GetTotalMemory(false);
                double freed = (before - after) / (1024.0 * 1024.0);

                LogActivity($"✅ Medição completa. Uso atual: {after / (1024.0 * 1024.0):F1} MB");
                RefreshDiagnostics();
            }
            finally
            {
                _isMeasuringMemory = false;
            }
        }

        private void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            LogActivity("🔄 RESETANDO TODOS OS MÓDULOS...");
            
            // Resetar para estado padrão
            var tray = GetTrayService();
            if (tray != null)
            {
                tray.IsTrayEnabled = true;
                tray.GamePriorityEnabled = false;
            }
            
            // Reiniciar AggressiveCleaner
            var cleanerTimer = typeof(AggressiveMemoryCleaner).GetField("_cleanupTimer", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DispatcherTimer;
            cleanerTimer?.Start();
            
            LogActivity("✅ Todos os módulos resetados para padrão");
            RefreshDiagnostics();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LogActivity("🔄 Atualização manual solicitada");
            RefreshDiagnostics();
        }

        // CONTROLE INDIVIDUAL DE TIMERS
        private void BtnToggleTrayTimer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tray = GetTrayService();
                if (tray != null)
                {
                    var timer = tray.GetType().GetField("_monitorTimer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(tray) as DispatcherTimer;
                    if (timer != null)
                    {
                        if (timer.IsEnabled)
                        {
                            timer.Stop();
                            LogActivity("⏱️ Tray._monitorTimer PARADO");
                        }
                        else
                        {
                            timer.Start();
                            LogActivity("⏱️ Tray._monitorTimer INICIADO");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro: {ex.Message}");
            }
            RefreshDiagnostics();
        }

        private void BtnToggleCleanerTimer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var timer = typeof(AggressiveMemoryCleaner).GetField("_cleanupTimer", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as DispatcherTimer;
                if (timer != null)
                {
                    if (timer.IsEnabled)
                    {
                        timer.Stop();
                        LogActivity("⏱️ Cleaner._cleanupTimer PARADO");
                    }
                    else
                    {
                        timer.Start();
                        LogActivity("⏱️ Cleaner._cleanupTimer INICIADO");
                    }
                }
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro: {ex.Message}");
            }
            RefreshDiagnostics();
        }

        private void BtnToggleDiagnosticTimer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_refreshTimer != null)
                {
                    if (_refreshTimer.IsEnabled)
                    {
                        _refreshTimer.Stop();
                        LogActivity("⏱️ Diagnostic._refreshTimer PARADO (atualizações pausadas)");
                    }
                    else
                    {
                        _refreshTimer.Start();
                        LogActivity("⏱️ Diagnostic._refreshTimer INICIADO");
                    }
                }
            }
            catch (Exception ex)
            {
                LogActivity($"❌ Erro: {ex.Message}");
            }
            RefreshDiagnostics();
        }

        private async void BtnCleanKitNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isCleaningKit) return;
            _isCleaningKit = true;
            try
            {
                LogActivity("🧹 LIMPEZA MANUAL DO KIT INICIADA...");
                long before = GC.GetTotalMemory(false);

                try
                {
                    // 1. Limpar buffer de logs do PartitionManager
                    var logBufferField = typeof(PartitionManager).GetField("_logBuffer", BindingFlags.NonPublic | BindingFlags.Static);
                    if (logBufferField?.GetValue(null) is System.Collections.IList logBuffer)
                    {
                        lock (logBuffer)
                        {
                            logBuffer.Clear();
                        }
                        LogActivity("✅ PartitionManager._logBuffer limpo");
                    }

                    // 2. Limpar ThreadLocal do LatencyAnalyzer
                    var latencySamplesField = typeof(LatencyAnalyzer).GetField("_latencySamples", BindingFlags.NonPublic | BindingFlags.Static);
                    var driverStatsField = typeof(LatencyAnalyzer).GetField("_driverStats", BindingFlags.NonPublic | BindingFlags.Static);

                    latencySamplesField?.GetValue(null)?.GetType().GetMethod("Clear")?.Invoke(latencySamplesField.GetValue(null), null);
                    driverStatsField?.GetValue(null)?.GetType().GetMethod("Clear")?.Invoke(driverStatsField.GetValue(null), null);
                    LogActivity("✅ LatencyAnalyzer collections limpas");

                    // 3. Medir apenas, sem forçar GC
                    await Task.Run(() =>
                    {
                        // Apenas medir, sem forçar GC
                    });

                    long after = GC.GetTotalMemory(false);
                    double freed = (before - after) / (1024.0 * 1024.0);

                    LogActivity($"✅ LIMPEZA COMPLETADA! Memória liberada: {freed:F1} MB");
                    LogActivity($"💡 Memória atual: {after / (1024.0 * 1024.0):F1} MB");
                }
                catch (Exception ex)
                {
                    LogActivity($"❌ Erro na limpeza: {ex.Message}");
                }

                RefreshDiagnostics();
            }
            finally
            {
                _isCleaningKit = false;
            }
        }
    }
}
