using System.Windows;
using System.Windows.Controls;

namespace KitLugia.GUI.Controls
{
    public partial class InfoButton : System.Windows.Controls.UserControl
    {
        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.Register(nameof(ToolTipText), typeof(string), typeof(InfoButton),
                new PropertyMetadata(string.Empty, OnToolTipTextChanged));

        public string ToolTipText
        {
            get => (string)GetValue(ToolTipTextProperty);
            set => SetValue(ToolTipTextProperty, value);
        }

        private static void OnToolTipTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (InfoButton)d;
            control.BtnInfo.ToolTip = e.NewValue as string;
        }

        public InfoButton()
        {
            InitializeComponent();
            if (!string.IsNullOrEmpty(ToolTipText))
                BtnInfo.ToolTip = ToolTipText;
        }
    }
}