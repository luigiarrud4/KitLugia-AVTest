using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using KitLugia.GUI.Helpers;
using Task = System.Threading.Tasks.Task;

namespace KitLugia.GUI.Pages
{
    public partial class SettingsPage : Page
    {
        // �� CORRE�?�fO: Usar LocalApplicationData em vez de ApplicationData (Roaming)
        // LocalApplicationData não depende de Roaming e é mais seguro
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia",
            "settings.json");
        
        private const string AutoStartRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "KitLugia";

        private bool _isSavingSettings;

        public SettingsPage()
        {
            InitializeComponent();
            _ = LoadSettingsAsync();

            // �� LIMPEZA: Liberar recursos ao sair da página
            this.Unloaded += SettingsPage_Unloaded;

            // Adicionar eventos para os novos toggles
            AddToggleEvents();
        }

        // �� CORRE�?�fO: Cleanup público para ser chamado via reflection pelo MainWindow
        public void Cleanup()
        {
            SaveSettings();
            ToggleStartup.Click -= ToggleStartup_ClickHandler;
            ToggleCloseToTray.Click -= ToggleCloseToTray_ClickHandler;
            ToggleTray.Click -= ToggleTray_ClickHandler;
            ToggleNotifications.Click -= OnToggleNotifications;
            ToggleVerboseLogging.Click -= OnToggleVerboseLogging;
            ToggleRAMMonitor.Click -= OnToggleRAMMonitor;
            ToggleGameBoost.Click -= OnToggleGameBoost;
            ToggleTurboBoot.Click -= OnToggleTurboBoot;
            ToggleTurboShutdown.Click -= OnToggleTurboShutdown;
            ToggleStandbyClean.Click -= OnToggleStandbyClean;
            ToggleIntroAnimation.Click -= OnToggleIntroAnimation;
            IntroDurationSlider.ValueChanged -= OnIntroDurationSlider_ValueChanged;
            this.Unloaded -= SettingsPage_Unloaded;

            // �� LIMPEZA: Limpa DataContext para liberar bindings
            this.DataContext = null;

            // �� LIMPEZA: Força GC para liberar memória imediatamente

            // �� LIMPEZA: Força Windows a liberar Working Set (reduz RAM no Task Manager)
        }

        private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }
        
        private void AddToggleEvents()
        {
            // Adicionar eventos para salvar automaticamente ao mudar
            ToggleStartup.Click += ToggleStartup_ClickHandler;
            ToggleCloseToTray.Click += ToggleCloseToTray_ClickHandler;
            ToggleTray.Click += ToggleTray_ClickHandler;
            ToggleNotifications.Click += OnToggleNotifications;
            ToggleVerboseLogging.Click += OnToggleVerboseLogging;

            ToggleRAMMonitor.Click += OnToggleRAMMonitor;
            ToggleGameBoost.Click += OnToggleGameBoost;
            ToggleTurboBoot.Click += OnToggleTurboBoot;
            ToggleTurboShutdown.Click += OnToggleTurboShutdown;
            ToggleStandbyClean.Click += OnToggleStandbyClean;

            // Intro Animation
            ToggleIntroAnimation.Click += OnToggleIntroAnimation;
            IntroDurationSlider.ValueChanged += OnIntroDurationSlider_ValueChanged;
        }

        private void ToggleStartup_ClickHandler(object s, RoutedEventArgs e) => ToggleStartup_Click(s, e);
        private void ToggleCloseToTray_ClickHandler(object s, RoutedEventArgs e) => ToggleCloseToTray_Click(s, e);
        private void ToggleTray_ClickHandler(object s, RoutedEventArgs e) => ToggleTray_Click();
        private void OnToggleNotifications(object s, RoutedEventArgs e) => SaveSettings();
        private void OnToggleVerboseLogging(object s, RoutedEventArgs e) => SaveSettings();
        private void OnToggleRAMMonitor(object s, RoutedEventArgs e) => SaveSettings();
        private void OnToggleGameBoost(object s, RoutedEventArgs e) => ToggleGameBoost_Click();
        private void OnToggleTurboBoot(object s, RoutedEventArgs e) => ToggleTurboBoot_Click();
        private void OnToggleTurboShutdown(object s, RoutedEventArgs e) => ToggleTurboShutdown_Click();
        private void OnToggleStandbyClean(object s, RoutedEventArgs e) => ToggleStandbyClean_Click();
        private void OnToggleIntroAnimation(object s, RoutedEventArgs e) => SaveSettings();
        private void OnIntroDurationSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            IntroDurationText.Text = $"{IntroDurationSlider.Value:F1} segundos";
            SaveSettings();
        }

        private async Task LoadSettingsAsync()
        {
            try
            {
                // �� VERIFICAR AUTO-START REAL DO SISTEMA - usa novo método que verifica o caminho
                ToggleStartup.IsChecked = KitLugia.GUI.Services.TrayIconService.IsAutoStartEnabled();

                // �� CARREGAR ESTADO DOS M�"MÓDULOS DO TRAYSERVICE
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                {
                    // Close to Tray
                    if (mainWindow.TrayService != null)
                    {
                        ToggleCloseToTray.IsChecked = mainWindow.TrayService.CloseToTray;
                    }
                    else
                    {
                        // Fallback se não conseguir acessar
                        ToggleCloseToTray.IsChecked = true;
                    }

                    if (mainWindow.TrayService != null)
                    {
                        var tray = mainWindow.TrayService;

                        // Carregar todos os módulos do TrayService
                        ToggleGameBoost.IsChecked = tray.GamePriorityEnabled;
                        ToggleTray.IsChecked = tray.IsTrayEnabled;
                        ToggleTurboBoot.IsChecked = tray.TurboBootEnabled;
                        ToggleTurboShutdown.IsChecked = tray.TurboShutdownEnabled;
                        ToggleStandbyClean.IsChecked = tray.StandbyCleanEnabled;
                    }
                }

                // Carregar configurações do arquivo em background
                AppSettings? settings = null;
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        string json = await Task.Run(() => File.ReadAllText(ConfigPath));
                        settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                    }
                }
                catch { /* arquivo corrompido, usa defaults */ }

                if (settings != null)
                {
                    if (System.Windows.Application.Current.MainWindow is MainWindow mw && mw.TrayService == null)
                        ToggleCloseToTray.IsChecked = settings.CloseToTray;

                    ToggleNotifications.IsChecked = settings.ShowNotifications;
                    ToggleVerboseLogging.IsChecked = settings.VerboseLogging;
                    ToggleRAMMonitor.IsChecked = settings.RAMMonitorEnabled;
                    ToggleDeveloperMode.IsChecked = settings.DeveloperMode;
                    ToggleIntroAnimation.IsChecked = settings.IntroAnimationEnabled;
                    IntroDurationSlider.Value = settings.IntroDuration;
                    IntroDurationText.Text = $"{settings.IntroDuration:F1} segundos";

                    ToggleRestorePoint.IsChecked = settings.CreateRestorePointBeforeUninstall;
                    ToggleAskRestorePoint.IsChecked = !settings.RememberRestorePointChoice;
                }
                else
                {
                    ToggleNotifications.IsChecked = true;
                    ToggleVerboseLogging.IsChecked = false;
                    ToggleRAMMonitor.IsChecked = true;
                    ToggleDeveloperMode.IsChecked = false;
                    ToggleIntroAnimation.IsChecked = true;
                    IntroDurationSlider.Value = 3.0;
                    IntroDurationText.Text = "3.0 segundos";
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("SettingsPage.LoadSettings", $"Erro ao carregar: {ex.Message}");
            }
        }

        private async void SaveSettings()
        {
            if (_isSavingSettings) return;
            _isSavingSettings = true;
            try
            {
                var settings = new AppSettings
                {
                    StartWithWindows = ToggleStartup.IsChecked ?? false,
                    CloseToTray = ToggleCloseToTray.IsChecked ?? true,
                    MinimizeToTray = ToggleTray.IsChecked ?? true,
                    IntroAnimationEnabled = ToggleIntroAnimation.IsChecked ?? true,
                    IntroDuration = IntroDurationSlider.Value,
                    ShowNotifications = ToggleNotifications.IsChecked ?? true,
                    VerboseLogging = ToggleVerboseLogging.IsChecked ?? false,
                    RAMMonitorEnabled = ToggleRAMMonitor.IsChecked ?? true,
                    DeveloperMode = ToggleDeveloperMode.IsChecked ?? false,
                    CreateRestorePointBeforeUninstall = ToggleRestorePoint.IsChecked ?? true,
                    RememberRestorePointChoice = !(ToggleAskRestorePoint.IsChecked ?? true)
                };

                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                    File.WriteAllText(ConfigPath, json);
                });

                ApplySettings(settings);
            }
            catch (Exception ex)
            {
                Logger.LogError("SettingsPage.SaveSettings", $"Erro ao salvar: {ex.Message}");
            }
            finally
            {
                _isSavingSettings = false;
            }
        }

        private void ApplySettings(AppSettings settings)
        {
            // Aplicar modo desenvolvedor
            DeveloperModeManager.IsDeveloperMode = settings.DeveloperMode;
            
            // TODO: Aplicar configuração de log detalhado quando implementado no Logger
            
            // Notificar a MainWindow para atualizar visibilidade do menu de debug
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.UpdateDebugMenuVisibility(settings.DeveloperMode);
            }
        }

        #region Teste de Paginas

        private async void BtnTestPages_Click(object sender, RoutedEventArgs e)
        {
            BtnTestPages.IsEnabled = false;
            TxtTestStatus.Text = "Iniciando teste...";

            var pages = (PageType[])Enum.GetValues(typeof(PageType));

            for (int i = 0; i < pages.Length; i++)
            {
                TxtTestStatus.Text = $"Testando {i + 1}/{pages.Length}: {pages[i]}...";
                await Task.Delay(600);

                try
                {
                    NavigationHelper.NavigateTo(pages[i]);
                    await Task.Delay(300);
                }
                catch (Exception ex)
                {
                    TxtTestStatus.Text = $"CRASH em {pages[i]}: {ex.Message}";
                    Logger.LogError("TestPages", $"Crash em {pages[i]}: {ex}");
                    BtnTestPages.IsEnabled = true;
                    return;
                }
            }

            TxtTestStatus.Text = $"Teste concluído! {pages.Length} páginas OK.";
            BtnTestPages.IsEnabled = true;
        }

        #endregion

        #region Event Handlers

        private void ToggleStartup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // �� Usar TrayIconService.SetAutoStart como nas outras páginas
                KitLugia.GUI.Services.TrayIconService.SetAutoStart(ToggleStartup.IsChecked == true);
                
                // Atualizar estado real
                ToggleStartup.IsChecked = KitLugia.GUI.Services.TrayIconService.IsAutoStartEnabled();
                
                Logger.Log($"⚙️ Auto-Start: {(ToggleStartup.IsChecked == true ? "ativado" : "desativado")}");
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleStartup_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleCloseToTray_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.CloseToTray = ToggleCloseToTray.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    Logger.Log($"⚙️ Close to Tray: {(ToggleCloseToTray.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleCloseToTray_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleTray_Click()
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.IsTrayEnabled = ToggleTray.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    mainWindow.TrayService.LoadSettings(); // Recarregar para aplicar
                    Logger.Log($"⚙️ Tray Icon: {(ToggleTray.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleTray_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleGameBoost_Click()
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.GamePriorityEnabled = ToggleGameBoost.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    mainWindow.TrayService.LoadSettings(); // Recarregar para aplicar
                    Logger.Log($"⚙️ GameBoost: {(ToggleGameBoost.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleGameBoost_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleTurboBoot_Click()
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.TurboBootEnabled = ToggleTurboBoot.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    mainWindow.TrayService.LoadSettings(); // Recarregar para aplicar
                    Logger.Log($"⚙️ Turbo Boot: {(ToggleTurboBoot.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleTurboBoot_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleTurboShutdown_Click()
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.TurboShutdownEnabled = ToggleTurboShutdown.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    mainWindow.TrayService.LoadSettings(); // Recarregar para aplicar
                    Logger.Log($"⚙️ Turbo Shutdown: {(ToggleTurboShutdown.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleTurboShutdown_Click", $"Erro: {ex.Message}");
            }
        }
        
        private void ToggleStandbyClean_Click()
        {
            try
            {
                if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow && mainWindow.TrayService != null)
                {
                    mainWindow.TrayService.StandbyCleanEnabled = ToggleStandbyClean.IsChecked == true;
                    mainWindow.TrayService.SaveSettings();
                    mainWindow.TrayService.LoadSettings(); // Recarregar para aplicar
                    Logger.Log($"⚙️ Standby Clean: {(ToggleStandbyClean.IsChecked == true ? "ativado" : "desativado")}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("ToggleStandbyClean_Click", $"Erro: {ex.Message}");
            }
        }

        private void ToggleDeveloperMode_Checked(object sender, RoutedEventArgs e)
        {
            Logger.Log("�Y�> Modo Desenvolvedor ATIVADO");
            SaveSettings();
            
            // Mostrar mensagem
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowSuccess("�Y�> Modo Desenvolvedor", "Menu de debug agora visível. Reinicie para aplicar todas as mudanças.");
            }
        }

        private void ToggleDeveloperMode_Unchecked(object sender, RoutedEventArgs e)
        {
            Logger.Log("� Modo Desenvolvedor DESATIVADO");
            SaveSettings();
            
            if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ShowInfo("� Modo Normal", "Menu de debug oculto.");
            }
        }

        #endregion
    }

    #region Classes de Suporte

    public class AppSettings
    {
        // Inicialização
        public bool StartWithWindows { get; set; }
        public bool CloseToTray { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool IntroAnimationEnabled { get; set; } = true;
        public double IntroDuration { get; set; } = 3.0;

        // Sistema
        public bool ShowNotifications { get; set; } = true;
        public bool VerboseLogging { get; set; }

        // Módulos do Kit (apenas RAMMonitor, os demais são gerenciados pelo TrayService)
        public bool RAMMonitorEnabled { get; set; } = true;

        // Desenvolvedor
        public bool DeveloperMode { get; set; }

        // Desinstalação (Revo-like restore point prompt)
        public bool CreateRestorePointBeforeUninstall { get; set; } = true;
        public bool RememberRestorePointChoice { get; set; }
    }

    public static class AppSettingsHelper
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitLugia", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }

    /// <summary>
    /// Gerenciador do Modo Desenvolvedor
    /// </summary>
    public static class DeveloperModeManager
    {
        public static bool IsDeveloperMode { get; set; }
        
        public static event EventHandler? DeveloperModeChanged;
        
        public static void Toggle()
        {
            IsDeveloperMode = !IsDeveloperMode;
            DeveloperModeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    #endregion
}
