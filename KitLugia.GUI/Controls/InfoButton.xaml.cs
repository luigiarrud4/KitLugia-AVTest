using System.Windows;
using System.Windows.Controls;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// Icone de informacao padrao do KitLugia (ℹ dourado, hover realca).
    /// Uso: controls:InfoButton ToolTipText="ajuda" — o tooltip e renderizado
    /// com quebra de linha + largura maxima + fundo escuro (legivel em textos longos).
    /// </summary>
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
            ((InfoButton)d).ApplyTip(e.NewValue as string);
        }

        public InfoButton()
        {
            InitializeComponent();
            ApplyTip(ToolTipText);
        }

        private void ApplyTip(string? text)
        {
            if (BtnInfo == null) return;

            // Conteudo STRING (igual aos botoes de info do TweaksPage/GPU que funcionam).
            // Montar Border/ToolTip customizado em codigo fazia o tooltip nao abrir.
            // Quebras de linha: usar &#x0a; no ToolTipText (o WPF renderiza como linhas).
            BtnInfo.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
