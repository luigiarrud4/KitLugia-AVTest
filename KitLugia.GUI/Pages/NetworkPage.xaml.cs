using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.Core;
using KitLugia.GUI.Controls;
using KitLugia.GUI.Helpers;

using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

#pragma warning disable CS4014
using Brushes = System.Windows.Media.Brushes;
using ToolTip = System.Windows.Controls.ToolTip;

namespace KitLugia.GUI.Pages
{
    public partial class NetworkPage : Page
    {
        private bool _isLoading = true;
        private bool _isFirstLoadComplete = false;
        private readonly SolidColorBrush _colorActive = new SolidColorBrush(Color.FromRgb(108, 203, 95));
        private readonly SolidColorBrush _colorDefault = new SolidColorBrush(Color.FromRgb(150, 150, 150));
        private readonly SolidColorBrush _colorWarning = new SolidColorBrush(Color.FromRgb(244, 129, 32));
        private readonly SolidColorBrush _colorError = new SolidColorBrush(Color.FromRgb(244, 67, 54));

        private List<AdapterManager.NetworkAdapterInfo> _physicalAdapters = new();
        private string _generatedMac = "";
        private DispatcherTimer _refreshTimer;
        private DispatcherTimer _dnsTimer;
        private DateTime _lastRefreshTime = DateTime.MinValue;

        public NetworkPage()
        {
            InitializeComponent();
            _ = LoadAllDataAsync();
            this.Loaded += NetworkPage_Loaded;
            this.Unloaded += NetworkPage_Unloaded;
        }

        private void NetworkPage_Loaded(object sender, RoutedEventArgs e)
        {
            StartRefreshTimers();
        }

        private async Task LoadAllDataAsync()
        {
            SetRefreshIndicator("Carregando...");
            var adapterTask = LoadAdapterInfoAsync();
            var dnsTask = LoadStatus();
            var settingsTask = LoadNetworkSettingsAsync();
            await Task.WhenAll(adapterTask, dnsTask, settingsTask);
            _isFirstLoadComplete = true;
            SetRefreshIndicator("OK");
        }

        private void StartRefreshTimers()
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (s, e) => await RefreshAdapterStatus();
            _refreshTimer.Start();

            _dnsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _dnsTimer.Tick += async (s, e) => await RefreshDnsStatus();
            _dnsTimer.Start();
        }

        private void SetRefreshIndicator(string message)
        {
            try
            {
                if (TxtRefreshStatus == null) return;
                TxtRefreshStatus.Text = message;
                if (message == "OK")
                    TxtRefreshStatus.Foreground = _colorDefault;
                else if (message == "Carregando..." || message.Contains("..."))
                    TxtRefreshStatus.Foreground = _colorWarning;
                else
                    TxtRefreshStatus.Foreground = _colorDefault;
            }
            catch { }
        }

        private async Task RefreshDnsStatus()
        {
            if (_isLoading) return;
            try
            {
                _isLoading = true;
                var dnsInfo = await Task.Run(() => Toolbox.GetActiveDnsInfo());
                await Dispatcher.InvokeAsync(() => UpdateDnsUi(dnsInfo.Provider, dnsInfo.DnsIp));
            }
            catch { }
            finally { _isLoading = false; }
        }

        private async Task LoadAdapterInfoAsync()
        {
            try
            {
                var (adapterName, adapterType, description, adapterGuid) = await Task.Run(() => Toolbox.GetAdapterWithHighestUsage());

                if (TxtAdapterInfo != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (adapterName == "Erro" || adapterName == "Desconhecido")
                            TxtAdapterInfo.Text = "N\u00e3o foi poss\u00edvel detectar adaptador de rede";
                        else
                            TxtAdapterInfo.Text = $"{adapterType}: {adapterName}\n{description}\n\U0001f4e1 Adaptador principal detectado";
                    });
                }

                _physicalAdapters = await Task.Run(() => AdapterManager.ListPhysicalAdapters());

                if (CmbNetworkAdapter != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CmbNetworkAdapter.Items.Clear();
                        PopulateAdapterComboBox();
                        CmbNetworkAdapter.SelectedIndex = _physicalAdapters.Count > 0 ? 0 : -1;
                    });
                }
            }
            catch
            {
                if (TxtAdapterInfo != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        TxtAdapterInfo.Text = "Erro ao carregar adaptadores de rede";
                    });
                }
            }
        }

        private void PopulateAdapterComboBox()
        {
            CmbNetworkAdapter.Items.Clear();
            if (_physicalAdapters.Count == 0)
            {
                CmbNetworkAdapter.Items.Add("Nenhum adaptador f\u00edsico encontrado");
            }
            else
            {
                foreach (var adp in _physicalAdapters)
                {
                    var statusIcon = adp.IsUp ? "\u25CF" : "\u25CB";
                    var macDisplay = !string.IsNullOrEmpty(adp.CurrentMac)
                        ? $" [{FormatMacDisplay(adp.CurrentMac)}]" : "";
                    CmbNetworkAdapter.Items.Add($"{statusIcon} {adp.Description}{macDisplay}");
                }
            }
        }

        private void UpdateAdapterInfoDisplay(int index)
        {
            if (index < 0 || index >= _physicalAdapters.Count)
            {
                TxtAdapterStatus.Text = "N\u00e3o dispon\u00edvel";
                AdapterStatusIndicator.Background = _colorDefault;
                TxtCurrentMac.Text = "---";
                BtnApplyMac.IsEnabled = false;
                BtnRestoreMac.IsEnabled = false;
                if (TxtSelectedAdapterForControl != null)
                    TxtSelectedAdapterForControl.Text = "Selecione um adaptador acima";
                return;
            }

            var adp = _physicalAdapters[index];

            AdapterStatusIndicator.Background = adp.IsUp ? _colorActive : _colorError;
            TxtAdapterStatus.Text = adp.IsUp ? "Ativo" : "Inativo";
            TxtAdapterStatus.Foreground = adp.IsUp ? _colorActive : _colorError;

            if (!string.IsNullOrEmpty(adp.CurrentMac))
            {
                TxtCurrentMac.Text = FormatMacDisplay(adp.CurrentMac);
                TxtCurrentMac.Foreground = _colorActive;
            }
            else
            {
                TxtCurrentMac.Text = "N\u00e3o detectado (padr\u00e3o da placa)";
                TxtCurrentMac.Foreground = _colorDefault;
            }

            var info = $"MAC: {(string.IsNullOrEmpty(adp.CurrentMac) ? "---" : FormatMacDisplay(adp.CurrentMac))}";
            if (!string.IsNullOrEmpty(adp.PermanentMac))
                info += $"  |  F\u00e1brica: {FormatMacDisplay(adp.PermanentMac)}";
            if (adp.SupportsSpoofing)
                info += "  |  \u2705 Suporta spoof";
            else
                info += "  |  \u26A0\uFE0F Pode n\u00e3o aceitar spoof";
            TxtAdapterMac.Text = info;

            if (TxtSelectedAdapterForControl != null)
            {
                var statusEmoji = adp.IsUp ? "\u2705" : "\u274C";
                TxtSelectedAdapterForControl.Text = $"{statusEmoji} {adp.Description}  |  {adp.ConnectionName}";
                TxtSelectedAdapterForControl.Foreground = adp.IsUp ? _colorActive : _colorError;
            }
        }

        private void SetProcessingState(string message)
        {
            AdapterStatusIndicator.Background = _colorWarning;
            TxtAdapterStatus.Text = message;
            TxtAdapterStatus.Foreground = _colorWarning;
        }

        private string FormatMacDisplay(string mac)
        {
            var clean = mac.Replace("-", "").Replace(":", "").ToUpper();
            if (clean.Length != 12) return mac;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
        }

        private async Task LoadNetworkSettingsAsync()
        {
            try
            {
                var (ctcp, rss, taskOffload, networkThrottling, interruptModeration, nagle, tcpRegistry) = await Task.Run(() =>
                (
                    Toolbox.IsCTCPConfigured(),
                    Toolbox.IsRSSEnabled(),
                    Toolbox.IsTaskOffloadEnabled(),
                    SystemTweaks.IsNetworkThrottlingDisabled(),
                    Toolbox.IsInterruptModerationDisabled(),
                    Toolbox.IsNagleAlgorithmDisabled(),
                    Toolbox.IsTcpRegistryTweaksApplied()
                ));

                await Dispatcher.InvokeAsync(() =>
                {
                    ChkCTCP.IsChecked = ctcp;
                    UpdateLabel(StatusCTCP, ctcp, "Ativo", "Padr\u00e3o");

                    ChkRSS.IsChecked = rss;
                    UpdateLabel(StatusRSS, rss, "Ativo", "Padr\u00e3o");

                    ChkTaskOffload.IsChecked = taskOffload;
                    UpdateLabel(StatusTaskOffload, taskOffload, "Ativo", "Padr\u00e3o");

                    ChkNetworkThrottling.IsChecked = networkThrottling;
                    UpdateLabel(StatusNetworkThrottling, networkThrottling, "Desativado", "Padr\u00e3o");

                    ChkInterruptModeration.IsChecked = interruptModeration;
                    UpdateLabel(StatusInterruptModeration, interruptModeration, "Desativado", "Padr\u00e3o");

                    ChkNagle.IsChecked = nagle;
                    UpdateLabel(StatusNagle, nagle, "Desativado", "Padr\u00e3o");

                    ChkTcpRegistry.IsChecked = tcpRegistry;
                    UpdateLabel(StatusTcpRegistry, tcpRegistry, "Ativo", "Padr\u00e3o");
                });
            }
            catch { }
        }

        private void UpdateLabel(TextBlock label, bool isActive, string activeText, string defaultText)
        {
            if (label == null) return;
            label.Text = isActive ? activeText : defaultText;
            label.Foreground = isActive ? _colorActive : _colorDefault;
        }

        private void CmbNetworkAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbNetworkAdapter == null || CmbNetworkAdapter.SelectedIndex < 0) return;

            var idx = CmbNetworkAdapter.SelectedIndex;
            if (idx >= 0 && idx < _physicalAdapters.Count)
            {
                var adp = _physicalAdapters[idx];
                if (TxtAdapterInfo != null)
                {
                    var statusIcon = adp.IsUp ? "\u2705" : "\u274C";
                    TxtAdapterInfo.Text = $"{statusIcon} {adp.Description}\n\U0001F4E1 Conex\u00e3o: {adp.ConnectionName}";
                }
                UpdateAdapterInfoDisplay(idx);
            }
            else
            {
                if (TxtAdapterInfo != null)
                    TxtAdapterInfo.Text = "\u26A0\uFE0F Nenhum adaptador f\u00edsico encontrado\nVerifique seus adaptadores de rede";
            }
        }

        public void Cleanup()
        {
            _refreshTimer?.Stop();
            _dnsTimer?.Stop();
            this.Loaded -= NetworkPage_Loaded;
            this.Unloaded -= NetworkPage_Unloaded;
            this.DataContext = null;
        }

        private void NetworkPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task LoadStatus()
        {
            try
            {
                var dnsInfo = await Task.Run(() => Toolbox.GetActiveDnsInfo());
                await Dispatcher.InvokeAsync(() => UpdateDnsUi(dnsInfo.Provider, dnsInfo.DnsIp));
            }
            catch { }
        }

        private void UpdateDnsUi(string provider, string ip)
        {
            if (BtnCloudflare == null) return;

            BtnCloudflare.Tag = null;
            BtnGoogle.Tag = null;
            BtnDhcp.Tag = null;

            TxtCurrentDnsIp.Text = string.IsNullOrEmpty(ip) || ip == "N/A" ? "Autom\u00e1tico / DHCP" : ip;
            TxtCurrentDnsIp.Foreground = _colorDefault;

            if (provider.ToUpper().Contains("CLOUDFLARE")) { BtnCloudflare.Tag = "Selected"; TxtCurrentDnsIp.Foreground = _colorActive; }
            else if (provider.ToUpper().Contains("GOOGLE")) { BtnGoogle.Tag = "Selected"; TxtCurrentDnsIp.Foreground = _colorActive; }
            else if (provider.ToUpper().Contains("DHCP")) { BtnDhcp.Tag = "Selected"; TxtCurrentDnsIp.Foreground = _colorActive; }
            else { TxtCurrentDnsIp.Text = $"{ip} (Custom)"; TxtCurrentDnsIp.Foreground = _colorWarning; }
        }

        private void UpdateLabel(TextBlock label, bool isActive)
        {
            if (label == null) return;
            label.Text = isActive ? "Otimizado" : "Padr\u00e3o";
            label.Foreground = isActive ? _colorActive : _colorDefault;
        }

        // =========================================================
        // SEÇÃO: DNS
        // =========================================================
        private async Task ApplyDns(string provider)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask($"Configurando DNS {provider}", "Network");

            mw.ShowInfo("DNS", $"Configurando {provider}...");
            var result = await Task.Run(() => Toolbox.SetDns(provider));

            Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);

            if (result.Success) mw.ShowSuccess("SUCESSO", result.Message);
            else mw.ShowError("ERRO", result.Message);

            LoadStatus();
        }

        private void BtnDnsCloudflare_Click(object sender, RoutedEventArgs e) => ApplyDns("Cloudflare");
        private void BtnDnsGoogle_Click(object sender, RoutedEventArgs e) => ApplyDns("Google");
        private void BtnDnsReset_Click(object sender, RoutedEventArgs e) => ApplyDns("DHCP");

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Limpando Cache DNS", "Network");
                var result = await Task.Run(() => Toolbox.FlushDnsCache());
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, result.Success, result.Message);
                mw.ShowSuccess("CACHE", result.Message);
            }
        }

        private async void BtnCleanNetworkSafe_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (!await mw.ShowConfirmationDialog(
                "Limpeza Segura de Rede\n\n" +
                "Isso ir\u00e1 executar:\n" +
                "\u2022 ipconfig /flushdns (limpar cache DNS)\n" +
                "\u2022 netsh winsock reset (resetar Winsock)\n" +
                "\u2022 netsh int ip reset (resetar TCP/IP)\n" +
                "\u2022 arp -d * (esvaziar tabela ARP)\n" +
                "\u2022 netsh winhttp reset proxy (remover proxy)\n" +
                "\u2022 cmdkey /delete:* (limpar credenciais salvas)\n" +
                "\u2022 certutil -urlcache * delete (limpar cache SSL)\n\n" +
                "N\u00c3O altera configura\u00e7\u00f5es permanentes do PC.\n" +
                "Deseja continuar?"))
                return;

            mw.ShowInfo("LIMPEZA", "Executando limpeza segura de rede...");
            var result = await Task.Run(() => Toolbox.CleanNetworkSafe());
            if (result.Success)
                mw.ShowSuccess("LIMPEZA", result.Message);
            else
                mw.ShowInfo("LIMPEZA", result.Message);
        }

        private async void BtnCleanNetworkFull_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (!await mw.ShowConfirmationDialog(
                "\u26A0\uFE0F LIMPEZA COMPLETA DE REDE \u26A0\uFE0F\n\n" +
                "Isso ir\u00e1 executar TUDO da limpeza segura, MAIS:\n\n" +
                "\u2022 netsh advfirewall reset\n" +
                "   \u2192 RESTAURA O FIREWALL DO ZERO\n" +
                "   \u2192 Remove TODAS as regras personalizadas\n" +
                "   \u2192 Regras de apps, jogos, antiv\u00edrus, VPNs ser\u00e3o PERDIDAS\n" +
                "   \u2192 Firewall volta ao estado original de f\u00e1brica\n\n" +
                "Recomendado apenas para casos extremos (ex: captcha infinito,\n" +
                "rede muito suja, problemas persistentes ap\u00f3s limpeza segura).\n\n" +
                "Tem certeza que deseja continuar?"))
                return;

            if (!await mw.ShowConfirmationDialog(
                "\u26A0\uFE0F CONFIRMA\u00c7\u00c3O FINAL \u26A0\uFE0F\n\n" +
                "VOC\u00ca EST\u00c1 PRESTES A RESETAR COMPLETAMENTE O FIREWALL.\n\n" +
                "Isso pode afetar programas que dependem de regras de firewall\n" +
                "personalizadas (jogos, servidores, VPNs, emuladores).\n\n" +
                "Deseja realmente prosseguir?"))
                return;

            mw.ShowInfo("LIMPEZA TOTAL", "Executando limpeza completa de rede...");
            var result = await Task.Run(() => Toolbox.CleanNetworkFull());
            if (result.Success)
                mw.ShowSuccess("LIMPEZA TOTAL", result.Message);
            else
                mw.ShowInfo("LIMPEZA TOTAL", result.Message);
        }

        // =========================================================
        // SEÇÃO: CONTROLE DO ADAPTADOR (LIGAR/DESLIGAR/RESTART)
        // =========================================================
        private async void BtnEnableAdapter_Click(object sender, RoutedEventArgs e)
        {
            var (name, success) = GetSelectedAdapterConnectionName();
            if (!success) return;
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (!await mw.ShowConfirmationDialog(
                $"Tem certeza que deseja ATIVAR o adaptador '{name}'?\n\n" +
                $"Isso pode restaurar sua conex\u00e3o de rede."))
                return;

            SetProcessingState("Ativando...");
            mw.ShowInfo("ATIVANDO", $"Ativando adaptador '{name}'...");
            var result = await Task.Run(() => AdapterManager.SetAdapterState(name, true));
            if (result.Success)
            {
                mw.ShowSuccess("ATIVO", result.Message);
                await RefreshAdapterStatus(forceDelay: true);
            }
            else
            {
                mw.ShowError("ERRO", result.Message);
                SetProcessingState("Falha");
            }
        }

        private async void BtnDisableAdapter_Click(object sender, RoutedEventArgs e)
        {
            var (name, success) = GetSelectedAdapterConnectionName();
            if (!success) return;
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (!await mw.ShowConfirmationDialog(
                $"Tem certeza que deseja DESLIGAR o adaptador '{name}'?\n\n" +
                $"Voc\u00ea perder\u00e1 a conex\u00e3o de rede at\u00e9 reativ\u00e1-lo manualmente ou clicar em 'Ligar'."))
                return;

            SetProcessingState("Desativando...");
            mw.ShowInfo("DESLIGANDO", $"Desativando adaptador '{name}'...");
            var result = await Task.Run(() => AdapterManager.SetAdapterState(name, false));
            if (result.Success)
            {
                mw.ShowSuccess("DESATIVADO", result.Message);
                await RefreshAdapterStatus(forceDelay: true);
            }
            else
            {
                mw.ShowError("ERRO", result.Message);
                SetProcessingState("Falha");
            }
        }

        private async void BtnRestartAdapter_Click(object sender, RoutedEventArgs e)
        {
            var (name, success) = GetSelectedAdapterConnectionName();
            if (!success) return;
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (!await mw.ShowConfirmationDialog(
                $"Tem certeza que deseja REINICIAR o adaptador '{name}'?\n\n" +
                $"A conex\u00e3o ser\u00e1 interrompida por alguns segundos.\n" +
                $"Aplicativos online podem ser temporariamente afetados."))
                return;

            SetProcessingState("Reiniciando...");
            mw.ShowInfo("REINICIANDO", $"Reiniciando adaptador '{name}'...\nA conex\u00e3o ser\u00e1 interrompida por alguns segundos.");
            var result = await AdapterManager.RestartAdapterAsync(name);
            if (result.Success)
            {
                mw.ShowSuccess("REINICIADO", result.Message);
                await RefreshAdapterStatus(forceDelay: true);
            }
            else
            {
                mw.ShowError("ERRO", result.Message);
            }
        }

        private (string Name, bool Success) GetSelectedAdapterConnectionName()
        {
            if (CmbNetworkAdapter.SelectedIndex < 0 || CmbNetworkAdapter.SelectedIndex >= _physicalAdapters.Count)
            {
                if (Application.Current.MainWindow is MainWindow mw)
                    mw.ShowError("ATEN\u00c7\u00c3O", "Selecione um adaptador de rede f\u00edsico primeiro.");
                return ("", false);
            }
            return (_physicalAdapters[CmbNetworkAdapter.SelectedIndex].ConnectionName, true);
        }

        private async Task RefreshAdapterStatus(bool forceDelay = false)
        {
            if (forceDelay)
                await Task.Delay(2000);

            if (_physicalAdapters.Count == 0) return;

            var refreshed = await Task.Run(() => AdapterManager.ListPhysicalAdapters());
            if (refreshed.Count == 0) return;

            await Dispatcher.InvokeAsync(() =>
            {
                var prevIndex = CmbNetworkAdapter.SelectedIndex;
                var prevName = prevIndex >= 0 && prevIndex < _physicalAdapters.Count
                    ? _physicalAdapters[prevIndex].ConnectionName : "";

                _physicalAdapters = refreshed;
                PopulateAdapterComboBox();

                // Tenta manter o mesmo adaptador selecionado pelo nome
                var newIndex = 0;
                if (!string.IsNullOrEmpty(prevName))
                {
                    for (int i = 0; i < _physicalAdapters.Count; i++)
                    {
                        if (_physicalAdapters[i].ConnectionName == prevName)
                        { newIndex = i; break; }
                    }
                }
                CmbNetworkAdapter.SelectedIndex = newIndex < _physicalAdapters.Count ? newIndex : 0;
                UpdateAdapterInfoDisplay(CmbNetworkAdapter.SelectedIndex);
                SetRefreshIndicator(DateTime.Now.ToString("HH:mm:ss"));
            });
        }

        // =========================================================
        // SEÇÃO: MAC SPOOFING
        // =========================================================
        private void BtnGenerateMac_Click(object sender, RoutedEventArgs e)
        {
            _generatedMac = AdapterManager.GenerateRandomMac();
            var formatted = FormatMacDisplay(_generatedMac);
            TxtNewMac.Text = formatted;
            TxtNewMac.Foreground = _colorActive;
            BtnApplyMac.IsEnabled = true;

            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowSuccess("MAC GERADO", $"Novo MAC: {formatted}");
        }

        private async void BtnApplyMac_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (string.IsNullOrEmpty(_generatedMac))
            {
                mw.ShowError("ATEN\u00c7\u00c3O", "Clique em 'Gerar Novo MAC' primeiro.");
                return;
            }

            if (CmbNetworkAdapter.SelectedIndex < 0 || CmbNetworkAdapter.SelectedIndex >= _physicalAdapters.Count)
            {
                mw.ShowError("ATEN\u00c7\u00c3O", "Selecione um adaptador de rede f\u00edsico primeiro.");
                return;
            }

            var adp = _physicalAdapters[CmbNetworkAdapter.SelectedIndex];
            var formattedMac = FormatMacDisplay(_generatedMac);
            var currentMacBefore = FormatMacDisplay(adp.CurrentMac);

            if (!await mw.ShowConfirmationDialog(
                $"Voc\u00ea est\u00e1 prestes a alterar o MAC address do adaptador:\n\n" +
                $"{adp.Description}\n" +
                $"MAC Atual: {currentMacBefore}\n" +
                $"Novo MAC:  {formattedMac}\n\n" +
                $"O adaptador ser\u00e1 reiniciado automaticamente.\n" +
                $"A conex\u00e3o ser\u00e1 interrompida por alguns segundos.\n\n" +
                $"Continuar?"))
                return;

            if (!adp.SupportsSpoofing)
            {
                var tryWorkaround = await mw.ShowConfirmationDialog(
                    $"Este adaptador N\u00c3O possui suporte oficial a NetworkAddress.\n\n" +
                    $"Driver: {adp.Description}\n\n" +
                    $"O Kit pode tentar adicionar o suporte manualmente no registro.\n" +
                    $"Deseja tentar este workaround?");

                if (tryWorkaround)
                {
                    mw.ShowInfo("WORKAROUND", "Adicionando suporte a NetworkAddress no registro...");
                    await Task.Run(() => AdapterManager.EnsureNetworkAddressSupport(adp.Id));
                    (adp.SupportsSpoofing, _) = await Task.Run(() => AdapterManager.CheckNetworkAddressSupport(adp.Id));
                }

                if (!adp.SupportsSpoofing)
                {
                    if (!await mw.ShowConfirmationDialog(
                        $"O driver pode n\u00e3o aceitar o novo MAC.\n\n" +
                        $"Mesmo assim, o valor ser\u00e1 escrito no registro — se o driver ignorar,\n" +
                        $"o MAC n\u00e3o ser\u00e1 alterado.\n\n" +
                        $"Tentar mesmo assim?"))
                        return;
                }
            }

            mw.ShowInfo("APLICANDO MAC", $"Aplicando MAC {formattedMac} em '{adp.Description}'...");

            var setResult = await Task.Run(() => AdapterManager.SetMacAddress(adp.Id, _generatedMac));
            if (!setResult.Success)
            {
                mw.ShowError("ERRO", setResult.Message);
                return;
            }

            mw.ShowInfo("REINICIANDO", "MAC aplicado no registro. Reiniciando adaptador...");

            var restartResult = await AdapterManager.RestartAdapterAsync(adp.ConnectionName);
            await Task.Delay(1000);

            var (changed, liveMac) = await Task.Run(() =>
                AdapterManager.VerifyMacChange(adp.ConnectionName, _generatedMac));

            if (restartResult.Success)
            {
                BtnRestoreMac.IsEnabled = true;
                await RefreshAdapterStatus(forceDelay: true);

                if (changed)
                {
                    mw.ShowSuccess("SUCESSO", $"MAC alterado para {formattedMac}\n{restartResult.Message}");
                }
                else
                {
                    var liveDisplay = !string.IsNullOrEmpty(liveMac) ? FormatMacDisplay(liveMac) : "N/A";
                    mw.ShowInfo("VERIFICA\u00c7\u00c3O",
                        $"MAC pode n\u00e3o ter sido alterado.\n" +
                        $"Esperado: {formattedMac}\n" +
                        $"Atual:    {liveDisplay}\n\n" +
                        $"O driver deste adaptador pode n\u00e3o suportar NetworkAddress.\n" +
                        $"Tente: 1) Reiniciar o PC  2) Verificar no Gerenciador de Dispositivos\n" +
                        $"(Propriedades > Avan\u00e7ado > Network Address)");
                }

                _generatedMac = "";
                BtnApplyMac.IsEnabled = false;
                TxtNewMac.Text = "Clique em 'Gerar' para criar um novo";
                TxtNewMac.Foreground = _colorWarning;
            }
            else
            {
                var msg = $"MAC foi aplicado no registro, mas houve um problema ao reiniciar o adaptador:\n{restartResult.Message}\n\n";
                msg += changed
                    ? "O MAC j\u00e1 est\u00e1 ativo mesmo assim."
                    : "Reinicie o adaptador manualmente ou reinicie o PC para aplicar.";

                mw.ShowInfo("ATEN\u00c7\u00c3O", msg);
            }
        }

        private async void BtnRestoreMac_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (CmbNetworkAdapter.SelectedIndex < 0 || CmbNetworkAdapter.SelectedIndex >= _physicalAdapters.Count)
            {
                mw.ShowError("ATEN\u00c7\u00c3O", "Selecione um adaptador de rede f\u00edsico primeiro.");
                return;
            }

            var adp = _physicalAdapters[CmbNetworkAdapter.SelectedIndex];

            if (!await mw.ShowConfirmationDialog(
                $"Restaurar o MAC original de f\u00e1brica do adaptador:\n\n" +
                $"{adp.Description}\n" +
                $"MAC Atual: {FormatMacDisplay(adp.CurrentMac)}\n\n" +
                $"O adaptador ser\u00e1 reiniciado automaticamente.\n" +
                $"Continuar?"))
                return;

            mw.ShowInfo("RESTAURANDO", $"Removendo MAC personalizado de '{adp.Description}'...");

            var restoreResult = await Task.Run(() => AdapterManager.RestoreOriginalMac(adp.Id, adp.NetCfgInstanceId, adp.ConnectionName));
            if (!restoreResult.Success)
            {
                mw.ShowError("ERRO", restoreResult.Message);
                return;
            }

            mw.ShowInfo("REINICIANDO", "MAC original restaurado. Reiniciando adaptador...");

            var restartResult = await AdapterManager.RestartAdapterAsync(adp.ConnectionName);
            await Task.Delay(1000);

            var (changed, _) = await Task.Run(() =>
                AdapterManager.VerifyMacChange(adp.ConnectionName, ""));

            if (restartResult.Success)
            {
                BtnRestoreMac.IsEnabled = false;
                await RefreshAdapterStatus(forceDelay: true);
                mw.ShowSuccess("RESTAURADO", $"MAC original restaurado com sucesso.\n{restartResult.Message}");
            }
            else
            {
                mw.ShowSuccess("RESTAURADO",
                    $"MAC original restaurado no registro.\n{restartResult.Message}\n\n" +
                    $"Reinicie o adaptador manualmente ou o PC para aplicar.");
            }
        }

        // =========================================================
        // AUTO-DETECT MAC
        // =========================================================
        private async void BtnAutoDetectMac_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (CmbNetworkAdapter.SelectedIndex < 0 || CmbNetworkAdapter.SelectedIndex >= _physicalAdapters.Count)
            {
                mw.ShowError("ATENÇÃO", "Selecione um adaptador de rede físico primeiro.");
                return;
            }

            if (!await mw.ShowConfirmationDialog(
                "Auto-detect MAC\n\n" +
                "Este processo irá testar até 40 MACs diferentes até encontrar um que funcione " +
                "no seu adaptador. O adaptador será reiniciado a cada tentativa.\n\n" +
                "Deseja continuar?"))
                return;

            var adp = _physicalAdapters[CmbNetworkAdapter.SelectedIndex];
            BtnAutoDetectMac.IsEnabled = false;
            TxtAutoDetectProgress.Visibility = Visibility.Visible;
            TxtAutoDetectProgress.Text = "Iniciando auto-detect...";

            var result = await AdapterManager.AutoDetectMacAsync(
                adp.Id, adp.ConnectionName,
                progress => Dispatcher.Invoke(() => TxtAutoDetectProgress.Text = progress));

            BtnAutoDetectMac.IsEnabled = true;

            if (result != null)
            {
                TxtNewMac.Text = FormatMacDisplay(result);
                BtnApplyMac.IsEnabled = true;
                BtnApplyMacText.Text = "✅ MAC detectado! Aplicar?";
                await RefreshAdapterStatus(forceDelay: true);
                mw.ShowSuccess("AUTO-DETECT",
                    $"MAC funcional encontrado: {FormatMacDisplay(result)}\n\n" +
                    $"O MAC já foi aplicado no registro e está ativo no adaptador.");
            }
            else
            {
                mw.ShowError("AUTO-DETECT",
                    "Nenhum MAC funcionou após 40 tentativas.\n\n" +
                    "Isso pode ocorrer em adaptadores muito restritivos. " +
                    "Tente um adaptador diferente ou reinicie o PC e tente novamente.");
            }
        }

        // =========================================================
        // SEÇÃO: TCP/IP GLOBAL
        // =========================================================
        private async void ChkCTCP_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkCTCP.IsEnabled = false;
            StatusCTCP.Text = "Aplicando...";
            StatusCTCP.Foreground = _colorWarning;

            try
            {
                if (ChkCTCP.IsChecked == true)
                {
                    var result = await Task.Run(() => Toolbox.ApplyLatencyCongestionControl());
                    if (result.Success)
                    {
                        mw.ShowSuccess("CTCP", "Algoritmo CTCP aplicado para jogos.");
                        StatusCTCP.Text = "Ativo";
                        StatusCTCP.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkCTCP.IsChecked = false;
                        StatusCTCP.Text = "Erro";
                        StatusCTCP.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => Toolbox.RevertLatencyCongestionControl());
                    if (result.Success)
                    {
                        mw.ShowSuccess("CTCP", "CTCP revertido para padr\u00e3o.");
                        StatusCTCP.Text = "Padr\u00e3o";
                        StatusCTCP.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkCTCP.IsChecked = true;
                        StatusCTCP.Text = "Erro";
                        StatusCTCP.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkCTCP.IsEnabled = true;
            }
        }

        private async void ChkRSS_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkRSS.IsEnabled = false;
            StatusRSS.Text = "Aplicando...";
            StatusRSS.Foreground = _colorWarning;

            try
            {
                if (ChkRSS.IsChecked == true)
                {
                    var result = await Task.Run(() => Toolbox.EnableRSS());
                    if (result.Success)
                    {
                        mw.ShowSuccess("RSS", "RSS habilitado para multi-core.");
                        StatusRSS.Text = "Ativo";
                        StatusRSS.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkRSS.IsChecked = false;
                        StatusRSS.Text = "Erro";
                        StatusRSS.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => Toolbox.DisableRSS());
                    if (result.Success)
                    {
                        mw.ShowSuccess("RSS", "RSS desabilitado.");
                        StatusRSS.Text = "Desativado";
                        StatusRSS.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkRSS.IsChecked = true;
                        StatusRSS.Text = "Erro";
                        StatusRSS.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkRSS.IsEnabled = true;
            }
        }

        private async void ChkTaskOffload_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkTaskOffload.IsEnabled = false;
            StatusTaskOffload.Text = "Aplicando...";
            StatusTaskOffload.Foreground = _colorWarning;

            try
            {
                if (ChkTaskOffload.IsChecked == true)
                {
                    var result = await Task.Run(() => Toolbox.EnableTaskOffload());
                    if (result.Success)
                    {
                        mw.ShowSuccess("TaskOffload", "TaskOffload habilitado para reduzir carga CPU.");
                        StatusTaskOffload.Text = "Ativo";
                        StatusTaskOffload.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkTaskOffload.IsChecked = false;
                        StatusTaskOffload.Text = "Erro";
                        StatusTaskOffload.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => Toolbox.DisableTaskOffload());
                    if (result.Success)
                    {
                        mw.ShowSuccess("TaskOffload", "TaskOffload desabilitado.");
                        StatusTaskOffload.Text = "Desativado";
                        StatusTaskOffload.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkTaskOffload.IsChecked = true;
                        StatusTaskOffload.Text = "Erro";
                        StatusTaskOffload.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkTaskOffload.IsEnabled = true;
            }
        }

        private async void ChkNetworkThrottling_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkNetworkThrottling.IsEnabled = false;
            StatusNetworkThrottling.Text = "Aplicando...";
            StatusNetworkThrottling.Foreground = _colorWarning;

            try
            {
                if (ChkNetworkThrottling.IsChecked == true)
                {
                    var result = await Task.Run(() => Toolbox.DisableNetworkThrottling());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Network Throttling", "Network Throttling desativado para m\u00e1ximo desempenho.");
                        StatusNetworkThrottling.Text = "Desativado";
                        StatusNetworkThrottling.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkNetworkThrottling.IsChecked = false;
                        StatusNetworkThrottling.Text = "Erro";
                        StatusNetworkThrottling.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => Toolbox.EnableNetworkThrottling());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Network Throttling", "Network Throttling habilitado (padr\u00e3o).");
                        StatusNetworkThrottling.Text = "Ativo";
                        StatusNetworkThrottling.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkNetworkThrottling.IsChecked = true;
                        StatusNetworkThrottling.Text = "Erro";
                        StatusNetworkThrottling.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkNetworkThrottling.IsEnabled = true;
            }
        }

        // =========================================================
        // SEÇÃO: ADAPTADOR DE REDE (NIC)
        // =========================================================
        private async void ChkInterruptModeration_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkInterruptModeration.IsEnabled = false;
            StatusInterruptModeration.Text = "Aplicando...";
            StatusInterruptModeration.Foreground = _colorWarning;

            try
            {
                if (ChkInterruptModeration.IsChecked == true)
                {
                    var result = await Task.Run(() => SystemTweaks.OptimizeNetworkAdapterForGaming());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Interrupt Moderation", "Interrupt Moderation desabilitado para reduzir lat\u00eancia.");
                        StatusInterruptModeration.Text = "Desativado";
                        StatusInterruptModeration.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkInterruptModeration.IsChecked = false;
                        StatusInterruptModeration.Text = "Erro";
                        StatusInterruptModeration.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => SystemTweaks.RevertNetworkAdapterSettings());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Interrupt Moderation", "Interrupt Moderation revertido para padr\u00e3o.");
                        StatusInterruptModeration.Text = "Padr\u00e3o";
                        StatusInterruptModeration.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkInterruptModeration.IsChecked = true;
                        StatusInterruptModeration.Text = "Erro";
                        StatusInterruptModeration.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkInterruptModeration.IsEnabled = true;
            }
        }

        private async void ChkNagle_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkNagle.IsEnabled = false;
            StatusNagle.Text = "Aplicando...";
            StatusNagle.Foreground = _colorWarning;

            try
            {
                if (ChkNagle.IsChecked == true)
                {
                    var result = await Task.Run(() => SystemTweaks.DisableNagleAlgorithm());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Nagle's Algorithm", "Nagle's Algorithm desabilitado para reduzir lat\u00eancia.");
                        StatusNagle.Text = "Desativado";
                        StatusNagle.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkNagle.IsChecked = false;
                        StatusNagle.Text = "Erro";
                        StatusNagle.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => SystemTweaks.RevertNagleAlgorithm());
                    if (result.Success)
                    {
                        mw.ShowSuccess("Nagle's Algorithm", "Nagle's Algorithm revertido para padr\u00e3o.");
                        StatusNagle.Text = "Padr\u00e3o";
                        StatusNagle.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkNagle.IsChecked = true;
                        StatusNagle.Text = "Erro";
                        StatusNagle.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkNagle.IsEnabled = true;
            }
        }

        // =========================================================
        // SEÇÃO: REGISTRY TWEAKS
        // =========================================================
        private async void ChkTcpRegistry_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !(Application.Current.MainWindow is MainWindow mw)) return;

            ChkTcpRegistry.IsEnabled = false;
            StatusTcpRegistry.Text = "Aplicando...";
            StatusTcpRegistry.Foreground = _colorWarning;

            try
            {
                if (ChkTcpRegistry.IsChecked == true)
                {
                    var result = await Task.Run(() => Toolbox.ApplyTcpRegistryTweaks());
                    if (result.Success)
                    {
                        mw.ShowSuccess("TCP Registry", "Registry tweaks de TCP aplicados para gaming.");
                        StatusTcpRegistry.Text = "Ativo";
                        StatusTcpRegistry.Foreground = _colorActive;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkTcpRegistry.IsChecked = false;
                        StatusTcpRegistry.Text = "Erro";
                        StatusTcpRegistry.Foreground = _colorError;
                    }
                }
                else
                {
                    var result = await Task.Run(() => Toolbox.RevertTcpRegistryTweaks());
                    if (result.Success)
                    {
                        mw.ShowSuccess("TCP Registry", "Registry tweaks de TCP revertidos para padr\u00e3o.");
                        StatusTcpRegistry.Text = "Padr\u00e3o";
                        StatusTcpRegistry.Foreground = _colorDefault;
                    }
                    else
                    {
                        mw.ShowError("ERRO", result.Message);
                        ChkTcpRegistry.IsChecked = true;
                        StatusTcpRegistry.Text = "Erro";
                        StatusTcpRegistry.Foreground = _colorError;
                    }
                }
            }
            finally
            {
                ChkTcpRegistry.IsEnabled = true;
            }
        }

        // =========================================================
        // BOTÃO: RESETAR PILHA DE REDE
        // =========================================================
        private async void BtnResetNetwork_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            if (await mw.ShowConfirmationDialog("Deseja resetar TODAS as configura\u00e7\u00f5es de rede para o padr\u00e3o do Windows?\n\nIsso ir\u00e1 reverter todas as otimiza\u00e7\u00f5es aplicadas."))
            {
                mw.ShowInfo("RESETANDO", "Resetando pilha de rede...");
                await Task.Run(() => SystemTweaks.ResetEthernetSettings());
                mw.ShowSuccess("RESETADO", "Pilha de rede resetada com sucesso!");
            }
        }

    }
}
