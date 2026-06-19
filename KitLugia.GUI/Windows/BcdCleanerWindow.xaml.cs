using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KitLugia.GUI.Windows
{
    public class BcdEntryInfo
    {
        public string Guid { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool IsCritical { get; set; } = false;
    }

    public partial class BcdCleanerWindow : Window
    {
        public List<BcdEntryInfo> Entries { get; private set; } = new List<BcdEntryInfo>();
        public List<string> SelectedGuids { get; private set; } = new List<string>();

        public BcdCleanerWindow()
        {
            InitializeComponent();
        }

        public BcdCleanerWindow(List<BcdEntryInfo> entries) : this()
        {
            Entries = entries;
            EntriesList.ItemsSource = Entries;

            // Marca entradas críticas como não selecionáveis
            foreach (var entry in Entries.Where(e => e.IsCritical))
            {
                // Se precisar desabilitar checkbox de entradas críticas no futuro
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in Entries.Where(e => !e.IsCritical))
            {
                entry.IsCritical = false; // Temporariamente para permitir seleção
            }
            // Atualiza visualmente - precisaria de binding TwoWay
            RefreshList();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in Entries)
            {
                entry.IsCritical = true; // Marca todas como críticas temporariamente
            }
            RefreshList();
        }

        private void RefreshList()
        {
            EntriesList.ItemsSource = null;
            EntriesList.ItemsSource = Entries;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            SelectedGuids.Clear();

            // Coleta GUIDs selecionados
            for (int i = 0; i < EntriesList.Items.Count; i++)
            {
                var container = EntriesList.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                if (container != null)
                {
                    var checkbox = FindVisualChild<System.Windows.Controls.CheckBox>(container);
                    if (checkbox != null && checkbox.IsChecked == true)
                    {
                        var entry = EntriesList.Items[i] as BcdEntryInfo;
                        if (entry != null && !entry.IsCritical)
                        {
                            SelectedGuids.Add(entry.Guid);
                        }
                    }
                }
            }

            if (SelectedGuids.Count == 0)
            {
                System.Windows.MessageBox.Show("Nenhuma entrada selecionada para remoção.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null!;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var resultOfChild = FindVisualChild<T>(child);
                if (resultOfChild != null)
                    return resultOfChild;
            }
            return null!;
        }
    }
}
