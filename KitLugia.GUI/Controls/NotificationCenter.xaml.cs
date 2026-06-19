using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using KitLugia.GUI;

// --- CORREÇÕES DE AMBIGUIDADE ---
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;

namespace KitLugia.GUI.Controls
{
    public partial class NotificationCenter : UserControl
    {
        public bool IsOpen { get; private set; } = false;

        public NotificationCenter()
        {
            InitializeComponent();

            // Liga a UI aos dados
            ListNotifications.ItemsSource = NotificationHistoryManager.History;

            // Monitora mudanças para mostrar o aviso "Vazio"
            NotificationHistoryManager.History.CollectionChanged += History_CollectionChanged;
            CheckEmptyState();
        }

        private void History_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            CheckEmptyState();
        }

        private void CheckEmptyState()
        {
            if (PanelEmpty != null)
            {
                PanelEmpty.Visibility = NotificationHistoryManager.History.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        // --- BOTÕES DE AÇÃO ---

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            NotificationHistoryManager.ClearAll();
        }

        private void BtnRemoveItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NotificationItem itemToRemove)
            {
                NotificationHistoryManager.Remove(itemToRemove);
            }
        }

        // --- ANIMAÇÃO ABRIR/FECHAR ---

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            if (IsOpen) return;
            this.Visibility = Visibility.Visible;
            if (this.Resources["OpenMenu"] is Storyboard sb)
            {
                sb.Begin();
                IsOpen = true;
            }
        }

        public void Close()
        {
            if (!IsOpen) return;
            if (this.Resources["CloseMenu"] is Storyboard sb)
            {
                sb.Completed += (s, e) =>
                {
                    this.Visibility = Visibility.Collapsed;
                    IsOpen = false;
                };
                sb.Begin();
            }
        }

        private void OverlayBg_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Close();
        }
    }
}