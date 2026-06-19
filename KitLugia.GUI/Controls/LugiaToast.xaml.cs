using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    // AQUI ESTAVA O ERRO: A linha "public enum NotificationType..." foi REMOVIDA.
    // O sistema agora vai ler o Enum do arquivo Enums.cs que você criou antes.

    public partial class LugiaToast : UserControl, INotifyPropertyChanged
    {
        public event Action<LugiaToast>? Dismissed;
        private bool _isDismissing = false;
        private DispatcherTimer _lifeTimer;

        // Variáveis para o contador e proteção contra spam visual
        private int _count = 1;
        private long _lastAnimationTick = 0;

        // Identificador único para saber se é duplicata
        public string NotificationId { get; set; } = string.Empty;
        public NotificationType ToastType { get; private set; }

        public LugiaToast()
        {
            InitializeComponent();
            this.DataContext = this;

            // Define o tempo de vida (4 segundos)
            _lifeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _lifeTimer.Tick += (s, e) => Dismiss();
        }

        public void SetContent(string title, string message, NotificationType type)
        {
            TxtTitle.Text = title.ToUpper();
            TxtMessage.Text = message;
            ToastType = type;
            SetValue(ToastTypeProperty, type);

            // Gera o ID. Se for apenas "Info" genérico, usa um ID fixo para agrupar todas as infos.
            if (type == NotificationType.Info && title == "AGUARDE")
                NotificationId = "GENERIC_WAIT";
            else
                NotificationId = $"{type}|{title}|{message}";

            _lifeTimer.Start();
        }

        public void UpdateMessage(string newMessage)
        {
            TxtMessage.Text = newMessage;
            ResetTimer();
        }

        // LÓGICA DO CONTADOR (x2, x3...)
        public void IncrementCounter()
        {
            _count++;
            TxtCount.Text = $"x{_count}";
            BadgeCounter.Visibility = Visibility.Visible;

            // Reinicia o timer para a notificação ficar mais tempo na tela
            ResetTimer();

            // --- PROTEÇÃO CONTRA AUTO CLICKER / SPAM ---
            long currentTick = DateTime.Now.Ticks;
            if (currentTick - _lastAnimationTick > 1000000) // 100ms em Ticks
            {
                _lastAnimationTick = currentTick;

                // Animação leve de "Pop" no contador
                var scaleTrans = new ScaleTransform(1, 1);
                BadgeCounter.RenderTransform = scaleTrans;

                // CORREÇÃO AQUI: Usando System.Windows.Point explicitamente
                BadgeCounter.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

                var anim = new DoubleAnimation(1.3, 1.0, TimeSpan.FromMilliseconds(150));
                scaleTrans.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                scaleTrans.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        }

        private void ResetTimer()
        {
            if (_isDismissing) return;
            _lifeTimer.Stop();
            _lifeTimer.Start();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            Dismiss(); // Clique fecha instantaneamente
        }

        public void Dismiss()
        {
            if (_isDismissing) return;
            _isDismissing = true;
            _lifeTimer.Stop();

            this.IsHitTestVisible = false; // Mouse passa através enquanto some

            if (Resources["SlideOutAnimation"] is Storyboard sb)
            {
                sb.Completed -= OnSlideOutCompleted;
                sb.Completed += OnSlideOutCompleted;
                sb.Begin(this);
            }
            else
            {
                OnSlideOutCompleted(null, EventArgs.Empty);
            }
        }

        private void OnSlideOutCompleted(object? sender, EventArgs e)
        {
            this.Opacity = 0;
            this.Visibility = Visibility.Collapsed;
            Dismissed?.Invoke(this);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }

        public void TransitionToType(NotificationType newType, string newTitle, string newMessage)
        {
            // Para o timer antigo
            _lifeTimer.Stop();

            // Atualiza propriedades
            ToastType = newType;
            SetValue(ToastTypeProperty, newType);
            TxtTitle.Text = newTitle.ToUpper();
            TxtMessage.Text = newMessage;

            // Dispara animação de transição
            if (Resources["TypeTransitionAnimation"] is Storyboard sb)
            {
                sb.Begin(this);
            }

            // Reinicia timer com tempo maior para o usuário ver o resultado
            _lifeTimer.Interval = TimeSpan.FromSeconds(6);
            _lifeTimer.Start();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public static readonly DependencyProperty ToastTypeProperty = DependencyProperty.Register(
            "ToastType", typeof(NotificationType), typeof(LugiaToast), new PropertyMetadata(NotificationType.Info));
    }
}