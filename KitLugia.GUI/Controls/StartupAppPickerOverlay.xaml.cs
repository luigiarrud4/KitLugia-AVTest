using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using KitLugia.Core;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace KitLugia.GUI.Controls
{
    public partial class StartupAppPickerOverlay : WpfUserControl
    {
        public event Action<string>? AppSelected;

        public StartupAppPickerOverlay(List<StartupAppDetails> apps)
        {
            InitializeComponent();

            var items = new ObservableCollection<StartupAppViewModel>();
            foreach (var app in apps)
            {
                var vm = new StartupAppViewModel(app);
                vm.SelectCommand = new RelayCommand(() =>
                {
                    AppSelected?.Invoke(app.Name);
                    Close();
                });
                items.Add(vm);
            }
            AppList.ItemsSource = items;
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
                overlayContainer.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class StartupAppViewModel
    {
        public StartupAppDetails App { get; }
        public string Name => App.Name;
        public string FullCommand => App.FullCommand;
        public string Location => App.Location;
        public ICommand? SelectCommand { get; set; }

        public StartupAppViewModel(StartupAppDetails app)
        {
            App = app;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public RelayCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
