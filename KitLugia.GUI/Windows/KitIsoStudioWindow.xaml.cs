using System.Windows;
using System.Windows.Input;

namespace KitLugia.GUI.Windows
{
    public partial class KitIsoStudioWindow : Window
    {
        public KitIsoStudioWindow()
        {
            InitializeComponent();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && e.ClickCount == 1)
                try { DragMove(); } catch { }
        }

        private void BtnToggleMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
        private void BtnApplyDebloat_Click(object sender, RoutedEventArgs e) { }
        private void BtnPickDriverFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Selecione a pasta com drivers (.inf)" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtDriverFolder.Text = dlg.SelectedPath;
        }
    }
}
