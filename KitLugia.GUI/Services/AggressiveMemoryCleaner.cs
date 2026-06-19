using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// Alias para resolver ambiguidades
using Application = System.Windows.Application;
using Logger = KitLugia.Core.Logger;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Só limpa quando o uso de memória atinge o limite configurado
    /// </summary>
    public static class AggressiveMemoryCleaner
    {
        private static DispatcherTimer? _monitorTimer;
        private static DateTime _lastCleanup = DateTime.MinValue;
        private static readonly object _cleanupLock = new();
        private static EventHandler? _monitorTickHandler;

        // Configurações
        private static long _memoryLimitMB = 150; // Limite padrão: 150MB
        private static long _memoryLimitBytes => _memoryLimitMB * 1024 * 1024;
        private static int _checkIntervalSeconds = 30;

        // Estatísticas
        public static long TotalMemoryFreed { get; private set; }
        public static int CleanupCount { get; private set; }
        public static DateTime LastCleanupTime { get; private set; }
        public static long MemoryLimitMB => _memoryLimitMB;

        /// <summary>
        /// Define o limite de memória em MB para acionar limpeza
        /// </summary>
        public static void SetMemoryLimit(long limitMB)
        {
            _memoryLimitMB = Math.Max(50, limitMB); // Mínimo 50MB
            Logger.Log($"🧹 Limite de memória definido: {_memoryLimitMB} MB");
        }

        /// <summary>
        /// Inicia monitoramento inteligente de memória
        /// </summary>
        public static void StartIntelligentMonitoring(int checkIntervalSeconds = 30, long memoryLimitMB = 150)
        {
            StopIntelligentMonitoring();

            _memoryLimitMB = memoryLimitMB;
            _checkIntervalSeconds = checkIntervalSeconds;

            _monitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(_checkIntervalSeconds)
            };

            _monitorTickHandler = (s, e) => CheckMemoryAndCleanupIfNeeded();
            _monitorTimer.Tick += _monitorTickHandler;
            _monitorTimer.Start();

            Logger.Log($"🧹 Monitoramento inteligente iniciado - Limite: {_memoryLimitMB}MB, Verificação: {_checkIntervalSeconds}s");
        }

        /// <summary>
        /// Para monitoramento
        /// </summary>
        public static void StopIntelligentMonitoring()
        {
            if (_monitorTimer != null && _monitorTickHandler != null)
            {
                _monitorTimer.Tick -= _monitorTickHandler;
            }
            _monitorTimer?.Stop();
            _monitorTimer = null;
            _monitorTickHandler = null;
        }

        /// <summary>
        /// Verifica uso de memória e limpa se necessário
        /// </summary>
        private static void CheckMemoryAndCleanupIfNeeded()
        {
            try
            {
                var currentMemory = GC.GetTotalMemory(false);

                // Só limpa se:
                // 1. Atingiu o limite
                // 2. Passou pelo menos 30 segundos desde a última limpeza
                if (currentMemory > _memoryLimitBytes &&
                    DateTime.Now - _lastCleanup > TimeSpan.FromSeconds(30))
                {
                    Logger.Log($"🧹 Memória atual: {currentMemory / 1024 / 1024:F1}MB - Limite: {_memoryLimitMB}MB - Iniciando limpeza...");
                    _ = PerformIntelligentCleanup();
                }
            }
            catch { }
        }

        /// <summary>
        /// </summary>
        public static async Task<(long MemoryBefore, long MemoryAfter, long Freed)> PerformIntelligentCleanup()
        {
            lock (_cleanupLock)
            {
                if (DateTime.Now - _lastCleanup < TimeSpan.FromSeconds(30))
                    return (0, 0, 0); // Evita limpezas muito frequentes

                _lastCleanup = DateTime.Now;
            }

            var before = GC.GetTotalMemory(false);

            try
            {
                // 1. Limpa bindings WPF órfãos (memory leaks reais)
                CleanupOrphanedBindings();

                // 2. Força GC leve (só 1 passagem, não agressivo)
                await ForceLightGC();

                var after = GC.GetTotalMemory(false);
                var freed = before - after;

                if (freed > 1024 * 1024) // Só loga se liberou mais de 1MB
                {
                    TotalMemoryFreed += freed;
                    CleanupCount++;
                    LastCleanupTime = DateTime.Now;

                    Logger.Log($"🧹 Limpeza concluída: {freed / 1024 / 1024:F1}MB liberado (Total: {TotalMemoryFreed / 1024 / 1024:F1}MB)");
                }

                return (before, after, freed);
            }
            catch
            {
                return (before, before, 0);
            }
        }

        // Mantém o método antigo para compatibilidade (chama o novo)
        public static async Task<(long MemoryBefore, long MemoryAfter, long Freed)> PerformAggressiveCleanup()
        {
            return await PerformIntelligentCleanup();
        }

        /// <summary>
        /// Encontra todas as instâncias de páginas carregadas
        /// </summary>
        private static IEnumerable<Page> FindAllPageInstances()
        {

            // Típico: 1-10 páginas carregadas simultaneamente
            var pages = new List<Page>(10);

            try
            {
                if (Application.Current != null)
                {
                    foreach (Window window in Application.Current.Windows)
                    {
                        if (window.Content is Page page)
                        {
                            pages.Add(page);
                        }

                        FindPagesInVisualTree(window, pages);
                    }
                }
            }
            catch { }

            return pages;
        }

        /// <summary>
        /// Procura páginas na árvore visual
        /// </summary>
        private static void FindPagesInVisualTree(DependencyObject parent, List<Page> pages)
        {
            try
            {
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is Page page && !pages.Contains(page))
                        pages.Add(page);

                    FindPagesInVisualTree(child, pages);
                }
            }
            catch { }
        }

        /// <summary>
        /// Limpa bindings WPF órfãos (memory leaks reais)
        /// </summary>
        private static void CleanupOrphanedBindings()
        {
            try
            {
                var pages = FindAllPageInstances();
                foreach (var page in pages)
                {
                    if (page.DataContext != null && !IsPageActive(page))
                    {
                        page.DataContext = null;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Verifica se uma página está ativa/visível
        /// </summary>
        private static bool IsPageActive(Page page)
        {
            try
            {
                return page.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Mais eficiente que GC.Collect() - permite que o GC trabalhe naturalmente
        /// </summary>
        private static async Task ForceLightGC()
        {
            await Task.Run(() =>
            {
                try
                {
                    // Salva o modo atual
                    var oldMode = GCSettings.LatencyMode;

                    try
                    {
                        // Define SustainedLowLatency - suprime Gen 2 foreground, apenas Gen 0/1 + background Gen 2
                        // Isso permite que o GC seja menos intrusivo sem causar stutter
                        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

                        // Pequena pausa para permitir que o GC ajuste
                        Thread.Sleep(50);

                        // Se ainda estiver sob pressão de memória, faz uma coleta leve
                        if (GC.GetTotalMemory(false) > _memoryLimitBytes)
                        {
                            GC.Collect(1, GCCollectionMode.Optimized, false);
                            GC.WaitForPendingFinalizers();
                        }
                    }
                    finally
                    {
                        // Restaura o modo original
                        GCSettings.LatencyMode = oldMode;
                    }
                }
                catch { }
            });
        }
        
        /// <summary>
        /// Obtém relatório detalhado do estado da memória
        /// </summary>
        public static string GetDetailedMemoryReport()
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                proc.Refresh();

                var gcMemory = GC.GetTotalMemory(false);
                var workingSet = proc.WorkingSet64;
                var privateMemory = proc.PrivateMemorySize64;

                return $"""
                    === RELATÓRIO DE MEMÓRIA INTELIGENTE ===

                    📊 GC Memory: {gcMemory / 1024 / 1024:F1} MB
                    🏠 Working Set: {workingSet / 1024 / 1024:F1} MB
                    🔒 Private Memory: {privateMemory / 1024 / 1024:F1} MB

                    📈 Gerações GC:
                       Gen 0: {GC.CollectionCount(0)} coleções
                       Gen 1: {GC.CollectionCount(1)} coleções
                       Gen 2: {GC.CollectionCount(2)} coleções

                    🎯 Limite Configurado: {_memoryLimitMB} MB
                    🧹 Limpezas Realizadas: {CleanupCount}
                    💾 Total Liberado: {TotalMemoryFreed / 1024 / 1024:F1} MB
                    ⏰ Última Limpeza: {LastCleanupTime:HH:mm:ss}
                    ===========================================
                    """;
            }
            catch (Exception ex)
            {
                return $"Erro ao gerar relatório: {ex.Message}";
            }
        }
    }
}
