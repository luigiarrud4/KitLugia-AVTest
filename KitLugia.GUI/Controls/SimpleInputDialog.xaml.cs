using System.Windows;
using System.Windows.Input;

namespace KitLugia.GUI.Controls
{
    public partial class SimpleInputDialog : Window
    {
        public string InputText { get; private set; } = string.Empty;

        public SimpleInputDialog(string title, string message, string defaultValue = "")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtMessage.Text = message;
            TxtInput.Text = defaultValue;
            TxtInput.Focus();
            TxtInput.SelectAll();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            InputText = TxtInput.Text;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TxtInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnOk_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                BtnCancel_Click(sender, e);
            }
        }

        public static string? Show(string title, string message, string defaultValue = "")
        {
            var dlg = new SimpleInputDialog(title, message, defaultValue);
            if (dlg.ShowDialog() == true)
            {
                return dlg.InputText;
            }
            return null;
        }
    }
}
