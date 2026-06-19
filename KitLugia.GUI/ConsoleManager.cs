using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

// --- CORREÇÃO CRÍTICA: Resolve a ambiguidade ---
using Application = System.Windows.Application;

namespace KitLugia.GUI
{
    public static class ConsoleManager
    {
        // A lista de linhas de texto que aparecerá no terminal
        public static ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();

        // Evento para avisar a UI para rolar para o final
        public static event Action? OnLogAdded;


        public static bool IsDebugEnabled { get; set; } = false;

        private static readonly ConcurrentQueue<string> _pending = new();
        private static bool _flushScheduled;
        private const int MaxLines = 500;
        private const int BatchSize = 50;

        private static void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;

            if (Application.Current?.Dispatcher == null) return;
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
                Logs.Add(line);
            }

            // Trim excess
            if (!KitLugia.Core.Logger.DisableOutputLimit && Logs.Count > MaxLines)
            {
                int remove = Logs.Count - MaxLines;
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
            if (Application.Current?.Dispatcher == null) return;
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Logs.Clear();
                _pending.Clear();
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

        // Comando para controlar limite de logs via console
        public static void HandleConsoleCommand(string command)
        {
            if (command.Trim().ToLower() == "loglimit")
            {
                KitLugia.Core.Logger.ToggleOutputLimit();
            }
        }
    }
}