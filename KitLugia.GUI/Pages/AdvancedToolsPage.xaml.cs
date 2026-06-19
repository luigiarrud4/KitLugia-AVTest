using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Forms;
using KitLugia.Core;
using Microsoft.Win32;
// Resolução de Conflitos WPF vs WinForms
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Pages
{
    public partial class AdvancedToolsPage : Page
    {
        // ==========================================
        // ISO EDITOR (Full Page)
        // ==========================================
        private void BtnOpenIsoEditor_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.NavigateToPage(PageType.IsoEditor);
        }

        public AdvancedToolsPage()
        {
            InitializeComponent();
            // ðŸ”¥ LIMPEZA: Liberar recursos ao sair da página
            this.Unloaded += AdvancedToolsPage_Unloaded;
        }

        // ðŸ”¥ CORREÃ‡ÃƒO: Cleanup público para ser chamado via reflection pelo MainWindow
        public void Cleanup()
        {
            this.Unloaded -= AdvancedToolsPage_Unloaded;


            this.DataContext = null;


        }

        private void AdvancedToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        // ==========================================
        // WINBOOT (NO-USB)
        // ==========================================
        private void BtnWinboot_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.NavigateToPage(PageType.Winboot);
        }

        // ==========================================
        // GERENCIADOR DE PARTIÃ‡Ã•ES (Launcher)
        // ==========================================
        private void BtnOpenPartitions_Click(object sender, RoutedEventArgs e)
        {
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.NavigateToPage(PageType.Partitions);
        }
    }
}
