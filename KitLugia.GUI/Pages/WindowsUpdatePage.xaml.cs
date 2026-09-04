using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MainWindow = KitLugia.GUI.MainWindow;

namespace KitLugia.GUI.Pages
{
    public partial class WindowsUpdatePage : Page
    {
        private bool _isBusy;
        private bool _isRefreshing;
        private List<InsiderChannelInfo>? _channelInfos;
        private int _currentBuild;
        private string? _currentUbr;

        public WindowsUpdatePage()
        {
            InitializeComponent();
            Loaded += WindowsUpdatePage_Loaded;
            Unloaded += WindowsUpdatePage_Unloaded;
        }

        public void Cleanup()
        {
            Loaded -= WindowsUpdatePage_Loaded;
            Unloaded -= WindowsUpdatePage_Unloaded;
            DataContext = null;
        }

        private void WindowsUpdatePage_Unloaded(object sender, RoutedEventArgs e) => Cleanup();

        private async void WindowsUpdatePage_Loaded(object sender, RoutedEventArgs e)
        {
            DisableMouseWheelSelection(CmbChannel);
            DisableMouseWheelSelection(CmbPauseDays);
            PopulateChannels();
            await RefreshSystemStatusAsync();
        }
        private void PopulateChannels()
        {
            try
            {
                var status = OfflineInsiderManager.GetStatus();
                var infos = OfflineInsiderManager.GetChannelInfos(status.BuildNumber, status.IsServer);
                _channelInfos = infos;

                CmbChannel.Items.Clear();
                foreach (var info in infos)
                {
                    CmbChannel.Items.Add(new ComboBoxItem
                    {
                        Content = $"{info.DisplayName} — versao alvo: {info.TargetVersion}",
                        Tag = ((int)info.Channel).ToString(),
                        IsEnabled = info.Available,
                        ToolTip = info.Description
                    });
                }

                if (CmbChannel.Items.Count > 0)
                    CmbChannel.SelectedIndex = 0;

                UpdateChannelInfo();
            }
            catch (Exception ex)
            {
                ShowError("Erro", $"Falha ao listar canais: {ex.Message}");
            }
        }

        private InsiderChannelInfo? GetSelectedChannelInfo()
        {
            if (_channelInfos == null || CmbChannel.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string tag || !int.TryParse(tag, out int choice))
                return null;
            return _channelInfos.FirstOrDefault(c => (int)c.Channel == choice);
        }

        private void CmbChannel_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateChannelInfo();

        private void UpdateChannelInfo()
        {
            var info = GetSelectedChannelInfo();
            if (info == null)
            {
                TxtChannelInfo.Text = "Nenhum canal disponivel para este PC.";
                TxtBuildCompare.Text = "Detectando build do sistema...";
                return;
            }

            TxtChannelInfo.Text = info.Available
                ? $"{info.DisplayName}: {info.Description}"
                : $"{info.DisplayName}: NAO disponivel para este PC. {info.Description}";

            UpdateBuildCompare(info);
        }

        private void UpdateBuildCompare(InsiderChannelInfo info)
        {
            if (_currentBuild <= 0)
            {
                TxtBuildCompare.Text = "Build atual: detectando...";
                return;
            }

            var buildStr = _currentBuild.ToString();
            if (!string.IsNullOrEmpty(_currentUbr)) buildStr += "." + _currentUbr;

            string result;
            var target = info.TargetVersion;
            var targetNum = ParseTargetNumber(target);

            if (targetNum == null)
            {
                result = "nao foi possivel comparar com o alvo do canal.";
            }
            else if (target.Contains("proximo RTM", StringComparison.OrdinalIgnoreCase))
            {
                result = _currentBuild >= targetNum.Value
                    ? $"seu build ja esta no nivel do canal (numeros batem)."
                    : $"seu build esta abaixo do alvo ({targetNum}) — atualize pelo Windows Update.";
            }
            else if (target.EndsWith("+"))
            {
                result = _currentBuild >= targetNum.Value
                    ? $"seu build atende o alvo ({targetNum}+) — numeros batem."
                    : $"seu build esta abaixo do alvo ({targetNum}+) — faltam updates pelo Windows Update.";
            }
            else
            {
                result = _currentBuild == targetNum.Value
                    ? "numeros batem (build igual ao alvo do canal)."
                    : _currentBuild > targetNum.Value
                        ? $"seu build ja ultrapassou o alvo ({targetNum})."
                        : $"seu build esta abaixo do alvo ({targetNum}).";
            }

            TxtBuildCompare.Text = $"Seu build: {buildStr} | Canal {info.DisplayName} (alvo {target}) -> {result}";
        }

        private static int? ParseTargetNumber(string target)
        {
            var digits = new string(target.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : null;
        }

        private async Task RefreshSystemStatusAsync()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            try
            {
                var status = await Task.Run(() => WindowsUpdateManager.GetStatus());
                _currentBuild = status.CurrentBuild;
                _currentUbr = status.UBR;

                TxtBuild.Text = status.CurrentBuild > 0
                    ? $"{status.CurrentBuild}" + (!string.IsNullOrEmpty(status.UBR) ? $".{status.UBR}" : "")
                    : "N/A";
                TxtVersion.Text = !string.IsNullOrEmpty(status.DisplayVersion)
                    ? $"{status.DisplayVersion}" + (!string.IsNullOrEmpty(status.CurrentVersion) ? $" (NT {status.CurrentVersion})" : "")
                    : "N/A";

                TxtWUStatus.Text = status.IsWUServiceRunning ? "Rodando" : "Parado";
                TxtWUStatus.Foreground = status.IsWUServiceRunning
                    ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentColor")
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMuted");

                TxtUSOStatus.Text = status.IsUSOServiceRunning ? "Rodando" : "Parado";
                TxtUSOStatus.Foreground = TxtWUStatus.Foreground;

                TxtWisvcStatus.Text = status.IsFlightingServiceRunning ? "Rodando" : "Parado";
                TxtWisvcStatus.Foreground = status.IsFlightingServiceRunning
                    ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentColor")
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMuted");

                TxtInsiderStatus.Text = status.IsInsiderEnrolled ? "Inscrito" : "Nao inscrito";
                TxtInsiderStatus.Foreground = status.IsInsiderEnrolled
                    ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentColor")
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMuted");

                TxtInsiderChannel.Text = !string.IsNullOrEmpty(status.InsiderChannel)
                    ? $"{status.InsiderChannel}"
                    : "Nenhum";

                TxtFlightSigning.Text = status.FlightSigningEnabled ? "Ativado" : "Desativado";
                TxtFlightSigning.Foreground = status.FlightSigningEnabled
                    ? (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentColor")
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("TextMuted");

                TxtPausedStatus.Text = status.IsPaused ? "Sim" : "Nao";
                TxtPausedStatus.Foreground = status.IsPaused
                    ? System.Windows.Media.Brushes.Orange
                    : (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentColor");

                TxtPauseDays.Text = status.IsPaused
                    ? $"{status.PauseDaysRemaining} dias restantes"
                    : "N/A";

                TxtTargetRelease.Text = !string.IsNullOrEmpty(status.TargetReleaseVersionInfo)
                    ? status.TargetReleaseVersionInfo
                    : "Nenhum";

                TxtDeferralDays.Text = status.DeferralDays > 0
                    ? $"{status.DeferralDays} dias"
                    : "Nenhum";

                UpdateChannelInfo();
            }
            catch (Exception ex)
            {
                ShowError("Erro", $"Falha ao obter status do sistema: {ex.Message}");
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e) => await RefreshSystemStatusAsync();

        private int GetSelectedPauseDays()
        {
            if (CmbPauseDays.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out int days))
                return days;
            return 7;
        }

        /// <summary>
        /// Impede que o wheel do mouse troque a seleção de um ComboBox FECHADO e, em vez
        /// de engolir o evento (o que travava o scroll da página com o cursor sobre o box),
        /// repassa o delta ao ScrollViewer externo — padrão RouteWheel da StoreRemakePage.
        /// Com o popup ABERTO o combo mantém o comportamento padrão (wheel rola a lista).
        /// </summary>
        private void DisableMouseWheelSelection(System.Windows.Controls.ComboBox combo)
        {
            combo.PreviewMouseWheel += (s, e) =>
            {
                // Popup aberto: quem recebe o wheel é a lista do popup — deixa o padrão.
                if (combo.IsDropDownOpen) return;

                // Bloqueia a troca de item sem clique mas não mata o scroll da página:
                // marca Handled e repassa o delta ao ScrollViewer que envolve a página.
                e.Handled = true;
                if (MainScroll != null)
                    MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta / 3.0);
            };
        }

        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            var days = GetSelectedPauseDays();
            await RunWithLoadingAsync("Pausando updates...", async () =>
            {
                await Task.Run(() => WindowsUpdateManager.PauseUpdates(days));
                ShowInfo("Sucesso", $"Updates pausados por {days} dias.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Retomando updates...", async () =>
            {
                await Task.Run(() => WindowsUpdateManager.ResumeUpdates());
                ShowInfo("Sucesso", "Updates retomados.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnRefreshScan_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Iniciando scan de updates...", async () =>
            {
                await Task.Run(() => WindowsUpdateManager.RefreshScan());
                ShowInfo("Sucesso", "Scan de update iniciado.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnSetTarget_Click(object sender, RoutedEventArgs e)
        {
            var target = TxtTargetVersion.Text.Trim();
            if (string.IsNullOrEmpty(target))
            {
                ShowInfo("Atencao", "Digite uma versao alvo (ex: 23H2, 24H2) ou use 'Limpar Target Version' para remover.");
                return;
            }

            await RunWithLoadingAsync("Definindo target version...", async () =>
            {
                await Task.Run(() => WindowsUpdateManager.SetTargetVersion(target));
                ShowInfo("Sucesso", $"Target version definido para: {target}");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnClearTarget_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Limpando target version...", async () =>
            {
                await Task.Run(() => WindowsUpdateManager.ClearTargetVersion());
                TxtTargetVersion.Text = "";
                ShowInfo("Sucesso", "Target version removido.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnEnroll_Click(object sender, RoutedEventArgs e)
        {
            var info = GetSelectedChannelInfo();
            if (info == null)
            {
                ShowInfo("Atencao", "Nenhum canal disponivel para este PC.");
                return;
            }
            if (!info.Available)
            {
                ShowInfo("Atencao", $"O canal {info.DisplayName} nao esta disponivel para este PC (build atual).");
                return;
            }

            await RunWithLoadingAsync($"Inscricao no canal {info.DisplayName}...", async () =>
            {
                await Task.Run(() => OfflineInsiderManager.Enroll(info.Channel));
                ShowInfo("Inscricao", $"Inscrito no canal {info.DisplayName} com sucesso. Reinicie o PC para aplicar.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnRefreshWu_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Limpando cache do Windows Update...", async () =>
            {
                await Task.Run(() => OfflineInsiderManager.RefreshWUScan());
                ShowInfo("Sucesso", "Cache do Windows Update limpo e servicos reiniciados.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnResetInsider_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Resetando configuracao Insider...", async () =>
            {
                await Task.Run(() => OfflineInsiderManager.ResetConfig());
                ShowInfo("Sucesso", "Configuracao Insider resetada.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnUnenroll_Click(object sender, RoutedEventArgs e)
        {
            await RunWithLoadingAsync("Desinscrevendo (limpeza total)...", async () =>
            {
                await Task.Run(() => OfflineInsiderManager.Unenroll(fullCleanup: true));
                ShowInfo("Sucesso", "Desinscricao completa. Flight signing desativado.");
            });
            await RefreshSystemStatusAsync();
        }

        private async void BtnRefreshInstalled_Click(object sender, RoutedEventArgs e) => await RefreshInstalledUpdatesAsync();

        private async Task RefreshInstalledUpdatesAsync()
        {
            try
            {
                var updates = await Task.Run(() => UpdateControlManager.ListInstalledUpdates());
                LstInstalledUpdates.ItemsSource = updates;
                if (updates.Count == 0)
                    ShowInfo("Info", "Nenhum KB instalado encontrado ou o Windows Update Agent nao respondeu.");
            }
            catch (Exception ex)
            {
                ShowError("Erro", $"Falha ao listar updates instalados: {ex.Message}");
            }
        }

        private async void BtnRemoveUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (LstInstalledUpdates.SelectedItem is not InstalledUpdate selected || string.IsNullOrEmpty(selected.HotFixId))
            {
                ShowInfo("Atencao", "Selecione um KB instalado na lista primeiro.");
                return;
            }

            var kb = selected.HotFixId.ToUpperInvariant();
            var confirm = MessageBox.Show(
                $"Remover o update {kb} ({selected.Description})?\n\n" +
                "Isso desfaz o patch instalado — os binarios voltam a versao anterior (downgrade). " +
                "KBs de qualidade mais novos que dependiam dele tambem serao removidos.\n\n" +
                "Recomendado: criar um restore point antes.",
                "Confirmar remocao (downgrade)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            await RunWithLoadingAsync($"Removendo {kb} (wusa /uninstall)...", async () =>
            {
                var result = await Task.Run(() => UpdateControlManager.UninstallUpdate(kb));
                var desc = UpdateControlManager.DescribeExitCode(result.ExitCode);
                var msg = $"{kb}: {desc}";
                if (result.ExitCode == 0 || result.ExitCode == 3010)
                    ShowInfo("Sucesso", msg);
                else
                {
                    var detail = !string.IsNullOrWhiteSpace(result.Output)
                        ? $"\n\nDetalhes:\n{result.Output.Trim()}"
                        : "";
                    ShowError("Falha na remocao", msg + detail);
                }
            });
            await RefreshInstalledUpdatesAsync();
        }

        private void BtnBrowseUpdate_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Selecionar pacote de update (.msu / .cab)",
                Filter = "Pacotes de update (*.msu;*.cab)|*.msu;*.cab|Todos os arquivos (*.*)|*.*"
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TxtUpdatePath.Text = dlg.FileName;
        }

        private async void BtnInstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            var path = TxtUpdatePath.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                ShowInfo("Atencao", "Informe o caminho de um .msu ou .cab (ou clique em Procurar...).");
                return;
            }

            var confirm = MessageBox.Show(
                $"Instalar o update:\n{path}\n\nIsso roda wusa.exe /quiet (msu) ou DISM /Add-Package (cab). " +
                "O PC nao reinicia sozinho.",
                "Confirmar instalacao", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            await RunWithLoadingAsync("Instalando update...", async () =>
            {
                var result = await Task.Run(() => UpdateControlManager.InstallUpdatePackage(path));
                var msg = UpdateControlManager.DescribeExitCode(result.ExitCode);
                if (result.ExitCode == 0 || result.ExitCode == 3010)
                    ShowInfo("Sucesso", msg);
                else
                {
                    var detail = !string.IsNullOrWhiteSpace(result.Output)
                        ? $"\n\nDetalhes:\n{result.Output.Trim()}"
                        : "";
                    ShowError("Falha na instalacao", msg + detail);
                }
            });
            await RefreshInstalledUpdatesAsync();
        }

        /// <summary>
        /// Executa uma operação assíncrona mostrando o overlay de loading e
        /// desabilitando a UI enquanto a operação roda. Nunca bloqueia a UI thread.
        /// </summary>
        private async Task RunWithLoadingAsync(string loadingMessage, Func<Task> operation)
        {
            if (_isBusy)
            {
                ShowInfo("Aguarde", "Ja existe uma operacao em andamento.");
                return;
            }

            _isBusy = true;
            SetBusyState(true);
            var mw = Application.Current.MainWindow as MainWindow;
            mw?.ShowLoading(loadingMessage);
            try
            {
                await operation();
            }
            catch (UnauthorizedAccessException)
            {
                ShowInfo("Acesso Negado", "Execute como administrador.");
            }
            catch (Exception ex)
            {
                ShowError("Erro", ex.Message);
            }
            finally
            {
                mw?.HideLoading();
                SetBusyState(false);
                _isBusy = false;
            }
        }

        private void SetBusyState(bool busy)
        {
            foreach (var child in FindVisualChildren<System.Windows.Controls.Control>(this))
            {
                if (child is System.Windows.Controls.Button btn)
                    btn.IsEnabled = !busy;
            }
        }

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    yield return typed;
                foreach (var grandchild in FindVisualChildren<T>(child))
                    yield return grandchild;
            }
        }

        private void ShowInfo(string title, string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowInfo(title, msg);
            else
                MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ShowError(string title, string msg)
        {
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ShowError(title, msg);
            else
                MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
