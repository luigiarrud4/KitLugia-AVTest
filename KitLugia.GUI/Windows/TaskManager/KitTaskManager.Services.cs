using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.Core.TaskManager;

namespace KitLugia.GUI.Windows.TaskManager
{
    // Partial: abas SERVIÇOS e INICIALIZAÇÃO — carregamento, filtros, ordenação padrão.
    public partial class KitTaskManagerWindow
    {
// ══════════════════════════════════════════════
        //  SERVICES TAB
        // ══════════════════════════════════════════════
        private async Task LoadServicesAsync()
        {
            TxtServiceStatus.Text = "Carregando serviços...";
            var services = await Task.Run(() =>
            {
                try { return BackgroundProcessManager.GetAllServices(); }
                catch { return new List<ServiceInfo>(); }
            });
            _allServices = services;
            _servicesLoaded = true;
            ApplyServiceFilter("Todos");
            TxtServiceCount.Text = $"— {services.Count} serviços";
            TxtServiceStatus.Text = $"{services.Count(s => s.Status == "Executando")} executando, {services.Count(s => s.Status == "Parado")} parados";
        }

        private bool _servicesLoaded = false;
        private bool _startupLoaded = false;

        private void CmbServiceFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_servicesLoaded) return;
            if (CmbServiceFilter?.SelectedItem is ComboBoxItem item && item.Content is string filter)
                ApplyServiceFilter(filter);
        }

        private void ApplyServiceFilter(string filter)
        {
            if (_allServices == null || DgServices == null) return;
            var filtered = filter switch
            {
                "Executando" => _allServices.Where(s => s.Status == "Executando").ToList(),
                "Parados" => _allServices.Where(s => s.Status == "Parado").ToList(),
                _ => _allServices.ToList()
            };
            // Busca global (barra do topo) também filtra serviços
            string q = _lastSearchQuery;
            if (!string.IsNullOrEmpty(q))
            {
                filtered = filtered.Where(s =>
                    s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (s.Manufacturer?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }
            DgServices.ItemsSource = filtered;
            TxtServiceCount.Text = $"— {filtered.Count} serviços";
        }

        private void MenuStartService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    controller.Start();
                    TxtStatus.Text = $"▶ Serviço {svc.DisplayName} iniciado.";
                    _ = LoadServicesAsync();
                }
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao iniciar serviço: {ex.Message}"; }
        }

        private void MenuStopService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    controller.Stop();
                    TxtStatus.Text = $"⏹ Serviço {svc.DisplayName} parado.";
                    _ = LoadServicesAsync();
                }
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao parar serviço: {ex.Message}"; }
        }

        private void BtnOpenServicesMsc_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("services.msc"){UseShellExecute=true}); } catch (Exception ex){ TxtServiceStatus.Text = $"Erro: {ex.Message}"; }
        }

        private void BtnStartupOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) { TxtStartupStatus.Text = "Selecione um item."; return; }
            try
            {
                string path = app.FullCommand ?? app.Name;
                if (string.IsNullOrEmpty(path)) return;
                var m = System.Text.RegularExpressions.Regex.Match(path, "\"([^\"]+)\"|(\\S+\\.exe)");
                string file = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : path.Split(' ')[0];
                if (System.IO.File.Exists(file)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); else System.Diagnostics.Process.Start("explorer.exe", System.IO.Path.GetDirectoryName(file) ?? ".");
            } catch (Exception ex){ TxtStartupStatus.Text = $"Erro: {ex.Message}"; }
        }

        private void DgStartup_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Padrão da aba principal: duplo-clique abre a localização do item
            if (DgStartup.SelectedItem is StartupAppDetails) BtnStartupOpenFolder_Click(sender, e);
        }
        private void BtnStartupSearchWeb_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://www.google.com/search?q={Uri.EscapeDataString(app.Name)}") {UseShellExecute=true}); } catch {}
        }
        private void BtnStartupRemoveOrphans_Click(object sender, RoutedEventArgs e)
        {
            int removed=0;
            foreach(var a in _allStartupApps.ToList()){
                try{
                    string p2 = a.FullCommand ?? "";
                    var mm = System.Text.RegularExpressions.Regex.Match(p2, "\"([^\"]+)\"|(\\S+\\.exe)");
                    string f = mm.Success ? (mm.Groups[1].Success ? mm.Groups[1].Value : mm.Groups[2].Value) : p2.Split(' ')[0].Trim('"');
                    if(!string.IsNullOrEmpty(f) && !System.IO.File.Exists(f) && !System.IO.Directory.Exists(f)){
                        try{ StartupManager.SetStartupItemState(a.Name,false); removed++; }catch{}
                    }
                }catch{}
            }
            TxtStartupStatus.Text = removed>0 ? $"{removed} órfãs desabilitadas." : "Nenhuma órfã encontrada.";
            _ = LoadStartupAppsAsync();
        }

        // Performance helpers
        private void CmbPerfInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_graphTimer==null) return;
            if ((sender as System.Windows.Controls.ComboBox)?.SelectedItem is System.Windows.Controls.ComboBoxItem ci && ci.Content is string txt){
                if(txt.Contains("Pausado")) _graphTimer.Stop(); else { int sec = txt.Contains("2s")?2:1; _graphTimer.Interval = TimeSpan.FromSeconds(sec); _graphTimer.Start(); }
            }
        }
        private void BtnPerfCopy_Click(object sender, RoutedEventArgs e)
        {
            try{
                string txt = $"CPU {TxtCpuUsage.Text} | RAM {TxtMemUsage.Text} | Disco {TxtDiskUsage.Text} | Rede {TxtNetUsage.Text} | GPU {TxtGpuUsage.Text}";
                System.Windows.Clipboard.SetText(txt); TxtStatus.Text = "📋 Métricas copiadas."; }catch{}
        }
        private void BtnPerfResmon_Click(object sender, RoutedEventArgs e){ try{ System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("resmon.exe"){UseShellExecute=true}); }catch{} }
        // (legado removido: ShowPerfDetail/Load*Detail substituídos pela lista de dispositivos Win11)

        private void MenuRestartService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    controller.Stop();
                    controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    controller.Start();
                    TxtStatus.Text = $"🔄 Serviço {svc.DisplayName} reiniciado.";
                    _ = LoadServicesAsync();
                }
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao reiniciar serviço: {ex.Message}"; }
        }

        // ══════════════════════════════════════════════
        //  ORDENAÇÃO PADRÃO (igual aba Processos) — clique no cabeçalho alterna ↑/↓
        // ══════════════════════════════════════════════
        private string? _svcSortProp;
        private ListSortDirection _svcSortDir = ListSortDirection.Ascending;

        private void DgServices_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string prop = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(prop)) return;
            if (_svcSortProp == prop)
                _svcSortDir = _svcSortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else { _svcSortProp = prop; _svcSortDir = ListSortDirection.Ascending; }

            var src = DgServices.ItemsSource as IEnumerable<ServiceInfo>;
            if (src == null) return;
            Func<ServiceInfo, string> key = prop switch
            {
                "DisplayName" => s => s.DisplayName ?? "",
                "Status" => s => s.Status ?? "",
                "StartMode" => s => s.StartMode ?? "",
                "Manufacturer" => s => s.Manufacturer ?? "",
                _ => s => s.Name ?? "",
            };
            var sorted = _svcSortDir == ListSortDirection.Ascending
                ? src.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList()
                : src.OrderByDescending(key, StringComparer.OrdinalIgnoreCase).ToList();
            DgServices.ItemsSource = sorted;

            foreach (var c in DgServices.Columns) c.SortDirection = null;
            e.Column.SortDirection = _svcSortDir;
            TxtServiceStatus.Text = $"Ordenado por \"{e.Column.Header}\" ({(_svcSortDir == ListSortDirection.Ascending ? "A→Z" : "Z→A")}).";
        }

        private string? _startupSortProp;
        private ListSortDirection _startupSortDir = ListSortDirection.Ascending;

        private void DgStartup_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            string prop = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(prop)) return;
            if (_startupSortProp == prop)
                _startupSortDir = _startupSortDir == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else { _startupSortProp = prop; _startupSortDir = ListSortDirection.Ascending; }

            var src = DgStartup.ItemsSource as IEnumerable<StartupAppDetails>;
            if (src == null) return;
            Func<StartupAppDetails, string> key = prop switch
            {
                "FullCommand" => a => a.FullCommand ?? "",
                "Location" => a => a.Location ?? "",
                "Status" => a => a.Status.ToString(),
                _ => a => a.Name ?? "",
            };
            var sorted = _startupSortDir == ListSortDirection.Ascending
                ? src.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList()
                : src.OrderByDescending(key, StringComparer.OrdinalIgnoreCase).ToList();

            // Reaplica o filtro ativo por cima da nova ordem
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(DgStartup.ItemsSource);
            bool hadFilter = view?.Filter != null;
            DgStartup.ItemsSource = sorted;
            if (hadFilter && view != null) { view.Filter = null; }
            ApplyStartupFilter(); // reatribui o filtro + contagem

            foreach (var c in DgStartup.Columns) c.SortDirection = null;
            e.Column.SortDirection = _startupSortDir;
            TxtStartupStatus.Text = $"Ordenado por \"{e.Column.Header}\" ({(_startupSortDir == ListSortDirection.Ascending ? "A→Z" : "Z→A")}).";
        }

        // ══════════════════════════════════════════════
        //  STARTUP TAB
        // ══════════════════════════════════════════════
        private async Task LoadStartupAppsAsync()
        {
            TxtStartupStatus.Text = "Carregando apps de inicialização...";
            var apps = await Task.Run(() =>
            {
                try { return StartupManager.GetStartupAppsWithDetails(); }
                catch { return new List<StartupAppDetails>(); }
            });
            _allStartupApps = apps;
            _startupLoaded = true;
            DgStartup.ItemsSource = apps;
            TxtStartupCount.Text = $"— {apps.Count} apps";
            TxtStartupStatus.Text = $"{apps.Count(a => a.Status != StartupStatus.Enabled && a.Status != StartupStatus.Disabled)} ativos";
        }

        private void TxtStartupSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyStartupFilter();
        private void CmbStartupFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_startupLoaded) return;
            ApplyStartupFilter();
        }
        private void ApplyStartupFilter()
        {
            if (!_startupLoaded || _allStartupApps == null || DgStartup == null || DgStartup.ItemsSource == null) return;
            string q = (TxtStartupSearch.Text ?? "").Trim();
            // Busca global (barra do topo) + busca local da aba se somam (AND)
            string gq = _lastSearchQuery;
            string filter = ((CmbStartupFilter.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string) ?? "Todos";
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(DgStartup.ItemsSource);
            if (view == null) return;
            view.Filter = o =>
            {
                if (o is not StartupAppDetails a) return false;
                if (filter == "Habilitados" && a.Status == StartupStatus.Disabled) return false;
                if (filter == "Desabilitados" && a.Status != StartupStatus.Disabled) return false;
                if (filter == "Alto impacto" && !(a.Status != StartupStatus.Disabled && (a.Status == StartupStatus.Elevated || a.Status == StartupStatus.TurboBoot || a.IsInBootTray))) return false;
                if (filter == "Órfãos")
                {
                    string f = a.ExePath;
                    if (!string.IsNullOrEmpty(f) && !System.IO.File.Exists(f) && !System.IO.Directory.Exists(f)) return true;
                    return false;
                }
                if (!string.IsNullOrEmpty(q) || !string.IsNullOrEmpty(gq))
                {
                    string hay = $"{a.Name} {a.FullCommand} {a.Location} {a.ExePath}";
                    if (!string.IsNullOrEmpty(q) && !hay.Contains(q, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!string.IsNullOrEmpty(gq) && !hay.Contains(gq, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            };
            view.Refresh();
            TxtStartupCount.Text = $"{view.Cast<object>().Count()} itens";
        }

        private void MenuEnableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) return;
            try
            {
                StartupManager.SetStartupItemState(app.Name, true);
                TxtStatus.Text = $"✅ {app.Name} habilitado na inicialização.";
                _ = LoadStartupAppsAsync();
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro: {ex.Message}"; }
        }

        private void MenuDisableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) return;
            try
            {
                StartupManager.SetStartupItemState(app.Name, false);
                TxtStatus.Text = $"❌ {app.Name} desabilitado na inicialização.";
                _ = LoadStartupAppsAsync();
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro: {ex.Message}"; }
        }
    }
}
