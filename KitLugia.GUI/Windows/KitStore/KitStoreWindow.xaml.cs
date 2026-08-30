using System.Windows;
using System.Windows.Input;

namespace KitLugia.GUI.Windows.KitStore
{
    public partial class KitStoreWindow : Window
    {
        public KitStoreWindow()
        {
            InitializeComponent();
            MainFrame.Content = new Pages.WindowsSettings.StoreRemakePage();
            StateChanged += (s, e) =>
            {
                IconMaximize.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            };
        }

        private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { }
        }

        public static void ShowStandalone()
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            var w = new KitStoreWindow();
            if (owner != null && owner.IsVisible && owner != w) w.Owner = owner;
            w.Show();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
