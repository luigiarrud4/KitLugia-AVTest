using System.Windows;
using System.Windows.Input;

namespace KitLugia.GUI.Windows
{
    public partial class StoreRemakeWindow : Window
    {
        public StoreRemakeWindow()
        {
            InitializeComponent();
            MainFrame.Content = new Pages.WindowsSettings.StoreRemakePage();
            // Ao contrário do TaskManager (ShowInTaskbar=False, minimize bloqueado), Store PODE minimizar
            StateChanged += (s, e) =>
            {
                IconMaximize.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
