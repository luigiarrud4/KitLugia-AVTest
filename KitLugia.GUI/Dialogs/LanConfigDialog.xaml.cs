using System;
using System.Windows;

namespace KitLugia.GUI.Dialogs
{
    public partial class LanConfigDialog : Window
    {
        public int Port { get; private set; }

        public LanConfigDialog()
        {
            InitializeComponent();
            TxtPort.Focus();
        }

        private void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtPort.Text, out int port) && port > 0 && port <= 65535)
            {
                Port = port;
                DialogResult = true;
                Close();
            }
            else
            {
                System.Windows.MessageBox.Show("Por favor, digite uma porta válida (1-65535)", "Porta Inválida", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
