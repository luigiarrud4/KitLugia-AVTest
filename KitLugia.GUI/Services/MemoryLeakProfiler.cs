using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Profiler profissional para diagnóstico de memory leaks em WPF
    /// Rastreia objetos vivos, event handlers, e bindings
    /// </summary>
    public static class MemoryLeakProfiler
    {
        private static readonly Dictionary<string, int> _instanceCounts = new();
        private static readonly Dictionary<string, List<WeakReference>> _trackedObjects = new();
        private static DispatcherTimer? _profilerTimer;
        private static bool _isRunning = false;
        private static EventHandler? _profilerTickHandler;

        public static void StartProfiling(int intervalSeconds = 5)
        {
            if (_isRunning) return;
            _isRunning = true;

            _profilerTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };
            _profilerTickHandler = ProfilerTimer_Tick;
            _profilerTimer.Tick += _profilerTickHandler;
            _profilerTimer.Start();

            ConsoleManager.WriteLine("[PROFILER] 🔍 Diagnóstico de memory leak iniciado");
        }

        public static void StopProfiling()
        {
            if (_profilerTimer != null && _profilerTickHandler != null)
            {
                _profilerTimer.Tick -= _profilerTickHandler;
            }
            _profilerTimer?.Stop();
            _profilerTimer = null;
            _profilerTickHandler = null;
            _isRunning = false;
            ConsoleManager.WriteLine("[PROFILER] 🛑 Diagnóstico parado");
        }

        private static void ProfilerTimer_Tick(object? sender, EventArgs e)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            AnalyzeHeap();
        }

        /// <summary>
        /// Analisa o heap para encontrar tipos suspeitos
        /// </summary>
        private static void AnalyzeHeap()
        {
            // Conta instâncias de páginas
            var pageTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .Where(t => t.IsSubclassOf(typeof(Page)) && !t.IsAbstract);

            foreach (var pageType in pageTypes)
            {
                var instances = GetAliveInstances(pageType);
                var key = pageType.Name;

                if (_instanceCounts.ContainsKey(key))
                {
                    var previousCount = _instanceCounts[key];
                    if (instances.Count > previousCount)
                    {
                        var growth = instances.Count - previousCount;
                        ConsoleManager.WriteLine($"[PROFILER] ⚠️ {key}: {previousCount} → {instances.Count} (+{growth})");

                        // Se cresceu mais de 2 instâncias, é leak
                        if (instances.Count > 2)
                        {
                            ReportLeakSuspect(pageType, instances);
                        }
                    }
                }

                _instanceCounts[key] = instances.Count;
            }
        }

        /// <summary>
        /// Verifica event handlers não desinscritos via reflection
        /// </summary>
        public static void CheckEventHandlers(object target, string context = "")
        {
            if (target == null) return;

            var type = target.GetType();
            var eventFields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType));

            foreach (var field in eventFields)
            {
                var value = field.GetValue(target) as Delegate;
                if (value?.GetInvocationList().Length > 0)
                {
                    var count = value.GetInvocationList().Length;
                    if (count > 1)
                    {
                        ConsoleManager.WriteLine($"[PROFILER] ⚠️ {context}.{field.Name}: {count} handlers (possível acúmulo)");
                    }
                }
            }
        }

        /// <summary>
        /// Verifica bindings que podem estar vazando
        /// </summary>
        public static void CheckBindings(FrameworkElement element)
        {
            if (element == null) return;

            // Verifica DataContext que pode estar segurando referências
            if (element.DataContext != null)
            {
                var dcType = element.DataContext.GetType();
                if (!dcType.Name.Contains("ViewModel") && !dcType.IsPrimitive)
                {
                    ConsoleManager.WriteLine($"[PROFILER] ℹ️ {element.GetType().Name} DataContext: {dcType.Name}");
                }
            }
        }

        /// <summary>
        /// Rastreia um objeto específico para ver se é coletado
        /// </summary>
        public static void TrackObject(object obj, string label)
        {
            var typeName = obj.GetType().Name;
            if (!_trackedObjects.ContainsKey(typeName))
            {

                // Típico: 1-10 instâncias rastreadas por tipo
                _trackedObjects[typeName] = new List<WeakReference>(10);
            }

            // Limpa referências mortas
            _trackedObjects[typeName].RemoveAll(wr => !wr.IsAlive);

            // Adiciona nova referência
            _trackedObjects[typeName].Add(new WeakReference(obj));

            ConsoleManager.WriteLine($"[PROFILER] 📊 Rastreando {label}: {typeName} (total: {_trackedObjects[typeName].Count})");
        }

        /// <summary>
        /// Gera relatório detalhado de suspeitos de leak
        /// </summary>
        private static void ReportLeakSuspect(Type type, List<object> instances)
        {
            ConsoleManager.WriteLine($"[PROFILER] 🚨 LEAK DETECTADO: {type.Name}");

            foreach (var instance in instances.Take(3)) // Analisa as 3 primeiras
            {
                CheckEventHandlers(instance, type.Name);

                if (instance is FrameworkElement fe)
                {
                    CheckBindings(fe);

                    // Verifica se tem referências a timers
                    var timerFields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                        .Where(f => f.FieldType.Name.Contains("Timer"));

                    foreach (var timerField in timerFields)
                    {
                        var timerValue = timerField.GetValue(instance);
                        if (timerValue != null)
                        {
                            ConsoleManager.WriteLine($"[PROFILER] ⏱️ {type.Name}.{timerField.Name} = {timerValue.GetType().Name}");
                        }
                    }
                }
            }
        }

        private static List<object> GetAliveInstances(Type type)
        {
            var result = new List<object>();
            // Nota: Isso é uma aproximação - não podemos realmente enumerar o heap
            // mas podemos rastrear objetos que registramos

            if (_trackedObjects.ContainsKey(type.Name))
            {
                result = _trackedObjects[type.Name]
                    .Where(wr => wr.IsAlive)
                    .Select(wr => wr.Target!)
                    .Where(o => o != null)
                    .ToList();
            }

            return result;
        }

        /// <summary>
        /// Força análise imediata
        /// </summary>
        public static void ForceAnalysis()
        {
            ConsoleManager.WriteLine("[PROFILER] 🔍 Análise forçada iniciada...");
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            AnalyzeHeap();
            ConsoleManager.WriteLine("[PROFILER] ✅ Análise concluída");
        }

        /// <summary>
        /// Verifica estáticos que podem estar segurando referências
        /// </summary>
        public static void CheckStaticReferences()
        {
            ConsoleManager.WriteLine("[PROFILER] 🔍 Verificando referências estáticas...");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName?.Contains("KitLugia") == true);

            foreach (var assembly in assemblies)
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .Where(f => !f.FieldType.IsPrimitive && f.FieldType != typeof(string));

                    foreach (var field in staticFields)
                    {
                        var value = field.GetValue(null);
                        if (value != null && !value.GetType().IsValueType)
                        {
                            ConsoleManager.WriteLine($"[PROFILER] 📌 STATIC: {type.Name}.{field.Name} = {value.GetType().Name}");
                        }
                    }
                }
            }
        }
    }
}
