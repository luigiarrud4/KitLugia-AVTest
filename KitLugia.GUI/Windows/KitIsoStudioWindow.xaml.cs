using System.Linq;
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
        private void BtnApplyDebloat_Click(object sender, RoutedEventArgs e) => SetAppxPacks(true);

        private void BtnUnmarkDebloat_Click(object sender, RoutedEventArgs e) => SetAppxPacks(false);

        /// <summary>Marca/desmarca todos os pacotes AppX listados na aba "AppX (40+)".</summary>
        private void SetAppxPacks(bool marked)
        {
            if (PanelAppxPacks == null) return;
            foreach (var cb in PanelAppxPacks.Children.OfType<System.Windows.Controls.CheckBox>())
                cb.IsChecked = marked;
        }

        /// <summary>Carrega um .reg escolhido pelo usuario no campo de registro custom.</summary>
        private void BtnImportReg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar arquivo de registro",
                Filter = "Registro do Windows (*.reg)|*.reg|Todos os arquivos (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                TxtReg.Text = System.IO.File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"N\u00E3o foi poss\u00EDvel ler o arquivo:\n{ex.Message}",
                    "Importar .reg", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearReg_Click(object sender, RoutedEventArgs e) => TxtReg.Clear();
        private void BtnPickDriverFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Selecione a pasta com drivers (.inf)" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtDriverFolder.Text = dlg.SelectedPath;
        }
    }
}
