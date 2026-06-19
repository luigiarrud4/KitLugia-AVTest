using System;
using System.Collections.ObjectModel;
using System.Windows;
using KitLugia.GUI.Controls;
using Application = System.Windows.Application;

namespace KitLugia.GUI
{
    public class NotificationItem
    {
        // ID único para poder remover itens específicos
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public NotificationType Type { get; set; }

        public string TimeString => Timestamp.ToString("HH:mm");

        public string Icon
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "✅",
                    NotificationType.Error => "❌",
                    NotificationType.Info => "ℹ️",
                    _ => "📝"
                };
            }
        }

        // Cor para o XAML (Borda lateral)
        public string ColorHex
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "#4CAF50", // Verde
                    NotificationType.Error => "#FF5555",   // Vermelho
                    NotificationType.Info => "#FFD700",    // Dourado
                    _ => "#FFFFFF"
                };
            }
        }

        // Cor de fundo leve para diferenciar (Opcional, usado no XAML)
        public string BackgroundHex
        {
            get
            {
                return Type switch
                {
                    NotificationType.Success => "#104CAF50",
                    NotificationType.Error => "#10FF5555",
                    NotificationType.Info => "#10FFD700",
                    _ => "#1F1F1F"
                };
            }
        }
    }

    public static class NotificationHistoryManager
    {
        private static ObservableCollection<NotificationItem> _history = new ObservableCollection<NotificationItem>();

        public static ObservableCollection<NotificationItem> History => _history;

        // Evento para avisar a Janela Principal que o contador mudou
        public static event Action? OnCountChanged;

        public static void Add(string title, string message, NotificationType type)
        {
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _history.Insert(0, new NotificationItem
                    {
                        Title = title,
                        Message = message,
                        Type = type,
                        Timestamp = DateTime.Now
                    });

                    if (_history.Count > 50)
                    {
                        _history.RemoveAt(_history.Count - 1);
                    }

                    OnCountChanged?.Invoke();
                });
            }
        }

        public static void Remove(NotificationItem item)
        {
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_history.Contains(item))
                    {
                        _history.Remove(item);
                        OnCountChanged?.Invoke();
                    }
                });
            }
        }

        public static void ClearAll()
        {
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _history.Clear();
                    OnCountChanged?.Invoke();
                });
            }
        }
    }
}