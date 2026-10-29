using System;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;

namespace KitLugia.GUI.Pages
{
    public partial class MacSpoofPage : Page
    {
        public MacSpoofPage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadAdapters();
            BtnRandom.Click += (s, e) => { TxtNewMac.Text = AdapterManager.GetRandomMacAddress(ChkUse02.IsChecked == true); };
            BtnChange.Click += (s, e) => ApplyMac();
            BtnReset.Click += (s, e) => ResetMac();
            BtnDelete.Click += (s, e) => DeleteAdapter();
            CmbAdapters.SelectionChanged += (s, e) => UpdateInfo();
        }

        private void LoadAdapters()
        {
            CmbAdapters.Items.Clear();
            foreach (var a in AdapterManager.ListPhysicalAdapters())
                CmbAdapters.Items.Add(a);
            if (CmbAdapters.Items.Count > 0) CmbAdapters.SelectedIndex = 0;
        }

        private AdapterManager.NetworkAdapterInfo Selected() => CmbAdapters.SelectedItem as AdapterManager.NetworkAdapterInfo;

        private void UpdateInfo()
        {
            var a = Selected();
            if (a == null) return;
            TxtCurrentMac.Text = $"MAC atual: {a.CurrentMac}";
            TxtOriginalMac.Text = $"MAC original: {a.PermanentMac}";
            a.HasChanged = !string.Equals(a.CurrentMac, a.PermanentMac, StringComparison.OrdinalIgnoreCase);
            TxtStatus.Text = $"Status: {(a.HasChanged ? "Alterado" : "Original")}";
        }

        private void ApplyMac()
        {
            var a = Selected();
            if (a == null) return;
            string mac = TxtNewMac.Text.Trim().Replace(":", "").Replace("-", "");
            if (mac.Length != 12) { System.Windows.MessageBox.Show("MAC inválido (12 hex)"); return; }
            // escrever via registry (mesmo padrão do AdapterManager)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\Class\{{4D36E972-E325-11CE-BFC1-08002BE10318}}\{a.Id}",
                    writable: true);
                if (key == null) { System.Windows.MessageBox.Show("Não foi possível abrir registro"); return; }
                key.SetValue("NetworkAddress", mac, Microsoft.Win32.RegistryValueKind.String);
                a.HasChanged = true;
                a.ChangeStatus = "Temporário";
                UpdateInfo();
                System.Windows.MessageBox.Show("MAC aplicado. Reinicie o adaptador para efetivar.");
            }
            catch (Exception ex) { System.Windows.MessageBox.Show("Erro: " + ex.Message); }
        }

        private void ResetMac()
        {
            var a = Selected();
            if (a == null) return;
            if (AdapterManager.ResetToOriginalMac(a)) { UpdateInfo(); System.Windows.MessageBox.Show("MAC restaurado."); }
            else System.Windows.MessageBox.Show("Falha ao restaurar.");
        }

        private void DeleteAdapter()
        {
            var a = Selected();
            if (a == null) return;
            if (System.Windows.MessageBox.Show("Deletar adaptador do registro? (irreversível)", "Confirmar", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
            {
                if (AdapterManager.DeleteAdapterFromRegistry(a)) { LoadAdapters(); System.Windows.MessageBox.Show("Adaptador deletado."); }
                else System.Windows.MessageBox.Show("Falha ao deletar.");
            }
        }
    }
}
