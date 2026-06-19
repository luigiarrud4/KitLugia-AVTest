using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

// CORREÇÃO DE AMBIGUIDADE (WPF vs WinForms)
using UserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    public partial class LugiaConfirmationOverlay : UserControl
    {
        // TaskCompletionSource permite que a gente "espere" (await) o clique do botão
        private TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        public LugiaConfirmationOverlay(string message)
        {
            InitializeComponent();
            TxtMessage.Text = message;
        }

        // Método que a MainWindow vai chamar para esperar a resposta
        public Task<bool> WaitForUserSelection()
        {
            return _tcs.Task;
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            _tcs.TrySetResult(true);
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            _tcs.TrySetResult(false);
        }
    }
}