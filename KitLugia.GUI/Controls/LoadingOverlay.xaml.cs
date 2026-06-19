using System;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.GUI.Controls
{
    public partial class LoadingOverlay : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(string), typeof(LoadingOverlay), 
                new PropertyMetadata("Processando...", OnMessageChanged));

        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LoadingOverlay overlay)
            {
                overlay.LoadingText.Text = e.NewValue as string ?? "Processando...";
            }
        }

        public LoadingOverlay()
        {
            InitializeComponent();
        }
    }
}
