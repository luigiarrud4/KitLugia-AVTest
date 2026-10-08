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
            _servicesLoadedAt = DateTime.Now;
            // Reaplica o filtro que está NA COMBO, não "Todos": recarregar a lista
            // (ou trocar de aba) não pode descartar a escolha do usuário.
            ApplyServiceFilter(GetServiceFilter());
            TxtServiceCount.Text = $"— {services.Count} serviços";
            TxtServiceStatus.Text = $"{services.Count(s => s.Status == "Executando")} executando, {services.Count(s => s.Status == "Parado")} parados";
            UpdateServiceActionButtons();
        }

        private bool _servicesLoaded = false;
        private bool _startupLoaded = false;

        private void CmbServiceFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            // NÃO exige _servicesLoaded: mudar o filtro antes da 1ª carga é normal e o
            // guard antigo engolia o clique (a combo dizia "Parados" e a grade seguia
            // mostrando tudo — o "botão de filtro não funciona").
            if (CmbServiceFilter?.SelectedItem is ComboBoxItem item && item.Content is string filter)
                ApplyServiceFilter(filter);
        }

        private void ApplyServiceFilter(string filter)
        {
            if (_allServices == null || DgServices == null) return;
            if (string.IsNullOrEmpty(filter)) filter = "Todos";
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
            // Reaplica a ORDENAÇÃO escolhida pelo usuário (o filtro antigo trocava o
            // ItemsSource e voltava para a ordem do WMI).
            filtered = SortServices(filtered);

            DgServices.ItemsSource = filtered;
            TxtServiceCount.Text = $"— {filtered.Count} de {_allServices.Count} serviços";
            if (!_servicesLoaded) TxtServiceStatus.Text = "Carregando serviços...";
        }

        private List<ServiceInfo> SortServices(List<ServiceInfo>? src)
        {
            if (src == null || src.Count == 0) return src ?? new List<ServiceInfo>();
            if (string.IsNullOrEmpty(_svcSortProp)) return src;
            Func<ServiceInfo, string> key = _svcSortProp switch
            {
                "DisplayName" => s => s.DisplayName ?? "",
                "Status" => s => s.Status ?? "",
                "StartMode" => s => s.StartMode ?? "",
                "Manufacturer" => s => s.Manufacturer ?? "",
                _ => s => s.Name ?? "",
            };
            return _svcSortDir == ListSortDirection.Ascending
                ? src.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList()
                : src.OrderByDescending(key, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async void MenuStartService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            TxtStatus.Text = $"▶ Iniciando {svc.DisplayName}...";
            // ServiceController.Start/Stop/WaitForStatus fala com o SCM e pode levar DEZESSEGUNDOS
            // (o reinício tinha WaitForStatus(10s) na thread da UI): a janela ficava travada.
            string result = await Task.Run(() =>
            {
                try
                {
                    using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                    if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                    {
                        controller.Start();
                        return $"▶ Serviço {svc.DisplayName} iniciado.";
                    }
                    return $"O serviço {svc.DisplayName} já estava em execução.";
                }
                catch (Exception ex) { return $"Erro ao iniciar serviço: {ex.Message}"; }
            });
            TxtStatus.Text = result;
            _ = LoadServicesAsync();
        }

        private async void MenuStopService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            TxtStatus.Text = $"⏹ Parando {svc.DisplayName}...";
            string result = await Task.Run(() =>
            {
                try
                {
                    using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                    if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        controller.Stop();
                        return $"⏹ Serviço {svc.DisplayName} parado.";
                    }
                    return $"O serviço {svc.DisplayName} já estava parado.";
                }
                catch (Exception ex) { return $"Erro ao parar serviço: {ex.Message}"; }
            });
            TxtStatus.Text = result;
            _ = LoadServicesAsync();
        }

        // ══════════════════════════════════════════════
        //  ESTADO DOS BOTÕES (Seleção)
        //  Antes os botões ficavam SEMPRE habilitados e, sem linha selecionada, o
        //  handler fazia `return` mudo: o usuario clica e "nao acontece nada".
        //  Agora eles acendem/apagam com a seleção e o recarregar é explícito.
        // ══════════════════════════════════════════════

        private void DgServices_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateServiceActionButtons();

        private void UpdateServiceActionButtons()
        {
            try
            {
                var svc = DgServices?.SelectedItem as ServiceInfo;
                bool has = svc != null;
                if (BtnSvcStart != null) BtnSvcStart.IsEnabled = has && svc!.Status != "Executando";
                if (BtnSvcStop != null) BtnSvcStop.IsEnabled = has && svc!.Status == "Executando";
                if (BtnSvcRestart != null) BtnSvcRestart.IsEnabled = has;
                if (BtnSvcReload != null) BtnSvcReload.IsEnabled = _servicesBusy == 0;
            }
            catch { }
        }

        private int _servicesBusy;

        private async void BtnSvcReload_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.Exchange(ref _servicesBusy, 1) != 0) return;
            try { await LoadServicesAsync(); }
            finally { Interlocked.Exchange(ref _servicesBusy, 0); UpdateServiceActionButtons(); }
        }

        private DateTime _servicesLoadedAt = DateTime.MinValue;
        private DateTime _startupLoadedAt = DateTime.MinValue;

        private async Task ReloadServicesSafeAsync()
        {
            if (Interlocked.Exchange(ref _servicesBusy, 1) != 0) return;
            try { await LoadServicesAsync(); }
            catch (Exception ex) { try { TxtServiceStatus.Text = $"Erro: {ex.Message}"; } catch { } }
            finally { Interlocked.Exchange(ref _servicesBusy, 0); UpdateServiceActionButtons(); }
        }

        private async Task ReloadStartupSafeAsync()
        {
            if (Interlocked.CompareExchange(ref _servicesBusy, 1, 0) != 0) return;
            try { await LoadStartupAppsAsync(); }
            catch (Exception ex) { try { TxtStartupStatus.Text = $"Erro: {ex.Message}"; } catch { } }
            finally { Interlocked.Exchange(ref _servicesBusy, 0); UpdateStartupActionButtons(); }
        }

        private void DgStartup_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartupActionButtons();

        private void UpdateStartupActionButtons()
        {
            try
            {
                var app = DgStartup?.SelectedItem as StartupAppDetails;
                bool has = app != null;
                if (BtnStartupEnable != null) BtnStartupEnable.IsEnabled = has && app!.Status != StartupStatus.Enabled;
                if (BtnStartupDisable != null) BtnStartupDisable.IsEnabled = has && app!.Status != StartupStatus.Disabled;
                if (BtnStartupFolder != null) BtnStartupFolder.IsEnabled = has;
                if (BtnStartupWeb != null) BtnStartupWeb.IsEnabled = has;
            }
            catch { }
        }

        private async void BtnStartupReload_Click(object sender, RoutedEventArgs e)
        {
            // Já há uma varredura em curso (órfãs/recarregar): ela recarrega no fim.
            if (Interlocked.CompareExchange(ref _servicesBusy, 1, 0) != 0) return;
            try { await LoadStartupAppsAsync(); }
            finally { Interlocked.Exchange(ref _servicesBusy, 0); UpdateStartupActionButtons(); }
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
        private async void BtnStartupRemoveOrphans_Click(object sender, RoutedEventArgs e)
        {
            if (Interlocked.Exchange(ref _servicesBusy, 1) != 0) return;
            if (BtnStartupOrphans != null) BtnStartupOrphans.IsEnabled = false;
            try
            {
                var snapshot = _allStartupApps.ToList();
                TxtStartupStatus.Text = $"Verificando {snapshot.Count} itens de inicialização...";
                // File.Exists/Directory.Exists em caminho de rede pode BLOQUEAR por vários
                // segundos; o laço inteiro (e o SetStartupItemState) vai para a thread-pool.
                int removed = await Task.Run(() =>
                {
                    int n = 0;
                    foreach (var a in snapshot)
                    {
                        try
                        {
                            string p2 = a.FullCommand ?? "";
                            var mm = System.Text.RegularExpressions.Regex.Match(p2, "\"([^\"]+)\"|(\\S+\\.exe)");
                            string f = mm.Success ? (mm.Groups[1].Success ? mm.Groups[1].Value : mm.Groups[2].Value) : p2.Split(' ')[0].Trim('"');
                            if (!string.IsNullOrEmpty(f) && !System.IO.File.Exists(f) && !System.IO.Directory.Exists(f))
                            {
                                try { StartupManager.SetStartupItemState(a.Name, false); n++; } catch { }
                            }
                        }
                        catch { }
                    }
                    return n;
                });
                TxtStartupStatus.Text = removed > 0 ? $"{removed} órfãs desabilitadas." : "Nenhuma órfã encontrada.";
            }
            catch (Exception ex)
            {
                TxtStartupStatus.Text = $"Erro ao procurar órfãs: {ex.Message}";
            }
            finally
            {
                // SEMPRE devolve o botão e a trava: sem este finally uma exceção deixava o
                // "🗑️ Órfãs" cinza e a aba travada até fechar a janela.
                _ = LoadStartupAppsAsync();
                Interlocked.Exchange(ref _servicesBusy, 0);
                if (BtnStartupOrphans != null) BtnStartupOrphans.IsEnabled = true;
            }
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

        private async void MenuRestartService_Click(object sender, RoutedEventArgs e)
        {
            if (DgServices.SelectedItem is not ServiceInfo svc) return;
            TxtStatus.Text = $"🔄 Reiniciando {svc.DisplayName}...";
            string result = await Task.Run(() =>
            {
                try
                {
                    using var controller = new System.ServiceProcess.ServiceController(svc.Name);
                    if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                    {
                        controller.Stop();
                        controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        controller.Start();
                        return $"🔄 Serviço {svc.DisplayName} reiniciado.";
                    }
                    return $"O serviço {svc.DisplayName} não estava em execução.";
                }
                catch (Exception ex) { return $"Erro ao reiniciar serviço: {ex.Message}"; }
            });
            TxtStatus.Text = result;
            _ = LoadServicesAsync();
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
            _startupLoadedAt = DateTime.Now;
            DgStartup.ItemsSource = apps;
            TxtStartupCount.Text = $"— {apps.Count} apps";
            // "ativos" = HABILITADOS. A condição anterior (diferente de Enabled E diferente de
            // Disabled) contava justamente os que NÃO estão em nenhum dos dois — o número
            // estava invertido.
            TxtStartupStatus.Text = $"{apps.Count(a => a.Status == StartupStatus.Enabled)} habilitados, " +
                                    $"{apps.Count(a => a.Status == StartupStatus.Disabled)} desabilitados";
            // Reaplica o filtro ativo: recarregar (após habilitar/desabilitar) mantinha a
            // lista inteira visível mesmo com um filtro escolhido.
            ApplyStartupFilter();
            UpdateStartupActionButtons();
        }

        private void TxtStartupSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyStartupFilter();
        private void CmbStartupFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
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

        private async void MenuEnableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) return;
            TxtStatus.Text = $"Habilitando {app.Name}...";
            // SetStartupItemState mexe em Run/RunOnce +HKLM (precisa de elevação): na thread
            // da UI qualquer espera de UAC/política congelava o Gerenciador inteiro.
            string err = await Task.Run(() =>
            {
                try { StartupManager.SetStartupItemState(app.Name, true); return ""; }
                catch (Exception ex) { return ex.Message; }
            });
            TxtStatus.Text = string.IsNullOrEmpty(err) ? $"✅ {app.Name} habilitado na inicialização." : $"Erro: {err}";
            _ = LoadStartupAppsAsync();
        }

        private async void MenuDisableStartup_Click(object sender, RoutedEventArgs e)
        {
            if (DgStartup.SelectedItem is not StartupAppDetails app) return;
            TxtStatus.Text = $"Desabilitando {app.Name}...";
            string err = await Task.Run(() =>
            {
                try { StartupManager.SetStartupItemState(app.Name, false); return ""; }
                catch (Exception ex) { return ex.Message; }
            });
            TxtStatus.Text = string.IsNullOrEmpty(err) ? $"❌ {app.Name} desabilitado na inicialização." : $"Erro: {err}";
            _ = LoadStartupAppsAsync();
        }
    }
}
