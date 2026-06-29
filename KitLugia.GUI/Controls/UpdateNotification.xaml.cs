using System;
using System.Windows;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    public partial class UpdateNotification : UserControl
    {
        public event Action<UpdateNotification>? Dismissed;
        public event Action? ActionClicked;
        private bool _isDismissing;

        public UpdateNotification()
        {
            InitializeComponent();
        }

        public void SetContent(string title, string message, Action? onAction = null)
        {
            TxtTitle.Text = title.ToUpper();
            TxtMessage.Text = message;
            VersionBorder.Visibility = Visibility.Collapsed;
            ChangelogBorder.Visibility = Visibility.Collapsed;
            TxtOkBtn.Text = "OK";
            if (onAction != null)
                ActionClicked += onAction;
        }

        public void SetUpdateContent(string title, string message, string newVersion, string oldVersion, string? changelog = null)
        {
            TxtTitle.Text = title.ToUpper();
            TxtMessage.Text = message;
            VersionBorder.Visibility = Visibility.Visible;
            bool isReinstall = oldVersion == newVersion;
            TxtVersionLine.Text = $"{oldVersion}  --->  {newVersion}";
            TxtReinstallLabel.Text = isReinstall ? "Reinstalado" : "";
            TxtOkBtn.Text = "ENTENDIDO";
            if (!string.IsNullOrEmpty(changelog))
            {
                ChangelogBorder.Visibility = Visibility.Visible;
                TxtChangelog.Text = changelog;
            }
        }

        public void Dismiss()
        {
            if (_isDismissing) return;
            _isDismissing = true;

            IsHitTestVisible = false;

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
            Opacity = 0;
            Visibility = Visibility.Collapsed;
            Dismissed?.Invoke(this);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ActionClicked?.Invoke();
            Dismiss();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Dismiss();
        }
    }
}
