using System.Collections.Generic;
using System.Windows;

namespace KitLugia.GUI.Windows
{
    public partial class PresetDialog : Window
    {
        public class OptimizationItem
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public bool IsSelected { get; set; }
        }

        public List<OptimizationItem> SelectedOptimizations { get; private set; }

        public PresetDialog(string presetName, string presetDescription, List<OptimizationItem> optimizations)
        {
            InitializeComponent();
            
            PresetTitle.Text = presetName;
            PresetDescription.Text = presetDescription;
            
            OptimizationsList.ItemsSource = optimizations;
            SelectedOptimizations = optimizations;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
