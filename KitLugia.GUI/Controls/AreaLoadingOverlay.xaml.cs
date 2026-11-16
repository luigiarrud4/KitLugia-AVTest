using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KitLugia.GUI.Controls
{
    /// <summary>
    /// Overlay de area oficial do kit (Variante A, docs/UI_PROGRESS.md):
    /// scrim #CC000000 + Win11SpinnerTemplate + mensagem branca + linha de
    /// progresso dourada opcional. Substitui os markups inline repetidos nas paginas.
    /// </summary>
    // Qualificado: o projeto referencia WinForms e 'UserControl' fica ambiguo (CS0104).
    public partial class AreaLoadingOverlay : System.Windows.Controls.UserControl
    {
        public AreaLoadingOverlay()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyMessagePulse(AnimateMessage);
        }

        /// <summary>Spinner interno (o template ja se anima no Loaded; exposto p/ codigo legado).</summary>
        public ContentControl Spinner => SpinnerHost;

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(nameof(Message), typeof(string), typeof(AreaLoadingOverlay),
                new PropertyMetadata("", (d, e) => ((AreaLoadingOverlay)d).MessageText.Text = (string)e.NewValue ?? ""));

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(AreaLoadingOverlay),
                new PropertyMetadata("", (d, e) =>
                {
                    var c = (AreaLoadingOverlay)d;
                    var text = (string)e.NewValue ?? "";
                    c.SubtitleText.Text = text;
                    c.SubtitleText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                }));

        public static readonly DependencyProperty ProgressProperty =
            DependencyProperty.Register(nameof(Progress), typeof(string), typeof(AreaLoadingOverlay),
                new PropertyMetadata("", (d, e) =>
                {
                    var c = (AreaLoadingOverlay)d;
                    var text = (string)e.NewValue ?? "";
                    c.ProgressText.Text = text;
                    c.ProgressText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                }));

        public static readonly DependencyProperty AnimateMessageProperty =
            DependencyProperty.Register(nameof(AnimateMessage), typeof(bool), typeof(AreaLoadingOverlay),
                new PropertyMetadata(false, (d, e) => ((AreaLoadingOverlay)d).ApplyMessagePulse((bool)e.NewValue)));

        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        /// <summary>Subtitulo cinza abaixo da mensagem; vazio/nulo oculta.</summary>
        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        /// <summary>Linha de progresso dourada; string vazia/nula oculta.</summary>
        public string Progress
        {
            get => (string)GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        /// <summary>Pulso de opacidade 1.0-&gt;0.55 (auto-reverse, 1.1s, infinito) na mensagem.</summary>
        public bool AnimateMessage
        {
            get => (bool)GetValue(AnimateMessageProperty);
            set => SetValue(AnimateMessageProperty, value);
        }

        private void ApplyMessagePulse(bool on)
        {
            if (MessageText == null) return;
            if (on)
            {
                var pulse = new DoubleAnimation(1.0, 0.55, TimeSpan.FromSeconds(1.1))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                MessageText.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                MessageText.BeginAnimation(OpacityProperty, null);
                MessageText.Opacity = 1.0;
            }
        }
    }
}