using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Serviço singleton para rastrear tarefas em segundo plano
    /// Permite que o usuário saiba quando há tarefas rodando mesmo após trocar de aba
    /// </summary>
    public class BackgroundTaskTracker : INotifyPropertyChanged
    {
        private static readonly BackgroundTaskTracker _instance = new BackgroundTaskTracker();
        public static BackgroundTaskTracker Instance => _instance;

        private readonly Dictionary<string, BackgroundTaskInfo> _activeTasks = new Dictionary<string, BackgroundTaskInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BackgroundTaskInfo> _completedTasks = new List<BackgroundTaskInfo>();
        private readonly DispatcherTimer _durationUpdateTimer;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<TaskStatusChangedEventArgs>? TaskStatusChanged;

        public bool HasActiveTasks => _activeTasks.Count > 0;
        public int ActiveTaskCount => _activeTasks.Count;

        private BackgroundTaskTracker()
        {
            _durationUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _durationUpdateTimer.Tick += DurationUpdateTimer_Tick;
            _durationUpdateTimer.Start();
        }

        private void DurationUpdateTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var task in _activeTasks.Values)
            {
                task.NotifyDurationChanged();
            }
        }

        /// <summary>
        /// Registra uma nova tarefa (gera taskId automaticamente)
        /// </summary>
        public string RegisterTask(string taskName, string pageName)
        {
            // Verifica se já existe uma tarefa com o mesmo nome e página (tanto em execução quanto no histórico)
            var existingTask = _activeTasks.Values.FirstOrDefault(t =>
                t.TaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase) &&
                t.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == TaskStatus.Running);

            if (existingTask != null)
            {
                // Incrementa o contador da tarefa existente e retorna o taskId existente
                existingTask.StackCount++;
                existingTask.NotifyStackCountChanged();
                KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Stack incrementada: {taskName} (Página: {pageName}) - Contagem: {existingTask.StackCount}");
                return existingTask.TaskId;
            }

            // Verifica se existe no histórico e move de volta para ativas
            var historyTask = _completedTasks.FirstOrDefault(t =>
                t.TaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase) &&
                t.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase));

            if (historyTask != null)
            {
                // Remove do histórico
                _completedTasks.Remove(historyTask);

                // Reseta a tarefa para execução
                historyTask.Status = TaskStatus.Running;
                historyTask.StartTime = DateTime.Now;
                historyTask.EndTime = null;
                historyTask.StackCount++;
                historyTask.NotifyStackCountChanged();

                // Adiciona de volta às tarefas ativas
                _activeTasks[historyTask.TaskId] = historyTask;

                OnPropertyChanged(nameof(HasActiveTasks));
                OnPropertyChanged(nameof(ActiveTaskCount));
                TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(historyTask, TaskStatus.Running));

                KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Reativada do histórico: {taskName} (Página: {pageName}) - Contagem: {historyTask.StackCount}");
                return historyTask.TaskId;
            }

            string taskId = Guid.NewGuid().ToString();
            var taskInfo = new BackgroundTaskInfo
            {
                TaskId = taskId,
                TaskName = taskName,
                PageName = pageName,
                StartTime = DateTime.Now,
                Status = TaskStatus.Running,
                StackCount = 1
            };

            _activeTasks[taskId] = taskInfo;
            OnPropertyChanged(nameof(HasActiveTasks));
            OnPropertyChanged(nameof(ActiveTaskCount));
            TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(taskInfo, TaskStatus.Running));

            KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Iniciada: {taskName} (Página: {pageName})");
            return taskId;
        }

        /// <summary>
        /// Registra uma nova tarefa com taskId específico
        /// </summary>
        public void RegisterTask(string taskId, string taskName, string pageName)
        {
            // Verifica se já existe uma tarefa com o mesmo nome e página
            var existingTask = _activeTasks.Values.FirstOrDefault(t =>
                t.TaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase) &&
                t.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase) &&
                t.Status == TaskStatus.Running);

            if (existingTask != null)
            {
                // Incrementa o contador da tarefa existente
                existingTask.StackCount++;
                existingTask.NotifyStackCountChanged();
                KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Stack incrementada: {taskName} (Página: {pageName}) - Contagem: {existingTask.StackCount}");
                return;
            }

            // Verifica se existe no histórico e move de volta para ativas
            var historyTask = _completedTasks.FirstOrDefault(t =>
                t.TaskName.Equals(taskName, StringComparison.OrdinalIgnoreCase) &&
                t.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase));

            if (historyTask != null)
            {
                // Remove do histórico
                _completedTasks.Remove(historyTask);

                // Reseta a tarefa para execução
                historyTask.Status = TaskStatus.Running;
                historyTask.StartTime = DateTime.Now;
                historyTask.EndTime = null;
                historyTask.StackCount++;
                historyTask.NotifyStackCountChanged();

                // Adiciona de volta às tarefas ativas
                _activeTasks[historyTask.TaskId] = historyTask;

                OnPropertyChanged(nameof(HasActiveTasks));
                OnPropertyChanged(nameof(ActiveTaskCount));
                TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(historyTask, TaskStatus.Running));

                KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Reativada do histórico: {taskName} (Página: {pageName}) - Contagem: {historyTask.StackCount}");
                return;
            }

            var taskInfo = new BackgroundTaskInfo
            {
                TaskId = taskId,
                TaskName = taskName,
                PageName = pageName,
                StartTime = DateTime.Now,
                Status = TaskStatus.Running,
                StackCount = 1
            };

            _activeTasks[taskId] = taskInfo;
            OnPropertyChanged(nameof(HasActiveTasks));
            OnPropertyChanged(nameof(ActiveTaskCount));
            TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(taskInfo, TaskStatus.Running));

            KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Iniciada: {taskName} (Página: {pageName})");
        }

        /// <summary>
        /// Atualiza o progresso de uma tarefa
        /// </summary>
        public void UpdateTaskProgress(string taskId, string progress)
        {
            if (_activeTasks.TryGetValue(taskId, out var taskInfo))
            {
                taskInfo.Progress = progress;
                taskInfo.LastUpdateTime = DateTime.Now;
                TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(taskInfo, TaskStatus.ProgressUpdate));
            }
        }

        /// <summary>
        /// Completa uma tarefa
        /// </summary>
        public void CompleteTask(string taskId, bool success = true, string? message = null)
        {
            if (_activeTasks.TryGetValue(taskId, out var taskInfo))
            {
                taskInfo.StackCount--;

                if (taskInfo.StackCount <= 0)
                {
                    taskInfo.Status = success ? TaskStatus.Completed : TaskStatus.Failed;
                    taskInfo.EndTime = DateTime.Now;
                    taskInfo.Message = message;

                    // Adiciona ao histórico de tarefas concluídas
                    _completedTasks.Insert(0, taskInfo);

                    // Mantém apenas as últimas 20 tarefas no histórico
                    if (_completedTasks.Count > 20)
                    {
                        _completedTasks.RemoveAt(_completedTasks.Count - 1);
                    }

                    // Remove das tarefas ativas
                    _activeTasks.Remove(taskId);

                    OnPropertyChanged(nameof(HasActiveTasks));
                    OnPropertyChanged(nameof(ActiveTaskCount));

                    TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(taskInfo, taskInfo.Status));

                    KitLugia.Core.Logger.Log($"[BACKGROUND TASK] {(success ? "Concluída" : "Falhou")}: {taskInfo.TaskName} (Página: {taskInfo.PageName})");
                }
                else
                {
                    taskInfo.NotifyStackCountChanged();
                    KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Stack decrementada: {taskInfo.TaskName} (Página: {taskInfo.PageName}) - Contagem: {taskInfo.StackCount}");
                }
            }
        }

        /// <summary>
        /// Remove uma tarefa
        /// </summary>
        private void RemoveTask(string taskId)
        {
            if (_activeTasks.Remove(taskId))
            {
                OnPropertyChanged(nameof(HasActiveTasks));
                OnPropertyChanged(nameof(ActiveTaskCount));
            }
        }

        /// <summary>
        /// Obtém informações de uma tarefa específica
        /// </summary>
        public BackgroundTaskInfo? GetTask(string taskId)
        {
            return _activeTasks.TryGetValue(taskId, out var taskInfo) ? taskInfo : null;
        }

        /// <summary>
        /// Obtém todas as tarefas (ativas + histórico recente)
        /// </summary>
        public IEnumerable<BackgroundTaskInfo> GetAllTasks()
        {
            return _activeTasks.Values.Concat(_completedTasks);
        }

        /// <summary>
        /// Limpa o histórico de tarefas concluídas
        /// </summary>
        public void ClearCompletedHistory()
        {
            _completedTasks.Clear();
        }

        /// <summary>
        /// Obtém tarefas de uma página específica
        /// </summary>
        public IEnumerable<BackgroundTaskInfo> GetTasksForPage(string pageName)
        {
            foreach (var task in _activeTasks.Values)
            {
                if (task.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase))
                {
                    yield return task;
                }
            }
        }

        /// <summary>
        /// Cancela uma tarefa específica
        /// </summary>
        public void CancelTask(string taskId)
        {
            if (_activeTasks.TryGetValue(taskId, out var taskInfo))
            {
                taskInfo.Status = TaskStatus.Cancelled;
                taskInfo.EndTime = DateTime.Now;
                TaskStatusChanged?.Invoke(this, new TaskStatusChangedEventArgs(taskInfo, TaskStatus.Cancelled));
                KitLugia.Core.Logger.Log($"[BACKGROUND TASK] Cancelada: {taskInfo.TaskName} (Página: {taskInfo.PageName})");
                RemoveTask(taskId);
            }
        }

        /// <summary>
        /// Cancela todas as tarefas de uma página
        /// </summary>
        public void CancelTasksForPage(string pageName)
        {
            var tasksToCancel = new List<string>();
            foreach (var kvp in _activeTasks)
            {
                if (kvp.Value.PageName.Equals(pageName, StringComparison.OrdinalIgnoreCase))
                {
                    tasksToCancel.Add(kvp.Key);
                }
            }

            foreach (var taskId in tasksToCancel)
            {
                CancelTask(taskId);
            }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Informações sobre uma tarefa em segundo plano
    /// </summary>
    public class BackgroundTaskInfo : INotifyPropertyChanged
    {
        public string TaskId { get; set; } = "";
        public string TaskName { get; set; } = "";
        public string PageName { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public TaskStatus Status { get; set; }
        public string Progress { get; set; } = "";
        public string? Message { get; set; }
        public int StackCount { get; set; } = 1;

        private long _lastAnimationTick = 0;

        public TimeSpan Duration => EndTime.HasValue ? EndTime.Value - StartTime : DateTime.Now - StartTime;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void NotifyDurationChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
        }

        public void NotifyStackCountChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StackCount)));

            // Animação de "pop" no contador (proteção contra spam)
            long currentTick = DateTime.Now.Ticks;
            if (currentTick - _lastAnimationTick > 10000000) // 1 segundo em Ticks (mais lento que notificações)
            {
                _lastAnimationTick = currentTick;
                TriggerStackAnimation?.Invoke(this);
            }
        }

        // Evento para disparar animação de "pop" no UI
        public event Action<BackgroundTaskInfo>? TriggerStackAnimation;
    }

    /// <summary>
    /// Status de uma tarefa em segundo plano
    /// </summary>
    public enum TaskStatus
    {
        Running,
        ProgressUpdate,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Argumentos do evento TaskStatusChanged
    /// </summary>
    public class TaskStatusChangedEventArgs : EventArgs
    {
        public BackgroundTaskInfo TaskInfo { get; }
        public TaskStatus Status { get; }

        public TaskStatusChangedEventArgs(BackgroundTaskInfo taskInfo, TaskStatus status)
        {
            TaskInfo = taskInfo;
            Status = status;
        }
    }
}
