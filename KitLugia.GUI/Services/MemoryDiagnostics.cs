using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using KitLugia.GUI.Pages;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Serviço de diagnóstico de memory leak para KitLugia
    /// Monitora uso de RAM e detecta vazamentos ao navegar entre páginas
    /// </summary>
    public static class MemoryDiagnostics
    {
        private static readonly Process _currentProcess = Process.GetCurrentProcess();
        private static DispatcherTimer? _monitorTimer;
        private static long _lastMemoryBytes;
        private static int _navigationCount;
        private static DateTime _startTime;
        
        // Estatísticas
        public static long PeakMemoryBytes { get; private set; }
        public static int NavigationCount => _navigationCount;
        public static TimeSpan Uptime => DateTime.Now - _startTime;
        
        /// <summary>
        /// Inicia monitoramento de memória
        /// </summary>
        public static void StartMonitoring(int intervalSeconds = 5)
        {
            _startTime = DateTime.Now;
            _lastMemoryBytes = GetCurrentMemoryBytes();
            
            _monitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            
            _monitorTimer.Tick += (s, e) =>
            {
                var currentBytes = GetCurrentMemoryBytes();
                var deltaBytes = currentBytes - _lastMemoryBytes;
                var deltaMB = deltaBytes / (1024.0 * 1024.0);
                
                if (currentBytes > PeakMemoryBytes)
                    PeakMemoryBytes = currentBytes;
                
                // Log se houve crescimento significativo
                if (deltaMB > 10) // Mais de 10MB
                {
                    var msg = $"[MEMORY] +{deltaMB:F1}MB | Total: {currentBytes / (1024.0 * 1024.0):F1}MB | Navegações: {_navigationCount}";
                    System.Diagnostics.Debug.WriteLine(msg);
                    KitLugia.Core.Logger.Log(msg);
                }
                
                _lastMemoryBytes = currentBytes;
            };
            
            _monitorTimer.Start();
            System.Diagnostics.Debug.WriteLine("[MemoryDiagnostics] Monitoramento iniciado");
        }
        
        /// <summary>
        /// Para o monitoramento
        /// </summary>
        public static void StopMonitoring()
        {
            _monitorTimer?.Stop();
            _monitorTimer = null;
        }
        
        /// <summary>
        /// Obtém uso atual de memória em bytes
        /// </summary>
        public static long GetCurrentMemoryBytes()
        {
            _currentProcess.Refresh();
            return _currentProcess.WorkingSet64;
        }
        
        /// <summary>
        /// Obtém informações formatadas de memória
        /// </summary>
        public static string GetMemoryReport()
        {
            var current = GetCurrentMemoryBytes();
            var currentMB = current / (1024.0 * 1024.0);
            var peakMB = PeakMemoryBytes / (1024.0 * 1024.0);
            var uptime = Uptime;
            
            return $"""
                === Memory Diagnostics Report ===
                Current: {currentMB:F1} MB
                Peak: {peakMB:F1} MB
                Uptime: {uptime:h\:mm\:ss}
                Navigations: {_navigationCount}
                Process ID: {_currentProcess.Id}
                Threads: {_currentProcess.Threads.Count}
                Handles: {_currentProcess.HandleCount}
                =================================
                """;
        }
        
        /// <summary>
        /// Registra uma navegação de página
        /// </summary>
        public static void TrackNavigation(string fromPage, string toPage)
        {
            _navigationCount++;
            var memBefore = GetCurrentMemoryBytes();
            
            // Log da navegação
            var msg = $"[NAVIGATION #{_navigationCount}] {fromPage} -> {toPage} | Mem: {memBefore / (1024.0 * 1024.0):F1}MB";
            System.Diagnostics.Debug.WriteLine(msg);
            KitLugia.Core.Logger.Log(msg);
            
            // Força GC para verificar memória liberada
            Task.Run(() =>
            {
                Thread.Sleep(1000); // Espera 1 segundo
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                GC.WaitForPendingFinalizers();
                
                var memAfter = GetCurrentMemoryBytes();
                var diffMB = (memAfter - memBefore) / (1024.0 * 1024.0);
                
                if (diffMB > 5) // Alerta se cresceu mais de 5MB
                {
                    var alert = $"[MEMORY LEAK ALERT] Pós-navegação: +{diffMB:F1}MB não liberados!";
                    System.Diagnostics.Debug.WriteLine(alert);
                    KitLugia.Core.Logger.Log(alert);
                }
            });
        }
        
        /// <summary>
        /// Força coleta de lixo e retorna estatísticas
        /// </summary>
        public static (long Before, long After, long Freed) ForceGarbageCollection()
        {
            var before = GetCurrentMemoryBytes();
            
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
            
            var after = GetCurrentMemoryBytes();
            var freed = before - after;
            
            return (before, after, freed);
        }
        
        /// <summary>
        /// Verifica se há vazamento de memória comparando com baseline
        /// </summary>
        public static bool DetectLeak(long baselineBytes, double thresholdPercent = 50)
        {
            var current = GetCurrentMemoryBytes();
            var growthPercent = ((current - baselineBytes) / (double)baselineBytes) * 100;
            
            return growthPercent > thresholdPercent;
        }
    }
    
    /// <summary>
    /// Extensão para Pages implementarem IDisposable corretamente
    /// </summary>
    public interface IDisposablePage : IDisposable
    {
        void OnNavigatedFrom();
        void OnNavigatedTo();
    }
    
    /// <summary>
    /// Helper para gerenciar recursos de páginas
    /// </summary>
    public static class PageResourceHelper
    {
        /// <summary>
        /// Limpa recursos comuns de uma página WPF
        /// NOTA: Cada página deve implementar seu próprio cleanup no evento Unloaded
        /// </summary>
        public static void CleanupPageResources(Page page)
        {
            // Limpeza é feita via evento Unloaded em cada página
            // Este método é um placeholder para extensões futuras
            System.Diagnostics.Debug.WriteLine($"[PageResourceHelper] Navegação de {page?.GetType().Name}");
        }
    }
}
