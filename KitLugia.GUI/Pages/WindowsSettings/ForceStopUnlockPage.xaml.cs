using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;

using Application = System.Windows.Application;
using MainWindow = KitLugia.GUI.MainWindow;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class ForceStopUnlockPage : Page
    {
        private bool _isLoading;

        public ForceStopUnlockPage()
        {
            InitializeComponent();
            this.Loaded += async (s, e) => await RefreshStatus();
            this.Unloaded += (s, e) => Cleanup();
        }

        public void Cleanup()
        {
            this.DataContext = null;
        }

        private async Task RefreshStatus()
        {
            _isLoading = true;
            try
            {
                await Task.Run(() =>
                {
                    bool isAdded = SystemTweaks.IsForceStopUnlockAdded();

                    Dispatcher.Invoke(() =>
                    {
                        ChkEnable.IsChecked = isAdded;
                        TxtMenuStatus.Text = isAdded ? "✅ Ativo no menu de contexto" : "❌ Inativo";
                        TxtMenuStatus.Foreground = isAdded
                            ? System.Windows.Media.Brushes.LightGreen
                            : System.Windows.Media.Brushes.Gray;

                        TxtHandleStatus.Text = "✅ Incluso no Kit";
                        TxtHandleStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                    });
                });
            }
            catch { }
            finally { _isLoading = false; }
        }

        private async void ChkEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            _isLoading = true;
            try
            {
                bool target = ChkEnable.IsChecked == true;
                await Task.Run(() =>
                {
                    if (target) SystemTweaks.AddForceStopUnlock();
                    else SystemTweaks.RemoveForceStopUnlock();
                });

                if (Application.Current.MainWindow is MainWindow mw)
                {
                    if (target)
                        mw.ShowSuccess("FORCE STOP UNLOCK", "Opção adicionada ao menu de contexto.");
                    else
                        mw.ShowInfo("FORCE STOP UNLOCK", "Opção removida do menu de contexto.");
                }

                await RefreshStatus();
            }
            catch { }
            finally { _isLoading = false; }
        }


    }
}
