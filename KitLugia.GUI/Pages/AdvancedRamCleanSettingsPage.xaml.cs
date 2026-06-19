using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class AdvancedRamCleanSettingsPage : Page
    {
        private bool _isSavingSettings;

        public AdvancedRamCleanSettingsPage()
        {
            InitializeComponent();
            _ = LoadSettingsAsync();
            CheckWindowsVersion();

            Unloaded += AdvancedRamCleanSettingsPage_Unloaded;

            SliderAutoReductLimit.ValueChanged += SliderAutoReductLimit_ValueChanged;
            SliderAutoReductInterval.ValueChanged += SliderAutoReductInterval_ValueChanged;
            SliderWarningLevel.ValueChanged += SliderWarningLevel_ValueChanged;
            SliderDangerLevel.ValueChanged += SliderDangerLevel_ValueChanged;
        }

        private void SliderAutoReductLimit_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TxtAutoReductLimit.Text = $"{(int)e.NewValue}%";
        }

        private void SliderAutoReductInterval_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TxtAutoReductInterval.Text = $"{(int)e.NewValue}s";
        }

        private void SliderWarningLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TxtWarningLevel.Text = $"{(int)e.NewValue}%";
        }

        private void SliderDangerLevel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            TxtDangerLevel.Text = $"{(int)e.NewValue}%";
        }


        public void Cleanup()
        {
            Unloaded -= AdvancedRamCleanSettingsPage_Unloaded;
            SliderAutoReductLimit.ValueChanged -= SliderAutoReductLimit_ValueChanged;
            SliderAutoReductInterval.ValueChanged -= SliderAutoReductInterval_ValueChanged;
            SliderWarningLevel.ValueChanged -= SliderWarningLevel_ValueChanged;
            SliderDangerLevel.ValueChanged -= SliderDangerLevel_ValueChanged;

            this.DataContext = null;
        }

        private void AdvancedRamCleanSettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }
        
        private void CheckWindowsVersion()
        {
            var windowsVersion = SystemInfo.GetWindowsVersion();
            var versionString = SystemInfo.GetWindowsVersionString();
            
            Logger.Log($"🖥️ Sistema detectado: {versionString}");
            
            // Desabilitar opções não suportadas
            if (!SystemInfo.IsFeatureSupported("RegistryCache"))
            {
                ChkRegistryCache.IsChecked = false;
                ChkRegistryCache.IsEnabled = false;
                Logger.Log("⚠️ Registry Cache desabilitado - não suportado nesta versão do Windows");
            }
            
            if (!SystemInfo.IsFeatureSupported("CombineMemoryLists"))
            {
                ChkCombineLists.IsChecked = false;
                ChkCombineLists.IsEnabled = false;
                Logger.Log("⚠️ Combine Memory Lists desabilitado - não suportado nesta versão do Windows");
            }
        }
        
        private async Task LoadSettingsAsync()
        {
            var data = await Task.Run<Dictionary<string, int>?>(() =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\AdvancedRamClean");
                    if (key == null) return null;
                    return new Dictionary<string, int>
                    {
                        ["WorkingSet"] = (int)key.GetValue("WorkingSet", 1),
                        ["SystemFileCache"] = (int)key.GetValue("SystemFileCache", 1),
                        ["StandbyPriority0"] = (int)key.GetValue("StandbyPriority0", 1),
                        ["StandbyList"] = (int)key.GetValue("StandbyList", 0),
                        ["ModifiedList"] = (int)key.GetValue("ModifiedList", 0),
                        ["CombineLists"] = (int)key.GetValue("CombineLists", 1),
                        ["RegistryCache"] = (int)key.GetValue("RegistryCache", 1),
                        ["ModifiedFileCache"] = (int)key.GetValue("ModifiedFileCache", 1),
                        ["AutoReductLimit"] = (int)key.GetValue("AutoReductLimit", 90),
                        ["AutoReductInterval"] = (int)key.GetValue("AutoReductInterval", 30),
                        ["WarningLevel"] = (int)key.GetValue("WarningLevel", 70),
                        ["DangerLevel"] = (int)key.GetValue("DangerLevel", 90)
                    };
                }
                catch { return null; }
            });

            if (data == null) return;

            ChkWorkingSet.IsChecked = data["WorkingSet"] == 1;
            ChkSystemFileCache.IsChecked = data["SystemFileCache"] == 1;
            ChkStandbyPriority0.IsChecked = data["StandbyPriority0"] == 1;
            ChkStandbyList.IsChecked = data["StandbyList"] == 1;
            ChkModifiedList.IsChecked = data["ModifiedList"] == 1;
            ChkCombineLists.IsChecked = data["CombineLists"] == 1;
            ChkRegistryCache.IsChecked = data["RegistryCache"] == 1;
            ChkModifiedFileCache.IsChecked = data["ModifiedFileCache"] == 1;
            SliderAutoReductLimit.Value = data["AutoReductLimit"];
            SliderAutoReductInterval.Value = data["AutoReductInterval"];
            SliderWarningLevel.Value = data["WarningLevel"];
            SliderDangerLevel.Value = data["DangerLevel"];
            TxtAutoReductLimit.Text = $"{data["AutoReductLimit"]}%";
            TxtAutoReductInterval.Text = $"{data["AutoReductInterval"]}s";
            TxtWarningLevel.Text = $"{data["WarningLevel"]}%";
            TxtDangerLevel.Text = $"{data["DangerLevel"]}%";
        }
        
        private async Task SaveSettingsAsync()
        {
            var ws = ChkWorkingSet.IsChecked == true ? 1 : 0;
            var sfc = ChkSystemFileCache.IsChecked == true ? 1 : 0;
            var spo = ChkStandbyPriority0.IsChecked == true ? 1 : 0;
            var sl = ChkStandbyList.IsChecked == true ? 1 : 0;
            var ml = ChkModifiedList.IsChecked == true ? 1 : 0;
            var cl = ChkCombineLists.IsChecked == true ? 1 : 0;
            var rc = ChkRegistryCache.IsChecked == true ? 1 : 0;
            var mfc = ChkModifiedFileCache.IsChecked == true ? 1 : 0;
            var arl = (int)SliderAutoReductLimit.Value;
            var ari = (int)SliderAutoReductInterval.Value;
            var wl = (int)SliderWarningLevel.Value;
            var dl = (int)SliderDangerLevel.Value;

            await Task.Run(() =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\KitLugia\AdvancedRamClean");
                    key.SetValue("WorkingSet", ws);
                    key.SetValue("SystemFileCache", sfc);
                    key.SetValue("StandbyPriority0", spo);
                    key.SetValue("StandbyList", sl);
                    key.SetValue("ModifiedList", ml);
                    key.SetValue("CombineLists", cl);
                    key.SetValue("RegistryCache", rc);
                    key.SetValue("ModifiedFileCache", mfc);
                    key.SetValue("AutoReductLimit", arl);
                    key.SetValue("AutoReductInterval", ari);
                    key.SetValue("WarningLevel", wl);
                    key.SetValue("DangerLevel", dl);
                    Logger.Log("✅ AdvancedRamCleanSettings: Configurações salvas");
                }
                catch (Exception ex)
                {
                    Logger.LogError("AdvancedRamCleanSettingsPage.SaveSettings", $"Erro: {ex.Message}");
                }
            });
        }
        
        private void BtnRestoreDefault_Click(object sender, RoutedEventArgs e)
        {
            // Restaurar configurações padrão do MemReduct
            ChkWorkingSet.IsChecked = true;
            ChkSystemFileCache.IsChecked = true;
            ChkStandbyPriority0.IsChecked = true;
            ChkStandbyList.IsChecked = false;
            ChkModifiedList.IsChecked = false;
            ChkCombineLists.IsChecked = true;
            ChkRegistryCache.IsChecked = true;
            ChkModifiedFileCache.IsChecked = true;
            
            SliderAutoReductLimit.Value = 90;
            SliderAutoReductInterval.Value = 30;
            SliderWarningLevel.Value = 70;
            SliderDangerLevel.Value = 90;
            
            Logger.Log("🔄 AdvancedRamCleanSettings: Restaurado para padrão MemReduct");
        }
        
        private async void BtnAggressiveMode_Click(object sender, RoutedEventArgs e)
        {
            if (_isSavingSettings) return;
            _isSavingSettings = true;
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    var result = await mw.ShowConfirmationDialog("Este modo habilita a Modified Page List, que pode causar travamentos. Deseja continuar?");
                    if (!result) return;
                }
                
                ChkWorkingSet.IsChecked = true;
                ChkSystemFileCache.IsChecked = true;
                ChkStandbyPriority0.IsChecked = true;
                ChkStandbyList.IsChecked = true;
                ChkModifiedList.IsChecked = true;
                ChkCombineLists.IsChecked = true;
                ChkRegistryCache.IsChecked = true;
                ChkModifiedFileCache.IsChecked = true;
                
                Logger.Log("⚠️ AdvancedRamCleanSettings: Modo agressivo ativado");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnAggressiveMode_Click", ex.Message);
            }
            finally { _isSavingSettings = false; }
        }
        
        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_isSavingSettings) return;
            _isSavingSettings = true;
            try
            {
                await SaveSettingsAsync();
                
                if (Application.Current.MainWindow is MainWindow mw)
                {
                    mw.ShowSuccess("CONFIGURAÇÕES", "Configurações avançadas de limpeza de RAM salvas com sucesso!");
                    mw.NavigateToPage(PageType.TraySettings);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnSave_Click", ex.Message);
            }
            finally { _isSavingSettings = false; }
        }
        
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.NavigateToPage(PageType.TraySettings);
        }
    }
}
