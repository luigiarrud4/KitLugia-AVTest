using System.Windows;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// Estado vazio padrao do kit (F4 do docs/UI_CONSISTENCY_PLAN.md).
    /// Uso: coloque sobre a area da lista e alterne <see cref="Visibility"/>.
    /// </summary>
    // Qualificado: o projeto referencia WinForms e 'UserControl' fica ambiguo (CS0104).
    public partial class EmptyStateView : System.Windows.Controls.UserControl
    {
        public EmptyStateView()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyStateView),
                new PropertyMetadata("\uD83D\uDD0D", (d, e) => ((EmptyStateView)d).TxtIcon.Text = (string)e.NewValue));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyStateView),
                new PropertyMetadata("Nada por aqui", (d, e) => ((EmptyStateView)d).TxtTitle.Text = (string)e.NewValue));

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(nameof(Hint), typeof(string), typeof(EmptyStateView),
                new PropertyMetadata("Ajuste os filtros ou a pesquisa.", (d, e) => ((EmptyStateView)d).TxtHint.Text = (string)e.NewValue));

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        /// <summary>Mostra ou esconde conforme a contagem.</summary>
        public void Update(int itens)
        {
            Visibility = itens <= 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
