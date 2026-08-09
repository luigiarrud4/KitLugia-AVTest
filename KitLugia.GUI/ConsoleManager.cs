using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using KitLugia.GUI.Logging;

// --- CORREÇÃO CRÍTICA: Resolve a ambiguidade ---
using Application = System.Windows.Application;

namespace KitLugia.GUI
{
    public static class ConsoleManager
    {
        // Linhas que aparecem no terminal. Limitada a um teto alto (MaxMirrorLines)
        // apenas para a UI virtualizada nunca precisar materializar tudo.
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        // Evento para avisar a UI para rolar para o final
        public static event Action? OnLogAdded;

        public static bool IsDebugEnabled { get; set; } = false;

        private static readonly ConcurrentQueue<string> _pending = new();
        private static bool _flushScheduled;
        private const int BatchSize = 50;
        // Teto do espelho em memória. O arquivo guarda tudo (LogStore) — "copiar tudo"
        // não depende deste teto. 20k linhas na memória = ~3 MB, sem estourar RAM.
        private const int MaxMirrorLines = LogStore.MaxInMemoryLines;

        private static void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;

            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushBatch));
        }

        private static void FlushBatch()
        {
            _flushScheduled = false;
            var batch = new List<string>(BatchSize);

            while (_pending.TryDequeue(out var msg))
            {
                batch.Add(msg);
                if (batch.Count >= BatchSize) break;
            }

            if (batch.Count == 0) return;

            foreach (var line in batch)
            {
                // Sempre persiste tudo no disco (completo, sem limite artificial de 500).
                LogStore.AppendLine(line);
                Logs.Add(line);
            }

            // Trim do espelho da UI: remove do INÍCIO mantendo as mais recentes.
            if (Logs.Count > MaxMirrorLines)
            {
                int remove = Logs.Count - MaxMirrorLines;
                for (int i = 0; i < remove; i++)
                    Logs.RemoveAt(0);
            }

            OnLogAdded?.Invoke();
        }

        public static void WriteLine(string message)
        {
            _pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
            ScheduleFlush();
        }

        public static void WriteError(string error)
        {
            _pending.Enqueue($"[{DateTime.Now:HH:mm:ss}] [ERRO] {error}");
            ScheduleFlush();
        }

        public static void Clear()
        {
            if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.HasShutdownFinished) return;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Logs.Clear();
                _pending.Clear();
                LogStore.Clear();
            }));
        }

        // Método para otimizar performance - retorna logs recentes
        public static List<string> GetRecentLogs(int count)
        {
            var recentLogs = new List<string>();
            int startIndex = Math.Max(0, Logs.Count - count);

            for (int i = startIndex; i < Logs.Count; i++)
            {
                recentLogs.Add(Logs[i]);
            }

            return recentLogs;
        }

        // O limite de 500 linhas foi substituído pela virtualização + LogStore completo.
        // Mantido pelo console por compatibilidade; agora informa a realidade.
        public static void HandleConsoleCommand(string command)
        {
            if (command.Trim().ToLower() == "loglimit")
            {
                WriteLine("🔓 Logs são ILIMITADOS por design: arquivo completo em disco + virtualização na interface.");
                WriteLine($"📁 Arquivo de log: {LogStore.FilePath}");
            }
        }
    }
}