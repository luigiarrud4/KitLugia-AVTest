using System.Windows;

namespace KitLugia.WinPE
{
    public partial class InputDialog : Window
    {
        public string? Value { get; private set; }

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputBox.Text = defaultValue;
            InputBox.Focus();
        }

        private void BtnOk_Click(object _, RoutedEventArgs e)
        {
            Value = InputBox.Text;
            DialogResult = true;
        }

        private void BtnCancel_Click(object _, RoutedEventArgs e)
        {
            Value = null;
            DialogResult = false;
        }

        public static string? Show(string title, string prompt, string defaultValue = "")
        {
            var dlg = new InputDialog(title, prompt, defaultValue);
            dlg.Owner = Application.Current.MainWindow;
            return dlg.ShowDialog() == true ? dlg.Value : null;
        }
    }
}
