using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// UserControl reutilizável para diálogos modais
    /// Uso:
    /// <ModalDialog Title="Título do Modal" DialogMaxWidth="600" DialogMaxHeight="700">
    ///     <StackPanel>
    ///         <!-- Seu conteúdo aqui -->
    ///     </StackPanel>
    /// </ModalDialog>
    /// 
    /// No code-behind:
    /// var modal = new ModalDialog { Title = "Título", Content = seuConteudo };
    /// modal.Closed += (s, e) => { /* Ao fechar */ };
    /// </summary>
    public partial class ModalDialog : UserControl
    {
        // Dependency Properties
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(ModalDialog), 
                new PropertyMetadata("TÍTULO"));

        public static readonly DependencyProperty DialogMaxWidthProperty =
            DependencyProperty.Register("DialogMaxWidth", typeof(double), typeof(ModalDialog), 
                new PropertyMetadata(600.0));

        public static readonly DependencyProperty DialogMaxHeightProperty =
            DependencyProperty.Register("DialogMaxHeight", typeof(double), typeof(ModalDialog), 
                new PropertyMetadata(700.0));

        // Properties
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public double DialogMaxWidth
        {
            get => (double)GetValue(DialogMaxWidthProperty);
            set => SetValue(DialogMaxWidthProperty, value);
        }

        public double DialogMaxHeight
        {
            get => (double)GetValue(DialogMaxHeightProperty);
            set => SetValue(DialogMaxHeightProperty, value);
        }

        // Event
        public event EventHandler? Closed;

        public ModalDialog()
        {
            InitializeComponent();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            OnClosed();
        }

        protected virtual void OnClosed()
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Define o conteúdo do modal
        /// </summary>
        public void SetContent(UIElement content)
        {
            ContentPresenter.Content = content;
        }

        /// <summary>
        /// Fecha o modal programaticamente
        /// </summary>
        public void Close()
        {
            OnClosed();
        }
    }
}
