using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.GUI.Services;
using KitLugia.Core;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Colors = System.Windows.Media.Colors;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace KitLugia.GUI.Pages
{
    public partial class GameBoostPage : Page
    {
        private CancellationTokenSource _cts = new();
        private List<CustomMotorProfile> _customProfiles = new();
        private bool _isInitializing = true;
        private bool _isRestoring = false;
        private string? _editingProfileId = null;
        private int _engineIndexBeforeCustom = -1; // Índice do motor antes de abrir o overlay custom
        private bool _isLoadingSettings = false;
        private CustomMotorProfile? _profileToDelete = null;


        public static bool AutoOpenCustomOverlayOnLoad = false;
        // CORREÇÃO: Usar LocalApplicationData em vez de ApplicationData (Roaming)
        // LocalApplicationData não depende de Roaming e é mais seguro
        private readonly string _profilesFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "custom_profiles.json");
        private readonly string _engineConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "last_engine.json");
        private readonly string _gameBoostSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "gameboost_settings.json");

        public GameBoostPage()
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();

            // IMPORTANTE: Inicialização começa como true (vai ser setado false depois)
            _isInitializing = true;

            // NOVO: Carrega perfis customizados salvos primeiro (necessário para RestoreEngineSelection)
            LoadCustomProfiles();

            // NOVO: Carrega configurações do GameBoost (toggle e checkboxes)
            LoadGameBoostSettings();

            // CORREÇÃO: Marca inicialização como completa
            _isInitializing = false;

            // CORREÇÃO: Inicializa timer e carrega lista inicial de processos
            InitializeTimer();

            // LIMPEZA: Para timer ao sair da página
            this.Unloaded += GameBoostPage_Unloaded;
        }

        // NOVO: Evento Loaded - restaura seleção do motor após renderização completa
        private void GameBoostPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // CORREÇÃO: Define SelectedIndex padrão antes de restaurar (evita SelectionChanged)
                if (CmbEngine != null && CmbEngine.Items.Count > 0)
                {
                    _isRestoring = true;
                    CmbEngine.SelectedIndex = 0;
                    _isRestoring = false;
                }

                // NOVO: Carrega o último motor escolhido e restaura seleção
                var lastEngineConfig = LoadLastEngine();
                RestoreEngineSelection(lastEngineConfig);


                if (AutoOpenCustomOverlayOnLoad)
                {
                    AutoOpenCustomOverlayOnLoad = false;
                    OpenCustomMotorOverlay();
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao restaurar seleção no Loaded: {ex.Message}");
            }
        }
        
        // CORREÇÃO: Cleanup público para ser chamado via reflection pelo MainWindow
        public void Cleanup()
        {
            // Cancela todas as tasks em background
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            // Limpa todas as listas e bindings
            _customProfiles?.Clear();
            _customProfiles = null!;

            // CORREÇÃO: Desinscreve do evento Unloaded para evitar memory leak do WPF
            this.Unloaded -= GameBoostPage_Unloaded;

            // LIMPEZA: Limpa DataContext para liberar bindings
            this.DataContext = null;

            // LIMPEZA: Força GC para liberar memória imediatamente

            // LIMPEZA: Força Windows a liberar Working Set (reduz RAM no Task Manager)
            MemoryHelper.TrimWorkingSet();
        }

        // NOVO: Helper para obter TrayIconService
        private static TrayIconService? GetTrayService()
        {
            if (Application.Current.MainWindow is MainWindow mw)
                return mw.TrayService;
            return null;
        }

        private void GameBoostPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private void InitializeTimer()
        {
            // Timer de monitoramento removido - usando CurrentForegroundPid do TrayIconService
        }

        // NOVO: Obtém título da janela usando Win32 API (sem acessar processo)
        private string GetWindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";

            try
            {
                int length = Win32Api.GetWindowTextLength(hwnd);
                if (length == 0) return "";

                StringBuilder sb = new StringBuilder(length + 1);
                Win32Api.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private double GetCpuUsage(Process proc)
        {
            try
            {
                using var counter = new System.Diagnostics.PerformanceCounter("Process", "% Processor Time", proc.ProcessName, true);
                counter.NextValue();
                System.Threading.Thread.Sleep(100);
                return counter.NextValue() / Environment.ProcessorCount;
            }
            catch { }
            return 0;
        }

        private void TglGameBoost_Checked(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;
            
            if (TxtStatus != null) TxtStatus.Text = "Ativo e monitorando processos";
            if (StatusIndicator != null) StatusIndicator.Fill = new SolidColorBrush(Colors.Lime);
            KitLugia.Core.Logger.Log("🚀 GameBoost Pro ativado via interface");
            
            // CORREÇÃO: Ativa E inicializa o GameBoost no serviço
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.GamePriorityEnabled = true;
                try
                {
                    mw.TrayService.InitializeGameBoost();
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao inicializar: {ex.Message}");
                }
                mw.TrayService.SaveSettings();
            }
            
            SaveGameBoostSettings();
        }

        private void TglGameBoost_Unchecked(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;
            
            if (TxtStatus != null) TxtStatus.Text = "Desativado";
            if (StatusIndicator != null) StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
            KitLugia.Core.Logger.Log("🚀 GameBoost Pro desativado via interface");
            
            // CORREÇÃO: Desativa E encerra o GameBoost no serviço
            var mw = Application.Current.MainWindow as MainWindow;
            if (mw?.TrayService != null)
            {
                mw.TrayService.GamePriorityEnabled = false;
                try
                {
                    mw.TrayService.ShutdownGameBoost();
                }
                catch (Exception ex)
                {
                    KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao encerrar: {ex.Message}");
                }
                mw.TrayService.SaveSettings();
            }
            
            SaveGameBoostSettings();
        }


        private void LoadGameBoostSettings()
        {
            _isLoadingSettings = true;
            try
            {

                var mw = Application.Current.MainWindow as MainWindow;
                
                // IMPORTANTE: NfO usar padrões! Só ler do serviço.
                bool gameBoostEnabled = false; // Começa com false
                bool trayEnabled = false;
                bool autoStartEnabled = false;
                bool unparkCpuEnabled = false;
                bool closeToTray = false;
                bool proBalance = false;
                
                if (mw?.TrayService != null)
                {
                    // Lê valores atuais do serviço (já carregados do Registry)
                    gameBoostEnabled = mw.TrayService.GamePriorityEnabled;
                    trayEnabled = mw.TrayService.IsTrayEnabled;
                    autoStartEnabled = Services.TrayIconService.IsAutoStartEnabled();
                    closeToTray = mw.TrayService.CloseToTray;
                    proBalance = mw.TrayService.ProBalance;
                }
                
                // Tenta carregar do JSON (se existir)
                if (File.Exists(_gameBoostSettingsPath))
                {
                    try
                    {
                        string json = File.ReadAllText(_gameBoostSettingsPath);
                        var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                        if (settings != null)
                        {

                            if (settings.ContainsKey("gameBoostEnabled"))
                                gameBoostEnabled = GetPropertyValue<bool>(settings["gameBoostEnabled"]);
                            if (settings.ContainsKey("trayEnabled"))
                                trayEnabled = GetPropertyValue<bool>(settings["trayEnabled"]);
                            if (settings.ContainsKey("autoStartEnabled"))
                                autoStartEnabled = GetPropertyValue<bool>(settings["autoStartEnabled"]);
                            if (settings.ContainsKey("unparkCpuEnabled"))
                                unparkCpuEnabled = GetPropertyValue<bool>(settings["unparkCpuEnabled"]);
                            if (settings.ContainsKey("closeToTray"))
                                closeToTray = GetPropertyValue<bool>(settings["closeToTray"]);
                            if (settings.ContainsKey("proBalance"))
                                proBalance = GetPropertyValue<bool>(settings["proBalance"]);
                        }
                    }
                    catch (Exception ex)
                    {
                        KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao ler JSON: {ex.Message}");
                    }
                }


                // Isso garante que as 3 páginas (GameBoost, TraySettings, Settings) estejam sempre sincronizadas
                // Os valores do TrayService têm prioridade sobre o JSON
                if (mw?.TrayService != null)
                {
                    trayEnabled = mw.TrayService.IsTrayEnabled;
                    closeToTray = mw.TrayService.CloseToTray;
                    gameBoostEnabled = mw.TrayService.GamePriorityEnabled;
                    proBalance = mw.TrayService.ProBalance;
                }


                autoStartEnabled = Services.TrayIconService.IsAutoStartEnabled();

                // Aplica valores à UI
                if (TglGameBoost != null) TglGameBoost.IsChecked = gameBoostEnabled;
                if (ChkTrayIcon != null) ChkTrayIcon.IsChecked = trayEnabled;
                if (ChkStartWithWindows != null) ChkStartWithWindows.IsChecked = autoStartEnabled;
                if (ChkUnparkCpu != null) ChkUnparkCpu.IsChecked = unparkCpuEnabled;
                if (ChkCloseToTray != null) ChkCloseToTray.IsChecked = closeToTray;
                if (ChkProBalance != null) ChkProBalance.IsChecked = proBalance;


                string gameBarPath = @"C:\Windows\System32\GameBarPresenceWriter.exe";
                string backupPath = @"C:\Windows\System32\GameBarPresenceWriter.exe.bak";
                bool gameBarDisabled = File.Exists(backupPath) && !File.Exists(gameBarPath);
                

                if (File.Exists(gameBarPath) && File.Exists(backupPath))
                {
                    gameBarDisabled = false;
                    KitLugia.Core.Logger.Log("⚠️ Windows recriou GameBarPresenceWriter.exe - precisa desativar novamente");
                }
                

                if (gameBarDisabled)
                {
                    KitLugia.Core.Logger.Log("✅. GameBarPresenceWriter está desativado (.bak)");
                }
                else
                {
                    KitLugia.Core.Logger.Log("ℹ️ GameBarPresenceWriter está ativo (.exe)");
                }
                
                if (ChkGameBarPresenceWriter != null) ChkGameBarPresenceWriter.IsChecked = gameBarDisabled;
                
                // Atualiza indicador de status
                if (gameBoostEnabled)
                {
                    if (TxtStatus != null) TxtStatus.Text = "Ativo e monitorando processos";
                    if (StatusIndicator != null) StatusIndicator.Fill = new SolidColorBrush(Colors.LimeGreen);
                }
                else
                {
                    if (TxtStatus != null) TxtStatus.Text = "Desativado";
                    if (StatusIndicator != null) StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                }
                
                // Atualiza status do ProBalance
                UpdateProBalanceStatusText(proBalance);
                

                // Só lemos, não escrevemos durante o load.


                // e pode interferir com o motor restaurado
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao carregar configurações: {ex.Message}");
            }
            finally
            {
                _isLoadingSettings = false;
            }
        }


        private T GetPropertyValue<T>(object value)
        {
            if (value is System.Text.Json.JsonElement element)
            {
                if (typeof(T) == typeof(bool))
                    return (T)(object)element.GetBoolean();
                if (typeof(T) == typeof(int))
                    return (T)(object)element.GetInt32();
                if (typeof(T) == typeof(string))
                {
                    string? strValue = element.GetString();
                    return (T)(object)(strValue ?? "");
                }
                if (typeof(T) == typeof(double))
                    return (T)(object)element.GetDouble();
            }
            return (T)Convert.ChangeType(value, typeof(T));
        }


        private void SaveGameBoostSettings()
        {
            try
            {
                var mw = Application.Current.MainWindow as MainWindow;
                if (mw?.TrayService != null)
                {
                    mw.TrayService.GamePriorityEnabled = TglGameBoost?.IsChecked == true;
                    mw.TrayService.SetTrayEnabled(ChkTrayIcon?.IsChecked == true);
                    mw.TrayService.CloseToTray = ChkCloseToTray?.IsChecked == true;
                    mw.TrayService.ProBalance = ChkProBalance?.IsChecked == true;
                    // Persiste a preferência do GameBarPresenceWriter para verificação automática
                    mw.TrayService.GameBarPresenceWriterDisabled = ChkGameBarPresenceWriter?.IsChecked == true;
                    mw.TrayService.SaveSettings();
                }
                
                Services.TrayIconService.SetAutoStart(ChkStartWithWindows?.IsChecked == true);
                
                var settings = new Dictionary<string, object>
                {
                    { "gameBoostEnabled", TglGameBoost?.IsChecked == true },
                    { "trayEnabled", ChkTrayIcon?.IsChecked == true },
                    { "autoStartEnabled", ChkStartWithWindows?.IsChecked == true },
                    { "unparkCpuEnabled", ChkUnparkCpu?.IsChecked == true },
                    { "closeToTray", ChkCloseToTray?.IsChecked == true },
                    { "proBalance", ChkProBalance?.IsChecked == true }
                };
                
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_gameBoostSettingsPath, json);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao salvar configurações: {ex.Message}");
            }
        }

        // NOVO: Handler do toggle TrayIcon (ToggleButton)
        private async void ChkTrayIcon_Click(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;

            try
            {
                ChkTrayIcon.IsEnabled = false;
                bool isChecked = ChkTrayIcon.IsChecked == true; // Captura ANTES do Task.Run

                await Task.Run(() =>
                {
                    var mw = Application.Current.MainWindow as MainWindow;
                    if (mw?.TrayService != null)
                    {
                        mw.TrayService.SetTrayEnabled(isChecked);
                        KitLugia.Core.Logger.Log($"🎮 Tray Icon: {(isChecked ? "ativado" : "desativado")}");
                    }
                });

                ChkTrayIcon.IsEnabled = true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar Tray Icon: {ex.Message}");
                ChkTrayIcon.IsEnabled = true;
            }
        }

        // NOVO: Handler do toggle StartWithWindows (ToggleButton)
        private async void ChkStartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;

            try
            {
                ChkStartWithWindows.IsEnabled = false;
                bool isChecked = ChkStartWithWindows.IsChecked == true; // Captura ANTES do Task.Run

                await Task.Run(() =>
                {
                    Services.TrayIconService.SetAutoStart(isChecked);

                    // Atualizar estado real
                    Dispatcher.Invoke(() =>
                    {
                        bool actualState = Services.TrayIconService.IsAutoStartEnabled();
                        if (actualState != isChecked)
                        {
                            KitLugia.Core.Logger.Log($"🎮 AutoStart: estado real {actualState} difere do solicitado {isChecked}");
                        }
                        ChkStartWithWindows.IsChecked = actualState;
                        KitLugia.Core.Logger.Log($"🎮 AutoStart: {(actualState ? "ativado" : "desativado")}");
                    });
                });

                ChkStartWithWindows.IsEnabled = true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar AutoStart: {ex.Message}");
                ChkStartWithWindows.IsEnabled = true;
            }
        }

        // NOVO: Handler do toggle UnparkCpu (ToggleButton)
        private async void ChkUnparkCpu_Click(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;

            try
            {
                bool isEnabled = ChkUnparkCpu.IsChecked == true;

                // NOVO: Desabilita toggle e mostra loading
                ChkUnparkCpu.IsEnabled = false;

                await Task.Run(() =>
                {
                    if (isEnabled)
                    {
                        // Aplica unpark CPU
                        var result = KitLugia.Core.SystemTweaks.UnparkCpuPowerConfig();
                        if (result.Success)
                        {
                            KitLugia.Core.Logger.Log($"s Unpark CPU: {result.Message}");
                            Dispatcher.Invoke(() => SaveGameBoostSettings());
                        }
                        else
                        {
                            KitLugia.Core.Logger.Log($"⚠️ Unpark CPU falhou: {result.Message}");
                            // Reverte toggle em caso de erro
                            Dispatcher.Invoke(() => ChkUnparkCpu.IsChecked = false);
                        }
                    }
                    else
                    {
                        // Reverte unpark CPU
                        var result = KitLugia.Core.SystemTweaks.RevertUnparkCpuPowerConfig();
                        if (result.Success)
                        {
                            KitLugia.Core.Logger.Log($"s Unpark CPU revertido: {result.Message}");
                            Dispatcher.Invoke(() => SaveGameBoostSettings());
                        }
                        else
                        {
                            KitLugia.Core.Logger.Log($"⚠️ Revert Unpark CPU falhou: {result.Message}");
                        }
                    }
                });

                // NOVO: Reabilita toggle após operação
                ChkUnparkCpu.IsEnabled = true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar Unpark CPU: {ex.Message}");
                ChkUnparkCpu.IsEnabled = true;
            }
        }

        // NOVO: Handler do toggle CloseToTray (ToggleButton)
        private async void ChkCloseToTray_Click(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;

            try
            {
                ChkCloseToTray.IsEnabled = false;
                bool isChecked = ChkCloseToTray.IsChecked == true; // Captura ANTES do Task.Run

                await Task.Run(() =>
                {
                    var mw = Application.Current.MainWindow as MainWindow;
                    if (mw?.TrayService != null)
                    {
                        mw.TrayService.CloseToTray = isChecked;
                        mw.TrayService.SaveSettings();
                        KitLugia.Core.Logger.Log($"🎮 Close to Tray: {(isChecked ? "ativado" : "desativado")}");
                    }
                });

                ChkCloseToTray.IsEnabled = true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar Close to Tray: {ex.Message}");
                ChkCloseToTray.IsEnabled = true;
            }
        }
        
        private async void ChkProBalance_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoadingSettings) return;

            try
            {
                ChkProBalance.IsEnabled = false;
                bool newState = ChkProBalance.IsChecked == true;
                var trayService = (Application.Current.MainWindow as MainWindow)?.TrayService;

                await Task.Run(() =>
                {
                    if (trayService != null)
                    {
                        trayService.ProBalance = newState;
                        trayService.SaveSettings();

                        Dispatcher.Invoke(() => UpdateProBalanceStatusText(newState));

                        if (!newState)
                        {
                            trayService.RestoreAllThrottledProcesses();
                            KitLugia.Core.Logger.Log("s-️ ProBalance desativado - Todos os processos foram restaurados ao normal");
                        }
                        else
                        {
                            KitLugia.Core.Logger.Log("s-️ ProBalance ativado - Gerenciamento de processos retomado");
                        }
                    }
                });

                ChkProBalance.IsEnabled = true;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar ProBalance: {ex.Message}");
                ChkProBalance.IsEnabled = true;
            }
        }

        private async void ChkGameBarPresenceWriter_Click(object sender, RoutedEventArgs e)
        {
            // PROTEÇÃO: Ignora eventos durante carregamento inicial
            if (_isLoadingSettings) return;

            try
            {
                // CORREÇÃO: Captura o valor do checkbox ANTES de desabilitar (evita threading error)
                bool isEnabled = ChkGameBarPresenceWriter.IsChecked == true;
                ChkGameBarPresenceWriter.IsEnabled = false;

                await Task.Run(() =>
                {
                    string gameBarPath = @"C:\Windows\System32\GameBarPresenceWriter.exe";
                    string backupPath = @"C:\Windows\System32\GameBarPresenceWriter.exe.bak";

                    if (isEnabled)
                    {

                        try
                        {
                            if (File.Exists(gameBarPath))
                            {

                                try
                                {
                                    SystemUtils.RunExternalProcess("taskkill", "/F /IM GameBarPresenceWriter.exe", true);
                                    KitLugia.Core.Logger.Log("Processo GameBarPresenceWriter.exe terminado");
                                }
                                catch { /* Ignora se processo não estiver rodando */ }


                                System.Threading.Thread.Sleep(500);


                                if (File.Exists(backupPath))
                                {

                                    try
                                    {
                                        SystemUtils.RunExternalProcess("takeown", $"/f \"{backupPath}\"", true);
                                        KitLugia.Core.Logger.Log("🔑 Ownership tomado do .bak antigo");
                                        

                                        SystemUtils.RunExternalProcess("icacls", $"\"{backupPath}\" /grant *S-1-3-4:F /t /c /l", true);
                                        KitLugia.Core.Logger.Log("🔓 Permissões concedidas para Everyone no .bak");
                                        

                                        System.Threading.Thread.Sleep(500);
                                        

                                        File.Delete(backupPath);
                                        KitLugia.Core.Logger.Log("✅ Arquivo .bak antigo excluído com sucesso");
                                    }
                                    catch (Exception ex)
                                    {
                                        KitLugia.Core.Logger.Log($"⚠️ Erro ao excluir .bak antigo: {ex.Message}");
                                    }
                                }


                                SystemUtils.RunExternalProcess("takeown", $"/f \"{gameBarPath}\"", true);
                                KitLugia.Core.Logger.Log("🔑 Ownership tomado com sucesso do .exe");


                                SystemUtils.RunExternalProcess("icacls", $"\"{gameBarPath}\" /grant *S-1-3-4:F /t /c /l", true);
                                KitLugia.Core.Logger.Log("🔓 Permissões concedidas para Everyone no .exe");


                                System.Threading.Thread.Sleep(500);


                                File.Move(gameBarPath, backupPath);
                                KitLugia.Core.Logger.Log("✅ GameBarPresenceWriter.exe renomeado para .bak - Processo desativado");
                            }
                            else if (File.Exists(backupPath))
                            {
                                KitLugia.Core.Logger.Log("⚠️ GameBarPresenceWriter.exe já está desativado (arquivo .bak existe)");
                            }
                        }
                        catch (Exception ex)
                        {
                            KitLugia.Core.Logger.Log($"✅ Erro ao renomear GameBarPresenceWriter.exe: {ex.Message}");
                        }
                    }
                    else
                    {

                        try
                        {
                            if (File.Exists(backupPath))
                            {

                                if (File.Exists(gameBarPath))
                                {
                                    try
                                    {
                                        File.Delete(gameBarPath);
                                        KitLugia.Core.Logger.Log("Y-'️ Arquivo .exe recriado excluído (restaurando do .bak)");
                                    }
                                    catch (Exception ex)
                                    {
                                        KitLugia.Core.Logger.Log($"⚠️ Erro ao excluir .exe recriado: {ex.Message}");
                                    }
                                }


                                SystemUtils.RunExternalProcess("takeown", $"/f \"{backupPath}\"", true);
                                KitLugia.Core.Logger.Log("🔑 Ownership tomado do arquivo .bak");


                                SystemUtils.RunExternalProcess("icacls", $"\"{backupPath}\" /grant *S-1-3-4:F /t /c /l", true);
                                KitLugia.Core.Logger.Log("🔓 Permissões concedidas para Everyone");


                                System.Threading.Thread.Sleep(500);


                                File.Move(backupPath, gameBarPath);
                                KitLugia.Core.Logger.Log("🔄 GameBarPresenceWriter.exe restaurado - Processo reativado");
                            }
                            else if (File.Exists(gameBarPath))
                            {
                                KitLugia.Core.Logger.Log("⚠️ GameBarPresenceWriter.exe já está ativo (arquivo .exe existe)");
                            }
                        }
                        catch (Exception ex)
                        {
                            KitLugia.Core.Logger.Log($"✅ Erro ao restaurar GameBarPresenceWriter.exe: {ex.Message}");
                        }
                    }
                });


                Dispatcher.Invoke(() => ChkGameBarPresenceWriter.IsEnabled = true);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao configurar GameBarPresenceWriter: {ex.Message}");
                Dispatcher.Invoke(() => ChkGameBarPresenceWriter.IsEnabled = true);
            }
        }
        

        private void UpdateProBalanceStatusText(bool isEnabled)
        {
            if (TxtProBalanceStatus == null) return;
            
            if (isEnabled)
            {
                TxtProBalanceStatus.Text = "ATIVADO";
                TxtProBalanceStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Verde #4CAF50
            }
            else
            {
                TxtProBalanceStatus.Text = "DESATIVADO";
                TxtProBalanceStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)); // Vermelho #F44336
            }
        }


        private void CmbEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {

                if (_isInitializing)
                {
                    return;
                }


                if (_isRestoring)
                {
                    return;
                }

                // Ignora eventos quando ComboBox não está pronto
                if (CmbEngine == null || CmbEngine.SelectedItem == null)
                {
                    return;
                }

                if (CmbEngine.SelectedItem is ComboBoxItem selected)
                {
                    // Pega a tag do item selecionado
                    if (selected.Tag == null)
                    {
                        return;
                    }

                    var tagValue = selected.Tag.ToString();
                    

                    if (tagValue == "custom")
                    {
                        OpenCustomMotorOverlay();
                        return;
                    }
                    

                    var customProfile = _customProfiles.FirstOrDefault(p => p.Id == tagValue);
                    if (customProfile != null)
                    {
                        ApplyCustomMotorProfile(customProfile);


                        SaveLastEngine(0, customProfile.Id);
                        return;
                    }

                    if (!int.TryParse(tagValue, out int engineNumber))
                    {
                        return;
                    }

                    // s️ AVISO: Mostra alerta para V2, V3 e V4 sobre possíveis travamentos
                    if (engineNumber != 1)
                    {
                        string engineName = engineNumber switch
                        {
                            2 => "V2 - FPS Estável",
                            3 => "V3 - Extremo",
                            4 => "V4 - Extreme Pro",
                            _ => "Desconhecido"
                        };

                        var result = System.Windows.MessageBox.Show(
                            $"⚠️ ATENÇÃO - Motor {engineName}\n\n" +
                            "Este motor pode causar travamentos inesperados no sistema.\n\n" +
                            "Recomendações:\n" +
                            "⚠️ Feche aplicativos desnecessários antes de usar\n" +
                            "⚠️ V2: Pode causar micro-travamentos em alguns jogos\n" +
                            "⚠️ V3: Pode causar travamentos mais frequentes\n" +
                            "⚠️ V4: Prioridade RealTime + Critical I/O - APENAS para jogos pesados!\n\n" +
                            "Use por sua conta e risco!\n\n" +
                            "Deseja continuar?",
                            "⚠️ Aviso de Performance",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result == MessageBoxResult.No)
                        {
                            // Volta para V1
                            CmbEngine.SelectedIndex = 0;
                            return;
                        }
                    }

                    // Chama o serviço para trocar o motor
                    Services.TrayIconService.SetEngine(engineNumber);

                    // Atualiza descrição na UI
                    if (TxtEngineDescription != null)
                    {
                        TxtEngineDescription.Text = engineNumber switch
                        {
                            1 => "YY V1 - Original Plus: Win32PrioritySeparation + CPU/IO/Page boost (PADRfO) - Seguro e estável",
                            2 => "YY V2 - FPS Estável Plus: P-Cores + GameMode + Win32PrioritySeparation + EcoQoS OFF + ProBalance (>8%)",
3 => "V3 - Extremo Plus: P-Cores + GameMode + Timer 1ms (audio!) + Scheduler Boost + ProBalance agressivo (>3%)",
                    4 => "V4 - Extreme Pro: RealTime + Critical I/O + P-Cores + Network + Win32 + ProBalance OFF",
                    _ => "Desconhecido"
                };
            }

                    // Mostra mensagem de confirmação
                    string engineNameConfirm = engineNumber switch
                    {
                        1 => "V1 - ORIGINAL",
                        2 => "V2 - FPS Estável",
                        3 => "V3 - Extremo",
                        4 => "V4 - Extreme Pro",
                        _ => "Desconhecido"
                    };


                    SaveLastEngine(engineNumber, null);


                    ReapplyBoostToCurrentForeground();
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ GameBoost: ERRO em SelectionChanged: {ex.Message}");
                System.Windows.MessageBox.Show($"Erro ao trocar motor: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        

        private async void ReapplyBoostToCurrentForeground()
        {
            try
            {
                // Obtém o processo em foreground atual
                var foregroundWindow = Services.Win32Api.GetForegroundWindow();
                Services.Win32Api.GetWindowThreadProcessId(foregroundWindow, out uint foregroundPid);

                if (foregroundPid > 0)
                {
                    string taskId = Services.BackgroundTaskTracker.Instance.RegisterTask("Reaplicando Boost", "GameBoost");

                    // Força re-aplicação do boost com o novo motor (em background)
                    await Task.Run(() =>
                    {
                        if (_cts?.IsCancellationRequested == true) return;
                        Services.TrayIconService.ForceReapplyBoost(foregroundPid);
                    });

                    Services.BackgroundTaskTracker.Instance.CompleteTask(taskId, true, "Boost reaplicado com sucesso");


                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ Erro ao re-aplicar boost: {ex.Message}");
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Mostrar lista de exceções e configurações
            var exceptions = Services.TrayIconService.GetUserExceptions();
            string exceptionList = exceptions.Count > 0 
                ? string.Join(", ", exceptions) 
                : "Nenhum (padrão: Discord, Opera GX, Spotify, etc.)";
            
            string currentEngine = Services.TrayIconService.GetEngineDescription(Services.TrayIconService.CurrentEngine);
            
            MessageBox.Show(
                $"Configurações do GameBoost Pro:\n\n" +
                $"Motor Atual: {currentEngine}\n\n" +
                "YY V1 - ORIGINAL PLUS:\n" +
                "  • CPU: High Priority\n" +
                "  • I/O: High (3)\n" +
                "  • Page: Maximum (5)\n" +
                "  • Timer: Não boosta\n" +
                "  • EcoQoS: Não aplica\n" +
                "  • ProBalance: Não aplica\n" +
                "  • Win32PrioritySeparation: ATIVADO\n\n" +
                "YY V2 - FPS ESTÁVEL PLUS:\n" +
                "  • CPU: High Priority\n" +
                "  • I/O: High (3)\n" +
                "  • Page: Maximum (5)\n" +
                "  • EcoQoS: DESATIVADO\n" +
                "  • ProBalance: >8% CPU\n" +
                "  • ThreadEfficiencyMode: P-Cores\n" +
                "  • GameClassInfo: ATIVADO\n" +
                "  • Win32PrioritySeparation: ATIVADO\n\n" +
                "V3 - EXTREMO PLUS (Tudo no máximo):\n" +
                "  • CPU: High Priority\n" +
                "  • I/O: High (3)\n" +
                "  • Page: Maximum (5)\n" +
                "  • Timer: 1ms (precisão balanceada)\n" +
                "  ⚠️ Pode causar estouros em áudio virtual (Voicemeeter, VB-Cable)\n" +
                "  • EcoQoS: DESATIVADO\n" +
                "  • Network: Throttling OFF\n" +
                "  • ProBalance: >3% CPU (agressivo)\n" +
                "  • ThreadEfficiencyMode: P-Cores\n" +
                "  • GameClassInfo: ATIVADO\n" +
                "  • Win32PrioritySeparation: ATIVADO\n\n" +
                "Exceções do Usuário:\n" + exceptionList + "\n\n" +
                "Windows 11 25H2 Optimized",
                "GameBoost Pro Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #region Y" CUSTOM MOTOR PROFILES


        private void OpenCustomMotorOverlay()
        {
            _editingProfileId = null;
            _engineIndexBeforeCustom = CmbEngine.SelectedIndex;
            
            // Inicializa controles com valores padrão
            TxtCustomProfileName.Text = "Meu Motor Personalizado";
            CmbCpuPriority.SelectedIndex = 1; // High
            CmbIoPriority.SelectedIndex = 1; // High
            CmbPagePriority.SelectedIndex = 1; // Maximum
            CmbThreadMemory.SelectedIndex = 0; // Normal
            
            // Inicializa toggles com valores padrão
            TglTimerResolution.IsChecked = false;
            TglEcoQoS.IsChecked = false;
            TglNetworkBoost.IsChecked = false;
            TglProBalance.IsChecked = true;
            TglThreadEfficiencyMode.IsChecked = false;
            TglGameClassInfo.IsChecked = true;
            TglWin32PrioritySeparation.IsChecked = true;
            
            // Atualiza textos de status dos toggles
            UpdateToggleStatus(TglTimerResolution, false);
            UpdateToggleStatus(TglEcoQoS, false);
            UpdateToggleStatus(TglNetworkBoost, false);
            UpdateToggleStatus(TglProBalance, true);
            UpdateToggleStatus(TglThreadEfficiencyMode, false);
            UpdateToggleStatus(TglGameClassInfo, true);
            UpdateToggleStatus(TglWin32PrioritySeparation, true);
            

            if (!Services.Win32Api.IsCpuHybrid())
            {
                TglThreadEfficiencyMode.IsChecked = false;
                TglThreadEfficiencyMode.IsEnabled = false;
                if (TxtThreadEfficiencyStatus != null)
                {
                    TxtThreadEfficiencyStatus.Text = "N/A";
                    TxtThreadEfficiencyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 128, 128, 128));
                }
            }
            else
            {
                TglThreadEfficiencyMode.IsEnabled = true;
            }
            
            SliderProBalance.Value = 5;
            TxtProBalanceValue.Text = "5%";

            // Mostra overlay
            OverlayCustomMotor.Visibility = Visibility.Visible;
        }


        private void OpenEditCustomMotorOverlay(CustomMotorProfile profile)
        {
            _editingProfileId = profile.Id;
            _engineIndexBeforeCustom = CmbEngine.SelectedIndex;
            
            // Preenche controles com valores do perfil existente
            TxtCustomProfileName.Text = profile.Name;
            
            // CPU Priority
            CmbCpuPriority.SelectedIndex = profile.CpuPriority.ToLower() switch
            {
                "normal" => 0,
                "high" => 1,
                "realtime" => 2,
                _ => 1
            };
            
            // I/O Priority
            CmbIoPriority.SelectedIndex = profile.IoPriority;
            
            // Page Priority
            CmbPagePriority.SelectedIndex = profile.PagePriority;
            
            // Thread Memory Priority
            CmbThreadMemory.SelectedIndex = profile.ThreadMemoryPriority;
            
            // Toggles
            TglTimerResolution.IsChecked = profile.TimerResolution;
            TglEcoQoS.IsChecked = profile.EcoQoS;
            TglNetworkBoost.IsChecked = profile.NetworkBoost;
            TglProBalance.IsChecked = profile.ProBalanceEnabled;
            TglThreadEfficiencyMode.IsChecked = profile.ThreadEfficiencyMode;
            TglGameClassInfo.IsChecked = profile.GameClassInfo;
            TglWin32PrioritySeparation.IsChecked = profile.Win32PrioritySeparation;
            
            // Atualiza textos de status dos toggles
            UpdateToggleStatus(TglTimerResolution, profile.TimerResolution);
            UpdateToggleStatus(TglEcoQoS, profile.EcoQoS);
            UpdateToggleStatus(TglNetworkBoost, profile.NetworkBoost);
            UpdateToggleStatus(TglProBalance, profile.ProBalanceEnabled);
            UpdateToggleStatus(TglThreadEfficiencyMode, profile.ThreadEfficiencyMode);
            UpdateToggleStatus(TglGameClassInfo, profile.GameClassInfo);
            UpdateToggleStatus(TglWin32PrioritySeparation, profile.Win32PrioritySeparation);
            

            if (!Services.Win32Api.IsCpuHybrid())
            {
                TglThreadEfficiencyMode.IsChecked = false;
                TglThreadEfficiencyMode.IsEnabled = false;
                if (TxtThreadEfficiencyStatus != null)
                {
                    TxtThreadEfficiencyStatus.Text = "N/A";
                    TxtThreadEfficiencyStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 128, 128, 128));
                }
            }
            else
            {
                TglThreadEfficiencyMode.IsEnabled = true;
            }
            
            // ProBalance Threshold
            SliderProBalance.Value = profile.ProBalanceThreshold;
            TxtProBalanceValue.Text = $"{profile.ProBalanceThreshold}%";

            // Mostra overlay
            OverlayCustomMotor.Visibility = Visibility.Visible;
        }


        private void SliderProBalance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtProBalanceValue != null)
            {
                TxtProBalanceValue.Text = $"{(int)SliderProBalance.Value}%";
            }
        }


        private void ToggleButton_Checked(object sender, RoutedEventArgs e)
        {
            UpdateToggleStatus(sender as ToggleButton, true);
        }

        private void ToggleButton_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateToggleStatus(sender as ToggleButton, false);
        }

        private void UpdateToggleStatus(ToggleButton? toggle, bool isChecked)
        {
            if (toggle is null) return;

            var redBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 107, 107));
            var greenBrush = new SolidColorBrush(Colors.LimeGreen);
            var turquoiseBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 78, 205, 196));

            if (toggle == TglTimerResolution && TxtTimerResolutionStatus != null)
            {
                TxtTimerResolutionStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtTimerResolutionStatus.Foreground = isChecked ? greenBrush : redBrush;
                if (PanelTimerAudioWarning != null)
                    PanelTimerAudioWarning.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (toggle == TglEcoQoS && TxtEcoQoSStatus != null)
            {
                TxtEcoQoSStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtEcoQoSStatus.Foreground = isChecked ? greenBrush : redBrush;
            }
            else if (toggle == TglNetworkBoost && TxtNetworkBoostStatus != null)
            {
                TxtNetworkBoostStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtNetworkBoostStatus.Foreground = isChecked ? greenBrush : redBrush;
            }
            else if (toggle == TglProBalance && TxtProBalanceStatusCustom != null)
            {
                TxtProBalanceStatusCustom.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtProBalanceStatusCustom.Foreground = isChecked ? turquoiseBrush : redBrush;
            }
            else if (toggle == TglThreadEfficiencyMode && TxtThreadEfficiencyStatus != null)
            {
                TxtThreadEfficiencyStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtThreadEfficiencyStatus.Foreground = isChecked ? greenBrush : redBrush;
            }
            else if (toggle == TglGameClassInfo && TxtGameClassInfoStatus != null)
            {
                TxtGameClassInfoStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtGameClassInfoStatus.Foreground = isChecked ? greenBrush : redBrush;
            }
            else if (toggle == TglWin32PrioritySeparation && TxtWin32PrioritySeparationStatus != null)
            {
                TxtWin32PrioritySeparationStatus.Text = isChecked ? "ATIVADO" : "DESATIVADO";
                TxtWin32PrioritySeparationStatus.Foreground = isChecked ? greenBrush : redBrush;
            }
        }


        private void BtnCancelCustomMotor_Click(object sender, RoutedEventArgs e)
        {
            OverlayCustomMotor.Visibility = Visibility.Collapsed;
            _editingProfileId = null;
            _isRestoring = true;
            if (_engineIndexBeforeCustom >= 0)
                CmbEngine.SelectedIndex = _engineIndexBeforeCustom;
            _isRestoring = false;
            Services.TrayIconService.ClearCustomEngine();
            ReapplyBoostToCurrentForeground();

            // Atualiza descrição na UI
            if (TxtEngineDescription != null)
            {
                int restoredEngine = (int)Services.TrayIconService.CurrentEngine;
                TxtEngineDescription.Text = restoredEngine switch
                {
                    1 => "V1 - Original Plus: Win32PrioritySeparation + CPU/IO/Page boost (PADRÃO) - Seguro e estável",
                    2 => "V2 - FPS Estável Plus: P-Cores + GameMode + Win32PrioritySeparation + EcoQoS OFF + ProBalance (>8%)",
                    3 => "V3 - Extremo Plus: P-Cores + GameMode + Timer 1ms (audio!) + Scheduler Boost + ProBalance agressivo (>3%)",
                    4 => "V4 - Extreme Pro: RealTime + Critical I/O + P-Cores + Network + Win32 + ProBalance OFF",
                    _ => "Desconhecido"
                };
            }
        }


        private void BtnSaveCustomMotor_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Valida nome
                var profileName = TxtCustomProfileName.Text?.Trim();
                if (string.IsNullOrWhiteSpace(profileName))
                {
                    MessageBox.Show("Por favor, digite um nome para o perfil.", "Nome Obrigatório", 
                                  MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Coleta valores dos controles
                var profile = new CustomMotorProfile
                {
                    Id = _editingProfileId ?? ("custom_" + Guid.NewGuid().ToString("N")[..8]),
                    Name = profileName,
                    CpuPriority = CmbCpuPriority.SelectedIndex switch
                    {
                        0 => "Normal",
                        1 => "High",
                        2 => "RealTime",
                        _ => "High"
                    },
                    IoPriority = CmbIoPriority.SelectedIndex,
                    PagePriority = CmbPagePriority.SelectedIndex,
                    ThreadMemoryPriority = CmbThreadMemory.SelectedIndex,
                    TimerResolution = TglTimerResolution.IsChecked == true,
                    EcoQoS = TglEcoQoS.IsChecked == true,
                    NetworkBoost = TglNetworkBoost.IsChecked == true,
                    ProBalanceEnabled = TglProBalance.IsChecked == true,
                    ProBalanceThreshold = (int)SliderProBalance.Value,
                    ThreadEfficiencyMode = TglThreadEfficiencyMode.IsChecked == true,
                    GameClassInfo = TglGameClassInfo.IsChecked == true,
                    Win32PrioritySeparation = TglWin32PrioritySeparation.IsChecked == true
                };

                bool isEditing = !string.IsNullOrEmpty(_editingProfileId);

                if (isEditing)
                {

                    var existingProfile = _customProfiles.FirstOrDefault(p => p.Id == _editingProfileId);
                    if (existingProfile != null)
                    {
                        // Atualiza na lista
                        _customProfiles.Remove(existingProfile);
                        _customProfiles.Add(profile);
                        
                        // Atualiza no ComboBox (remove e readiciona)
                        for (int i = 0; i < CmbEngine.Items.Count; i++)
                        {
                            if (CmbEngine.Items[i] is ComboBoxItem item && item.Tag?.ToString() == _editingProfileId)
                            {
                                CmbEngine.Items.RemoveAt(i);
                                break;
                            }
                        }
                        AddCustomProfileToComboBox(profile);
                        
                        MessageBox.Show($"Perfil '{profile.Name}' atualizado com sucesso!", "Perfil Atualizado", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {

                    _customProfiles.Add(profile);
                    AddCustomProfileToComboBox(profile);
                    
                    MessageBox.Show($"Perfil '{profile.Name}' criado com sucesso!", "Perfil Salvo", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }

                // Fecha overlay
                OverlayCustomMotor.Visibility = Visibility.Collapsed;
                
                // Limpa estado de edição
                _editingProfileId = null;


                SaveCustomProfiles();

                // Seleciona o perfil no ComboBox (isso dispara SelectionChanged que aplica o perfil)
                SelectCustomProfile(profile.Id);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ GameBoost: Erro ao salvar perfil: {ex.Message}");
                MessageBox.Show($"Erro ao salvar perfil: {ex.Message}", "Erro", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void AddCustomProfileToComboBox(CustomMotorProfile profile)
        {
            // Cria o item principal
            var item = new ComboBoxItem
            {
                Content = $"⚙️ {profile.Name}",
                Tag = profile.Id,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Container com botões de editar e excluir
            var container = new Grid();
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textBlock = new TextBlock 
            { 
                Text = $"⚙️ {profile.Name}", 
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            };
            Grid.SetColumn(textBlock, 0);

            // Botão Editar (ícone branco/amarelo claro)
            var editBtn = new System.Windows.Controls.Button
            {
                Content = "✏️",
                Width = 24,
                Height = 24,
                FontSize = 12,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Editar perfil",
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 215, 0)) // Amarelo dourado claro
            };
            editBtn.Click += (s, e) => 
            { 
                e.Handled = true;
                CmbEngine.IsDropDownOpen = false;
                OpenEditCustomMotorOverlay(profile); 
            };
            Grid.SetColumn(editBtn, 1);

            // Botão Excluir (ícone branco/vermelho claro)
            var deleteBtn = new System.Windows.Controls.Button
            {
                Content = "🗑️",
                Width = 24,
                Height = 24,
                FontSize = 12,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Excluir perfil",
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 255, 100, 100)) // Vermelho claro
            };
            deleteBtn.Click += (s, e) => 
            { 
                e.Handled = true;
                CmbEngine.IsDropDownOpen = false;
                ShowDeleteConfirmation(profile); 
            };
            Grid.SetColumn(deleteBtn, 2);

            container.Children.Add(textBlock);
            container.Children.Add(editBtn);
            container.Children.Add(deleteBtn);

            item.Content = container;
            item.Tag = profile.Id;

            // Insere antes do item "Personalizado" (último item)
            CmbEngine.Items.Insert(CmbEngine.Items.Count - 1, item);
        }


        private void SelectCustomProfile(string profileId)
        {
            _isRestoring = true;
            for (int i = 0; i < CmbEngine.Items.Count; i++)
            {
                if (CmbEngine.Items[i] is ComboBoxItem item && item.Tag?.ToString() == profileId)
                {
                    CmbEngine.SelectedIndex = i;
                    break;
                }
            }
            _isRestoring = false;
        }


        private void ApplyCustomMotorProfile(CustomMotorProfile profile)
        {
            try
            {
                
                // Converte para configurações do TrayIconService
                var config = new Services.CustomEngineConfig
                {
                    CpuPriority = profile.CpuPriority,
                    IoPriorityLevel = profile.IoPriority,
                    PagePriorityLevel = profile.PagePriority,
                    TimerBoost = profile.TimerResolution,
                    EcoQoSEnabled = profile.EcoQoS,
                    ProBalance = profile.ProBalanceEnabled,
                    ProBalanceCpuThreshold = profile.ProBalanceThreshold,
                    NetworkBoost = profile.NetworkBoost,
                    ThreadMemoryPriority = profile.ThreadMemoryPriority,
                    ThreadEfficiencyMode = profile.ThreadEfficiencyMode,
                    GameClassInfo = profile.GameClassInfo,
                    Win32PrioritySeparation = profile.Win32PrioritySeparation
                };

                // Configura o motor personalizado no serviço
                Services.TrayIconService.SetCustomEngine(config);

                // Atualiza descrição na UI
                if (TxtEngineDescription != null)
                {
                    TxtEngineDescription.Text = $"⚙️ {profile.Name}: Perfil personalizado | CPU: {profile.CpuPriority} | ProBalance: {(profile.ProBalanceEnabled ? $"ON (> {profile.ProBalanceThreshold}%)" : "OFF")}";
                }

                // Re-aplica ao foreground (async para não travar UI)
                ReapplyBoostToCurrentForeground();
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ GameBoost: Erro ao aplicar perfil: {ex.Message}");
                MessageBox.Show($"Erro ao aplicar perfil: {ex.Message}", "Erro", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private void ShowDeleteConfirmation(CustomMotorProfile profile)
        {
            _profileToDelete = profile;
            TxtDeleteProfileName.Text = profile.Name;
            OverlayConfirmDelete.Visibility = Visibility.Visible;
        }


        private void BtnCancelDelete_Click(object sender, RoutedEventArgs e)
        {
            OverlayConfirmDelete.Visibility = Visibility.Collapsed;
            _profileToDelete = null;
        }


        private void BtnConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            ConfirmDeleteProfile();
            OverlayConfirmDelete.Visibility = Visibility.Collapsed;
        }


        private void ConfirmDeleteProfile()
        {
            if (_profileToDelete == null) return;

            try
            {
                // Remove da lista
                _customProfiles.Remove(_profileToDelete);

                // Remove do ComboBox
                for (int i = CmbEngine.Items.Count - 1; i >= 0; i--)
                {
                    if (CmbEngine.Items[i] is ComboBoxItem item && item.Tag?.ToString() == _profileToDelete.Id)
                    {
                        CmbEngine.Items.RemoveAt(i);
                        break;
                    }
                }


                SaveCustomProfiles();

                // Volta para Padrão Windows
                _isRestoring = true;
                CmbEngine.SelectedIndex = 0;
                _isRestoring = false;

                _profileToDelete = null;
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"✅ GameBoost: Erro ao excluir perfil: {ex.Message}");
            }
        }


        private void LoadCustomProfiles()
        {
            try
            {
                if (!File.Exists(_profilesFilePath)) return;

                var json = File.ReadAllText(_profilesFilePath);
                var profiles = JsonSerializer.Deserialize<List<CustomMotorProfile>>(json);
                
                if (profiles != null && profiles.Count > 0)
                {
                    _customProfiles = profiles;
                    
                    // Adiciona cada perfil ao ComboBox
                    foreach (var profile in _customProfiles)
                    {
                        AddCustomProfileToComboBox(profile);
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao carregar perfis: {ex.Message}");
            }
        }


        private void SaveCustomProfiles()
        {
            try
            {
                // Garante que o diretório existe
                var directory = Path.GetDirectoryName(_profilesFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                };
                var json = JsonSerializer.Serialize(_customProfiles, options);
                File.WriteAllText(_profilesFilePath, json);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao salvar perfis: {ex.Message}");
            }
        }


        private class EngineConfig
        {
            public string EngineType { get; set; } = "fixed"; // "fixed" ou "custom"
            public int EngineNumber { get; set; } = 1; // 1, 2, 3 (se fixed)
            public string? CustomProfileId { get; set; } = null; // ID do perfil (se custom)
        }


        private EngineConfig LoadLastEngine()
        {
            try
            {
                if (!File.Exists(_engineConfigPath))
                {
                    return new EngineConfig { EngineType = "fixed", EngineNumber = 1 };
                }

                var json = File.ReadAllText(_engineConfigPath);
                var config = JsonSerializer.Deserialize<EngineConfig>(json);

                if (config != null)
                {

                    if (config.EngineType == "custom" || config.EngineNumber == 0)
                    {
                        if (!string.IsNullOrEmpty(config.CustomProfileId))
                        {
                            return config;
                        }
                    }
                    if (config.EngineType == "fixed" && config.EngineNumber >= 1 && config.EngineNumber <= 4)
                    {
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao carregar motor: {ex.Message}");
            }

            return new EngineConfig { EngineType = "fixed", EngineNumber = 1 };
        }


        private void SaveLastEngine(int engineNumber, string? customProfileId = null)
        {
            try
            {
                // Garante que o diretório existe
                var directory = Path.GetDirectoryName(_engineConfigPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var config = new EngineConfig
                {
                    EngineType = string.IsNullOrEmpty(customProfileId) ? "fixed" : "custom",
                    EngineNumber = engineNumber,
                    CustomProfileId = customProfileId
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(_engineConfigPath, json);
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao salvar motor: {ex.Message}");
            }
        }


        private void RestoreEngineSelection(EngineConfig config)
        {
            try
            {

                _isRestoring = true;


                if (config.EngineType == "custom" || config.EngineNumber == 0)
                {
                    if (!string.IsNullOrEmpty(config.CustomProfileId))
                    {
                        // Procura o perfil no ComboBox
                        for (int i = 0; i < CmbEngine.Items.Count; i++)
                        {
                            if (CmbEngine.Items[i] is ComboBoxItem item)
                            {
                                var tagValue = item.Tag?.ToString();
                                if (tagValue == config.CustomProfileId)
                                {
                                    CmbEngine.SelectedIndex = i;

                                    // Aplica o perfil customizado
                                    var profile = _customProfiles?.FirstOrDefault(p => p.Id == config.CustomProfileId);
                                    if (profile != null)
                                    {
                                        ApplyCustomMotorProfile(profile);
                                    }

                                    _isRestoring = false;
                                    return;
                                }
                            }
                        }
                    }
                    // Se não encontrou o perfil customizado, volta para V1 sem aplicar
                    CmbEngine.SelectedIndex = 0;
                    _isRestoring = false;
                    return;
                }

                // Motor fixo (V1, V2, V3)
                for (int i = 0; i < CmbEngine.Items.Count; i++)
                {
                    if (CmbEngine.Items[i] is ComboBoxItem item)
                    {
                        var tagValue = item.Tag?.ToString();
                        if (tagValue == config.EngineNumber.ToString())
                        {
                            CmbEngine.SelectedIndex = i;

                            // Atualiza a descrição também
                            if (TxtEngineDescription != null)
                            {
                                TxtEngineDescription.Text = config.EngineNumber switch
                                {
                                    1 => "🟢 V1 - Original Plus: Win32PrioritySeparation + CPU/IO/Page boost (PADRÃO) - Seguro e estável",
                                    2 => "🟡 V2 - FPS Estável Plus: P-Cores + GameMode + Win32PrioritySeparation + EcoQoS OFF + ProBalance (>8%)",
                                    3 => "🔴 V3 - Extremo Plus: P-Cores + GameMode + Timer 1ms (audio!) + Scheduler Boost + ProBalance agressivo (>3%)",
                                    _ => "Desconhecido"
                                };
                            }

                            // Aplica o motor
                            Services.TrayIconService.SetEngine(config.EngineNumber);

                            _isRestoring = false;
                            return;
                        }
                    }
                }

                _isRestoring = false;
            }
            catch (Exception ex)
            {
                _isRestoring = false;
                KitLugia.Core.Logger.Log($"⚠️ GameBoost: Erro ao restaurar seleção: {ex.Message}");
            }
        }

        #endregion
    }


    public class CustomMotorProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string CpuPriority { get; set; } = "High";
        public int IoPriority { get; set; } = 1;
        public int PagePriority { get; set; } = 1;
        public bool TimerResolution { get; set; } = false;
        public bool EcoQoS { get; set; } = false;
        public bool ProBalanceEnabled { get; set; } = true;
        public int ProBalanceThreshold { get; set; } = 5;
        public bool NetworkBoost { get; set; } = false;
        public int ThreadMemoryPriority { get; set; } = 0;
        public bool ThreadEfficiencyMode { get; set; } = false; // false=P-cores, true=E-cores
        public bool GameClassInfo { get; set; } = true; // Sinaliza ao Windows como jogo
        public bool Win32PrioritySeparation { get; set; } = true; // Registry scheduler boost
    }

    public class ProcessInfo
    {
        public string Name { get; set; } = "";
        public string Pid { get; set; } = "";
        public string CpuUsage { get; set; } = "";
        public System.Windows.Media.Brush CpuColor { get; set; } = Brushes.White;
        public string Priority { get; set; } = "";
        public System.Windows.Media.Brush PriorityColor { get; set; } = Brushes.Gray;
        public bool IsForeground { get; set; }
    }
}
