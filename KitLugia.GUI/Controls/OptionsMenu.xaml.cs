using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace KitLugia.GUI.Controls
{
    public partial class OptionsMenu : System.Windows.Controls.UserControl
    {
        public class OptionItem
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool IsSelected { get; set; }
        }

        public List<OptionItem> SelectedOptions { get; private set; }

        public event RoutedEventHandler? ApplyClicked;
        public event RoutedEventHandler? CancelClicked;

        public OptionsMenu()
        {
            InitializeComponent();
            SelectedOptions = new List<OptionItem>();
            
            ApplyButton.Click += (s, e) => ApplyClicked?.Invoke(this, e);
            CancelButton.Click += (s, e) => CancelClicked?.Invoke(this, e);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelClicked?.Invoke(this, e);
        }

        public void SetOptions(string title, string description, List<OptionItem> options)
        {
            TitleText.Text = title;
            DescriptionText.Text = description;
            OptionsList.ItemsSource = options;
            SelectedOptions = options;
        }

        public List<OptionItem> GetSelectedOptions()
        {
            return SelectedOptions;
        }
    }
}
