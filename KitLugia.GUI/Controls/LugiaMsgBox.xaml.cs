using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    public partial class LugiaMsgBox : UserControl
    {
        // Evento que avisa a janela principal que o botão foi clicado
        public event RoutedEventHandler? OkClicked;

        public LugiaMsgBox()
        {
            InitializeComponent();
        }

        // Método para atualizar o texto sem recriar o controle
        public void SetContent(string message, string title)
        {
            TxtMessage.Text = message;
            if (!string.IsNullOrEmpty(title))
                TxtTitle.Text = title.ToUpper();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Dispara o evento para quem estiver ouvindo (MainWindow)
            OkClicked?.Invoke(this, e);
        }
    }
}