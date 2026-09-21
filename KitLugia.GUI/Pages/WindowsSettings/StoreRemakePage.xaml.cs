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
    /// <summary>
    /// Store Remake — interface no estilo UniGetUI (Discover/Installed/Updates):
    /// busca unificada winget/choco/msstore com filtros de fonte, DataGrid virtualizado,
    /// botão principal de ação e operações CLI com as flags corretas do UniGetUI
    /// (--id --exact, --accept-*-agreements, --include-unknown, choco -y --no-progress).
    /// Toda a lógica de parsing fica no StoreEngine (Core) — a GUI não duplica parsing.
    /// </summary>
    public partial class StoreRemakePage : Page
    {
        private readonly ObservableCollection<StoreAppVM> _installed = new();
        private readonly ObservableCollection<StoreAppVM> _results = new();
        private string? _wingetPath;
        private string? _chocoPath;
        private string? _pipPath;
        private string? _npmPath;
        private string? _dotnetPath;
        private string? _cargoPath;
        private bool _devManagersLoaded;

        private enum StoreTab { Discover, Installed, Updates }
        // Página padrão: Atualizações — o usuário abre e já age no que precisa
        private StoreTab _activeTab = StoreTab.Updates;

        private int _busy;
        private int _searchBusy;
        private System.Windows.Threading.DispatcherTimer? _searchAnimTimer;
        private System.Windows.Threading.DispatcherTimer? _progressHideTimer;
        private double _lastPct = -1;

        private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _iconLock = new();
        private Dictionary<string, UninstallInfo>? _uninstallCache;
        private DateTime _uninstallCacheTime = DateTime.MinValue;

        private static string IconCacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitLugia", "IconCache");
        private static string GetIconCachePath(string appId)
        {
            var safe = string.Join("", appId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(IconCacheDir, safe + ".png");
        }

        // Sugestões do Discover (query vazia) — monikers/ids populares no índice local do winget
        private static readonly string[] DiscoverSuggestions =
        {
            "vscode", "googlechrome", "firefox", "7zip", "notepad++", "vlc", "steam",
            "discord", "spotify", "obs", "git", "python", "nodejs", "powertoys", "terminal",
            "winrar", "qbittorrent", "jdownloader"
        };

        public StoreRemakePage()
        {
            InitializeComponent();
            Loaded += async (_, __) => await OnLoadedAsync();
            Unloaded += StoreRemakePage_Unloaded;
            LvPackages.SelectionChanged += (_, __) => UpdateMainToolbarState();
        }

        private void StoreRemakePage_Unloaded(object sender, RoutedEventArgs e)
        {
            try { _searchAnimTimer?.Stop(); _searchAnimTimer = null; } catch { }
            try { _progressHideTimer?.Stop(); _progressHideTimer = null; } catch { }
        }

        private async Task OnLoadedAsync()
        {
            Log("Detectando winget / choco...");
            _wingetPath = StoreEngine.FindWingetPath();
            _chocoPath = StoreEngine.FindChoco();
            TxtWingetStatus.Text = $"winget: {(_wingetPath != null ? "OK" : "não encontrado")}  ·  choco: {(_chocoPath != null ? "OK" : "não encontrado")}";
            TxtWingetStatus.Foreground = new SolidColorBrush(_wingetPath != null ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0xFF, 0x8C, 0x00));
            if (_wingetPath == null && _chocoPath == null)
                Log("Nenhum gerenciador encontrado — apenas dados em cache estarão disponíveis.");
            // Header/tabs corretos já no load; a lista preenche quando o refresh termina
            SwitchTab(StoreTab.Updates);
            await RefreshInstalledAsync(force: false);
        }

        // ─────────────────────────── Abas (Discover / Installed / Updates) ───────────────────────────

        private void SwitchTab(StoreTab tab)
        {
            _activeTab = tab;
            // Header estilo UniGetUI: título 28pt + subtítulo por aba
            (string title, string sub, string btnText, string btnIcon) = tab switch
            {
                StoreTab.Discover => ("Descobrir pacotes", "Busque no winget, Chocolatey e MS Store — instalar novos ou reinstalar existentes", "Instalar selecionado", "\uE896"),
                StoreTab.Installed => ("Pacotes instalados", "Apps neste PC — desinstalar, reinstalar ou ver detalhes", "Desinstalar selecionado", "\uE74D"),
                StoreTab.Updates => ("Atualizações", "Pacotes instalados com versão mais nova disponível — atualize um por um ou todos", "Atualizar tudo", "\uE895"),
                _ => ("Pacotes", "", "Ação", "\uE896")
            };
            TxtPageTitle.Text = title;
            TxtPageSubtitle.Text = sub;
            MainToolbarText.Text = btnText;
            MainToolbarIcon.Text = btnIcon;

            var active = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x40));
            var activeBorder = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            var idle = new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x32));
            BtnTabDiscover.Background = tab == StoreTab.Discover ? active : idle;
            BtnTabInstalled.Background = tab == StoreTab.Installed ? active : idle;
            BtnTabUpdates.Background = tab == StoreTab.Updates ? active : idle;
            BtnTabDiscover.BorderBrush = tab == StoreTab.Discover ? activeBorder : idle;
            BtnTabInstalled.BorderBrush = tab == StoreTab.Installed ? activeBorder : idle;
            BtnTabUpdates.BorderBrush = tab == StoreTab.Updates ? activeBorder : idle;

            switch (tab)
            {
                case StoreTab.Discover: RenderDiscover(); break;
                case StoreTab.Installed: RenderInstalled(); break;
                case StoreTab.Updates: RenderUpdates(); break;
            }
            UpdateMainToolbarState();
        }

        private void BtnTabDiscover_Click(object sender, RoutedEventArgs e)
        {
            SwitchTab(StoreTab.Discover);
            // Primeira visita ao Discover: carrega sugestões do índice local
            if (_results.Count == 0 && _searchBusy == 0)
                _ = DoSearchAsync();
        }
        private void BtnTabInstalled_Click(object sender, RoutedEventArgs e) => SwitchTab(StoreTab.Installed);
        private void BtnTabUpdates_Click(object sender, RoutedEventArgs e) => SwitchTab(StoreTab.Updates);

        private void RenderDiscover()
        {
            LvPackages.ItemsSource = _results;
            TxtListInfo.Text = _results.Count > 0 ? $"{_results.Count} resultados" : "";
            TxtEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_results.Count == 0 && string.IsNullOrWhiteSpace(TxtSearch.Text))
                TxtEmpty.Text = "Digite um termo para buscar ou aguarde as sugestões...";
        }

        private void RenderInstalled()
        {
            // Pacotes de dev (pip/npm/...) não são "apps" — aparecem só em Atualizações
            var apps = _installed.Where(a => !a.DevOnly).ToList();
            LvPackages.ItemsSource = new ObservableCollection<StoreAppVM>(apps);
            TxtListInfo.Text = $"{apps.Count} apps · {apps.Count(a => a.HasUpdate)} com atualização";
            TxtEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Text = "Nenhum app encontrado. Verifique se winget/choco estão instalados ou clique Atualizar.";
        }

        private void RenderUpdates()
        {
            var ups = _installed.Where(a => a.HasUpdate).ToList();
            LvPackages.ItemsSource = new ObservableCollection<StoreAppVM>(ups);
            int dev = ups.Count(a => a.DevOnly);
            TxtListInfo.Text = ups.Count > 0 ? $"{ups.Count} atualização(ões) disponível(is)" + (dev > 0 ? $" · {dev} de dev (pip/npm/…)" : "") : "";
            // Durante o refresh inicial não diz "Tudo atualizado!" — o spinner já sinaliza o carregamento
            if (_busy == 1)
            {
                TxtEmpty.Visibility = Visibility.Collapsed;
                return;
            }
            TxtEmpty.Visibility = ups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtEmpty.Text = "Tudo atualizado!";
        }

        // ─────────────────────────── Botão principal (toolbar) ───────────────────────────

        private StoreAppVM? SelectedApp => LvPackages.SelectedItem as StoreAppVM;

        private void UpdateMainToolbarState()
        {
            try
            {
                switch (_activeTab)
                {
                    case StoreTab.Discover:
                        MainToolbarButton.IsEnabled = SelectedApp != null;
                        MainToolbarText.Text = SelectedApp is { } d
                            ? (d.HasUpdate ? "Atualizar selecionado" : d.IsInstalled ? "Reinstalar selecionado" : "Instalar selecionado")
                            : "Instalar selecionado";
                        MainToolbarButton.ToolTip = SelectedApp != null
                            ? $"{MainToolbarText.Text}: {SelectedApp.Name}"
                            : "Selecione um pacote na lista para habilitar a ação";
                        break;
                    case StoreTab.Installed:
                        MainToolbarButton.IsEnabled = SelectedApp != null;
                        break;
                    case StoreTab.Updates:
                        MainToolbarButton.IsEnabled = _installed.Count(a => a.HasUpdate) > 0;
                        break;
                }
            }
            catch { }
        }

        private async void MainToolbarButton_Click(object sender, RoutedEventArgs e)
        {
            switch (_activeTab)
            {
                case StoreTab.Discover:
                    if (SelectedApp is { } app)
                    {
                        var verb = app.HasUpdate ? "Atualizar" : app.IsInstalled ? "Reinstalar" : "Instalar";
                        if (MessageBox.Show($"{verb} {app.Name}?" + (app.IsInstalled && !app.HasUpdate ? "\n\nEste pacote já está instalado — será reinstalado por cima." : ""),
                            "Store Remake", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                            await InstallOrUpgradeAsync(app, false);
                    }
                    break;
                case StoreTab.Installed:
                    if (SelectedApp is { } inst)
                    {
                        if (MessageBox.Show($"Desinstalar {inst.Name}?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                            await UninstallAsync(inst);
                    }
                    break;
                case StoreTab.Updates:
                    await UpgradeAllAsync();
                    break;
            }
        }

        /// <summary>Desinstala da lista Atualizações (dev) — volta a aparecer se ainda desatualizado no refresh.</summary>
        private void RemoveFromLists(StoreAppVM app)
        {
            try
            {
                _installed.Remove(app);
                if (LvPackages.ItemsSource is ObservableCollection<StoreAppVM> oc) oc.Remove(app);
            }
            catch { }
        }

        // ─────────────────────── Gerenciadores de dev (pip/npm/dotnet/cargo) ───────────────────────

        /// <summary>Detecção dos gerenciadores de dev — 1x por vida da página (where é barato mas não é grátis).</summary>
        private async Task EnsureDevManagersAsync()
        {
            if (_devManagersLoaded) return;
            _devManagersLoaded = true;
            try
            {
                (_pipPath, _npmPath, _dotnetPath, _cargoPath) = await Task.Run(() =>
                    (StoreEngine.FindPipPath(),
                     StoreEngine.FindFirstOnPath("npm.cmd"),
                     StoreEngine.FindFirstOnPath("dotnet.exe"),
                     StoreEngine.FindFirstOnPath("cargo.exe")));
                var found = new List<string>();
                if (_pipPath != null) found.Add("pip");
                if (_npmPath != null) found.Add("npm");
                if (_dotnetPath != null) found.Add("dotnet");
                if (_cargoPath != null) found.Add("cargo");
                if (found.Count > 0)
                    Log($"Gerenciadores de dev: {string.Join(", ", found)} (updates agregados na aba Atualizações)");
            }
            catch { }
        }

        /// <summary>Updates dos gerenciadores de dev — cada query já roda em Task.Run.</summary>
        private async Task<List<KitLugia.Core.KitStore.StoreApp>> QueryDevUpdatesAsync()
        {
            await EnsureDevManagersAsync();
            var t1 = Task.Run(() => StoreEngine.QueryPipOutdated(_pipPath));
            var t2 = Task.Run(() => StoreEngine.QueryNpmOutdated(_npmPath));
            var t3 = Task.Run(() => StoreEngine.QueryDotnetToolUpdates(_dotnetPath));
            var t4 = Task.Run(() => StoreEngine.QueryCargoUpdates(_cargoPath));
            await Task.WhenAll(t1, t2, t3, t4);
            var all = new List<KitLugia.Core.KitStore.StoreApp>();
            all.AddRange(t1.Result); all.AddRange(t2.Result); all.AddRange(t3.Result); all.AddRange(t4.Result);
            return all;
        }

        // ─────────────────────────── Refresh (instalados + updates) ───────────────────────────

        private async Task RefreshInstalledAsync(bool force = false)
        {
            if (force) StoreEngine.InvalidateCache();
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) { Log("Operação em andamento — ignorando novo refresh."); return; }
            try
            {
                LoadingSpinnerPanel.Visibility = Visibility.Visible;
                TxtLoadingMsg.Text = "Carregando pacotes instalados...";
                TxtEmpty.Visibility = Visibility.Collapsed;
                TxtListInfo.Text = "";
                _installed.Clear();

                try
                {
                    // Cache TTL 3 min no Engine — reabrir a página é ~10x mais rápido
                    var installedTask = Task.Run(() => StoreEngine.QueryWingetInstalledCached(_wingetPath, force));
                    var upgradesTask = Task.Run(() => StoreEngine.QueryWingetUpgrades(_wingetPath));
                    var chocoTask = Task.Run(() => StoreEngine.QueryChocoOutdated(_chocoPath));
                    var appxTask = Task.Run(() => StoreEngine.QueryAppxPackages());
                    var devTask = QueryDevUpdatesAsync();

                    await Task.WhenAll(installedTask, upgradesTask, chocoTask, appxTask, devTask);
                    var installed = installedTask.Result;
                    var upgrades = upgradesTask.Result;
                    var chocoUpgs = chocoTask.Result;
                    var appxList = appxTask.Result;
                    var devUpgs = devTask.Result;

                    // Merge por Id com comparação semântica de versão
                    var map = new Dictionary<string, StoreAppVM>(StringComparer.OrdinalIgnoreCase);
                    int dupCount = 0;
                    foreach (var a in installed)
                    {
                        var vm = ToVM(a);
                        vm.ShowInstall = false;
                        vm.ShowUninstall = true;
                        var key = Key(vm);
                        if (key.Length == 0) continue;
                        if (!map.TryGetValue(key, out var existing))
                            map[key] = vm;
                        else
                        {
                            dupCount++;
                            if (StoreEngine.CompareVersions(vm.Version, existing.Version) > 0)
                                existing.Version = vm.Version;
                        }
                    }
                    if (dupCount > 0) Log($"Winget: {dupCount} duplicatas mescladas (multi-arch/fonte) — mantida maior versão");

                    foreach (var u in upgrades)
                    {
                        var key = (u.Id ?? "").Trim().ToLowerInvariant();
                        if (key.Length == 0) continue;
                        if (map.TryGetValue(key, out var ex))
                        {
                            ex.AvailableVersion = u.AvailableVersion ?? "";
                            if (!string.IsNullOrEmpty(u.Version)) ex.Version = u.Version;
                        }
                        else if (!map.ContainsKey(key))
                        {
                            var vm = ToVM(u);
                            vm.ShowInstall = false;
                            vm.ShowUninstall = true;
                            map[key] = vm;
                        }
                    }

                    foreach (var u in chocoUpgs)
                    {
                        var key = (u.Id ?? "").Trim().ToLowerInvariant();
                        if (key.Length == 0) continue;
                        if (map.TryGetValue(key, out var ex2))
                            ex2.AvailableVersion = u.AvailableVersion ?? "";
                        else
                        {
                            var vm = ToVM(u);
                            vm.Source = "choco";
                            vm.ShowInstall = false;
                            vm.ShowUninstall = true;
                            map[key + "|choco"] = vm;
                        }
                    }

                    // Gerenciadores de dev (pip/npm/dotnet/cargo): update é o próprio gerenciador,
                    // NÃO vão para "Instalados" — só aparecem como atualizações (igual UniGetUI)
                    foreach (var d in devUpgs)
                    {
                        var key = ((d.Id ?? "") + "|" + d.Source).Trim().ToLowerInvariant();
                        if (key.Length == 0 || map.ContainsKey(key)) continue;
                        var vm = ToVM(d);
                        vm.ShowInstall = false;
                        vm.ShowUninstall = false;
                        vm.DevOnly = true;
                        map[key] = vm;
                    }

                    foreach (var kv in map.Values
                        .OrderByDescending(v => v.HasUpdate)
                        .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                        _installed.Add(kv);

                    int ups = _installed.Count(a => a.HasUpdate);
                    int devUps = _installed.Count(a => a.DevOnly);
                    TxtStatusCounts.Text = $"{_installed.Count - devUps} instalados · {ups} updates · {appxList.Count} pacotes Store" +
                                           (devUps > 0 ? $" · {devUps} dev (pip/npm/…)" : "");
                    _ = Task.Run(() => LoadIconsForList(_installed.Take(60).ToList()));
                    Log($"Instalados: {_installed.Count - devUps} | atualizações: {ups - devUps} | dev: {devUps} | Store: {appxList.Count}");
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
                UpdateMainToolbarState();
            }

            // Re-render da aba atual com os dados novos + atualiza badges "instalado" do Discover
            await MarkInstalledFlagsAsync();
            switch (_activeTab)
            {
                case StoreTab.Installed: RenderInstalled(); break;
                case StoreTab.Updates: RenderUpdates(); break;
            }
        }

        private static string Key(StoreAppVM vm) => (string.IsNullOrEmpty(vm.Id) ? vm.Name : vm.Id).Trim().ToLowerInvariant();

        // ─── Detecção de "já instalado" (multi-fonte, estilo AppsPage/BCU) ───
        // 1) ids/nomes da lista winget+choco (StoreEngine)
        // 2) TODOS os DisplayNames/RegistryKeyNames do registro (RegistryProgramFactory —
        //    mesma engine do AppsPage: HKLM+HKCU, 32+64-bit) — pega apps instalados
        //    fora do winget (installer próprio, portable com uninstall, etc.)
        // Match: exato normalizado (só [a-z0-9]) ou contenção (token >= 4 chars).

        private HashSet<string> _installedKeyIndex = new(StringComparer.Ordinal);
        private HashSet<string> _registryKeyIndex = new(StringComparer.Ordinal);
        private DateTime _registryIndexTime = DateTime.MinValue;
        private int _lastInstalledMarked;

        /// <summary>Normaliza p/ match: minúsculo, só letras e dígitos ("7-Zip (x64)" → "7zipx64").</summary>
        private static string NormKey(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // Sufixos de convenção choco que atrapalham o match ("notepadplusplus.install")
        private static readonly string[] ChocoNoiseSuffixes = { "install", "portable", "appx", "package" };

        // Tokens de vendor genéricos: válidos p/ match EXATO, mas NUNCA p/ contenção
        // (evita "Microsoft.X" aparecer instalado só porque há N apps "Microsoft..." no registro)
        private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
        { "microsoft", "google", "adobe", "apple", "mozilla", "video", "the", "app", "software" };

        /// <summary>Tokens candidatos de um pacote: id completo, 1º segmento do id, id sem sufixo choco e nome.</summary>
        private static List<string> CandidateKeys(StoreAppVM vm)
        {
            var list = new List<string>();
            var id = vm.Id ?? "";
            var nid = NormKey(id);
            var nname = NormKey(vm.Name);
            if (nid.Length > 0) list.Add(nid);
            var dot = id.IndexOf('.');
            if (dot > 0) { var first = NormKey(id.Substring(0, dot)); if (first.Length > 0) list.Add(first); }
            foreach (var suf in ChocoNoiseSuffixes)
            {
                if (nid.Length > suf.Length + 2 && nid.EndsWith(suf, StringComparison.Ordinal))
                { list.Add(nid.Substring(0, nid.Length - suf.Length)); break; }
            }
            if (nname.Length > 0) list.Add(nname);
            return list.Distinct().Where(k => k.Length >= 3).ToList();
        }

        private void RebuildInstalledIndex()
        {
            _installedKeyIndex = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in _installed)
            {
                foreach (var k in CandidateKeys(i))
                    _installedKeyIndex.Add(k);
                // id sem parte do publisher também entra no índice exato
                var dot = (i.Id ?? "").IndexOf('.');
                if (dot > 0)
                {
                    var tail = NormKey(i.Id.Substring(dot + 1));
                    if (tail.Length >= 3) _installedKeyIndex.Add(tail);
                }
            }
        }

        /// <summary>Parte lenta (registro, centenas de chaves) — SEMPRE fora da UI thread.</summary>
        private async Task RebuildRegistryIndexAsync()
        {
            if (_registryKeyIndex.Count > 0 && (DateTime.UtcNow - _registryIndexTime).TotalMinutes < 10) return;
            try
            {
                var progs = await Task.Run(() => KitLugia.Core.UninstallTools.RegistryProgramFactory.GetInstalledPrograms());
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in progs)
                {
                    var a = NormKey(p.DisplayName);
                    var b = NormKey(p.RegistryKeyName);
                    if (a.Length >= 3) set.Add(a);
                    if (b.Length >= 3) set.Add(b);
                }
                _registryKeyIndex = set;
                _registryIndexTime = DateTime.UtcNow;
            }
            catch (Exception ex) { Log($"Índice de registro falhou: {ex.Message}"); }
        }

        private bool IsLikelyInstalled(StoreAppVM vm)
        {
            var keys = CandidateKeys(vm);
            // 1) Exato (id/nome normalizados) em qualquer índice
            foreach (var k in keys)
            {
                if (_installedKeyIndex.Contains(k) || _registryKeyIndex.Contains(k)) return true;
            }
            // 2) Contenção: nome/id instalado contém o token do resultado (token >= 4,
            //    fora da stoplist de vendors) ex: "firefox" contido em "mozillafirefoxx64enus"
            foreach (var k in keys)
            {
                if (k.Length < 4 || GenericTokens.Contains(k)) continue;
                if (_installedKeyIndex.Any(x => x.Contains(k))) return true;
                if (_registryKeyIndex.Any(x => x.Contains(k))) return true;
            }
            return false;
        }

        /// <summary>Sincroniza IsInstalled dos resultados do Discover com a lista de instalados atual.</summary>
        private async Task MarkInstalledFlagsAsync()
        {
            try
            {
                RebuildInstalledIndex(); // barato (memória)
                await RebuildRegistryIndexAsync(); // lento — Task.Run interno
                int marked = 0;
                foreach (var r in _results)
                {
                    var inst = IsLikelyInstalled(r);
                    if (inst) marked++;
                    if (r.IsInstalled != inst) r.IsInstalled = inst;
                }
                _lastInstalledMarked = marked;
            }
            catch { }
        }

        private StoreAppVM ToVM(StoreApp src)
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
                Description = src.Description ?? ""
            };
        }

        // ─────────────────────────── Busca (estilo UniGetUI Discover) ───────────────────────────

        private List<string> GetSelectedSources()
        {
            var srcs = new List<string>();
            if (ChkSrcWinget.IsChecked == true) srcs.Add("winget");
            if (ChkSrcChoco.IsChecked == true) srcs.Add("choco");
            if (ChkSrcStore.IsChecked == true) srcs.Add("msstore");
            return srcs;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (TxtSearchPlaceholder == null || BtnClearSearch == null) return;
            var hasText = !string.IsNullOrEmpty(TxtSearch.Text);
            TxtSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            BtnClearSearch.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                e.Handled = true;
                _ = DoSearchAsync();
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                TxtSearch.Text = "";
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Text = "";
            TxtSearch.Focus();
        }

        private async void BtnSearch_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

        private void SourceFilter_Changed(object sender, RoutedEventArgs e)
        {
            // Reexecuta a busca com os filtros novos se já há termo digitado (estilo UniGetUI)
            if (IsLoaded && !string.IsNullOrWhiteSpace(TxtSearch.Text) && _searchBusy == 0)
                _ = DoSearchAsync();
        }

        private async Task DoSearchAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _searchBusy, 1) == 1) { Log("Busca já em andamento — ignorando."); return; }
            try
            {
                if (_activeTab != StoreTab.Discover) SwitchTab(StoreTab.Discover);
                else { RenderDiscover(); UpdateMainToolbarState(); } // garante ItemsSource mesmo já estando na aba
                var q = (TxtSearch.Text ?? "").Trim();
                var srcs = GetSelectedSources();
                if (srcs.Count == 0) { TxtSearchInfo.Text = "Selecione ao menos uma fonte (winget/choco/msstore)."; return; }

                ShowSearchBar();
                TxtSearchInfo.Text = "Buscando...";
                _results.Clear();

                try
                {
                    if (q.Length < 2)
                    {
                        // Query vazia → sugestões do Discover (índice local instantâneo)
                        await LoadDiscoverSuggestions(srcs);
                    }
                    else
                    {
                        if (srcs.Contains("winget"))
                        {
                            var w = await Task.Run(() => StoreEngine.QueryWingetSearchLocal(_wingetPath, q));
                            foreach (var a in w)
                            {
                                var vm = ToVM(a);
                                vm.Source = string.IsNullOrEmpty(vm.Source) || vm.Source == "winget" ? "winget" : vm.Source;
                                vm.ShowInstall = true;
                                vm.ShowUninstall = false;
                                AddUnique(vm);
                            }
                        }
                        if (srcs.Contains("msstore"))
                        {
                            var ms = await Task.Run(() => StoreEngine.QueryWingetSearch(_wingetPath, q, "msstore"));
                            foreach (var a in ms)
                            {
                                var vm = ToVM(a);
                                vm.Source = "msstore";
                                vm.ShowInstall = true;
                                vm.ShowUninstall = false;
                                AddUnique(vm);
                            }
                        }
                        if (srcs.Contains("choco"))
                        {
                            if (string.IsNullOrEmpty(_chocoPath))
                                Log("Chocolatey não encontrado — filtro choco ignorado.");
                            else
                            {
                                var ch = await Task.Run(() => StoreEngine.QueryChocoSearch(_chocoPath, q));
                                foreach (var a in ch)
                                {
                                    var vm = ToVM(a);
                                    vm.Source = "choco";
                                    vm.ShowInstall = true;
                                    vm.ShowUninstall = false;
                                    AddUnique(vm);
                                }
                            }
                        }
                        // Marca os que já estão instalados (badge verde ✓ instalado + botão Reinstalar)
                        await MarkInstalledFlagsAsync();

                        TxtSearchInfo.Text = $"{_results.Count} resultado(s) para \"{q}\"";
                        Log($"Busca \"{q}\": {_results.Count} resultados ({string.Join("+", srcs)}) · {_lastInstalledMarked} já instalado(s)");
                    }

                    TxtListInfo.Text = _results.Count > 0 ? $"{_results.Count} resultados" : "";
                    TxtEmpty.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (_results.Count == 0 && q.Length >= 2) TxtEmpty.Text = "Nenhum resultado. Tente outro termo ou outra fonte.";
                    // SEMPRE (re)atribui a fonte da lista — SwitchTab pode não ter rodado se já estávamos na aba Discover
                    LvPackages.ItemsSource = _results;
                    _ = Task.Run(() => LoadIconsForList(_results.Take(30).ToList()));
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

        private void AddUnique(StoreAppVM vm)
        {
            var key = Key(vm) + "|" + vm.Source;
            if (_results.Any(x => (Key(x) + "|" + x.Source) == key)) return;
            _results.Add(vm);
        }

        private async Task LoadDiscoverSuggestions(List<string> srcs)
        {
            // Puxa sugestões do índice local do winget (instantâneo, sem rede)
            var added = 0;
            if (srcs.Contains("winget"))
            {
                foreach (var term in DiscoverSuggestions)
                {
                    if (added >= 24) break;
                    var res = await Task.Run(() => StoreEngine.QueryWingetSearchLocal(_wingetPath, term));
                    var first = res.FirstOrDefault();
                    if (first == null) continue;
                    var vm = ToVM(first);
                    vm.ShowInstall = true;
                    vm.ShowUninstall = false;
                    var before = _results.Count;
                    AddUnique(vm);
                    if (_results.Count > before) added++;
                }
            }
            await MarkInstalledFlagsAsync();
            if (srcs.Contains("choco") && !string.IsNullOrEmpty(_chocoPath) && added < 24)
            {
                foreach (var term in new[] { "vlc", "notepadplusplus", "firefox", "7zip", "greenshot" })
                {
                    if (added >= 24) break;
                    var res = await Task.Run(() => StoreEngine.QueryChocoSearch(_chocoPath, term, 1));
                    var first = res.FirstOrDefault();
                    if (first == null) continue;
                    var vm = ToVM(first);
                    vm.Source = "choco";
                    vm.ShowInstall = true;
                    vm.ShowUninstall = false;
                    var before = _results.Count;
                    AddUnique(vm);
                    if (_results.Count > before) added++;
                }
            }
            TxtSearchInfo.Text = added > 0 ? $"{added} sugestões — digite para buscar em todas as fontes" : "Digite um termo para buscar.";
            Log($"Discover: {added} sugestões carregadas do índice local · {_lastInstalledMarked} já instalado(s)");
        }

        // ─────────────────────────── Operações (flags do UniGetUI) ───────────────────────────

        /// <param name="refreshAfter">false em lote (Atualizar tudo) — a lista é recarregada UMA vez no fim.</param>
        private async Task InstallOrUpgradeAsync(StoreAppVM app, bool forceStop, bool refreshAfter = true)
        {
            if (app == null) return;
            bool isInstall = !_installed.Any(x => string.Equals(x.Id, app.Id, StringComparison.OrdinalIgnoreCase) && x.Source == app.Source)
                             && !_installed.Any(x => string.Equals(x.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            var verb = app.HasUpdate ? "Atualizando" : isInstall ? "Instalando" : "Reinstalando";
            Log($"{verb} {app.Id} ({app.Source}) force={forceStop}...");
            try
            {
                if (forceStop) ForceStopForApp(app);

                string exe, args;
                if (app.Source.Equals("choco", StringComparison.OrdinalIgnoreCase))
                {
                    exe = _chocoPath ?? "choco";
                    // UniGetUI ChocolateyPkgOperationHelper: id -y (+ --no-progress em install/upgrade)
                    // Reinstalar/instalar = install; só upgrade real quando há versão nova
                    args = app.HasUpdate ? $"upgrade \"{app.Id}\" -y --no-progress" : $"install \"{app.Id}\" -y --no-progress";
                }
                else if (app.Source.Equals("pip", StringComparison.OrdinalIgnoreCase))
                {
                    // UniGetUI PipPkgOperationHelper: install --upgrade id --no-input --no-color --no-cache
                    exe = _pipPath ?? "pip";
                    bool isPython = exe.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase);
                    args = (isPython ? "-m pip " : "") + $"install --upgrade {app.Id} --no-input --no-color --no-cache";
                }
                else if (app.Source.Equals("npm", StringComparison.OrdinalIgnoreCase))
                {
                    // UniGetUI Npm: UpdateVerb = install (install -g id atualiza)
                    exe = _npmPath ?? "npm";
                    args = app.Category == "global" ? $"install -g {app.Id}" : $"install {app.Id}";
                }
                else if (app.Source.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    // UniGetUI DotNet: UpdateVerb = update (dotnet tool update -g id)
                    exe = _dotnetPath ?? "dotnet";
                    args = $"tool update --global {app.Id}";
                }
                else if (app.Source.Equals("cargo", StringComparison.OrdinalIgnoreCase))
                {
                    // cargo-update: instala a versão nova do crate
                    exe = _cargoPath ?? "cargo";
                    args = $"install {app.Id}";
                }
                else
                {
                    exe = _wingetPath ?? "winget";
                    // UniGetUI WinGetPkgOperationHelper: --id X --exact + agreements + --silent
                    var baseArgs = $"--id \"{app.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
                    // Fixa a fonte quando conhecida — sem isso o winget pode falhar se o id
                    // existir em mais de uma fonte (winget+msstore) com interatividade desativada
                    if (app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                        baseArgs += " --source winget";
                    else if (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                        baseArgs += " --source msstore";
                    if (app.HasUpdate)
                        // update: + --include-unknown --force (igual UniGetUI)
                        args = $"upgrade {baseArgs} --include-unknown --force";
                    else
                        // install novo ou reinstalar por cima (upgrade sem pendência falha no winget)
                        args = $"install {baseArgs}";
                }

                SetProgress(0, $"{verb} {app.Name}...", "Iniciando download/instalação...");
                ShowToastProgress($"store_{app.Id}", verb, $"{app.Name} — preparando...");

                var output = await RunProcessStreamingAsync(app, exe, args);
                int exit = _lastExitCode;
                Log($"[{app.Id}] exit {exit}\n{Trunc(output, 1000)}");
                if (exit == 0 || exit == 3010 || exit == 1641 || exit == 1614 || exit == 1605)
                {
                    SetProgress(100, $"{app.Name} concluído.", "");
                    CompleteToastProgress($"store_{app.Id}", true, $"{(isInstall ? "Instalação" : "Atualização")} de {app.Name} concluída com sucesso.");
                }
                else
                {
                    SetProgress(0, $"{app.Name}: falhou (exit {exit}).", "");
                    CompleteToastProgress($"store_{app.Id}", false, $"{app.Name}: falhou (exit {exit}).");
                }

                HideProgressAfterDelay();
                if (refreshAfter)
                    await RefreshInstalledAsync(force: true);
            }
            catch (Exception ex)
            {
                Log($"Erro {(app.HasUpdate ? "upgrade" : "install")} {app.Id}: {ex.Message}");
                CompleteToastProgress($"store_{app.Id}", false, $"Erro ao processar {app.Name}: {ex.Message}");
                HideProgressAfterDelay();
            }
        }

        private async Task UninstallAsync(StoreAppVM app)
        {
            if (app == null) return;
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
                    args = $"uninstall --id \"{app.Id}\" --exact --silent --accept-source-agreements --disable-interactivity";
                }

                SetProgress(0, $"Desinstalando {app.Name}...", "Executando uninstall...");
                ShowToastProgress($"store_un_{app.Id}", "Desinstalando", $"{app.Name} — preparando...");
                var output = await RunProcessStreamingAsync(app, exe, args);
                int exit = _lastExitCode;
                Log($"[{app.Id}] uninstall exit {exit}\n{Trunc(output, 800)}");
                CompleteToastProgress($"store_un_{app.Id}", exit == 0, exit == 0 ? $"{app.Name} desinstalado." : $"{app.Name}: falhou (exit {exit}).");
                SetProgress(100, $"{app.Name} desinstalado.", "");
                HideProgressAfterDelay();
                await RefreshInstalledAsync(force: true);
            }
            catch (Exception ex)
            {
                Log($"Desinstalar erro: {ex.Message}");
                CompleteToastProgress($"store_un_{app.Id}", false, $"Erro ao desinstalar {app.Name}: {ex.Message}");
                HideProgressAfterDelay();
            }
        }

        private async Task UpgradeAllAsync()
        {
            var ups = _installed.Where(a => a.HasUpdate).ToList();
            if (ups.Count == 0) { ShowToastInfo("Nenhuma atualização pendente."); return; }
            var preview = string.Join("\n", ups.Take(8).Select(a => $"• {a.Name} ({a.Version} → {a.AvailableVersion})"));
            if (ups.Count > 8) preview += $"\n• +{ups.Count - 8} outro(s)";
            if (MessageBox.Show($"Atualizar {ups.Count} pacote(s)?\n\n{preview}",
                "Atualizar tudo", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            ShowToastProgress("store_batch", "Atualizando tudo", $"{ups.Count} pacote(s) pendente(s)...");
            int done = 0, failed = 0;
            foreach (var app in ups)
            {
                try
                {
                    // refreshAfter=false: recarregar a lista N vezes custaria N x ~30s de re-query
                    await InstallOrUpgradeAsync(app, forceStop: false, refreshAfter: false);
                    done++;
                }
                catch { failed++; }
                UpdateToastProgress("store_batch", $"{done} de {ups.Count} concluído(s) — {app.Name}");
            }
            CompleteToastProgress("store_batch", failed == 0, $"{done} pacote(s) atualizado(s){(failed > 0 ? $", {failed} falha(s)" : "")}.");
            // UMA única recarga no fim (força invalida o cache)
            await RefreshInstalledAsync(force: true);
        }

        private int _lastExitCode;

        /// <summary>Roda o processo com OEM encoding, streaming ao vivo (log + progresso) e sem timeout rígido.</summary>
        private async Task<string> RunProcessStreamingAsync(StoreAppVM app, string exe, string args)
        {
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
            proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) { sb.AppendLine(e.Data); OnInstallLine(app, e.Data); } };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) { sb.AppendLine(e.Data); OnInstallLine(app, e.Data); } };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
            _lastExitCode = proc.ExitCode;
            return sb.ToString();
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

        private class ProgressResult { public double Percentage; public string? Detail; }

        // Parse do progresso do winget/choco: "X MB / Y MB" / "XX%" / "Progress: 45%"
        private static ProgressResult? TryParseProgress(string line)
        {
            try
            {
                var mMB = System.Text.RegularExpressions.Regex.Match(line, @"(\d+[\.,]?\d*)\s*(MB|GB|KB)\s*/\s*(\d+[\.,]?\d*)\s*(MB|GB|KB)");
                if (mMB.Success)
                {
                    double current = ParseSize(mMB.Groups[1].Value, mMB.Groups[2].Value);
                    double total = ParseSize(mMB.Groups[3].Value, mMB.Groups[4].Value);
                    if (total > 0)
                        return new ProgressResult { Percentage = (current / total) * 100.0, Detail = $"{FormatSize(current)} / {FormatSize(total)}" };
                }
                var mPct = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})\s*%");
                if (mPct.Success && double.TryParse(mPct.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct2))
                    return new ProgressResult { Percentage = pct2, Detail = null };
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
                    if (InlineProgressPanel.Visibility != Visibility.Visible)
                        InlineProgressPanel.Visibility = Visibility.Visible;
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

        private void OnInstallLine(StoreAppVM app, string line)
        {
            var t = line.Trim();
            if (t.Length == 0) return;
            // Loga linhas significativas (pula barras de progresso █▓▒░)
            if (t[0] != '\u2588' && t[0] != '\u2591' && t[0] != '\u2592' && t[0] != '\u2593')
            {
                try { Log($"[{app.Name}] {Trunc(t, 200)}"); } catch { }
            }
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
                            SetProgress(p, $"Processando {app.Name}...", detail);
                        }
                        else if (result.Detail != null)
                            SetProgressDetail(result.Detail);
                    }
                    else if (phase != null)
                        SetProgress(_lastPct >= 0 ? _lastPct : 0, phase, t.Length > 90 ? t.Substring(0, 90) + "…" : t);
                    else
                        SetProgressDetail(t);
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
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
                    Log($"ForceStop: nenhum processo correspondente a {app.Id} (normal se o app não estava em execução).");
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

        // ─────────────────────────── Handlers de linha (lista) ───────────────────────────

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshInstalledAsync(force: true);

        private async void BtnInstallCard_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app)
            {
                LvPackages.SelectedItem = app;
                await InstallOrUpgradeAsync(app, false);
            }
        }

        private async void BtnUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) await InstallOrUpgradeAsync(app, false);
        }

        private async void BtnForceUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) await InstallOrUpgradeAsync(app, true);
        }

        private async void BtnUninstallOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app)
            {
                if (MessageBox.Show($"Desinstalar {app.Name}?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await UninstallAsync(app);
            }
        }

        private void BtnDetails_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is StoreAppVM app) ShowAppDetail(app);
        }

        // ─────────────────────────── Detail modal ───────────────────────────

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
                DetailActionBtn.Content = app.IsInstalled ? "Reinstalar" : "Instalar";
                DetailActionBtn.Visibility = Visibility.Visible;
                DetailForceBtn.Visibility = Visibility.Collapsed;
            }
            // Badge "já instalado" no modal (só faz sentido fora da aba Instalados)
            if (app.IsInstalled && !app.HasUpdate && app.ShowInstall)
            {
                DetailInstalledInfo.Visibility = Visibility.Visible;
                DetailInstalledVersion.Text = string.IsNullOrEmpty(app.Version) ? "Versão instalada desconhecida" : $"Versão {app.Version} já presente — Reinstalar vai por cima";
            }
            else
                DetailInstalledInfo.Visibility = Visibility.Collapsed;
            DetailUninstallBtn.Visibility = app.ShowUninstall ? Visibility.Visible : Visibility.Collapsed;
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
            if (sender is FrameworkElement fe && e.OriginalSource == fe)
                DetailOverlay.Visibility = Visibility.Collapsed;
        }

        private async void DetailAction_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            var app = _detailApp;
            DetailOverlay.Visibility = Visibility.Collapsed;
            if (app.HasUpdate)
                await InstallOrUpgradeAsync(app, false);
            else
                await InstallOrUpgradeAsync(app, false);
        }

        private async void DetailForce_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            var app = _detailApp;
            DetailOverlay.Visibility = Visibility.Collapsed;
            await InstallOrUpgradeAsync(app, true);
        }

        private async void DetailUninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_detailApp == null) return;
            var app = _detailApp;
            DetailOverlay.Visibility = Visibility.Collapsed;
            if (MessageBox.Show($"Desinstalar {app.Name}?", "Store Remake", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await UninstallAsync(app);
        }

        // ─────────────────────────── Navegação / janela ───────────────────────────

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw && mw.IsVisible)
            { mw.NavigateToPage(PageType.Windows); return; }
            var w = Window.GetWindow(this);
            if (w != null) w.Close();
            if (Application.Current.MainWindow is MainWindow mw2) mw2.NavigateToPage(PageType.Windows);
        }

        private void BtnPopOut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                KitLugia.GUI.Windows.KitStore.KitStoreWindow.ShowStandalone();
                Log("KitStore aberta em janela separada.");
            }
            catch (Exception ex) { Log($"Pop-out erro: {ex.Message}"); MessageBox.Show(ex.Message, "KitStore"); }
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string txt = TxtLog?.Text ?? "";
                if (string.IsNullOrEmpty(txt)) return;
                System.Windows.Clipboard.SetText(txt);
                Log("Log copiado para a área de transferência.");
            }
            catch (Exception ex) { Log("Falha ao copiar: " + ex.Message); }
        }

        // ─────────────────────────── Ícones (cache memória + disco) ───────────────────────────

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
            foreach (var app in apps.Take(60))
            {
                if (app.IconSource != null) continue;
                var a = app;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        // 1) Memória
                        lock (_iconLock)
                        {
                            if (_iconCache.TryGetValue(a.Id.ToLowerInvariant(), out var cached))
                            {
                                var bc = cached;
                                Dispatcher.BeginInvoke(new Action(() => { a.IconSource = bc; a.RaiseIcon(); }), System.Windows.Threading.DispatcherPriority.Background);
                                return;
                            }
                        }
                        // 2) Disco
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
                        // 3) Resolve do registro/filesystem
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
                        if (bmp == null) bmp = MakeMonogramIcon(a.Name, a.Id);
                        if (bmp != null)
                        {
                            lock (_iconLock) _iconCache[a.Id.ToLowerInvariant()] = bmp;
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

        // Avatar-monograma (igual MS Store p/ apps sem logo): inicial sobre cor estável por hash FNV-1a.
        private static ImageSource? MakeMonogramIcon(string? appName, string? appId)
        {
            try
            {
                var seed = (!string.IsNullOrEmpty(appName) ? appName : appId) ?? "?";
                char first = '?';
                foreach (var c in seed)
                {
                    if (char.IsLetterOrDigit(c)) { first = char.ToUpperInvariant(c); break; }
                }
                var palette = new[]
                {
                    Color.FromRgb(0x55, 0x7C, 0x93), Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x4A, 0x6E, 0x8E),
                    Color.FromRgb(0x6B, 0x5B, 0x95), Color.FromRgb(0x2E, 0x7D, 0x6E), Color.FromRgb(0xD1, 0x63, 0x63),
                    Color.FromRgb(0xCA, 0x50, 0x1A), Color.FromRgb(0x8B, 0x74, 0x52), Color.FromRgb(0x4E, 0x8E, 0x5A),
                    Color.FromRgb(0x5A, 0x6C, 0x9E)
                };
                uint hash = 2166136261;
                foreach (char c in seed) { hash ^= c; hash *= 16777619; }
                var bg = palette[hash % (uint)palette.Length];

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

        // ─────────────────────────── Log / helpers ───────────────────────────

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

        // Barra de busca indeterminada (anima fill da esquerda p/ direita)
        private void ShowSearchBar()
        {
            try
            {
                PbSearchBorder.Visibility = Visibility.Visible;
                if (_searchAnimTimer == null)
                {
                    _searchAnimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    double offset = 0;
                    _searchAnimTimer.Tick += (s, e) =>
                    {
                        try
                        {
                            offset += 4;
                            if (offset > 90) offset = -50;
                            ((TranslateTransform)PbSearchFill.RenderTransform).X = offset;
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
                ((TranslateTransform)PbSearchFill.RenderTransform).X = 0;
            }
            catch { }
        }

        // Toast helpers (delega para MainWindow LugiaToast)
        private void ShowToastProgress(string taskId, string title, string message)
        {
            try { if (Application.Current.MainWindow is MainWindow mw) mw.ShowProgressToast(taskId, title, message); } catch { }
        }
        private void UpdateToastProgress(string taskId, string message)
        {
            try { if (Application.Current.MainWindow is MainWindow mw) mw.UpdateProgressToast(taskId, message); } catch { }
        }
        private void CompleteToastProgress(string taskId, bool success, string message)
        {
            try { if (Application.Current.MainWindow is MainWindow mw) mw.CompleteProgressToast(taskId, success, message); } catch { }
        }
        private void ShowToastInfo(string message)
        {
            try { if (Application.Current.MainWindow is MainWindow mw) mw.ShowInfo("STORE", message); } catch { }
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
            private bool _showInstall = true;
            private bool _showUninstall = false;

            public string Name { get => _name; set { if (_name != value) { _name = value; OnChanged(nameof(Name)); } } }
            public string Id { get => _id; set { if (_id != value) { _id = value; OnChanged(nameof(Id)); } } }
            public string Publisher { get => _publisher; set { if (_publisher != value) { _publisher = value; OnChanged(nameof(Publisher)); } } }
            public string Version { get => _version; set { if (_version != value) { _version = value; OnChanged(nameof(Version)); OnChanged(nameof(HasUpdate)); OnChanged(nameof(HasUpdateVisibility)); OnChanged(nameof(DisplayVersion)); } } }
            public string AvailableVersion { get => _available; set { if (_available != value) { _available = value; OnChanged(nameof(AvailableVersion)); OnChanged(nameof(HasUpdate)); OnChanged(nameof(HasUpdateVisibility)); } } }
            public string Source { get => _source; set { if (_source != value) { _source = value; OnChanged(nameof(Source)); } } }
            public string Category { get => _category; set { if (_category != value) { _category = value; OnChanged(nameof(Category)); } } }
            public string Description { get => _description; set { if (_description != value) { _description = value; OnChanged(nameof(Description)); } } }

            /// <summary>Mostra botão Instalar (resultados de busca/Discover).</summary>
            public bool ShowInstall { get => _showInstall; set { if (_showInstall != value) { _showInstall = value; OnChanged(nameof(InstallBtnVisibility)); OnChanged(nameof(InstalledBadgeVisibility)); } } }
            /// <summary>Mostra botão Desinstalar (instalados/updates).</summary>
            public bool ShowUninstall { get => _showUninstall; set { if (_showUninstall != value) { _showUninstall = value; OnChanged(nameof(UninstallBtnVisibility)); } } }

            private bool _isInstalled;
            /// <summary>True se o pacote já consta na lista de instalados (badge + Reinstalar).</summary>
            public bool IsInstalled
            {
                get => _isInstalled;
                set
                {
                    if (_isInstalled != value)
                    {
                        _isInstalled = value;
                        OnChanged(nameof(IsInstalled));
                        OnChanged(nameof(InstalledBadgeVisibility));
                        OnChanged(nameof(InstallButtonText));
                        OnChanged(nameof(HasUpdate));
                        OnChanged(nameof(HasUpdateVisibility));
                        OnChanged(nameof(UpdateBtnVisibility));
                    }
                }
            }

            public Visibility InstalledBadgeVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
            /// <summary>Texto do botão primário: Atualizar (tem nova versão) / Reinstalar (já tem) / Instalar (novo).</summary>
            public string InstallButtonText => HasUpdate ? "Atualizar" : IsInstalled ? "Reinstalar" : "Instalar";

            private bool _devOnly;
            /// <summary>True p/ pacotes de gerenciadores de dev (pip/npm/dotnet/cargo) — só aparecem em Atualizações.</summary>
            public bool DevOnly
            {
                get => _devOnly;
                set { if (_devOnly != value) { _devOnly = value; OnChanged(nameof(DevOnly)); } }
            }

            public string DisplayVersion => string.IsNullOrEmpty(Version) ? "—" : Version;
            public bool HasUpdate => !string.IsNullOrEmpty(AvailableVersion) && !string.Equals(AvailableVersion, Version, StringComparison.OrdinalIgnoreCase);
            public Visibility HasUpdateVisibility => HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            public Visibility InstallBtnVisibility => ShowInstall ? Visibility.Visible : Visibility.Collapsed;
            public Visibility UpdateBtnVisibility => HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            public Visibility UninstallBtnVisibility => ShowUninstall ? Visibility.Visible : Visibility.Collapsed;

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
