using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KitLugia.GUI.Controls
{
    public partial class TaskStatusIndicator : System.Windows.Controls.UserControl
    {
        public TaskStatusIndicator()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                var rot = new DoubleAnimation
                {
                    From = 0, To = 360,
                    Duration = new Duration(TimeSpan.FromSeconds(1.2)),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, rot);
            };
        }

        public void StartSpinning()
        {
            SpinnerArc.Visibility = Visibility.Visible;
            SpinnerRotateHost.Visibility = Visibility.Visible;
            IconSuccess.Visibility = Visibility.Collapsed;
            IconFailure.Visibility = Visibility.Collapsed;
        }

        public void Complete(bool success)
        {
            SpinnerArc.Visibility = Visibility.Collapsed;
            SpinnerRotateHost.Visibility = Visibility.Collapsed;
            IconSuccess.Visibility = success ? Visibility.Visible : Visibility.Collapsed;
            IconFailure.Visibility = success ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Reset()
        {
            SpinnerArc.Visibility = Visibility.Collapsed;
            SpinnerRotateHost.Visibility = Visibility.Collapsed;
            IconSuccess.Visibility = Visibility.Collapsed;
            IconFailure.Visibility = Visibility.Collapsed;
        }
    }
}
