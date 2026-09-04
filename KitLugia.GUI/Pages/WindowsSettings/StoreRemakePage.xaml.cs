using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using KitLugia.Core.KitStore;

namespace KitLugia.GUI.Pages.WindowsSettings
{
    public partial class StoreRemakePage : Page
    {
        private readonly ObservableCollection<StoreAppVM> _installed = new();
        private readonly ObservableCollection<StoreAppVM> _searchResults = new();
        private bool _showingInstalled = true;
        private string? _wingetPath;
        private string? _chocoPath;
        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        public StoreRemakePage()
        {
            InitializeComponent();
            Loaded += async (_, __) => await OnLoadedAsync();
            Unloaded += StoreRemakePage_Unloaded;
            TxtSearch.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = DoSearchAsync(); } };
            // Busca ao vivo (igual MS Store): digitar já mostra apps, sem precisar clicar Buscar
            TxtSearch.TextChanged += (s, e) => ScheduleLiveSearch();
            _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); /* live search: dropdown so — busca full so via Enter/Buscar */ };
            // Fechar dropdown ao pressionar Escape
            TxtSearch.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { SearchPopup.IsOpen = false; e.Handled = true; } };
            // Fechar dropdown ao clicar fora da area de busca
            PreviewMouseDown += (s, e) =>
            {
                if (!SearchPopup.IsOpen) return;
                var popupChild = SearchPopup.Child as System.Windows.FrameworkElement;
                bool overPopup = popupChild != null && popupChild.IsMouseOver;
                bool overTextBox = TxtSearch.IsMouseOver;
                if (!overPopup && !overTextBox) SearchPopup.IsOpen = false;
            };
            // Hero cards: click handlers wired ONCE (Tag holds current app — set by PopulateHomeSections)
            HeroCardMain.MouseLeftButtonDown += HeroCard_Click;
            HeroSide1Card.MouseLeftButtonDown += HeroCard_Click;
            HeroSide2Card.MouseLeftButtonDown += HeroCard_Click;
        }
        private void HeroCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is StoreAppVM app)
                ShowAppDetail(app);
        }

        private void StoreRemakePage_Unloaded(object sender, RoutedEventArgs e)
        {
            try { _searchDebounce?.Stop(); } catch { }
            try { _progressHideTimer?.Stop(); _progressHideTimer = null; } catch { }
            try { _searchAnimTimer?.Stop(); _searchAnimTimer = null; } catch { }
        }

        private void ScheduleLiveSearch()
        {
            try
            {
                // Atualiza dropdown imediatamente (loco, sem debounce)
                UpdateSearchDropdown(TxtSearch.Text ?? "");
                if (_searchDebounce == null) return;
                _searchDebounce.Stop();
                _searchDebounce.Start(); // 400ms após parar de digitar dispara a busca
            }
            catch { }
        }

        private int _busy;
        private int _searchBusy;
        private enum StoreTab { Home, Apps, Games, Library, Downloads }
        private StoreTab _activeTab = StoreTab.Home;
        private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _iconLock = new();
        // Cache de uninstall entries para resolver ícones sem varrer registry N vezes
        private Dictionary<string, UninstallInfo>? _uninstallCache;
        private DateTime _uninstallCacheTime = DateTime.MinValue;
        // Ícone cache persistente em disco
        private static string IconCacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "IconCache");
        private static string GetIconCachePath(string appId)
        {
            var safe = string.Join("", appId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(IconCacheDir, safe + ".png");
        }

        // ── Tab visibility: ONE method hides all, shows the target ──
        private void ShowPanel(string tab)
        {
            _activeTab = Enum.Parse<StoreTab>(tab);
            HighlightNav(_activeTab);
            _showingInstalled = tab is "Library" or "Apps" or "Games";
            // Show/hide the 4 main content areas
            HomePanel.Visibility = tab == "Home" ? Visibility.Visible : Visibility.Collapsed;
            StandardContent.Visibility = tab is "Home" or "Downloads" ? Visibility.Collapsed : Visibility.Visible;
            DownloadsPanel.Visibility = tab == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
            SearchPopup.IsOpen = false;
            // Inside StandardContent: show/hide children based on tab
            bool showSearchInfo = tab is "Apps" or "Games" or "Library";
            bool showList = tab == "Library";
            bool showSearchGrid = tab is "Apps" or "Games";
            SearchInfoGrid.Visibility = showSearchInfo ? Visibility.Visible : Visibility.Collapsed;
            ListContainerBorder.Visibility = showList || showSearchGrid ? Visibility.Visible : Visibility.Collapsed;
            LvApps.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            SearchGrid.Visibility = showSearchGrid ? Visibility.Visible : Visibility.Collapsed;
            if (SearchScroll != null) SearchScroll.Visibility = showSearchGrid ? Visibility.Visible : Visibility.Collapsed;
            LoadingSpinnerPanel.Visibility = tab == "Library" && _installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Visibility = Visibility.Collapsed;
            // Downloads inner panels
            if (tab == "Downloads")
            {
                var ups = _installed.Where(a => a.HasUpdate).ToList();
                LvDownloads.ItemsSource = ups;
                DownloadsUpdatesTitle.Text = ups.Count > 0 ? $"Atualizações disponíveis ({ups.Count})" : "Nenhuma atualização pendente";
                DownloadsUpdatesSection.Visibility = Visibility.Visible;
                TxtDownloadsEmpty.Text = ups.Count == 0 ? "Tudo atualizado!" : "";
                TxtDownloadsEmpty.Visibility = ups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async Task OnLoadedAsync()
        {
            Log("Detectando winget / choco / MS Store...");
            _wingetPath = StoreEngine.FindWingetPath();
            _chocoPath = StoreEngine.FindChoco();
            TxtWingetStatus.Text = $"winget: {(_wingetPath != null ? "OK" : "não encontrado")}  ·  choco: {(_chocoPath != null ? "OK" : "não encontrado")}";
            TxtWingetStatus.Foreground = new SolidColorBrush(_wingetPath != null ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0xFF, 0x8C, 0x00));
            await RefreshInstalledAsync(force: false);
        }

        private async Task RefreshInstalledAsync(bool force = false)
        {
            if (force) StoreEngine.InvalidateCache();
            // delega para overload existente mantendo compatibilidade
            await RefreshInstalledInternalAsync(force);
        }

        private async Task RefreshInstalledInternalAsync(bool force)
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) { Log("Operação em andamento — ignorando novo refresh."); return; }
            try
            {
                LoadingSpinnerPanel.Visibility = Visibility.Visible;
                TxtEmpty.Visibility = Visibility.Collapsed;
                LvApps.Visibility = Visibility.Collapsed;
                SearchGrid.Visibility = Visibility.Collapsed;
                TxtListInfo.Text = "";
                _installed.Clear();

                try
                {
                    // Usa cache TTL 3 min — Store real re-query sempre, Kit é 10× mais rápido no re-open
                    var installedTask = Task.Run(() => force ? StoreEngine.QueryWingetInstalled(_wingetPath) : StoreEngine.QueryWingetInstalledCached(_wingetPath, false));
                    var upgradesTask = Task.Run(() => StoreEngine.QueryWingetUpgrades(_wingetPath));
                    var chocoTask = Task.Run(() => StoreEngine.QueryChocoOutdated(_chocoPath));
                    var appxTask = Task.Run(() => StoreEngine.QueryAppxPackages());

                    await Task.WhenAll(installedTask, upgradesTask, chocoTask, appxTask);

                    var installed = installedTask.Result;
                    var upgrades = upgradesTask.Result;
                    var chocoUpgs = chocoTask.Result;
                    var appxList = appxTask.Result;

                    // Merge por Id com comparação semântica de versão (não lexicográfica)
                    var map = new Dictionary<string, StoreAppVM>(StringComparer.OrdinalIgnoreCase);
                    int dupCount = 0;
                    foreach (var a in installed)
                    {
                        var vm = ToVM(a);
                        var key = (vm.Id ?? vm.Name ?? "").Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!map.TryGetValue(key, out var existing))
                            map[key] = vm;
                        else
                        {
                            dupCount++;
                            // Duplicata (ex: directx, WindowsAppRuntime multi-arch) — mantém maior versão sem spam de log
                            if (StoreEngine.CompareVersions(vm.Version, existing.Version) > 0)
                                existing.Version = vm.Version;
                        }
                    }
                    if (dupCount > 0) Log($"Winget: {dupCount} duplicatas mescladas (multi-arch/fonte) — mantida maior versão");
                    // Marca upgrades
                    foreach (var u in upgrades)
                    {
                        var key = (u.Id ?? "").Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) continue;
                        if (map.TryGetValue(key, out var ex))
                        {
                            ex.AvailableVersion = u.AvailableVersion ?? "";
                            if (!string.IsNullOrEmpty(u.Version)) ex.Version = u.Version;
                        }
                        else
                        {
                            var vm = ToVM(u);
                            if (!map.ContainsKey(key)) map[key] = vm;
                        }
                    }
                    foreach (var u in chocoUpgs)
                    {
                        var key = (u.Id ?? "").Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(key)) continue;
                        if (map.TryGetValue(key, out var ex2))
                            ex2.AvailableVersion = u.AvailableVersion ?? "";
                        else
                        {
                            var vm = ToVM(u);
                            vm.Source = "choco";
                            if (!map.ContainsKey(key)) map[key] = vm;
                        }
                    }
                    // Popula coleção ordenada (updates primeiro, depois alfabético)
                    foreach (var kv in map.Values
                        .OrderByDescending(v => v.HasUpdate)
                        .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                        _installed.Add(kv);

                    TxtStoreCount.Text = appxList.Count.ToString();
                    TxtStoreStatus.Text = appxList.Count > 0 ? $"{appxList.Count} pacotes" : "0";
                    TxtInstalledCount.Text = _installed.Count.ToString();
                    TxtSourcesInfo.Text = $"winget:{installed.Count} choco:{chocoUpgs.Count} store:{appxList.Count}";
                    var ups = _installed.Count(a => a.HasUpdate);
                    TxtUpdatesCount.Text = ups.ToString();

                    _ = Task.Run(() => LoadIconsForList(_installed.Take(40).ToList()));
                    Log($"Instalados: {_installed.Count} | atualizações: {ups} | Store: {appxList.Count}");
                    if (_installed.Count == 0)
                        TxtEmpty.Text = "Nenhum app encontrado. Verifique se winget/choco estão instalados ou clique Buscar.";
                    NavLibCount.Text = _installed.Count > 0 ? $"Biblioteca · {_installed.Count}" : "Biblioteca";
                    // Mostra a aba correta (Home por padrão, Library se já estava lá)
                    if (_activeTab == StoreTab.Home || _activeTab == (StoreTab)0)
                        ShowHome();
                    else
                        ShowInstalled();
                }
                catch (Exception ex)
                {
                    TxtEmpty.Text = $"Erro ao carregar: {ex.Message}";
                    Log($"Erro refresh: {ex.Message}");
                }
            }
            finally
            {
                LoadingSpinnerPanel.Visibility = Visibility.Collapsed;
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

        private StoreAppVM ToVM(KitLugia.Core.KitStore.StoreApp src)
        {
            return new StoreAppVM
            {
                Name = src.Name ?? "",
                Id = src.Id ?? "",
                Publisher = src.Publisher ?? "",
                Version = src.Version ?? "",
                AvailableVersion = src.AvailableVersion ?? "",
                Source = string.IsNullOrEmpty(src.Source) ? "winget" : src.Source,
                Category = src.Category ?? "",
                Description = src.Description ?? "",
                Rating = src.Rating,
                RatingCount = src.RatingCount
            };
        }

        private void ShowHome()
        {
            ShowPanel("Home");
            PopulateHomeSections();
        }

        private void ShowInstalled()
        {
            ShowPanel("Library");
            if (_installed.Count == 0)
            {
                TxtEmpty.Text = "Nenhum app encontrado. Verifique se winget/choco estão instalados ou clique Buscar.";
                TxtEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                LvApps.ItemsSource = _installed;
                TxtListInfo.Text = $"{_installed.Count} apps · {_installed.Count(a => a.HasUpdate)} com atualização";
            }
        }

        private void BtnCategory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var tag = (sender as Button)?.Tag as string ?? "Todos";
                Log($"Filtro categoria: {tag}");
                if (tag == "Todos" || string.IsNullOrEmpty(tag))
                {
                    ShowInstalled();
                    return;
                }
                // Filtro simples por publisher/nome contendo o termo — evita varrer categoria inexistente no winget
                var q = tag.ToLowerInvariant();
                var filtered = _installed.Where(a => (a.Name + " " + a.Publisher + " " + a.Category).ToLowerInvariant().Contains(q)).ToList();
                if (filtered.Count == 0)
                {
                    TxtEmpty.Text = $"Nenhum app em \"{tag}\". Tente Buscar.";
                    TxtEmpty.Visibility = Visibility.Visible;
                    LvApps.Visibility = Visibility.Collapsed;
                    SearchGrid.Visibility = Visibility.Collapsed;
                    TxtListInfo.Text = "0 resultados";
                    return;
                }
                TxtEmpty.Visibility = Visibility.Collapsed;
                LvApps.Visibility = Visibility.Visible;
                SearchGrid.Visibility = Visibility.Collapsed;
                LvApps.ItemsSource = new ObservableCollection<StoreAppVM>(filtered);
                TxtListInfo.Text = $"{filtered.Count} em {tag}";
            }
            catch (Exception ex) { Log($"Filtro erro: {ex.Message}"); }
        }

        private void ShowSearch()
        {
            var tab = _activeTab == StoreTab.Games ? "Games" : "Apps";
            ShowPanel(tab);
            if (_searchResults.Count == 0)
            {
                TxtEmpty.Text = "Nenhum resultado. Digite um termo e clique Buscar.";
                TxtEmpty.Visibility = Visibility.Visible;
            }
            else
            {
                SearchGrid.ItemsSource = _searchResults;
                TxtListInfo.Text = $"{_searchResults.Count} resultados";
            }
        }

        // --- Delegam para StoreEngine (sem duplicar parsing) ---
        private static string RunCapture(string exe, string args, int timeoutMs) => StoreEngine.RunCapture(exe, args, timeoutMs);

        // --- Search ---
        private async Task DoSearchAsync()
        {
            SearchPopup.IsOpen = false;
            if (System.Threading.Interlocked.Exchange(ref _searchBusy, 1) == 1) { Log("Busca já em andamento — ignorando."); return; }
            try
            {
                var q = (TxtSearch.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q) && _showingInstalled) return;
                var src = (CmbSource.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Todos";
                ShowSearchBar();
                TxtSearchInfo.Text = "Buscando...";
                _searchResults.Clear();

                try
                {
                    if (string.IsNullOrWhiteSpace(q))
                    {
                        var ups = await Task.Run(() => StoreEngine.QueryWingetUpgrades(_wingetPath));
                        foreach (var a in ups) _searchResults.Add(ToVM(a));
                        var chocos = await Task.Run(() => StoreEngine.QueryChocoOutdated(_chocoPath));
                        foreach (var a in chocos) _searchResults.Add(ToVM(a));
                        TxtSearchInfo.Text = (ups.Count + chocos.Count) == 0 ? "Nenhuma atualização disponível." : $"{ups.Count + chocos.Count} atualização(ões) disponível(is).";
                        Log($"Busca vazia -> atualizações: {ups.Count} winget + {chocos.Count} choco");
                    }
                    else if (src.IndexOf("MS Store", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var appx = await Task.Run(() => StoreEngine.QueryAppxPackages());
                        var filtered = appx.Where(p => p.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(40)
                                           .Select(p => new StoreAppVM { Name = p.Split('_')[0], Id = p, Version = "", Source = "msstore" });
                        foreach (var a in filtered) _searchResults.Add(a);
                        TxtSearchInfo.Text = $"{_searchResults.Count} pacote(s) Store com \"{q}\"";
                    }
                    else if (src.IndexOf("winget", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var res = await Task.Run(() => StoreEngine.QueryWingetSearchLocal(_wingetPath, q));
                        foreach (var a in res) _searchResults.Add(ToVM(a));
                        TxtSearchInfo.Text = $"{res.Count} resultado(s) winget para \"{q}\"";
                    }
                    else if (src.IndexOf("chocolatey", StringComparison.OrdinalIgnoreCase) >= 0 || src.IndexOf("choco", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (string.IsNullOrEmpty(_chocoPath)) TxtSearchInfo.Text = "Chocolatey não encontrado.";
                        else
                        {
                            var output = await Task.Run(() => RunCapture($"\"{_chocoPath}\"", $"search \"{q}\" --limit-output --by-id-only --order-by-popularity --page 0 --page-size 30", 25000));
                            foreach (var raw in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var line = raw.Trim();
                                if (string.IsNullOrEmpty(line) || line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
                                var parts = line.Split('|');
                                var id = parts[0].Trim();
                                var ver = parts.Length > 1 ? parts[1].Trim() : "";
                                _searchResults.Add(new StoreAppVM { Name = id, Id = id, Version = ver, Source = "choco" });
                            }
                            TxtSearchInfo.Text = $"{_searchResults.Count} resultado(s) choco para \"{q}\"";
                        }
                    }
                    else // Todos
                    {
                        var w = await Task.Run(() => StoreEngine.QueryWingetSearchLocal(_wingetPath, q));
                        foreach (var a in w) _searchResults.Add(ToVM(a));
                        if (_chocoPath != null)
                        {
                            var output = await Task.Run(() => RunCapture($"\"{_chocoPath}\"", $"search \"{q}\" --limit-output --page-size 10", 20000));
                            foreach (var raw in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var line = raw.Trim();
                                if (string.IsNullOrEmpty(line) || line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
                                var parts = line.Split('|');
                                var id = parts[0].Trim();
                                if (_searchResults.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                                var ver = parts.Length > 1 ? parts[1].Trim() : "";
                                _searchResults.Add(new StoreAppVM { Name = id, Id = id, Version = ver, Source = "choco" });
                            }
                        }
                        TxtSearchInfo.Text = $"{_searchResults.Count} resultado(s) para \"{q}\" (winget+choco)";
                    }
                    // Sempre mostra como grid de cards para fidelidade Store
                    if (_searchResults.Count > 0)
                    {
                        ShowPanel("Apps");
                        SearchGrid.ItemsSource = _searchResults;
                        TxtListInfo.Text = $"{_searchResults.Count} resultados";
                        _ = Task.Run(() => LoadIconsForList(_searchResults.Take(24).ToList()));
                    }
                    else
                    {
                        ShowSearch();
                    }
                }
                catch (Exception ex)
                {
                    TxtSearchInfo.Text = $"Erro na busca: {ex.Message}";
                    Log($"Busca erro: {ex.Message}");
                }
            }
            finally
            {
                HideSearchBar();
                System.Threading.Interlocked.Exchange(ref _searchBusy, 0);
            }
        }

        // --- Actions ---
        private async Task UpgradeOneAsync(StoreAppVM app, bool forceStop)
        {
            if (app == null) return;
            var verb = app.HasUpdate ? "Atualizando" : "Instalando";
            Log($"{verb} {app.Id} ({app.Source}) force={forceStop}...");
            try
            {
                if (forceStop) ForceStopForApp(app);

                string exe, args;
                bool isInstall = string.IsNullOrEmpty(app.Version) || !_installed.Any(x => string.Equals(x.Id, app.Id, StringComparison.OrdinalIgnoreCase));
                if (app.Source.Equals("choco", StringComparison.OrdinalIgnoreCase))
                {
                    exe = _chocoPath ?? "choco";
                    args = isInstall ? $"install \"{app.Id}\" -y --no-progress" : $"upgrade \"{app.Id}\" -y --no-progress";
                }
                else if (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                {
                    try { Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?ProductId={app.Id}") { UseShellExecute = true }); Log($"Abrindo Store para {app.Id}"); } catch (Exception ex) { Log($"Falha abrir Store: {ex.Message}"); }
                    return;
                }
                else
                {
                    exe = _wingetPath ?? "winget";
                    if (isInstall)
                        args = $"install --id \"{app.Id}\" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
                    else
                        args = $"upgrade --id \"{app.Id}\" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
                }

                // Mostra barra flutuante + toast de progresso
                SetProgress(0, $"{verb} {app.Name}...", "Iniciando download/instalação...");
                ShowToastProgress($"store_{app.Id}", verb, $"{app.Name} — preparando...");

                var oem = KitLugia.Core.SystemUtils.GetOemEncoding();
                var psi = new ProcessStartInfo(exe.Trim('"'), args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = oem,
                    StandardErrorEncoding = oem
                };

                using var proc = new Process { StartInfo = psi };
                var sb = new StringBuilder();
                // Stream ao vivo: atualiza log + barra conforme winget/choco emite linhas
                proc.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    sb.AppendLine(e.Data);
                    OnInstallLine(app, e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    sb.AppendLine(e.Data);
                    OnInstallLine(app, e.Data);
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync();

                var output = sb.ToString();
                Log($"[{app.Id}] exit {proc.ExitCode}\n{Trunc(output, 1000)}");
                if (proc.ExitCode == 0)
                {
                    SetProgress(100, $"{app.Name} concluído.", "");
                    CompleteToastProgress($"store_{app.Id}", true, $"{(isInstall ? "Instalação" : "Atualização")} de {app.Name} concluída com sucesso.");
                }
                else
                {
                    SetProgress(0, $"{app.Name}: falhou (exit {proc.ExitCode}).", "");
                    CompleteToastProgress($"store_{app.Id}", false, $"{app.Name}: falhou (exit {proc.ExitCode}).");
                }

                HideProgressAfterDelay();
                await RefreshInstalledAsync(force: true);
            }
            catch (Exception ex)
            {
                Log($"Erro {(app.HasUpdate ? "upgrade" : "install")} {app.Id}: {ex.Message}");
                CompleteToastProgress($"store_{app.Id}", false, $"Erro ao processar {app.Name}: {ex.Message}");
                HideProgressAfterDelay();
            }
        }

        private double _lastPct = -1;
        private System.Windows.Threading.DispatcherTimer? _progressHideTimer;
        private System.Windows.Threading.DispatcherTimer? _searchAnimTimer;

        // Processa cada linha de saída do winget/choco: atualiza barra (%) e log ao vivo.
        private void OnInstallLine(StoreAppVM app, string line)
        {
            var t = line.Trim();
            if (t.Length == 0) return;
            // Loga linhas significativas no painel de atividade (pula barras de progresso)
            if (t.Length > 0 && t[0] != '\u2588' && t[0] != '\u2591' && t[0] != '\u2592' && t[0] != '\u2593')
            {
                try { Log($"[{app.Name}] {Trunc(t, 200)}"); } catch { }
            }
            // Detecta fases do winget
            var phase = DetectWingetPhase(t);
            var result = TryParseProgress(t);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (result != null)
                    {
                        var p = Math.Max(0, Math.Min(100, result.Percentage));
                        if (Math.Abs(p - _lastPct) >= 1 || p >= 100 || p <= 0)
                        {
                            _lastPct = p;
                            var detail = result.Detail ?? (t.Length > 90 ? t.Substring(0, 90) + "…" : t);
                            SetProgress(p, $"Atualizando {app.Name}...", detail);
                        }
                        else if (result.Detail != null)
                        {
                            SetProgressDetail(result.Detail);
                        }
                    }
                    else if (phase != null)
                    {
                        SetProgress(_lastPct >= 0 ? _lastPct : 0, phase, t.Length > 90 ? t.Substring(0, 90) + "…" : t);
                    }
                    else
                    {
                        SetProgressDetail(t);
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // Detecta fases do winget: Downloading, Installing, Verifying
        private static string? DetectWingetPhase(string line)
        {
            if (line.IndexOf("Downloading", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Baixando...";
            if (line.IndexOf("Installing", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Instalando...";
            if (line.IndexOf("Verifying", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Verificando...";
            if (line.IndexOf("Starting", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Iniciando...";
            return null;
        }

        private class ProgressResult
        {
            public double Percentage;
            public string? Detail;
        }

        // Parse o progresso do winget/choco: "X MB / Y MB" / "XX%" / "XX %"
        private static ProgressResult? TryParseProgress(string line)
        {
            try
            {
                // Padrão winget: "██████▎ 20.0 MB / 94.8 MB" ou "269 MB / 305 MB "
                var mMB = System.Text.RegularExpressions.Regex.Match(line, @"(\d+[\.,]?\d*)\s*(MB|GB|KB)\s*/\s*(\d+[\.,]?\d*)\s*(MB|GB|KB)");
                if (mMB.Success)
                {
                    double current = ParseSize(mMB.Groups[1].Value, mMB.Groups[2].Value);
                    double total = ParseSize(mMB.Groups[3].Value, mMB.Groups[4].Value);
                    if (total > 0)
                    {
                        var pct = (current / total) * 100.0;
                        var detail = $"{FormatSize(current)} / {FormatSize(total)}";
                        return new ProgressResult { Percentage = pct, Detail = detail };
                    }
                }
                // Padrão: "45%" ou "45 %"
                var mPct = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})\s*%");
                if (mPct.Success && double.TryParse(mPct.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct2))
                    return new ProgressResult { Percentage = pct2, Detail = null };
                // Padrão choco: "Progress: 45%"
                var mProg = System.Text.RegularExpressions.Regex.Match(line, @"(?:progress|Progress)[^\d]{0,10}(\d{1,3})");
                if (mProg.Success && double.TryParse(mProg.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var p2))
                    return new ProgressResult { Percentage = p2, Detail = null };
            }
            catch { }
            return null;
        }

        private static double ParseSize(string value, string unit)
        {
            var v = double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            return unit.ToUpperInvariant() switch
            {
                "GB" => v * 1024,
                "MB" => v,
                "KB" => v / 1024,
                _ => v
            };
        }

        private static string FormatSize(double mb)
        {
            if (mb >= 1024) return $"{mb / 1024:F1} GB";
            if (mb >= 1) return $"{mb:F1} MB";
            return $"{mb * 1024:F0} KB";
        }

        private void SetProgress(double pct, string status, string detail)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Animação de entrada no primeiro show
                    if (InlineProgressPanel.Visibility != Visibility.Visible)
                    {
                        InlineProgressPanel.Opacity = 0;
                        var tt = new System.Windows.Media.TranslateTransform(30, 0);
                        InlineProgressPanel.RenderTransform = tt;
                        InlineProgressPanel.Visibility = Visibility.Visible;
                        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
                        var slideIn = new System.Windows.Media.Animation.DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(350));
                        slideIn.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                        InlineProgressPanel.BeginAnimation(System.Windows.UIElement.OpacityProperty, fadeIn);
                        tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideIn);
                    }
                    // Mede a largura real do track — fallback 600 se ainda não mediu
                    double w = InlineProgressTrack.ActualWidth > 10 ? InlineProgressTrack.ActualWidth : 600;
                    InlineProgressFill.Width = Math.Max(0, (pct / 100.0) * w);
                    TxtInlinePercent.Text = pct > 0 ? $"{pct:F0}%" : "";
                    if (!string.IsNullOrEmpty(status)) TxtInlineStatus.Text = status;
                    if (!string.IsNullOrEmpty(detail)) TxtInlineDetail.Text = detail.Length > 100 ? detail.Substring(0, 100) + "…" : detail;
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }
        private void SetProgressDetail(string detail)
        {
            try { Dispatcher.BeginInvoke(new Action(() => { TxtInlineDetail.Text = detail.Length > 100 ? detail.Substring(0, 100) + "…" : detail; }), System.Windows.Threading.DispatcherPriority.Background); } catch { }
        }
        private void HideProgressAfterDelay()
        {
            try
            {
                if (_progressHideTimer == null)
                {
                    _progressHideTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    _progressHideTimer.Tick += (s, e) => { _progressHideTimer.Stop(); InlineProgressPanel.Visibility = Visibility.Collapsed; _lastPct = -1; };
                }
                _progressHideTimer.Stop();
                _progressHideTimer.Start();
            }
            catch { }
        }

        private void ForceStopForApp(StoreAppVM app)
        {
            try
            {
                var keywords = new[] { app.Id, app.Name }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.ToLowerInvariant()).ToArray();
                if (keywords.Length == 0) return;
                var procs = Process.GetProcesses();
                int killed = 0;
                foreach (var p in procs)
                {
                    string procName = "";
                    try { procName = p.ProcessName.ToLowerInvariant(); } catch { try { p.Dispose(); } catch { } continue; }
                    bool match = keywords.Any(k => procName.Contains(SanitizeKeyword(k)) && SanitizeKeyword(k).Length >= 3);
                    if (!match)
                    {
                        try
                        {
                            var fn = p.MainModule?.FileName?.ToLowerInvariant() ?? "";
                            match = keywords.Any(k => !string.IsNullOrEmpty(fn) && fn.Contains(SanitizeKeyword(k)) && SanitizeKeyword(k).Length >= 3);
                        }
                        catch { }
                    }
                    if (match)
                    {
                        try { p.Kill(); killed++; Log($"ForceStop matou {procName} ({p.Id}) para {app.Id}"); } catch (Exception ex) { Log($"Falha matar {procName}: {ex.Message}"); }
                    }
                    try { p.Dispose(); } catch { }
                }
                if (killed == 0)
                    Log($"ForceStop: nenhum processo correspondente a {app.Id} (normal se app não estava em execução).");
                else
                    System.Threading.Thread.Sleep(700);
            }
            catch (Exception ex) { Log($"ForceStop erro: {ex.Message}"); }
        }

        private static string SanitizeKeyword(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var part = s.Split(new[] { '.', '-', '_', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => p.Length >= 3)
                        .OrderByDescending(p => p.Length)
                        .FirstOrDefault() ?? s;
            return part.ToLowerInvariant();
        }

        // --- Handlers ---
        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshInstalledAsync(force: true);
        private void BtnNavHome_Click(object sender, RoutedEventArgs e) => SwitchTab(StoreTab.Home);
        private async void HeroMainBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) await UpgradeOneAsync(app, false);
        }
        private void BtnTabInstalled_Click(object sender, RoutedEventArgs e) { _activeTab = StoreTab.Library; ShowInstalled(); HighlightNav(StoreTab.Library); }
        private void HomeInstalledItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is StoreAppVM app)
                ShowAppDetail(app);
        }
        private void BtnTabSearch_Click(object sender, RoutedEventArgs e) { _activeTab = StoreTab.Apps; ShowSearch(); HighlightNav(StoreTab.Apps); }
        private async void BtnSearch_Click(object sender, RoutedEventArgs e) { System.Threading.Interlocked.Exchange(ref _searchBusy, 0); await DoSearchAsync(); }
        private async void BtnUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) await UpgradeOneAsync(app, false);
        }
        private async void BtnForceUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) await UpgradeOneAsync(app, true);
        }
        private void BtnOpenApp_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app)
                ShowAppDetail(app);
        }

        // ── App detail modal ──
        private StoreAppVM? _detailApp;
        private void ShowAppDetail(StoreAppVM app)
        {
            _detailApp = app;
            DetailName.Text = app.Name;
            DetailPublisher.Text = app.Publisher;
            DetailSource.Text = app.Source;
            DetailId.Text = app.Id;
            DetailCategory.Text = string.IsNullOrEmpty(app.Category) ? "—" : app.Category;
            DetailVersion.Text = string.IsNullOrEmpty(app.Version) ? "" : $"Versão {app.Version}";
            DetailDescription.Text = string.IsNullOrEmpty(app.Description) ? "Sem descrição disponível." : app.Description;
            // Update info
            if (app.HasUpdate)
            {
                DetailUpdateInfo.Visibility = Visibility.Visible;
                DetailUpdateVersion.Text = $"{app.Version} → {app.AvailableVersion}";
                DetailActionBtn.Content = "Atualizar";
                DetailActionBtn.Visibility = Visibility.Visible;
                DetailForceBtn.Visibility = Visibility.Visible;
            }
            else
            {
                DetailUpdateInfo.Visibility = Visibility.Collapsed;
                DetailActionBtn.Content = "Abrir";
                DetailActionBtn.Visibility = Visibility.Visible;
                DetailForceBtn.Visibility = Visibility.Collapsed;
            }
            // Uninstall
            DetailUninstallBtn.Visibility = Visibility.Visible;
            // Load icon
            DetailIcon.Visibility = Visibility.Collapsed;
            DetailFallbackIcon.Visibility = Visibility.Visible;
            _ = Task.Run(() =>
            {
                var ic = TryResolveIconPath(app);
                if (ic != null) Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var bmp = Helpers.ProgramIconHelper.GetIconFromFile(ic);
                        if (bmp != null) { DetailIcon.Source = bmp; DetailIcon.Visibility = Visibility.Visible; DetailFallbackIcon.Visibility = Visibility.Collapsed; }
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            });
            DetailOverlay.Visibility = Visibility.Visible;
        }
        private void DetailClose_Click(object sender, RoutedEventArgs e) => DetailOverlay.Visibility = Visibility.Collapsed;
        private void DetailOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Clicar fora do card fecha o modal
            if (sender is FrameworkElement fe && e.OriginalSource == fe)
                DetailOverlay.Visibility = Visibility.Collapsed;
        }
        private async void DetailAction_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            DetailOverlay.Visibility = Visibility.Collapsed;
            if (_detailApp.HasUpdate)
                await UpgradeOneAsync(_detailApp, false);
            else
            {
                if (_detailApp.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    try { Process.Start(new ProcessStartInfo($"ms-windows-store://pdp/?ProductId={_detailApp.Id}") { UseShellExecute = true }); } catch { }
                else
                    await UpgradeOneAsync(_detailApp, false);
            }
        }
        private async void DetailForce_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            DetailOverlay.Visibility = Visibility.Collapsed;
            await UpgradeOneAsync(_detailApp, true);
        }
        private async void DetailUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            var app = _detailApp;
            DetailOverlay.Visibility = Visibility.Collapsed;
            if (MessageBox.Show($"Desinstalar {app.Name}?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Log($"Desinstalando {app.Id} ({app.Source})...");
            try
            {
                string exe, args;
                if (app.Source.Equals("choco", StringComparison.OrdinalIgnoreCase))
                {
                    exe = _chocoPath ?? "choco";
                    args = $"uninstall \"{app.Id}\" -y --no-progress";
                }
                else
                {
                    exe = _wingetPath ?? "winget";
                    args = $"uninstall --id \"{app.Id}\" --silent --accept-source-agreements --disable-interactivity";
                }
                var psi = new ProcessStartInfo(exe.Trim('"'), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                await proc.WaitForExitAsync();
                Log($"[{app.Id}] uninstall exit {proc.ExitCode}");
                await RefreshInstalledAsync(force: true);
            }
            catch (Exception ex) { Log($"Desinstalar erro: {ex.Message}"); }
        }
        private async void BtnUpgradeAllForce_Click(object sender, RoutedEventArgs e)
        {
            var ups = _installed.Where(a => a.HasUpdate).ToList();
            if (ups.Count == 0) { ShowToastInfo("Nenhuma atualização pendente."); return; }
            if (MessageBox.Show($"Atualizar {ups.Count} app(s) com Force Stop (matará processos em uso)?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ShowToastProgress("store_batch", "Atualizando tudo", $"{ups.Count} app(s) pendente(s)...");
            int done = 0;
            foreach (var app in ups)
            {
                await UpgradeOneAsync(app, true);
                done++;
                UpdateToastProgress("store_batch", $"{done} de {ups.Count} concluído(s) — {app.Name}");
            }
            CompleteToastProgress("store_batch", true, $"{ups.Count} app(s) atualizado(s) com Force Stop.");
        }
        private void BtnWsreset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Executando wsreset.exe (limpa cache MS Store)...");
                Process.Start(new ProcessStartInfo("wsreset.exe") { UseShellExecute = true });
                Log("wsreset iniciado — aguarde a Store reabrir.");
            }
            catch (Exception ex) { Log($"wsreset erro: {ex.Message}"); MessageBox.Show(ex.Message, "wsreset"); }
        }
        private async void BtnRepairStore_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reparar MS Store? Isso vai re-registrar os pacotes da Store (Get-AppXPackage) e pode levar ~1 min. Continuar?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ShowSearchBar();
            Log("Reparando MS Store (re-registrando pacotes)...");
            try
            {
                var ps = "powershell.exe";
                var args = "-NoProfile -ExecutionPolicy Bypass -Command \"Get-AppXPackage -AllUsers | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppXManifest.xml\\\"} \"";
                var outp = await Task.Run(() => RunCapture(ps, args, 90000));
                Log(Trunc(outp, 1500));
                MessageBox.Show("Reparo da Store concluído. Reinicie o PC se a Store ainda falhar.", "Store Remake");
            }
            catch (Exception ex) { Log($"Reparo Store erro: {ex.Message}"); }
            finally { HideSearchBar(); }
        }
        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw && mw.IsVisible)
            { mw.NavigateToPage(PageType.Windows); return; }
            var w = Window.GetWindow(this);
            if (w is Windows.KitStore.KitStoreWindow) w.Close(); else if (w != null) w.Close();
            if (Application.Current.MainWindow is MainWindow mw2) mw2.NavigateToPage(PageType.Windows);
        }

        private void BtnPopOut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                KitLugia.GUI.Windows.KitStore.KitStoreWindow.ShowStandalone();
                Log("KitStore aberta em janela separada (pasta dedicada KitStore, standalone).");
            }
            catch (Exception ex) { Log($"Pop-out erro: {ex.Message}"); MessageBox.Show(ex.Message, "KitStore"); }
        }

        private async void BtnCheckPhantoms_Click(object sender, RoutedEventArgs e)
        {
            TxtPhantomSummary.Text = " — verificando...";
            PhantomPanel.Visibility = Visibility.Visible;
            TxtPhantomLog.Text = "Varrendo AppxAllUserStore, StateChange, PendingDeletions, ContentDeliveryManager e ScanForUpdates...\n";
            ShowSearchBar();
            try
            {
                var report = await Task.Run(() => StoreEngine.BuildPhantomReport());
                TxtPhantomLog.Text = report;
                var issues = report.Contains("FANTASMA") || report.Contains("CORROMPIDO") || report.Contains("PENDENTE") || report.Contains("fantasma") ? " — problemas encontrados" : " — nenhum fantasma";
                TxtPhantomSummary.Text = issues;
                TxtPhantomSummary.Foreground = issues.Contains("problemas") ? new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)) : new SolidColorBrush(Color.FromRgb(0x7F, 0xBA, 0x00));
                Log($"Verificação fantasmas concluída: {issues.Trim()}");
            }
            catch (Exception ex) { TxtPhantomLog.Text += $"\nErro: {ex.Message}"; Log($"Check phantoms erro: {ex.Message}"); }
            finally { HideSearchBar(); }
        }

        private async void BtnBlockMinecraftPreview_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Bloquear Minecraft Preview Demo que volta sozinho?\n\nIsso vai:\n• Deprovisionar o pacote (Remove-AppxProvisionedPackage)\n• Remover para todos usuários\n• Criar Deprovisioned no registry\n• Pin blocking no winget\n• Desativar SubscribedContent-310093 (ContentDeliveryManager)\n\nContinuar?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ShowSearchBar();
            Log("Bloqueando Minecraft Preview fantasma...");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(RunCapture("powershell.exe", "-NoProfile -Command \"Get-AppxProvisionedPackage -Online | Where DisplayName -like '*MinecraftPreview*' | Remove-AppxProvisionedPackage -Online 2>&1 | Out-String\"", 30000));
                sb.AppendLine(RunCapture("powershell.exe", "-NoProfile -Command \"Get-AppxPackage -AllUsers *MinecraftPreview* | Remove-AppxPackage -AllUsers 2>&1 | Out-String\"", 30000));
                try
                {
                    using var k1 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\Microsoft.MinecraftPreview_8wekyb3d8bbwe");
                    k1?.SetValue("Deprovisioned", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    sb.AppendLine("Deprovisioned: OK");
                }
                catch (Exception ex) { sb.AppendLine($"Deprovisioned registry falhou: {ex.Message} (rode como admin)"); }
                try
                {
                    using var k2 = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
                    k2?.SetValue("SubscribedContent-310093Enabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    k2?.SetValue("SilentInstalledAppsEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    sb.AppendLine("ContentDeliveryManager SubscribedContent-310093Enabled=0: OK");
                }
                catch (Exception ex) { sb.AppendLine($"ContentDeliveryManager falhou: {ex.Message}"); }
                if (_wingetPath != null)
                {
                    var pinOut = await Task.Run(() => RunCapture($"\"{_wingetPath}\"", "pin add --id Microsoft.MinecraftPreview --blocking-pin --accept-source-agreements 2>&1", 15000));
                    sb.AppendLine("winget pin: " + Trunc(pinOut, 400));
                }
                else sb.AppendLine("winget pin: winget não encontrado, pulei");
                try
                {
                    using var k3 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\WindowsStore");
                    sb.AppendLine($"WindowsStore AutoDownload atual: {k3?.GetValue("AutoDownload") ?? "(não definido)"}");
                }
                catch { }
                TxtPhantomLog.Text = sb.ToString();
                PhantomPanel.Visibility = Visibility.Visible;
                Log("Minecraft Preview bloqueado. Reinicie e verifique pendências novamente.");
                MessageBox.Show("Minecraft Preview bloqueado. Reinicie o PC e clique em Verificar pendências para confirmar.", "Store Remake", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Log($"Block Minecraft erro: {ex.Message}"); MessageBox.Show(ex.Message, "Store Remake"); }
            finally { HideSearchBar(); }
        }

        // --- Abas ---
        private void HighlightNav(StoreTab tab)
        {
            try
            {
                var on = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
                var off = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
                // Borda + fonte destacada
                BtnNavHome.Background = tab == StoreTab.Home ? on : off;
                BtnNavApps.Background = tab == StoreTab.Apps ? on : off;
                BtnNavGames.Background = tab == StoreTab.Games ? on : off;
                BtnNavLibrary.Background = tab == StoreTab.Library ? on : off;
                try { BtnNavDownloads.Background = tab == StoreTab.Downloads ? on : off; } catch { }
                BtnNavHome.Foreground = tab == StoreTab.Home ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                BtnNavApps.Foreground = tab == StoreTab.Apps ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                BtnNavGames.Foreground = tab == StoreTab.Games ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                BtnNavLibrary.Foreground = tab == StoreTab.Library ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
                try { BtnNavDownloads.Foreground = tab == StoreTab.Downloads ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)); } catch { }
            }
            catch { }
        }
        private void ShowDownloads() => ShowPanel("Downloads");

        private void SwitchTab(StoreTab tab) => ShowPanel(tab.ToString());

        private void BtnTabGames_Click(object sender, RoutedEventArgs e) => SwitchTab(StoreTab.Games);
        private void BtnNavDownloads_Click(object sender, RoutedEventArgs e) => SwitchTab(StoreTab.Downloads);

        // --- Home tab: popula as seções com dados reais ---
        private void PopulateHomeSections()
        {
            try
            {
                // Stats
                HomeStatLibrary.Text = _installed.Count.ToString();
                HomeStatUpdates.Text = _installed.Count(a => a.HasUpdate).ToString();
                HomeStatStore.Text = TxtStoreCount.Text;
                HomeUpdatesCount.Text = $"({_installed.Count(a => a.HasUpdate)} disponível(is))";

                // Hero: pega os3 apps com atualização (ou os3 maiores)
                var featured = _installed.Where(a => a.HasUpdate).Take(3).ToList();
                if (featured.Count < 3)
                    featured.AddRange(_installed.Where(a => !featured.Contains(a)).Take(3 - featured.Count));
                if (featured.Count > 0)
                {
                    var main = featured[0];
                    HeroMainName.Text = main.Name;
                    HeroMainPublisher.Text = main.Publisher;
                    HeroMainVersion.Text = main.HasUpdate ? $"{main.Version} \u2192 {main.AvailableVersion}" : main.Version;
                    HeroMainSource.Text = main.HasUpdate ? "ATUALIZAÇÃO DISPONÍVEL" : main.Source.ToUpperInvariant();
                    HeroMainBtn.Tag = main;
                    HeroMainBtn.Content = main.HasUpdate ? "Atualizar" : "Abrir";
                    HeroCardMain.Tag = main;
                    _ = Task.Run(() => { var ic = TryResolveIconPath(main); if (ic != null) Dispatcher.BeginInvoke(new Action(() => { HeroMainIcon.Visibility = Visibility.Collapsed; HeroMainImage.Visibility = Visibility.Visible; HeroMainImage.Source = Helpers.ProgramIconHelper.GetIconFromFile(ic); }), System.Windows.Threading.DispatcherPriority.Background); });
                }
                if (featured.Count > 1)
                {
                    var s1 = featured[1];
                    HeroSide1Name.Text = s1.Name;
                    HeroSide1Publisher.Text = s1.Publisher;
                    HeroSide1Badge.Text = s1.HasUpdate ? $"{s1.Version} \u2192 {s1.AvailableVersion}" : s1.Source;
                    HeroSide1Card.Cursor = System.Windows.Input.Cursors.Hand;
                    HeroSide1Card.Tag = s1;
                    _ = Task.Run(() => { var ic = TryResolveIconPath(s1); if (ic != null) Dispatcher.BeginInvoke(new Action(() => { HeroSide1Icon.Visibility = Visibility.Collapsed; HeroSide1Image.Visibility = Visibility.Visible; HeroSide1Image.Source = Helpers.ProgramIconHelper.GetIconFromFile(ic); }), System.Windows.Threading.DispatcherPriority.Background); });
                }
                if (featured.Count > 2)
                {
                    var s2 = featured[2];
                    HeroSide2Name.Text = s2.Name;
                    HeroSide2Publisher.Text = s2.Publisher;
                    HeroSide2Badge.Text = s2.HasUpdate ? $"{s2.Version} \u2192 {s2.AvailableVersion}" : s2.Source;
                    HeroSide2Card.Cursor = System.Windows.Input.Cursors.Hand;
                    HeroSide2Card.Tag = s2;
                    _ = Task.Run(() => { var ic = TryResolveIconPath(s2); if (ic != null) Dispatcher.BeginInvoke(new Action(() => { HeroSide2Icon.Visibility = Visibility.Collapsed; HeroSide2Image.Visibility = Visibility.Visible; HeroSide2Image.Source = Helpers.ProgramIconHelper.GetIconFromFile(ic); }), System.Windows.Threading.DispatcherPriority.Background); });
                }

                // Updates section: cards horizontais
                HomeUpdatesPanel.Children.Clear();
                foreach (var app in _installed.Where(a => a.HasUpdate).Take(12))
                    HomeUpdatesPanel.Children.Add(MakeHomeAppCard(app, true));
                if (HomeUpdatesPanel.Children.Count == 0)
                    HomeUpdatesPanel.Children.Add(new TextBlock { Text = "Tudo atualizado!", Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)), FontSize = 12, Margin = new Thickness(0, 8, 0, 0) });

                // Popular section: pega apps conhecidos
                var popular = _installed.Where(a => IsPopularApp(a.Name)).Take(12).ToList();
                if (popular.Count < 6)
                    popular.AddRange(_installed.Where(a => !popular.Contains(a)).Take(12 - popular.Count));
                HomePopularPanel.Children.Clear();
                foreach (var app in popular)
                    HomePopularPanel.Children.Add(MakeHomeAppCard(app, false));

                // Installed grid: primeiros15
                var recent = _installed.Take(15).ToList();
                HomeInstalledGrid.ItemsSource = recent;
                HomeInstalledSpinner.Visibility = Visibility.Collapsed;
                HomeEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (recent.Count == 0) HomeEmptyText.Text = "Nenhum app instalado.";
                _ = Task.Run(() => LoadIconsForList(recent));
            }
            catch (Exception ex) { Log($"Home populate erro: {ex.Message}"); }
        }

        private static bool IsPopularApp(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var n = name.ToLowerInvariant();
            return n.Contains("chrome") || n.Contains("firefox") || n.Contains("edge") || n.Contains("vscode") ||
                   n.Contains("visual studio") || n.Contains("git") || n.Contains("python") || n.Contains("node") ||
                   n.Contains("notepad") || n.Contains("7-zip") || n.Contains("winrar") || n.Contains("obs") ||
                   n.Contains("steam") || n.Contains("discord") || n.Contains("spotify") || n.Contains("docker") ||
                   n.Contains("postman") || n.Contains("slack") || n.Contains("telegram") || n.Contains("whatsapp") ||
                   n.Contains("adobe") || n.Contains("java") || n.Contains("dotnet") || n.Contains("microsoft.");
        }

        private Border MakeHomeAppCard(StoreAppVM app, bool showUpdate)
        {
            var card = new Border
            {
                Width = 180, Height = 64, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var iconBorder = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32))
            };
            var iconText = new TextBlock
            {
                Text = "\uE772", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconText;
            Grid.SetColumn(iconBorder, 0);
            var info = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = app.Name, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            if (showUpdate && app.HasUpdate)
                info.Children.Add(new TextBlock { Text = $"{app.Version} \u2192 {app.AvailableVersion}", FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)), Margin = new Thickness(0, 2, 0, 0) });
            else
                info.Children.Add(new TextBlock { Text = app.Source, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)), Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(info, 1);
            grid.Children.Add(iconBorder);
            grid.Children.Add(info);
            card.Child = grid;
            card.MouseLeftButtonDown += (s, e) => _ = UpgradeOneAsync(app, false);
            // Load real icon
            _ = Task.Run(() => { var ic = TryResolveIconPath(app); if (ic != null) Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var bmp = Helpers.ProgramIconHelper.GetIconFromFile(ic);
                    if (bmp != null)
                    {
                        iconBorder.Child = null;
                        var img = new System.Windows.Controls.Image { Source = bmp, Width = 36, Height = 36, Stretch = System.Windows.Media.Stretch.Uniform };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        iconBorder.Child = img;
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background); });
            return card;
        }

        // --- Search dropdown (live suggestions) ---
        private void UpdateSearchDropdown(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query) || query.Length < 2) { SearchPopup.IsOpen = false; return; }
                var matches = _installed.Where(a =>
                    (a.Name != null && a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Id != null && a.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                ).Take(8).ToList();
                if (matches.Count == 0) { SearchPopup.IsOpen = false; return; }
                SearchDropdownResults.Children.Clear();
                foreach (var app in matches)
                {
                    var row = new Border { Padding = new Thickness(10, 8, 10, 8), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
                    row.MouseEnter += (s, e) => row.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
                    row.MouseLeave += (s, e) => row.Background = System.Windows.Media.Brushes.Transparent;
                    row.MouseLeftButtonDown += (s, e) => { TxtSearch.Text = app.Name; SearchPopup.IsOpen = false; System.Threading.Interlocked.Exchange(ref _searchBusy, 0); _ = DoSearchAsync(); };
                    var rg = new Grid();
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) };
                    icon.Child = new TextBlock { Text = "\uE772", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(icon, 0);
                    var txt = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    txt.Children.Add(new TextBlock { Text = app.Name, FontSize = 13, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    txt.Children.Add(new TextBlock { Text = app.HasUpdate ? $"{app.Source} \u2022 atualização disponível" : app.Source, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)) });
                    Grid.SetColumn(txt, 1);
                    rg.Children.Add(icon);
                    rg.Children.Add(txt);
                    row.Child = rg;
                    SearchDropdownResults.Children.Add(row);
                }
                SearchPopup.PlacementTarget = TxtSearch;
                SearchPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                SearchPopup.IsOpen = true;
            }
            catch { }
        }

        private async void BtnFix73CFB_Click(object sender, RoutedEventArgs e)
        {
            var target = Microsoft.VisualBasic.Interaction.InputBox("Pacote travado (família ou nome completo). Ex: MinecraftUWP, Minecraft, Microsoft.MinecraftUWP\nDeixe vazio para varredura geral de 0x80073CFB:", "Corrigir 0x80073CFB — supera Store", "Minecraft");
            if (target == null) return;
            target = target.Trim();
            // Se cancelar no InputBox retorna "" — tratamos como varredura geral se vazio
            ShowSearchBar();
            PhantomPanel.Visibility = Visibility.Visible;
            TxtPhantomLog.Text = $"Varrendo pacotes travados 0x80073CFB{(string.IsNullOrEmpty(target) ? "" : $" com filtro '{target}'")}...\n";
            try
            {
                var stuck = await Task.Run(() => StoreEngine.DetectStuckPackages(string.IsNullOrEmpty(target) ? null : target));
                if (stuck.Count == 0)
                {
                    TxtPhantomLog.Text += "Nenhum pacote travado encontrado. Tente filtro vazio ou verifique AppXDeployment logs.\n";
                    var rep = await Task.Run(() => StoreEngine.BuildPhantomReport());
                    TxtPhantomLog.Text += "\n" + rep;
                }
                else
                {
                    TxtPhantomLog.Text += $"Encontrados {stuck.Count} travado(s):\n";
                    foreach (var s in stuck.Take(10))
                        TxtPhantomLog.Text += $" • {s.FullName} — {s.Reason}\n";
                    TxtPhantomLog.Text += "\nCorrigindo (PackageStatus→0 + Remove -AllUsers + Deprovision + restart serviços)...\n";
                    foreach (var s in stuck.Take(3))
                    {
                        var log = await Task.Run(() => StoreEngine.FixStuckPackage(s.Family.Length > 2 ? s.Family : s.FullName, true));
                        TxtPhantomLog.Text += $"\n--- {s.Family} ---\n" + log + "\n";
                        Log($"Fix {s.Family} concluído");
                    }
                    TxtPhantomLog.Text += "\nFix concluído. Reinicie e tente instalar novamente. Se o Minecraft ainda falhar, use 'Reparar Store' + reboot.";
                    if (MessageBox.Show($"{stuck.Count} pacote(s) corrigido(s). Reiniciar agora para finalizar limpeza pendente?", "Corrigir 0x80073CFB", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 5") { UseShellExecute = true }); } catch { }
                    }
                }
                TxtPhantomSummary.Text = stuck.Count == 0 ? " — nenhum travado" : $" — {stuck.Count} corrigido(s)";
                TxtPhantomSummary.Foreground = stuck.Count == 0 ? new SolidColorBrush(Color.FromRgb(0x7F, 0xBA, 0x00)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
            }
            catch (Exception ex) { TxtPhantomLog.Text += $"\nErro: {ex.Message}"; Log($"Fix 0x80073CFB erro: {ex.Message}"); }
            finally { HideSearchBar(); }
        }
        private void LvApps_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            RouteWheel(e, MainScroll, LvApps);
        }
        private void MainScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) { }

        private void LogScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            RouteWheel(e, MainScroll, LogScroll);
        }
        private void SearchScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            RouteWheel(e, MainScroll, SearchScroll);
        }
        private void LvDownloads_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            RouteWheel(e, MainScroll, LvDownloads);
        }
        // Rola o ScrollViewer interno quando puder; ao chegar no topo/fim, repassa o restante ao MainScroll.
        // Isso destrava o scroll em QUALQUER ponto (meio da tela, sobre card/item/log) igual ao TaskManager.
        private void RouteWheel(System.Windows.Input.MouseWheelEventArgs e, ScrollViewer outer, FrameworkElement innerOwner)
        {
            if (e.Handled) return;
            var delta = e.Delta;
            var sv = FindVisualChild<ScrollViewer>(innerOwner);
            if (sv != null && sv.ExtentHeight > sv.ViewportHeight)
            {
                bool atTop = sv.VerticalOffset <= 0 && delta > 0;
                bool atBottom = sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 0.5 && delta < 0;
                if (atTop || atBottom)
                {
                    e.Handled = true;
                    outer.ScrollToVerticalOffset(outer.VerticalOffset - delta / 3.0);
                }
                // senão deixa o ScrollViewer interno rolar normalmente (não marca Handled)
            }
            else if (sv != null)
            {
                // interno não tem conteúdo a rolar -> repassa tudo ao MainScroll
                e.Handled = true;
                outer.ScrollToVerticalOffset(outer.VerticalOffset - delta / 3.0);
            }
            else if (outer != null)
            {
                e.Handled = true;
                outer.ScrollToVerticalOffset(outer.VerticalOffset - delta / 3.0);
            }
        }
        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string txt = TxtLog?.Text ?? "";
                if (string.IsNullOrEmpty(txt)) return;
                System.Windows.Clipboard.SetText(txt);
                Log("Log copiado para área de transferência.");
                try { KitLugia.Core.Logger.Log("[STORE] Log copiado (" + txt.Length + " chars)"); } catch { }
            }
            catch (Exception ex) { Log("Falha ao copiar: " + ex.Message); }
        }
        private void BtnInstallCard_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) _ = UpgradeOneAsync(app, false);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var r = FindVisualChild<T>(child);
                if (r != null) return r;
            }
            return null;
        }

        // --- Ícones (cacheado, sem varrer registry N vezes) ---
        private class UninstallInfo
        {
            public string DisplayName = "";
            public string DisplayIcon = "";
            public string InstallLocation = "";
            public string UninstallString = "";
        }

        private Dictionary<string, UninstallInfo> GetUninstallCache()
        {
            if (_uninstallCache != null && (DateTime.UtcNow - _uninstallCacheTime).TotalMinutes < 5) return _uninstallCache;
            var dict = new Dictionary<string, UninstallInfo>(StringComparer.OrdinalIgnoreCase);
            var roots = new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var baseK in roots)
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseK);
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using var sk = k.OpenSubKey(sub);
                            var dn = sk?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(dn)) continue;
                            var info = new UninstallInfo
                            {
                                DisplayName = dn,
                                DisplayIcon = sk?.GetValue("DisplayIcon") as string ?? "",
                                InstallLocation = sk?.GetValue("InstallLocation") as string ?? "",
                                UninstallString = sk?.GetValue("UninstallString") as string ?? ""
                            };
                            // index por subkey + displayname
                            if (!dict.ContainsKey(sub)) dict[sub] = info;
                            if (!dict.ContainsKey(dn)) dict[dn] = info;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            try
            {
                using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (hkcu != null)
                    foreach (var sub in hkcu.GetSubKeyNames())
                    {
                        try
                        {
                            using var sk = hkcu.OpenSubKey(sub);
                            var dn = sk?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(dn)) continue;
                            var info = new UninstallInfo { DisplayName = dn, DisplayIcon = sk?.GetValue("DisplayIcon") as string ?? "" };
                            if (!dict.ContainsKey(sub)) dict[sub] = info;
                            if (!dict.ContainsKey(dn)) dict[dn] = info;
                        }
                        catch { }
                    }
            }
            catch { }
            _uninstallCache = dict;
            _uninstallCacheTime = DateTime.UtcNow;
            return dict;
        }

        private void LoadIconsForList(List<StoreAppVM> apps)
        {
            try { Directory.CreateDirectory(IconCacheDir); } catch { }
            var sem = new System.Threading.SemaphoreSlim(6, 6);
            var tasks = new List<Task>();
            foreach (var app in apps.Take(40))
            {
                if (app.IconSource != null) continue;
                var a = app;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        // 1) Memory cache
                        lock (_iconLock)
                        {
                            if (_iconCache.TryGetValue(a.Id.ToLowerInvariant(), out var cached))
                            {
                                var bc = cached;
                                Dispatcher.BeginInvoke(new Action(() => { a.IconSource = bc; a.RaiseIcon(); }), System.Windows.Threading.DispatcherPriority.Background);
                                return;
                            }
                        }
                        // 2) Disk cache
                        var cachePath = GetIconCachePath(a.Id);
                        if (File.Exists(cachePath))
                        {
                            try
                            {
                                var bmpDisk = new BitmapImage();
                                bmpDisk.BeginInit();
                                bmpDisk.CacheOption = BitmapCacheOption.OnLoad;
                                bmpDisk.UriSource = new Uri(cachePath, UriKind.Absolute);
                                bmpDisk.EndInit();
                                bmpDisk.Freeze();
                                lock (_iconLock) _iconCache[a.Id.ToLowerInvariant()] = bmpDisk;
                                var bd = bmpDisk;
                                _ = Dispatcher.BeginInvoke(new Action(() => { a.IconSource = bd; a.RaiseIcon(); }), System.Windows.Threading.DispatcherPriority.Background);
                                return;
                            }
                            catch { }
                        }
                        // 3) Resolve from registry/filesystem
                        ImageSource? bmp = null;
                        var cand = TryResolveIconPath(a);
                        if (!string.IsNullOrEmpty(cand))
                        {
                            try
                            {
                                if (File.Exists(cand)) bmp = Helpers.ProgramIconHelper.GetIconFromFile(cand);
                                else if (Directory.Exists(cand)) bmp = Helpers.ProgramIconHelper.GetIconFromDirectory(cand);
                            }
                            catch { }
                        }
                        if (bmp == null && a.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                        {
                            try { bmp = Helpers.AppIconHelper.GetAppIcon(a.Id, 32, null); } catch { }
                        }
                        // Fallback: monograma
                        if (bmp == null) bmp = MakeMonogramIcon(a.Name, a.Id);
                        if (bmp != null)
                        {
                            lock (_iconLock) _iconCache[a.Id.ToLowerInvariant()] = bmp;
                            // 4) Save to disk cache (PNG)
                            try
                            {
                                if (bmp is BitmapSource bs)
                                {
                                    var encoder = new PngBitmapEncoder();
                                    encoder.Frames.Add(BitmapFrame.Create(bs));
                                    using var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None);
                                    encoder.Save(fs);
                                }
                            }
                            catch { }
                            var b = bmp;
                            _ = Dispatcher.BeginInvoke(new Action(() => { a.IconSource = b; a.RaiseIcon(); }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }
                    catch { }
                    finally { try { sem.Release(); } catch { } }
                }));
            }
            _ = Task.WhenAll(tasks);
        }

        // Gera avatar-monograma (igual MS Store para apps sem logo): inicial do nome sobre cor estável por hash.
        // Roda em background thread; o resultado é congelado (read-only) e seguro p/ usar na UI.
        private static ImageSource? MakeMonogramIcon(string? appName, string? appId)
        {
            try
            {
                var seed = (!string.IsNullOrEmpty(appName) ? appName : appId) ?? "?";
                var letter = seed.Trim();
                // pega a 1ª letra significativa do nome (ignora hífen/underscore/ponto no início)
                char first = '?';
                foreach (var c in letter)
                {
                    if (char.IsLetterOrDigit(c)) { first = char.ToUpperInvariant(c); break; }
                }
                // cor estável: palette do Windows (teal/azul/índigo/roxo/verde/coral/âmbar/laranja)
                var palette = new[]
                {
                    Color.FromRgb(0x55, 0x7C, 0x93), Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x4A, 0x6E, 0x8E),
                    Color.FromRgb(0x6B, 0x5B, 0x95), Color.FromRgb(0x2E, 0x7D, 0x6E), Color.FromRgb(0xD1, 0x63, 0x63),
                    Color.FromRgb(0xCA, 0x50, 0x1A), Color.FromRgb(0x8B, 0x74, 0x52), Color.FromRgb(0x4E, 0x8E, 0x5A),
                    Color.FromRgb(0x5A, 0x6C, 0x9E)
                };
                // FNV-1a 32 (determinístico entre execuções — != de string.GetHashCode que é randomizado por processo)
                uint hash = 2166136261;
                foreach (char c in seed) { hash ^= c; hash *= 16777619; }
                var bg = palette[hash % (uint)palette.Length];

                // desenha via DrawingVisual + RenderTargetBitmap (funciona em thread de fundo)
                const double S = 128;
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRoundedRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, S, S), 28, 28);
                    var ft = new FormattedText(first.ToString(),
                        System.Globalization.CultureInfo.CurrentCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold, Segoe UI"), 60, System.Windows.Media.Brushes.White, 1.25);
                    float cx = (float)((S - ft.Width) / 2.0);
                    float cy = (float)((S - ft.Height) / 2.0);
                    dc.DrawText(ft, new System.Windows.Point(cx, cy));
                }
                var rtb = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }

        private string? TryResolveIconPath(StoreAppVM app)
        {
            try
            {
                if (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) || (app.Id.Contains("_") && app.Id.Contains("__")))
                {
                    try { var bmp = Helpers.AppIconHelper.GetAppIcon(app.Id, 32, null); if (bmp != null) { app.IconSource = bmp; app.RaiseIcon(); return app.Id; } } catch { }
                }
                var cache = GetUninstallCache();
                // Tenta match exato por Id, depois Name
                if (!string.IsNullOrEmpty(app.Id) && cache.TryGetValue(app.Id, out var byId))
                {
                    if (!string.IsNullOrEmpty(byId.DisplayIcon)) { var cand = byId.DisplayIcon.Split(',')[0].Trim('"', ' ', '\''); if (!string.IsNullOrEmpty(cand)) return cand; }
                    if (!string.IsNullOrEmpty(byId.UninstallString)) { var c2 = ExtractPathFromUninstall(byId.UninstallString); if (!string.IsNullOrEmpty(c2)) return c2; }
                    if (!string.IsNullOrEmpty(byId.InstallLocation)) return byId.InstallLocation;
                }
                if (!string.IsNullOrEmpty(app.Name) && cache.TryGetValue(app.Name, out var byName))
                {
                    if (!string.IsNullOrEmpty(byName.DisplayIcon)) { var cand = byName.DisplayIcon.Split(',')[0].Trim('"', ' ', '\''); if (!string.IsNullOrEmpty(cand)) return cand; }
                    if (!string.IsNullOrEmpty(byName.UninstallString)) { var c2 = ExtractPathFromUninstall(byName.UninstallString); if (!string.IsNullOrEmpty(c2)) return c2; }
                    if (!string.IsNullOrEmpty(byName.InstallLocation)) return byName.InstallLocation;
                }
                // Fallback: busca por DisplayName parcial (evita Kimi -> Opencode)
                foreach (var kv in cache)
                {
                    if (kv.Value.DisplayName.Equals(app.Name, StringComparison.OrdinalIgnoreCase) ||
                        kv.Value.DisplayName.Equals(app.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        var di = kv.Value.DisplayIcon;
                        if (!string.IsNullOrEmpty(di)) { var cand = di.Split(',')[0].Trim('"', ' ', '\''); if (!string.IsNullOrEmpty(cand)) return cand; }
                        var us = kv.Value.UninstallString;
                        if (!string.IsNullOrEmpty(us)) { var c2 = ExtractPathFromUninstall(us); if (!string.IsNullOrEmpty(c2)) return c2; }
                        if (!string.IsNullOrEmpty(kv.Value.InstallLocation)) return kv.Value.InstallLocation;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? ExtractPathFromUninstall(string us)
        {
            try
            {
                us = us.Trim().Trim('"');
                if (us.EndsWith(".exe\"", StringComparison.OrdinalIgnoreCase)) us = us.Trim('"');
                if (us.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(us)) return us;
                if (us.Contains("\""))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(us, "\"([^\"]+\\.exe)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
                }
                if (us.Contains(" "))
                {
                    var pt = us.Split(new[] { ' ' }, 2);
                    if (pt.Length > 0 && File.Exists(pt[0].Trim('"'))) return pt[0].Trim('"');
                }
            }
            catch { }
            return null;
        }

        // --- Helpers ---
        private void Log(string msg)
        {
            try { KitLugia.Core.Logger.Log($"[STORE] {msg}"); } catch { }
            try
            {
                if (Dispatcher.CheckAccess()) AppendLog(msg);
                else Dispatcher.BeginInvoke(new Action(() => AppendLog(msg)), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }

        private void AppendLog(string msg)
        {
            try
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                TxtLog.Text = $"[{ts}] {msg}\n" + TxtLog.Text;
                if (TxtLog.Text.Length > 9000) TxtLog.Text = TxtLog.Text.Substring(0, 9000);
            }
            catch { }
        }
        private static string Trunc(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");

        // --- Search bar animada (barra fina estilo MS Store) ---
        private void ShowSearchBar()
        {
            try
            {
                PbSearchBorder.Visibility = Visibility.Visible;
                PbSearchFill.Width = 60;
                // Animação de loading: translada a barra de 0 a 100% repetidamente
                if (_searchAnimTimer == null)
                {
                    _searchAnimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    double offset = 0;
                    _searchAnimTimer.Tick += (s, e) =>
                    {
                        try
                        {
                            offset += 4;
                            if (offset > 300) offset = -60;
                            PbSearchFill.Margin = new Thickness(offset, 0, 0, 0);
                        }
                        catch { }
                    };
                }
                _searchAnimTimer.Start();
            }
            catch { }
        }
        private void HideSearchBar()
        {
            try
            {
                _searchAnimTimer?.Stop();
                PbSearchBorder.Visibility = Visibility.Collapsed;
                PbSearchFill.Margin = new Thickness(0, 0, 0, 0);
            }
            catch { }
        }

        // --- Toast helpers (delega para MainWindow LugiaToast) ---
        private void ShowToastProgress(string taskId, string title, string message)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowProgressToast(taskId, title, message);
            }
            catch { }
        }
        private void UpdateToastProgress(string taskId, string message)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.UpdateProgressToast(taskId, message);
            }
            catch { }
        }
        private void CompleteToastProgress(string taskId, bool success, string message)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.CompleteProgressToast(taskId, success, message);
            }
            catch { }
        }
        private void ShowToastInfo(string message)
        {
            try
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowInfo("STORE", message);
            }
            catch { }
        }

        public class StoreAppVM : INotifyPropertyChanged
        {
            private string _name = "";
            private string _id = "";
            private string _publisher = "";
            private string _version = "";
            private string _available = "";
            private string _source = "winget";
            private string _category = "";
            private string _description = "";
            public string Name { get => _name; set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } } }
            public string Id { get => _id; set { if (_id != value) { _id = value; OnChanged(nameof(Id)); } } }
            public string Publisher { get => _publisher; set { if (_publisher != value) { _publisher = value; OnChanged(nameof(Publisher)); } } }
            public string Version { get => _version; set { if (_version != value) { _version = value; OnChanged(nameof(Version)); OnChanged(nameof(HasUpdate)); OnChanged(nameof(HasUpdateVisibility)); OnChanged(nameof(UpdateBadgeVisibility)); } } }
            public string AvailableVersion { get => _available; set { if (_available != value) { _available = value; OnChanged(nameof(AvailableVersion)); OnChanged(nameof(HasUpdate)); OnChanged(nameof(HasUpdateVisibility)); OnChanged(nameof(UpdateBadgeVisibility)); } } }
            public string Source { get => _source; set { if (_source != value) { _source = value; OnChanged(nameof(Source)); } } }
            public string Category { get => _category; set { if (_category != value) { _category = value; OnChanged(nameof(Category)); } } }
            public string Description { get => _description; set { if (_description != value) { _description = value; OnChanged(nameof(Description)); } } }
            public double Rating { get; set; }
            public int RatingCount { get; set; }
            public bool HasUpdate => !string.IsNullOrEmpty(AvailableVersion) && !string.Equals(AvailableVersion, Version, StringComparison.OrdinalIgnoreCase);
            public Visibility HasUpdateVisibility => HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            public Visibility UpdateBadgeVisibility => HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            private ImageSource? _icon;
            public ImageSource? IconSource { get => _icon; set { _icon = value; OnChanged(nameof(IconSource)); OnChanged(nameof(IconVisibility)); OnChanged(nameof(FallbackIconVisibility)); } }
            public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;
            public Visibility FallbackIconVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;
            public void RaiseIcon() { OnChanged(nameof(IconSource)); OnChanged(nameof(IconVisibility)); OnChanged(nameof(FallbackIconVisibility)); }
            public event PropertyChangedEventHandler? PropertyChanged;
            void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }
}
