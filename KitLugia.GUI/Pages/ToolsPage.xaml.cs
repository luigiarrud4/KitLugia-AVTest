using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Controls;
using KitLugia.Core;
using WinForms = System.Windows.Forms; // Para FolderBrowserDialog
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    // Classe auxiliar para a lista de planos de energia
    public class PowerPlanItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Guid { get; set; } = "";
        public bool IsActive { get; set; } = false;
        public bool CanDelete { get; set; } = false;

        private bool _isConfirmingDelete = false;
        public bool IsConfirmingDelete
        {
            get => _isConfirmingDelete;
            set { if (_isConfirmingDelete != value) { _isConfirmingDelete = value; OnPropertyChanged(nameof(IsConfirmingDelete)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public partial class ToolsPage : Page
    {
        private readonly HashSet<string> _defaultGuids = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        { "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "a1841308-3541-4fab-bc81-f71556f20b4a", "e9a42b02-d5df-448d-aa00-03f14749eb61" };

        private int _initialTabIndex = 0;
        private bool _isToolOperation;

        public ToolsPage(int tabIndex = 0)
        {
            InitializeComponent();
            _initialTabIndex = tabIndex;
            Loaded += ToolsPage_Loaded;

            Unloaded += ToolsPage_Unloaded;
        }


        public void Cleanup()
        {
            CmbPowerPlans.ItemsSource = null;
            Loaded -= ToolsPage_Loaded;
            Unloaded -= ToolsPage_Unloaded;


            this.DataContext = null;



        }

        private void ToolsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void ToolsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (MainTabs != null) MainTabs.SelectedIndex = _initialTabIndex;
            RefreshPowerPlans();
        }

        // =========================================================
        // ABA 1: ENERGIA
        // =========================================================
        #region Power Logic

        private void RefreshPowerPlans()
        {
            if (CmbPowerPlans.ItemsSource is IEnumerable<PowerPlanItem> oldItems)
            {
                foreach (var item in oldItems) item.IsConfirmingDelete = false;
            }

            var plans = Toolbox.GetAllPowerPlans();
            var activePlan = Toolbox.GetActivePowerPlan();
            TxtCurrentPlan.Text = activePlan.Name;


            // Típico: 3-6 planos de energia (Balanced, High Performance, Power Saver, Ultimate Performance, etc)
            var powerPlanItems = new List<PowerPlanItem>(6);
            foreach (var p in plans)
            {
                bool isActive = p.Guid.Equals(activePlan.Guid, System.StringComparison.OrdinalIgnoreCase);
                powerPlanItems.Add(new PowerPlanItem
                {
                    Name = p.Name,
                    Guid = p.Guid,
                    IsActive = isActive,
                    CanDelete = !_defaultGuids.Contains(p.Guid) && !isActive
                });
            }
            CmbPowerPlans.ItemsSource = powerPlanItems;
            CmbPowerPlans.SelectedValue = activePlan.Guid;
        }

        private void BtnActivatePlan_Click(object sender, RoutedEventArgs e)
        {
            if (CmbPowerPlans.SelectedValue is string guid && Application.Current.MainWindow is MainWindow mw)
            {
                var result = Toolbox.SetActivePowerPlan(guid);
                if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                else mw.ShowError("ERRO", result.Message);

                if (result.Success) RefreshPowerPlans();
            }
        }

        private async void BtnDeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if ((sender as System.Windows.Controls.Button)?.Tag is PowerPlanItem planToDelete && Application.Current.MainWindow is MainWindow mw)
                {
                    if (planToDelete.IsConfirmingDelete)
                    {
                        var result = await Task.Run(() => Toolbox.DeletePowerPlan(planToDelete.Guid));
                        if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                        else mw.ShowError("ERRO", result.Message);

                        RefreshPowerPlans();
                    }
                    else
                    {
                        // Reseta outros botões de delete
                        if (CmbPowerPlans.ItemsSource is IEnumerable<PowerPlanItem> items)
                        {
                            foreach (var item in items) item.IsConfirmingDelete = false;
                        }

                        planToDelete.IsConfirmingDelete = true;
                        await Task.Delay(1500);
                        // Se ainda estiver na tela, reseta
                        if (planToDelete != null) planToDelete.IsConfirmingDelete = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDeleteItem_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnUltimate_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var result = await Task.Run(() => Toolbox.UnlockAndActivateUltimatePerformance());
                    if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                    else mw.ShowInfo("AVISO", result.Message);
                    RefreshPowerPlans();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnUltimate_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnBitsum_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var result = await Task.Run(() => Toolbox.ImportAndActivateBitsumPlan());
                    if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                    else mw.ShowError("ERRO", result.Message);
                    RefreshPowerPlans();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnBitsum_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }
        #endregion

        // =========================================================
        // ABA 2: REDE
        // =========================================================
        #region Network Logic

        private async Task ApplyDns(string provider)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.ShowInfo("CONFIGURANDO DNS", $"Aplicando DNS {provider}. A rede pode reconectar.");
                var result = await Task.Run(() => Toolbox.SetDns(provider));
                if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                else mw.ShowError("ERRO", result.Message);
            }
        }

        private async void BtnDnsCloudflare_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                await ApplyDns("Cloudflare");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDnsCloudflare_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnDnsGoogle_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                await ApplyDns("Google");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDnsGoogle_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnDnsDhcp_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                await ApplyDns("DHCP");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnDnsDhcp_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var result = await Task.Run(() => Toolbox.FlushDnsCache());
                    mw.ShowSuccess("CACHE DE DNS", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnFlushDns_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private async void BtnNetReset_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (System.Windows.MessageBox.Show("Isso irá resetar suas configurações de rede e requer reinicialização.\nContinuar?", "Aviso Crítico", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        var result = await Task.Run(() => Toolbox.ResetNetworkStack());
                        mw.ShowInfo("REPARO DE REDE", result.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnNetReset_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }
        #endregion

        // =========================================================
        // ABA 3: SISTEMA & LOJA
        // =========================================================
        #region System Logic

        private void BtnStoreReset_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                var result = Toolbox.ResetStoreCache();
                mw.ShowSuccess("MICROSOFT STORE", result.Message);
            }
        }

        private void BtnGamingServices_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                var result = Toolbox.RepairGamingServices();
                mw.ShowSuccess("GAMING SERVICES", result.Message);
            }
        }

        private async void BtnGpedit_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("GPEDIT", "A instalação será iniciada em nova janela.");

                await Task.Run(() => Toolbox.EnableGroupPolicyEditor());
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnGpedit_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }

        private void BtnGodMode_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                var result = Toolbox.ToggleGodMode();
                mw.ShowInfo("GOD MODE", result.Message);
            }
        }

        private void BtnDriverBackup_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "Selecione onde salvar o backup dos drivers";
                if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    if (Application.Current.MainWindow is MainWindow mw)
                    {
                        var backupResult = DriverManager.BackupDrivers(dialog.SelectedPath);
                        if (backupResult.Success) mw.ShowSuccess("BACKUP DE DRIVERS", backupResult.Message);
                        else mw.ShowError("ERRO NO BACKUP", backupResult.Message);
                    }
                }
            }
        }
        #endregion

        // =========================================================
        // ABA 4: EXPLORER & MENU (NOVA)
        // =========================================================
        #region Explorer Logic

        private void HandleTweakResult((bool Success, string Message) result)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
                else mw.ShowError("ERRO", result.Message);
            }
        }

        private void BtnToggleOwnership_Click(object sender, RoutedEventArgs e)
        {
            HandleTweakResult(Toolbox.ToggleTakeOwnershipContext());
        }

        private void BtnToggleCmd_Click(object sender, RoutedEventArgs e)
        {
            HandleTweakResult(Toolbox.ToggleCmdContext());
        }

        private void BtnToggleHidden_Click(object sender, RoutedEventArgs e)
        {
            HandleTweakResult(Toolbox.ToggleHiddenFiles());
        }

        private void BtnToggleExtensions_Click(object sender, RoutedEventArgs e)
        {
            HandleTweakResult(Toolbox.ToggleFileExtensions());
        }

        private async void BtnClearSpooler_Click(object sender, RoutedEventArgs e)
        {
            if (_isToolOperation) return;
            _isToolOperation = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.ShowInfo("AGUARDE", "Reiniciando serviços de impressão...");
                    var result = await Task.Run(() => Toolbox.ClearPrintSpooler());

                    if (result.Success) mw.ShowSuccess("IMPRESSÃO", result.Message);
                    else mw.ShowError("ERRO", result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnClearSpooler_Click", ex.Message);
            }
            finally
            {
                _isToolOperation = false;
            }
        }
        #endregion
    }
}