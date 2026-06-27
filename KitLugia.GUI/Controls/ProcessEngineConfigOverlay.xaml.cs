using System.Windows;
using KitLugia.GUI.Services;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    public partial class ProcessEngineConfigOverlay : WpfUserControl
    {
        public event Action<TrayIconService.ProcessEngineConfig>? ConfigSaved;
        public event Action? OverlayClosed;

        private readonly TrayIconService.ProcessEngineConfig _config;
        private readonly string _processName;

        public ProcessEngineConfigOverlay(string processName, TrayIconService.ProcessEngineConfig config)
        {
            InitializeComponent();
            _processName = processName;
            _config = config;
            LoadConfig();
        }

        public void Open()
        {
            Visibility = Visibility.Visible;
        }

        private void Close()
        {
            if (Parent is System.Windows.Controls.Grid overlayContainer)
            {
                overlayContainer.Children.Remove(this);
                if (overlayContainer.Children.OfType<UIElement>().All(c => c.Visibility != Visibility.Visible))
                    overlayContainer.Visibility = Visibility.Collapsed;
            }
            OverlayClosed?.Invoke();
        }

        private void LoadConfig()
        {
            int cpuIdx = _config.CpuPriority?.ToLowerInvariant() switch
            {
                "idle" => 0, "belownormal" => 1, "normal" => 2,
                "abovenormal" => 3, "high" => 4, "realtime" => 5, _ => 4
            };
            CboCpuPriority.SelectedIndex = cpuIdx;
            CboEfficiencyMode.SelectedIndex = _config.ThreadEfficiencyMode ? 1 : 0;
            CboIoPriority.SelectedIndex = _config.IoPriorityLevel switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 1 };
            CboPagePriority.SelectedIndex = _config.PagePriorityLevel >= 1 ? 1 : 0;
            CboMemoryPriority.SelectedIndex = _config.ThreadMemoryPriority switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 0 };

            ChkTimerBoost.IsChecked = _config.TimerBoost;
            ChkNetworkBoost.IsChecked = _config.NetworkBoost;
            ChkProBalance.IsChecked = _config.ProBalance;
            TxtProBalanceThreshold.Text = _config.ProBalanceCpuThreshold.ToString();
            PanelProBalanceThreshold.IsEnabled = _config.ProBalance;
            ChkGameClass.IsChecked = _config.GameClassInfo;
            ChkWin32Priority.IsChecked = _config.Win32PrioritySeparation;
            ChkEcoQoS.IsChecked = _config.EcoQoSEnabled;
        }

        private void ChkProBalance_Checked(object sender, RoutedEventArgs e)
        {
            PanelProBalanceThreshold.IsEnabled = ChkProBalance.IsChecked == true;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _config.CpuPriority = CboCpuPriority.SelectedIndex switch
            {
                0 => "Idle", 1 => "BelowNormal", 2 => "Normal",
                3 => "AboveNormal", 4 => "High", 5 => "RealTime", _ => "High"
            };
            _config.ThreadEfficiencyMode = CboEfficiencyMode.SelectedIndex == 1;
            _config.IoPriorityLevel = CboIoPriority.SelectedIndex;
            _config.PagePriorityLevel = CboPagePriority.SelectedIndex >= 1 ? 1 : 0;
            _config.ThreadMemoryPriority = CboMemoryPriority.SelectedIndex;
            _config.TimerBoost = ChkTimerBoost.IsChecked == true;
            _config.NetworkBoost = ChkNetworkBoost.IsChecked == true;
            _config.ProBalance = ChkProBalance.IsChecked == true;
            if (int.TryParse(TxtProBalanceThreshold.Text, out int cpuThr) && cpuThr >= 1 && cpuThr <= 100)
                _config.ProBalanceCpuThreshold = cpuThr;
            _config.GameClassInfo = ChkGameClass.IsChecked == true;
            _config.Win32PrioritySeparation = ChkWin32Priority.IsChecked == true;
            _config.EcoQoSEnabled = ChkEcoQoS.IsChecked == true;

            ConfigSaved?.Invoke(_config);
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
