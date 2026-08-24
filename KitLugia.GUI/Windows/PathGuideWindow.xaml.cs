using System.Windows;
using System.Windows.Input;

namespace KitLugia.GUI.Windows
{
    /// <summary>
    /// Janela "Guia do Explorador de PATH": legenda de cores, significado dos
    /// avisos e como o kit localiza programas ausentes (indexador USN/MFT
    /// embutido ou varredura direta).
    /// </summary>
    public partial class PathGuideWindow : Window
    {
        public PathGuideWindow()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnClose_MouseDown(object sender, MouseButtonEventArgs e) => Close();
    }
}
