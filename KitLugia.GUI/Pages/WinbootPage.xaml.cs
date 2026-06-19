using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class WinbootPage : Page
    {

        // Típico: 1-4 discos em sistemas comuns
        private List<DiskInfo> _disks = new List<DiskInfo>(4);
        private bool _isBusy = false;
        private bool _isUpgrade = false;
        private string? _customXmlPath = null;
        private bool _isUsingCustomXml = false;
        private string? _injectedPath = null;
        private bool _isLinux = false;
        private Action<string>? _logUpdateHandler;

        public WinbootPage()
        {
            InitializeComponent();


            _logUpdateHandler = (msg) => Dispatcher.Invoke(() => AppendLog(msg));
            WinbootManager.OnLogUpdate += _logUpdateHandler;


            this.Unloaded += WinbootPage_Unloaded;

            RefreshDisks();
            LoadAutomationProfiles();
        }


        public void Cleanup()
        {

            if (_logUpdateHandler != null)
            {
                WinbootManager.OnLogUpdate -= _logUpdateHandler;
                _logUpdateHandler = null;
            }
            this.Unloaded -= WinbootPage_Unloaded;


            this.DataContext = null;


            // callbacks em fila que ainda tentam acessar o controle. Apenas limpar o conteúdo.
            TxtLogViewer?.Clear();


            if (ComboAutomationProfiles != null)
            {
                ComboAutomationProfiles.ItemsSource = null;
                ComboAutomationProfiles.Items.Clear();
            }


            MemoryHelper.TrimWorkingSet();
        }

        private void WinbootPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void LoadAutomationProfiles()
        {
            try
            {
                var profiles = WinbootManager.GetAutomationProfiles();
                ComboAutomationProfiles.ItemsSource = profiles;
                
                // Auto-selecionar o recomendado se existir
                int recommendedIndex = profiles.FindIndex(p => p.IsRecommended);
                ComboAutomationProfiles.SelectedIndex = recommendedIndex >= 0 ? recommendedIndex : 0;
            }
            catch { }
        }

        private void ComboAutomationProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboAutomationProfiles.SelectedItem is WinbootManager.AutomationProfile profile)
            {
                // Reset flag quando usuário muda perfil
                _isUsingCustomXml = false;

                if (profile.FileName == null)
                {
                    // Gerador Interno: Mostra tudo
                    TxtProfileWarning.Visibility = Visibility.Collapsed;
                    _customXmlPath = null;
                    PanelGeneratorCheckboxes.Visibility = Visibility.Visible;
                    TxtCustomXmlInfo.Text = "Usando gerador interno";
                    TxtCustomXmlInfo.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextMuted");
                }
                else
                {
                    // Perfil E2B: Esconde gerador interno, mas mantém Conta de Usuário
                    _customXmlPath = profile.FullPath;
                    PanelGeneratorCheckboxes.Visibility = Visibility.Collapsed;
                    TxtCustomXmlInfo.Text = "PERFIL E2B: " + profile.FriendlyName;
                    TxtCustomXmlInfo.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentColor");

                    if (profile.IsDanger)
                    {
                        TxtProfileWarning.Text = "⚠️ ATENÇÃO: Este perfil apaga o disco automaticamente (WIPE). Use com cautela extrema!";
                        TxtProfileWarning.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        TxtProfileWarning.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }

        private void BtnImportXml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "XML Unattend (*.xml)|*.xml" };
            if (dlg.ShowDialog() == true)
            {
                _customXmlPath = dlg.FileName;
                _isUsingCustomXml = false; // IMPORTAR XML não é modo custom
                
                // Esconde gerador interno para IMPORTAR XML (diferente do CUSTOM)
                PanelGeneratorCheckboxes.Visibility = Visibility.Collapsed;
                PanelUserAccount.Visibility = Visibility.Collapsed;
                
                TxtCustomXmlInfo.Text = "IMPORTADO: " + Path.GetFileName(_customXmlPath);
                TxtCustomXmlInfo.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentColor");
                WinbootManager.Log($"XML Customizado importado: {_customXmlPath}");
                WinbootManager.Log("⚠️ Arquivo será usado diretamente sem ajustes do KitLugia");
            }
        }

        private void BtnBrowseCustomAutounattend_Click(object sender, RoutedEventArgs e)
        {
            // Abre o overlay de criação/edição de autounattend.xml
            if (!string.IsNullOrEmpty(_customXmlPath) && File.Exists(_customXmlPath))
            {
                TxtAutounattendXml.Text = File.ReadAllText(_customXmlPath);
            }
            else
            {
                // Gerar XML padrão usando Ookii.AnswerFile
                var tempPath = Path.GetTempPath() + "temp_autounattend.xml";
                WinbootManager.GenerateAutounattendXml(tempPath);
                if (File.Exists(tempPath))
                {
                    TxtAutounattendXml.Text = File.ReadAllText(tempPath);
                    File.Delete(tempPath);
                }
            }
            
            OverlayAutounattend.Visibility = Visibility.Visible;
            WinbootManager.Log("Overlay de criação de autounattend.xml aberto para modo custom");
        }


        private void AppendLog(string msg)
        {
            TxtLogViewer.AppendText(msg + Environment.NewLine);
            TxtLogViewer.ScrollToEnd();
        }

        private void RefreshDisks()
        {
            WinbootManager.Log("Atualizando lista de discos...");
            _disks = WinbootManager.GetDisks(true);
            ComboDisks.ItemsSource = _disks;
            if (_disks.Count > 0)
            {
                ComboDisks.SelectedIndex = 0;
                
                // Auto-selecionar partição C: (ou a maior com letra de unidade)
                var disk = _disks[0];
                var cPart = disk.Partitions.OrderByDescending(p => p.DriveLetter.Equals("C:", StringComparison.OrdinalIgnoreCase))
                                          .ThenByDescending(p => p.Size)
                                          .FirstOrDefault(p => !string.IsNullOrEmpty(p.DriveLetter) && p.Size >= 15000000000);
                
                if (cPart != null)
                {
                    ComboPartitions.SelectedItem = cPart;
                    WinbootManager.Log($"Auto-seleção Sniper: Partição {cPart.DriveLetter} ({cPart.Label}) escolhida como alvo.");
                }
            }
            TxtStatus.Text = "Discos atualizados.";
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshDisks();

        private void ComboDisks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboDisks.SelectedItem is DiskInfo disk)
            {
                WinbootManager.Log($"Disco selecionado: {disk.Index} ({disk.Model})");
                // FILTRO DE SEGURANÇA: Somente partições >= 8GB e que não sejam reservadas/sistema
                var safePartitions = disk.Partitions.Where(p => p.IsSafeToUse).ToList();
                ComboPartitions.ItemsSource = safePartitions;
                
                if (safePartitions.Count > 0)
                {
                    ComboPartitions.SelectedIndex = 0;
                    TxtStatus.Text = $"{safePartitions.Count} partições seguras encontradas no Disco {disk.Index}.";
                    // Verifica espaço automaticamente ao selecionar disco
                    CheckSpaceAndWarn();
                }
                else
                {
                    TxtStatus.Text = "Nenhuma partição adequada encontrada neste disco (Requer > 8GB).";
                }
            }
        }

        private void BtnBrowseIso_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Arquivos ISO (*.iso)|*.iso" };
            if (dlg.ShowDialog() == true)
            {
                TxtIsoPath.Text = dlg.FileName;
            }
        }

        private void BtnGenerateAutounattend_Click(object sender, RoutedEventArgs e)
        {
            // Abrir o overlay de geração do autounattend.xml
            OverlayAutounattend.Visibility = Visibility.Visible;
        }

        private void BtnGenerateAutounattendConfig_Click(object sender, RoutedEventArgs e)
        {
            // Abrir o overlay de geração do autounattend.xml (do overlay de configuração)
            OverlayAutounattend.Visibility = Visibility.Visible;
        }

        private void BtnEditAutounattend_Click(object sender, RoutedEventArgs e)
        {
            // Abrir o editor de autounattend.xml
            if (!string.IsNullOrEmpty(_customXmlPath) && File.Exists(_customXmlPath))
            {
                TxtAutounattendXml.Text = File.ReadAllText(_customXmlPath);
            }
            else
            {
                // Gerar XML padrão usando Ookii.AnswerFile
                var tempPath = Path.GetTempPath() + "temp_autounattend.xml";
                WinbootManager.GenerateAutounattendXml(tempPath);
                if (File.Exists(tempPath))
                {
                    TxtAutounattendXml.Text = File.ReadAllText(tempPath);
                    File.Delete(tempPath);
                }
            }
            OverlayAutounattendEditor.Visibility = Visibility.Visible;
        }

        private void BtnCancelEditor_Click(object sender, RoutedEventArgs e)
        {
            OverlayAutounattendEditor.Visibility = Visibility.Collapsed;
            TxtAutounattendXml.Clear();
        }

        private void BtnSaveEditor_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Arquivos XML (*.xml)|*.xml",
                FileName = "autounattend.xml",
                Title = "Salvar arquivo autounattend.xml"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, TxtAutounattendXml.Text);
                    _customXmlPath = dlg.FileName;
                    TxtCustomXmlInfo.Text = "IMPORTADO: " + Path.GetFileName(_customXmlPath);
                    TxtCustomXmlInfo.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentColor");
                    OverlayAutounattendEditor.Visibility = Visibility.Collapsed;
                    System.Windows.MessageBox.Show($"Arquivo autounattend.xml salvo com sucesso!\n\nLocal: {dlg.FileName}",
                        "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao salvar arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnCancelAutounattend_Click(object sender, RoutedEventArgs e)
        {
            OverlayAutounattend.Visibility = Visibility.Collapsed;
        }

        private void BtnContinueCustom_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Obtém configurações do overlay de autounattend
                bool bypass = ChkAuBypassTpm.IsChecked == true;
                bool local = ChkAuLocalAccount.IsChecked == true;
                bool privacy = ChkAuDisablePrivacy.IsChecked == true;
                bool fullAuto = ChkAuFullAuto.IsChecked == true;
                bool disableDefender = ChkAuDisableDefender.IsChecked == true;
                bool autoLogon = ChkAuAutoLogon.IsChecked == true;
                bool remoteDesktop = ChkAuRemoteDesktop.IsChecked == true;
                bool showAllEditions = ChkAuShowAllEditions.IsChecked == true;
                bool disableBitlocker = ChkAuDisableBitlocker.IsChecked == true;
                bool disableHibernate = ChkAuDisableHibernate.IsChecked == true;
                bool disableCopilot = ChkAuDisableCopilot.IsChecked == true;
                bool removeEdge = ChkAuRemoveEdge.IsChecked == true;
                bool removeCortana = ChkAuRemoveCortana.IsChecked == true;
                bool removeOneDrive = ChkAuRemoveOneDrive.IsChecked == true;
                bool disableSpotlight = ChkAuDisableSpotlight.IsChecked == true;
                bool disableNews = ChkAuDisableNews.IsChecked == true;
                bool disableChat = ChkAuDisableChat.IsChecked == true;
                bool disableAutoUpdate = ChkAuDisableAutoUpdate.IsChecked == true;
                bool disableDeliveryOpt = ChkAuDisableDeliveryOpt.IsChecked == true;
                bool delayUpdates = ChkAuDelayUpdates.IsChecked == true;
                bool longPaths = ChkAuLongPaths.IsChecked == true;
                bool disableLocation = ChkAuDisableLocation.IsChecked == true;
                bool disableActivity = ChkAuDisableActivity.IsChecked == true;
                bool disableAdID = ChkAuDisableAdID.IsChecked == true;
                bool disableErrorReporting = ChkAuDisableErrorReporting.IsChecked == true;
                bool disableInkWorkspace = ChkAuDisableInkWorkspace.IsChecked == true;
                bool disableSmartScreen = ChkAuDisableSmartScreen.IsChecked == true;
                bool disableDefenderSandbox = ChkAuDisableDefenderSandbox.IsChecked == true;
                bool disableUAC = ChkAuDisableUAC.IsChecked == true;
                bool hideEula = ChkAuHideEula.IsChecked == true;
                bool hideOEM = ChkAuHideOEM.IsChecked == true;
                bool hideWireless = ChkAuHideWireless.IsChecked == true;
                bool hideOnlineAccount = ChkAuHideOnlineAccount.IsChecked == true;
                bool protectYourPC = ChkAuProtectYourPC.IsChecked == true;
                string user = TxtAuUserName.Text ?? "Usuario";
                string pass = TxtAuPassword.Password;
                string computerName = TxtAuComputerName.Text ?? "";
                bool removeXbox = ChkAuRemoveXbox.IsChecked == true;
                bool removeMaps = ChkAuRemoveMaps.IsChecked == true;
                bool removeMail = ChkAuRemoveMail.IsChecked == true;
                bool removeWeather = ChkAuRemoveWeather.IsChecked == true;
                bool removeSports = ChkAuRemoveSports.IsChecked == true;
                bool removeMoney = ChkAuRemoveMoney.IsChecked == true;
                bool removePeople = ChkAuRemovePeople.IsChecked == true;
                bool removeSkype = ChkAuRemoveSkype.IsChecked == true;
                bool removeGroove = ChkAuRemoveGroove.IsChecked == true;
                bool removeMovies = ChkAuRemoveMovies.IsChecked == true;
                bool removeFeedback = ChkAuRemoveFeedback.IsChecked == true;
                bool removeGetStarted = ChkAuRemoveGetStarted.IsChecked == true;
                bool remove3DViewer = ChkAuRemove3DViewer.IsChecked == true;
                bool removePaint3D = ChkAuRemovePaint3D.IsChecked == true;

                // Obtém idioma e fuso horário
                string language = "pt-BR";
                string timeZone = "E. South America Standard Time";

                if (CmbAuLanguage.SelectedItem is ComboBoxItem langItem && langItem.Tag != null)
                {
                    language = langItem.Tag.ToString() ?? "pt-BR";
                }

                if (CmbAuTimeZone.SelectedItem is ComboBoxItem tzItem && tzItem.Tag != null)
                {
                    timeZone = tzItem.Tag.ToString() ?? "E. South America Standard Time";
                }

                // Obtém comandos pós-instalação
                var commands = TxtAuCommands.Text?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

                // Salva em arquivo temporário
                string tempPath = Path.Combine(Path.GetTempPath(), $"kitlugia_custom_autounattend_{Guid.NewGuid()}.xml");
                
                // Gera o arquivo usando WinbootManager
                WinbootManager.GenerateAutounattendXml(tempPath, bypass, local, privacy, user, pass, fullAuto, disableDefender, autoLogon, remoteDesktop, language, timeZone, commands,
                    showAllEditions, disableBitlocker, disableHibernate, disableCopilot, removeEdge, removeCortana, removeOneDrive, disableSpotlight, disableNews, disableChat,
                    disableAutoUpdate, disableDeliveryOpt, delayUpdates, longPaths, disableLocation, disableActivity, disableAdID, disableErrorReporting, disableInkWorkspace,
                    disableSmartScreen, disableDefenderSandbox, disableUAC, hideEula, hideOEM, hideWireless, hideOnlineAccount, protectYourPC, computerName,
                    removeXbox, removeMaps, removeMail, removeWeather, removeSports, removeMoney, removePeople, removeSkype, removeGroove, removeMovies, removeFeedback, removeGetStarted, remove3DViewer, removePaint3D);

                // Define como arquivo customizado
                _customXmlPath = tempPath;
                _isUsingCustomXml = true;

                // Fecha overlay de autounattend
                OverlayAutounattend.Visibility = Visibility.Collapsed;

                // Mostra gerador interno para ajustes adicionais
                PanelGeneratorCheckboxes.Visibility = Visibility.Visible;
                PanelUserAccount.Visibility = Visibility.Visible;

                // Atualiza UI
                TxtCustomXmlInfo.Text = "📁 CUSTOM: autounattend.xml (Gerado)";
                TxtCustomXmlInfo.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentColor");
                TxtConfigTitle.Text = "⚙️ CONFIGURAR INSTALAÇÃO (CUSTOM)";
                TxtConfigIsoInfo.Text = "Usando arquivo autounattend.xml customizado como base";

                WinbootManager.Log("Arquivo autounattend.xml customizado gerado a partir do gerador");
                WinbootManager.Log("⚠️ O arquivo será usado como base. Você pode ajustar configurações adicionais abaixo.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Erro ao gerar arquivo customizado: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnConfirmAutounattend_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Arquivos XML (*.xml)|*.xml",
                FileName = "autounattend.xml",
                Title = "Salvar arquivo autounattend.xml"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    // Obtém configurações do overlay de autounattend
                    bool bypass = ChkAuBypassTpm.IsChecked == true;
                    bool local = ChkAuLocalAccount.IsChecked == true;
                    bool privacy = ChkAuDisablePrivacy.IsChecked == true;
                    bool fullAuto = ChkAuFullAuto.IsChecked == true;
                    bool disableDefender = ChkAuDisableDefender.IsChecked == true;
                    bool autoLogon = ChkAuAutoLogon.IsChecked == true;
                    bool remoteDesktop = ChkAuRemoteDesktop.IsChecked == true;
                    bool showAllEditions = ChkAuShowAllEditions.IsChecked == true;
                    bool disableBitlocker = ChkAuDisableBitlocker.IsChecked == true;
                    bool disableHibernate = ChkAuDisableHibernate.IsChecked == true;
                    bool disableCopilot = ChkAuDisableCopilot.IsChecked == true;
                    bool removeEdge = ChkAuRemoveEdge.IsChecked == true;
                    bool removeCortana = ChkAuRemoveCortana.IsChecked == true;
                    bool removeOneDrive = ChkAuRemoveOneDrive.IsChecked == true;
                    bool disableSpotlight = ChkAuDisableSpotlight.IsChecked == true;
                    bool disableNews = ChkAuDisableNews.IsChecked == true;
                    bool disableChat = ChkAuDisableChat.IsChecked == true;
                    bool disableAutoUpdate = ChkAuDisableAutoUpdate.IsChecked == true;
                    bool disableDeliveryOpt = ChkAuDisableDeliveryOpt.IsChecked == true;
                    bool delayUpdates = ChkAuDelayUpdates.IsChecked == true;
                    bool longPaths = ChkAuLongPaths.IsChecked == true;
                    bool disableLocation = ChkAuDisableLocation.IsChecked == true;
                    bool disableActivity = ChkAuDisableActivity.IsChecked == true;
                    bool disableAdID = ChkAuDisableAdID.IsChecked == true;
                    bool disableErrorReporting = ChkAuDisableErrorReporting.IsChecked == true;
                    bool disableInkWorkspace = ChkAuDisableInkWorkspace.IsChecked == true;
                    bool disableSmartScreen = ChkAuDisableSmartScreen.IsChecked == true;
                    bool disableDefenderSandbox = ChkAuDisableDefenderSandbox.IsChecked == true;
                    bool disableUAC = ChkAuDisableUAC.IsChecked == true;
                    bool hideEula = ChkAuHideEula.IsChecked == true;
                    bool hideOEM = ChkAuHideOEM.IsChecked == true;
                    bool hideWireless = ChkAuHideWireless.IsChecked == true;
                    bool hideOnlineAccount = ChkAuHideOnlineAccount.IsChecked == true;
                    bool protectYourPC = ChkAuProtectYourPC.IsChecked == true;
                    string user = TxtAuUserName.Text ?? "Usuario";
                    string pass = TxtAuPassword.Password;
                    string computerName = TxtAuComputerName.Text ?? "";
                    bool removeXbox = ChkAuRemoveXbox.IsChecked == true;
                    bool removeMaps = ChkAuRemoveMaps.IsChecked == true;
                    bool removeMail = ChkAuRemoveMail.IsChecked == true;
                    bool removeWeather = ChkAuRemoveWeather.IsChecked == true;
                    bool removeSports = ChkAuRemoveSports.IsChecked == true;
                    bool removeMoney = ChkAuRemoveMoney.IsChecked == true;
                    bool removePeople = ChkAuRemovePeople.IsChecked == true;
                    bool removeSkype = ChkAuRemoveSkype.IsChecked == true;
                    bool removeGroove = ChkAuRemoveGroove.IsChecked == true;
                    bool removeMovies = ChkAuRemoveMovies.IsChecked == true;
                    bool removeFeedback = ChkAuRemoveFeedback.IsChecked == true;
                    bool removeGetStarted = ChkAuRemoveGetStarted.IsChecked == true;
                    bool remove3DViewer = ChkAuRemove3DViewer.IsChecked == true;
                    bool removePaint3D = ChkAuRemovePaint3D.IsChecked == true;

                    // Obtém idioma e fuso horário
                    string language = "pt-BR";
                    string timeZone = "E. South America Standard Time";

                    if (CmbAuLanguage.SelectedItem is ComboBoxItem langItem && langItem.Tag != null)
                    {
                        language = langItem.Tag.ToString() ?? "pt-BR";
                    }

                    if (CmbAuTimeZone.SelectedItem is ComboBoxItem tzItem && tzItem.Tag != null)
                    {
                        timeZone = tzItem.Tag.ToString() ?? "E. South America Standard Time";
                    }

                    // Obtém comandos pós-instalação
                    var commands = TxtAuCommands.Text?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

                    // Gera o arquivo usando WinbootManager
                    WinbootManager.GenerateAutounattendXml(dlg.FileName, bypass, local, privacy, user, pass, fullAuto, disableDefender, autoLogon, remoteDesktop, language, timeZone, commands,
                        showAllEditions, disableBitlocker, disableHibernate, disableCopilot, removeEdge, removeCortana, removeOneDrive, disableSpotlight, disableNews, disableChat,
                        disableAutoUpdate, disableDeliveryOpt, delayUpdates, longPaths, disableLocation, disableActivity, disableAdID, disableErrorReporting, disableInkWorkspace,
                        disableSmartScreen, disableDefenderSandbox, disableUAC, hideEula, hideOEM, hideWireless, hideOnlineAccount, protectYourPC, computerName,
                        removeXbox, removeMaps, removeMail, removeWeather, removeSports, removeMoney, removePeople, removeSkype, removeGroove, removeMovies, removeFeedback, removeGetStarted, remove3DViewer, removePaint3D);

                    OverlayAutounattend.Visibility = Visibility.Collapsed;

                    System.Windows.MessageBox.Show($"Arquivo autounattend.xml gerado com sucesso!\n\nLocal: {dlg.FileName}",
                        "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Erro ao gerar arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void TxtIsoPath_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtIsoPath.Text)) return;
            
            // Limpa tipo detectado anterior
            TxtDetectedIsoType.Text = "";
        }

        private void BtnBrowseInjected_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Selecione a pasta para injetar" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _injectedPath = dlg.SelectedPath;
                TxtInjectedPath.Text = _injectedPath;
                TxtInjectedPath.Foreground = System.Windows.Media.Brushes.White;
                
                // Recalcular Tamanho Preview
                int sizeGb = WinbootManager.CalculateRequiredSizeGB(_injectedPath);
                UpdateStatus($"Tamanho estimado da partição: {sizeGb} GB (Base + Injeção)");
            }
        }

        private void BtnCancelConfig_Click(object sender, RoutedEventArgs e)
        {
            OverlayConfig.Visibility = Visibility.Collapsed;
            _isUsingCustomXml = false;
            TxtConfigTitle.Text = "⚙️ CONFIGURAR INSTALAÇÃO";
            TxtConfigIsoInfo.Text = "Identificando ISO...";
        }

        private async void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtIsoPath.Text))
            {
                System.Windows.MessageBox.Show("Selecione uma imagem ISO primeiro.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ComboPartitions.SelectedItem == null)
            {
                System.Windows.MessageBox.Show("Selecione uma partição de origem.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _isBusy = true;
                UpdateStatus("Analisando ISO... (Isso pode levar alguns segundos)");
                OverlayBusy.Visibility = Visibility.Visible;

                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Analisando ISO", "Winboot");

                var info = await WinbootManager.IdentifyIsoType(TxtIsoPath.Text);

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, info != null, info != null ? "ISO identificada com sucesso" : "Falha ao identificar ISO");
                
                OverlayBusy.Visibility = Visibility.Collapsed;
                _isBusy = false;

                if (info != null)
                {
                    TxtConfigIsoInfo.Text = "DETECTADO: " + info.Value.Description;
                    bool isWin = info.Value.Description.Contains("KitLugia", StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(info.Value.SafetyWarning))
                    {
                        WinbootManager.Log("AVISO IMPORTANTE: " + info.Value.SafetyWarning);
                        TxtConfigIsoInfo.Foreground = System.Windows.Media.Brushes.OrangeRed;
                        TxtConfigIsoInfo.Text += " (Aviso)";
                    }
                    else
                    {
                        TxtConfigIsoInfo.Foreground = System.Windows.Media.Brushes.LightGreen;
                    }

                    // Ajustar UI do Overlay
                    bool isWindows = info.Value.Description.Contains("Windows", StringComparison.OrdinalIgnoreCase) || 
                                    info.Value.Description.Contains("KitLugia", StringComparison.OrdinalIgnoreCase);
                    _isLinux = !isWindows;

                    if (!isWindows)
                    {
                        PanelWindowsOptions.Visibility = Visibility.Collapsed;
                        ChkBypassTpm.IsChecked = false;
                        ChkLocalAccount.IsChecked = false;
                        ChkDisablePrivacy.IsChecked = false;
                        ChkFullAuto.IsChecked = false;
                        ChkInjectKit.IsChecked = false;
                        ChkAutoCleanup.IsChecked = false;
                        ChkMultiIso.IsChecked = true;
                        TxtCustomXmlInfo.Text = "Automação Windows desabilitada.";
                    }
                    else
                    {
                        PanelWindowsOptions.Visibility = Visibility.Visible;
                        ChkBypassTpm.IsChecked = true;
                        ChkLocalAccount.IsChecked = true;
                        ChkDisablePrivacy.IsChecked = true;
                        ChkFullAuto.IsChecked = true;
                        ChkInjectKit.IsChecked = true;
                        ChkAutoCleanup.IsChecked = true;
                        ChkMultiIso.IsChecked = false;
                        
                        if (_customXmlPath == null)
                            TxtCustomXmlInfo.Text = "Usando gerador interno";
                        else
                            TxtCustomXmlInfo.Text = "Perfil E2B selecionado.";
                    }

                    OverlayConfig.Visibility = Visibility.Visible;
                }
                else
                {
                    System.Windows.MessageBox.Show("Não foi possível identificar o tipo desta ISO. Ela pode não ser bootável ou está corrompida.", "Erro de Identificação", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                OverlayBusy.Visibility = Visibility.Collapsed;
                _isBusy = false;
                System.Windows.MessageBox.Show($"Erro: {ex.Message}");
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtIsoPath.Text))
            {
                ShowError("Selecione uma imagem ISO primeiro.");
                return;
            }

            SetBusy(true, "Lendo Edições da ISO...");
            _isUpgrade = true;

            try
            {
                string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Lendo Edições da ISO", "Winboot");

                var editions = await WinbootManager.GetIsoEditions(TxtIsoPath.Text);

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, editions.Count > 0, editions.Count > 0 ? $"{editions.Count} edições encontradas" : "Nenhuma edição encontrada");

                SetBusy(false);

                if (editions.Count == 0)
                {
                    ShowError("Não foi possível encontrar imagens de instalação nesta ISO.");
                    return;
                }

                // Configurar Overlay para Modo Upgrade
                TxtConfigTitle.Text = "🔄 ATUALIZAÇÃO IN-PLACE";
                TxtConfigIsoInfo.Text = "Selecione a edição para a qual deseja atualizar (Manter Arquivos).";
                PanelWindowsOptions.Visibility = Visibility.Collapsed;
                PanelUserAccount.Visibility = Visibility.Collapsed;
                PanelFileInjection.Visibility = Visibility.Collapsed;
                
                ComboAutomationProfiles.ItemsSource = editions;
                ComboAutomationProfiles.SelectedIndex = 0;
                
                OverlayConfig.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowError(ex.Message);
            }
        }

        private void ChkEmergencyPreBoot_Checked(object sender, RoutedEventArgs e)
        {
            TxtEmergencyWarning.Visibility = Visibility.Visible;
            WinbootManager.Log("[EMERGENCY] Modo Emergency Pre-Boot ativado. O sistema reiniciara para o WinRE modificado via DISM.");
        }

        private async void BtnConfirmStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpgrade)
            {
                if (ComboAutomationProfiles.SelectedItem is WinbootManager.WimEditionInfo info)
                {
                    OverlayConfig.Visibility = Visibility.Collapsed;
                    SetBusy(true, "Lançando Atualização...");

                    string upgradeTaskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Lançando Atualização In-Place", "Winboot");

                    bool success = await WinbootManager.StartInPlaceUpgrade(TxtIsoPath.Text, info.Index, info.EditionId);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(upgradeTaskId, success, success ? "Atualização iniciada com sucesso" : "Falha ao iniciar atualização");

                    SetBusy(false);
                    
                    if (success)
                    {
                        ShowSuccess("LANÇADO", "O instalador do Windows foi iniciado.\nContinue o processo pelas janelas do Setup.");
                    }
                    else
                    {
                        ShowError("Falha ao iniciar o processo de atualização.");
                    }
                }
                _isUpgrade = false; // Reset
                return;
            }

            OverlayConfig.Visibility = Visibility.Collapsed;

            // Log informativo se estiver usando arquivo customizado
            if (_isUsingCustomXml && !string.IsNullOrEmpty(_customXmlPath))
            {
                WinbootManager.Log($"📁 Usando arquivo autounattend.xml customizado: {Path.GetFileName(_customXmlPath)}");
                WinbootManager.Log("⚠️ As configurações adicionais selecionadas serão aplicadas junto com o arquivo customizado.");
            }

            if (_isLinux)
            {
                var result = System.Windows.MessageBox.Show(
                    "AVISO IMPORTANTE: Comportamento de Boot Linux\n\n" +
                    "Em alguns PCs, ao reiniciar, o sistema pode entrar direto no menu do Linux (GRUB) ou ficar em tela preta.\n\n" +
                    "SE ISSO ACONTECER:\n" +
                    "1. No menu do GRUB, selecione 'Windows Boot Manager' ou 'Boot from next volume'.\n" +
                    "2. Se não conseguir sair, force o desligamento e ligue novamente entrando na BIOS/Boot Menu (F12/Del) para selecionar o Windows.\n\n" +
                    "Deseja continuar com a criação?",
                    "Alerta de Dual Boot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
            }

            var selectedPart = ComboPartitions.SelectedItem as PartitionInfo;
            if (selectedPart == null) return;

            // Emergency Pre-Boot: deploy Alpine kernel + initramfs, reboot
            if (ChkEmergencyPreBoot.IsChecked == true)
            {
                int requiredGb = WinbootManager.CalculateRequiredSizeGB(_injectedPath);
                int sizeMb = requiredGb * 1024;

                var result = System.Windows.MessageBox.Show(
                    $"🚨 EMERGENCY PRE-BOOT (WinRE)\n\n" +
                    $"O KitLugia vai:\n" +
                    $"1. Modificar o Windows RE via DISM ({requiredGb}GB de shrink)\n" +
                    $"2. Configurar boot automatico no WinRE\n" +
                    $"3. REINICIAR o sistema\n\n" +
                    $"Apos o boot, o WinRE executara o diskpart, reduzira {selectedPart.DriveLetter}: e criara a particao KITLUGIA.\n" +
                    $"Quando terminar, o Windows reiniciara normalmente.\n" +
                    $"Execute o KitLugia NOVAMENTE para continuar com a extracao dos arquivos.\n\n" +
                    $"Deseja continuar?",
                    "Emergency Pre-Boot — KitLugia",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;

                if (_isBusy) return;
                _isBusy = true;
                OverlayBusy.Visibility = Visibility.Visible;

                string epbTaskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Emergency Pre-Boot (WinRE)", "Winboot");
                try
                {
                    var (ok, msg) = await EmergencyUEFIManager.DeployAsync(
                        (int)selectedPart.DiskIndex,
                        (int)selectedPart.Index,
                        (long)selectedPart.Size,
                        selectedPart.DriveLetter,
                        sizeMb,
                        WinbootManager.WINBOOT_LABEL,
                        UpdateProgress
                    );

                    if (!ok) throw new Exception(msg);

                    Services.BackgroundTaskTracker.Instance.CompleteTask(epbTaskId, true, "WinRE modificado implantado");

                    UpdateStatus("✅ Ambiente implantado. Reiniciando em 5 segundos...");
                    System.Windows.MessageBox.Show(
                        msg + "\n\nO sistema sera reiniciado AGORA.",
                        "Emergency Pre-Boot",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    await EmergencyUEFIManager.TriggerReboot();
                }
                catch (Exception ex)
                {
                    WinbootManager.Log($"EMERGENCY PRE-BOOT ERRO: {ex.Message}");
                    ShowError($"Falha: {ex.Message}");
                    Services.BackgroundTaskTracker.Instance.CompleteTask(epbTaskId, false, ex.Message);
                }
                finally
                {
                    SetBusy(false);
                }
                return;
            }

            string selectedIsoPath = TxtIsoPath.Text;
            bool bypass = ChkBypassTpm.IsChecked ?? false;
            bool local = ChkLocalAccount.IsChecked ?? false;
            bool privacy = ChkDisablePrivacy.IsChecked ?? false;
            bool inject = ChkInjectKit.IsChecked ?? false;
            bool cleanup = ChkAutoCleanup.IsChecked ?? false;
            bool auto = ChkFullAuto.IsChecked ?? false;
            

            bool disableDefender = ChkAuDisableDefender.IsChecked == true;
            bool autoLogon = ChkAuAutoLogon.IsChecked == true;
            bool remoteDesktop = ChkAuRemoteDesktop.IsChecked == true;
            bool showAllEditions = ChkAuShowAllEditions.IsChecked == true;
            bool disableBitlocker = ChkAuDisableBitlocker.IsChecked == true;
            bool disableHibernate = ChkAuDisableHibernate.IsChecked == true;
            bool disableCopilot = ChkAuDisableCopilot.IsChecked == true;
            bool removeEdge = ChkAuRemoveEdge.IsChecked == true;
            bool removeCortana = ChkAuRemoveCortana.IsChecked == true;
            bool removeOneDrive = ChkAuRemoveOneDrive.IsChecked == true;
            bool disableSpotlight = ChkAuDisableSpotlight.IsChecked == true;
            bool disableNews = ChkAuDisableNews.IsChecked == true;
            bool disableChat = ChkAuDisableChat.IsChecked == true;
            bool disableAutoUpdate = ChkAuDisableAutoUpdate.IsChecked == true;
            bool disableDeliveryOpt = ChkAuDisableDeliveryOpt.IsChecked == true;
            bool delayUpdates = ChkAuDelayUpdates.IsChecked == true;
            bool longPaths = ChkAuLongPaths.IsChecked == true;
            bool disableLocation = ChkAuDisableLocation.IsChecked == true;
            bool disableActivity = ChkAuDisableActivity.IsChecked == true;
            bool disableAdID = ChkAuDisableAdID.IsChecked == true;
            bool disableErrorReporting = ChkAuDisableErrorReporting.IsChecked == true;
            bool disableInkWorkspace = ChkAuDisableInkWorkspace.IsChecked == true;
            bool disableSmartScreen = ChkAuDisableSmartScreen.IsChecked == true;
            bool disableDefenderSandbox = ChkAuDisableDefenderSandbox.IsChecked == true;
            bool disableUAC = ChkAuDisableUAC.IsChecked == true;
            bool hideEula = ChkAuHideEula.IsChecked == true;
            bool hideOEM = ChkAuHideOEM.IsChecked == true;
            bool hideWireless = ChkAuHideWireless.IsChecked == true;
            bool hideOnlineAccount = ChkAuHideOnlineAccount.IsChecked == true;
            bool protectYourPC = ChkAuProtectYourPC.IsChecked == true;
            string computerName = TxtAuComputerName.Text ?? "";
            bool removeXbox = ChkAuRemoveXbox.IsChecked == true;
            bool removeMaps = ChkAuRemoveMaps.IsChecked == true;
            bool removeMail = ChkAuRemoveMail.IsChecked == true;
            bool removeWeather = ChkAuRemoveWeather.IsChecked == true;
            bool removeSports = ChkAuRemoveSports.IsChecked == true;
            bool removeMoney = ChkAuRemoveMoney.IsChecked == true;
            bool removePeople = ChkAuRemovePeople.IsChecked == true;
            bool removeSkype = ChkAuRemoveSkype.IsChecked == true;
            bool removeGroove = ChkAuRemoveGroove.IsChecked == true;
            bool removeMovies = ChkAuRemoveMovies.IsChecked == true;
            bool removeFeedback = ChkAuRemoveFeedback.IsChecked == true;
            bool removeGetStarted = ChkAuRemoveGetStarted.IsChecked == true;
            bool remove3DViewer = ChkAuRemove3DViewer.IsChecked == true;
            bool removePaint3D = ChkAuRemovePaint3D.IsChecked == true;
            bool safeMode = ChkSafeMode.IsChecked ?? false;
            bool isMultiIso = ChkMultiIso.IsChecked ?? false;
            string user = TxtUserName.Text;
            string pass = TxtPassword.Password;

            if (_isBusy) return;
            _isBusy = true;
            OverlayBusy.Visibility = Visibility.Visible;

            string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Criando Boot USB", "Winboot");

            try
            {
                // 1. Criar Partição (Tamanho Adaptativo)
                int requiredGb = WinbootManager.CalculateRequiredSizeGB(_injectedPath);
                int sizeMb = requiredGb * 1024;

                UpdateStatus($"Criando partição de {requiredGb}GB (Smart Size)...");
                bool partOk = await WinbootManager.CreateBootPartition(selectedPart.DriveLetter, sizeMb, WinbootManager.WINBOOT_LABEL, isMultiIso, false, selectedIsoPath, UpdateProgress,
                    async (msg) => await Dispatcher.InvokeAsync(() =>
                    {
                        var result = MessageBox.Show(
                            $"{msg}\n\nDeseja executar Compact OS agora?",
                            "⚠️ Compact OS — Liberar Espaço",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        return result == MessageBoxResult.Yes;
                    }));
                if (!partOk) throw new Exception("Falha ao criar partição. O disco pode não ter espaço não alocado suficiente.");

                // 2. Aguardar WMI estabilizar antes de enumerar discos
                await Task.Delay(2000);

                // 2. Localizar a nova partição
                var disks = WinbootManager.GetDisks();
                var newPart = disks.SelectMany(d => d.Partitions)
                                  .FirstOrDefault(p => p.Label.Equals(WinbootManager.WINBOOT_LABEL, StringComparison.OrdinalIgnoreCase) ||
                                                       p.Label.StartsWith("KITLUGIA", StringComparison.OrdinalIgnoreCase));

                if (newPart == null || string.IsNullOrEmpty(newPart.DriveLetter))
                    throw new Exception("A partição foi criada mas não recebeu uma letra de unidade.");

                string winbootDrive = newPart.DriveLetter;

                // 3. Extrair ISO com Detecção em Tempo Real
                UpdateStatus("Extraindo arquivos e detectando loader...");
                var bootInfo = await WinbootManager.ExtractFiles(selectedIsoPath, winbootDrive);

                if (bootInfo == null)
                {
                    throw new Exception("Falha ao extrair arquivos da ISO.");
                }
                

                UpdateStatus("Detectando idioma da ISO...");
                string detectedLanguage = WinbootManager.DetectIsoLanguage(selectedIsoPath, winbootDrive);
                WinbootManager.Log($"Idioma detectado: {detectedLanguage}");
                
                // 3.5 Aplicar Customizações (Rufus-style + Automação)
                if (!isMultiIso)
                {
                    UpdateStatus("Aplicando automações pós-instalação...");
                    bool customOk = await WinbootManager.ApplyCustomizations(
                        winbootDrive,
                        bypass,
                        local,
                        privacy,
                        inject,
                        cleanup,
                        _customXmlPath,
                        user,
                        pass,
                        auto,
                        (uint)selectedPart.DiskIndex,
                        (uint)selectedPart.Index,
                        _injectedPath,
                        safeMode,
                        downloadConfirmationCallback: (message) => Task.FromResult(ChkDownloadDotnet.IsChecked ?? true),
                        detectedLanguage: detectedLanguage,
                        disableDefender: disableDefender,
                        autoLogon: autoLogon,
                        remoteDesktop: remoteDesktop,
                        showAllEditions: showAllEditions,
                        disableBitlocker: disableBitlocker,
                        disableHibernate: disableHibernate,
                        disableCopilot: disableCopilot,
                        removeEdge: removeEdge,
                        removeCortana: removeCortana,
                        removeOneDrive: removeOneDrive,
                        disableSpotlight: disableSpotlight,
                        disableNews: disableNews,
                        disableChat: disableChat,
                        disableAutoUpdate: disableAutoUpdate,
                        disableDeliveryOpt: disableDeliveryOpt,
                        delayUpdates: delayUpdates,
                        longPaths: longPaths,
                        disableLocation: disableLocation,
                        disableActivity: disableActivity,
                        disableAdID: disableAdID,
                        disableErrorReporting: disableErrorReporting,
                        disableInkWorkspace: disableInkWorkspace,
                        disableSmartScreen: disableSmartScreen,
                        disableDefenderSandbox: disableDefenderSandbox,
                        disableUAC: disableUAC,
                        hideEula: hideEula,
                        hideOEM: hideOEM,
                        hideWireless: hideWireless,
                        hideOnlineAccount: hideOnlineAccount,
                        protectYourPC: protectYourPC,
                        computerName: computerName,
                        removeXbox: removeXbox,
                        removeMaps: removeMaps,
                        removeMail: removeMail,
                        removeWeather: removeWeather,
                        removeSports: removeSports,
                        removeMoney: removeMoney,
                        removePeople: removePeople,
                        removeSkype: removeSkype,
                        removeGroove: removeGroove,
                        removeMovies: removeMovies,
                        removeFeedback: removeFeedback,
                        removeGetStarted: removeGetStarted,
                        remove3DViewer: remove3DViewer,
                        removePaint3D: removePaint3D);

                    if (!customOk) WinbootManager.Log("Aviso: Falha ao aplicar algumas automações, mas o processo continuará.");
                }
                else
                {
                    WinbootManager.Log("Modo Multi-ISO detectado: Pulando customizações específicas de Windows (unattend.xml).");
                }

                // 4. Configurar BOOT
                UpdateStatus("Configurando BCD Bootloader...");
                string? bootGuid = null;
                
                if (bootInfo.Value.IsWim)
                {
                    bootGuid = await WinbootManager.CreateRamdiskEntry(
                        bootInfo.Value.Description, 
                        winbootDrive, 
                        bootInfo.Value.WimPath, 
                        bootInfo.Value.SdiPath);
                }
                else
                {
                    // Linux / Generic Multi-ISO - Modo INTEGRADO (Strelec Style)
                    UpdateStatus("Aplicando Turbo Boot (Patch Linux)...");
                    await WinbootManager.PatchLinuxConfig(winbootDrive);

                    if (WinbootManager.IsEfiMode())
                    {
                        UpdateStatus("Acelerando para Linux via NVRAM Direta...");
                        bootGuid = await WinbootManager.CreateDirectNvramBoot(winbootDrive, bootInfo.Value.Description);
                    }
                    else
                    {
                        // Fallback para Legacy
                        bootGuid = "{kitlugia-linux-legacy}";
                    }
                }

                if (bootGuid == null)
                {
                    if (bootInfo.Value.IsWim)
                    {
                        throw new Exception("Falha ao configurar o BCD (bcdedit). Verifique o log.");
                    }
                    else
                    {
                        WinbootManager.Log("Aviso: Não foi possível criar a Ponte de Boot. Fallback para modo Manual (F12).");
                        bootGuid = "{kitlugia-manual-f12}";
                    }
                }

                UpdateStatus("PROCESSO CONCLUÍDO COM SUCESSO!");
                
                string msg = "Winboot configurado com sucesso!\n\n";

                if (!bootInfo.Value.IsWim)
                {
                    if (bootGuid == "{kitlugia-uefi-jump}")
                    {
                        msg += " 🚀 MODO UNIVERSAL ATIVADO (SALTO DIRETO) 🚀 \n\n" +
                               "1. Reinicie o computador agora\n" +
                               "2. O KitLugia configurou um salto direto para o Linux.\n" +
                               "3. O PC entrará no menu do Linux AUTOMATICAMENTE!\n\n" +
                               "DICA: Você não precisa apertar F12 nem usar a Tela Azul.";
                    }
                    else if (bootGuid == "{kitlugia-refind-esp}")
                    {
                        msg += " 🚀 MODO rEFInd (GERENCIADOR DE BOOT UNIVERSAL) 🚀 \n\n" +
                               "1. Reinicie o computador agora\n" +
                               "2. O rEFInd será carregado automaticamente\n" +
                               "3. Use as setas para escolher Windows ou Linux\n" +
                               "4. Pressione Enter para bootar\n\n" +
                               "VANTAGEM: rEFInd detecta automaticamente ambos OSs!";
                    }
                    else if (bootGuid == "{kitlugia-manual-f12}")
                    {
                        msg += " ⚠️ MODO MANUAL (PONTE NÃO DISPONÍVEL) ⚠️ \n\n" +
                               "1. Reinicie o computador\n" +
                               "2. Aperte F12 (ou DEL/F2) durante o boot\n" +
                               "3. Selecione a partição KITLUGIA no menu da BIOS\n\n" +
                               "CAUSA: Não foi possível configurar o Menu de Boot automático (BCD).";
                    }
                    else
                    {
                        msg += " 🚀 MODO DUAL BOOT INTEGRADO (STRELEC ENGINE) \n\n" +
                               "1. Reinicie o computador\n" +
                               "2. Na Tela Azul (Windows Boot Menu), escolha a entrada de Linux\n" +
                               "3. O KitLugia usará o motor do Strelec para carregar o GRUB automaticamente.\n\n" +
                               "VANTAGEM: Você não precisa apertar F12. Funciona direto pela Tela Azul!";
                    }
                }
                else
                {
                    msg += "Agora você pode reiniciar seu PC.";
                }

                System.Windows.MessageBox.Show(msg, "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);

                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Boot USB criado com sucesso");
            }
            catch (Exception ex)
            {
                WinbootManager.Log($"FATAL ERROR: {ex.Message}");
                ShowError($"Falha crítica: {ex.Message}\n\nVerifique o LOG DETALHADO abaixo.");
                Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, false, ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }




        private async void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
             var candidates = WinbootManager.GetRemovablePartitions();

             if (candidates.Count == 0)
             {
                 if (ConfirmAction("Nenhuma partição Winboot detectada no disco.\n\nDeseja realizar uma varredura para limpar entradas de boot antigas (Múltiplos Boots na Tela Azul) criadas pelo KitLugia?"))
                 {
                     await ExecuteBcdCleanupWithUI();
                 }
                 return;
             }

             if (candidates.Count == 1)
             {
                 var target = candidates[0];
                 if (ConfirmAction($"Encontrada partição Winboot em {target.DriveLetter} ({target.Label} - {target.Size / 1024 / 1024 / 1024} GB).\n\nDeseja DELETAR a partição e as entradas de boot da Tela Azul?"))
                 {
                     ExecuteRemoval(target);
                 }
                 return;
             }

             // Múltiplos candidatos -> Mostrar card inline
             WinbootManager.Log($"Encontrados {candidates.Count} candidatos para remoção. Mostrando seletor...");
             ComboRemovalCandidates.ItemsSource = candidates.Select(p => new { Value = p, DisplayName = $"[{p.DriveLetter}] {p.Label} ({p.Size / 1024 / 1024 / 1024} GB) - Disk {p.DiskIndex}" }).ToList();
             ComboRemovalCandidates.SelectedIndex = 0;
             CardRemovalSelection.Visibility = Visibility.Visible;
        }

        private async Task ExecuteBcdCleanupWithUI()
        {
            WinbootManager.Log("Iniciando limpeza de entradas BCD...");

            // Escaneia as entradas BCD
            var entries = await WinbootManager.ScanWinbootForCleanup();

            if (entries.Count == 0)
            {
                System.Windows.MessageBox.Show("Nenhuma entrada BCD do KitLugia encontrada.", "Limpeza BCD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Converte para o formato da UI
            var uiEntries = entries.Select(e => new Windows.BcdEntryInfo
            {
                Guid = e.Guid,
                Description = e.Description,
                Reason = e.Reason,
                Type = e.Type,
                IsCritical = e.IsCritical
            }).ToList();

            // Mostra a janela de seleção
            var cleanerWindow = new Windows.BcdCleanerWindow(uiEntries);
            cleanerWindow.Owner = Window.GetWindow(this);

            if (cleanerWindow.ShowDialog() == true)
            {
                // Usuário confirmou remoção
                if (cleanerWindow.SelectedGuids.Count > 0)
                {
                    WinbootManager.Log($"Removendo {cleanerWindow.SelectedGuids.Count} entradas BCD selecionadas...");
                    await WinbootManager.RemoveWinboot(customGuids: cleanerWindow.SelectedGuids);
                    System.Windows.MessageBox.Show($"Limpeza concluída! {cleanerWindow.SelectedGuids.Count} entradas removidas.", "Sucesso", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    WinbootManager.Log("Nenhuma entrada selecionada para remoção.");
                }
            }
            else
            {
                WinbootManager.Log("Limpeza BCD cancelada pelo usuário.");
            }
        }

        private void BtnCancelRemoval_Click(object sender, RoutedEventArgs e)
        {
            CardRemovalSelection.Visibility = Visibility.Collapsed;
            WinbootManager.Log("Seleção de remoção cancelada pelo usuário.");
        }

        private void BtnConfirmRemoval_Click(object sender, RoutedEventArgs e)
        {
            if (ComboRemovalCandidates.SelectedItem == null) return;
            
            // Reflexão simples para pegar o Value do tipo anônimo (ou criar classe dedicada, mas dynamic funciona aqui)
            dynamic selectedItem = ComboRemovalCandidates.SelectedItem;
            PartitionInfo target = selectedItem.Value;

            CardRemovalSelection.Visibility = Visibility.Collapsed;
            
            if (ConfirmAction($"Deseja DELETAR a partição {target.DriveLetter} ({target.Label} - {target.Size / 1024 / 1024 / 1024} GB) e as entradas de boot da Tela Azul?"))
            {
                ExecuteRemoval(target);
            }
        }

        private async Task ExecuteRemoval(PartitionInfo target)
        {
             SetBusy(true, $"Removendo Winboot ({target.Label})...");
             try
             {
                 bool ok = await WinbootManager.RemoveWinboot(target);
                 if (ok)
                 {
                     ShowSuccess("REMOVIDO", "A partição foi deletada e as entradas de boot foram limpas com sucesso.\nO espaço foi devolvido à unidade vizinha.");
                     RefreshDisks();
                 }
                 else
                 {
                     ShowError("Houve um problema ao remover. Verifique o Log Detalhado (pode ser necessário limpar manualmente no DiskMgmt).");
                 }
             }
             catch(Exception ex)
             {
                 WinbootManager.Log($"Erro no processo de remoção: {ex.Message}");
                 ShowError($"Erro de excecão: {ex.Message}");
             }
             finally
             {
                 SetBusy(false);
             }
        }

        private async void ExecuteBcdCleanup()
        {
             SetBusy(true, "Limpando entradas BCD antigas...");
             try
             {
                 bool ok = await WinbootManager.CleanBcdEntriesAsync();
                 if (ok) ShowSuccess("LIMPEZA", "As entradas de boot antigas foram removidas da Tela Azul com sucesso.");
                 else ShowError("Houve um erro ao limpar o BCD. Verifique o Log Detalhado.");
             }
             catch(Exception ex)
             {
                 ShowError($"Erro de exceção: {ex.Message}");
             }
             finally
             {
                 SetBusy(false);
             }
        }


        private void SetBusy(bool busy, string status = "", string title = "EXECUTANDO OPERAÇÃO")
        {
            _isBusy = busy;
            OverlayBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy)
            {
                TxtOpTitle.Text = title;
                TxtOpStatus.Text = "Status: " + status;
                TxtOpDesc.Text = status;
                ProgOp.IsIndeterminate = true;
                PanelOpFooter.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateProgress(double percent, string message)
        {
            if (!_isBusy) return;

            Dispatcher.Invoke(() =>
            {
                TxtOpStatus.Text = $"Status: {message}";
                TxtOpDesc.Text = message;
                TxtLiveActivity.Text = message;

                if (percent >= 0)
                {
                    ProgOp.IsIndeterminate = false;
                    ProgOp.Value = percent;
                }
                else
                {
                    ProgOp.IsIndeterminate = true;
                }
            });
        }

        private void SetOperationComplete(bool success, string message)
        {
            if (!_isBusy) return;

            Dispatcher.Invoke(() =>
            {
                ProgOp.IsIndeterminate = false;
                ProgOp.Value = 100;
                TxtOpStatus.Text = success ? "Status: Concluído" : "Status: Erro";
                TxtOpDesc.Text = message;
                TxtLiveActivity.Text = message;
                PanelOpFooter.Visibility = Visibility.Visible;
            });
        }

        private void BtnCloseOverlay_Click(object sender, RoutedEventArgs e)
        {
            OverlayBusy.Visibility = Visibility.Collapsed;
            _isBusy = false;
        }

        private void UpdateStatus(string msg)
        {
            TxtStatus.Text = msg;
            if (_isBusy)
            {
                TxtOpStatus.Text = "Status: " + msg;
                TxtOpDesc.Text = msg;
                TxtLiveActivity.Text = msg;
            }
        }

        private void ShowError(string msg) => System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(msg, "Erro Winboot", MessageBoxButton.OK, MessageBoxImage.Error));
        private void ShowSuccess(string title, string msg) => System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information));
        private bool ConfirmAction(string msg) => System.Windows.MessageBox.Show(msg, "Winboot - Confirmação", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        // Propriedade para ser checada pelo MainWindow ao navegar
        public bool CanNavigate()
        {
            if (_isBusy) return false;
            return true;
        }

        /// <summary>
        /// Verifica se há espaço suficiente na partição selecionada e avisa o usuário.
        /// </summary>
        private async void CheckSpaceAndWarn()
        {
            if (ComboPartitions.SelectedItem is not KitLugia.Core.PartitionInfo part) return;
            if (string.IsNullOrEmpty(part.DriveLetter)) return;

            int requiredGB = WinbootManager.CalculateRequiredSizeGB(_injectedPath);

            var (hasEnough, freeGB, reqGB, message) = await Task.Run(() =>
                WinbootManager.CheckSpaceForInstallation(part.DriveLetter, requiredGB));

            if (!hasEnough)
            {
                WinbootManager.Log($"⚠️ ESPAÇO INSUFICIENTE: {message}");
                UpdateStatus($"⚠️ Espaço insuficiente: {freeGB} GB livres, necessário {reqGB} GB");

                System.Windows.MessageBox.Show(
                    $"⚠️ ESPAÇO INSUFICIENTE\n\n{message}\n\n" +
                    $"Libere espaço manualmente antes de criar a partição de instalação.",
                    "Espaço Insuficiente — KitLugia WinBoot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                UpdateStatus($"✅ Espaço OK: {freeGB} GB livres (necessário: {reqGB} GB)");
            }
        }
    }
}
