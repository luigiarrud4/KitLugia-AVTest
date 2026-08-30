using System.Windows;
using Application = System.Windows.Application;
using System;
using System.Linq;
using System.Threading.Tasks;
// A linha duplicada "using System.Windows;" foi removida daqui
using System.Windows.Interop;
using System.Windows.Media;

namespace KitLugia.GUI
{
    public partial class App : Application
    {
        public bool StartMinimized { get; set; } = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Renderização padrão (DirectWrite/hardware) - necessário para suporte a emojis, acentos e Unicode
            RenderOptions.ProcessRenderMode = RenderMode.Default;

            // ANTI-FLASH BRANCO GLOBAL: sobrescreve as cores do SISTEMA que o WPF usa como
            // fallback em templates default (ListBox/ScrollViewer/ComboBox/ToolTip etc.).
            // Qualquer controle ainda não estilizado mostra essas cores — nunca branco.
            var res = Resources;
            res[System.Windows.SystemColors.WindowBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x11, 0x11));
            res[System.Windows.SystemColors.ControlBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
            res[System.Windows.SystemColors.ControlLightBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x22));
            res[System.Windows.SystemColors.ControlLightLightBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x2A));
            res[System.Windows.SystemColors.MenuBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
            res[System.Windows.SystemColors.AppWorkspaceBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x0A, 0x0A));
            res[System.Windows.SystemColors.HighlightBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
            res[System.Windows.SystemColors.InfoBrushKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25));

            if (e.Args.Length > 0)
            {
                KitLugia.Core.Logger.Log($"Argumentos recebidos: {string.Join(", ", e.Args)}");
                StartMinimized = e.Args.Contains("--tray");
                KitLugia.Core.Logger.Log($"StartMinimized: {StartMinimized}");
            }

            // Modo auto-update: baixa o ZIP e abre o updater visível, depois fecha
            if (e.Args.Contains("--update"))
            {
                base.OnStartup(e);
                _ = RunAutoUpdateAsync();
                return;
            }

            // Modo --unlock / --takeown: abre a janela Force Stop Unlock diretamente (via context menu)
            string? unlockPath = KitLugia.GUI.Program.UnlockPath;
            string? takeOwnPath = KitLugia.GUI.Program.TakeOwnPath;
            if (!string.IsNullOrEmpty(unlockPath) || !string.IsNullOrEmpty(takeOwnPath))
            {
                bool isTakeOwn = !string.IsNullOrEmpty(takeOwnPath);
                string targetPath = isTakeOwn ? takeOwnPath! : unlockPath!;
                KitLugia.Core.Logger.Log($"[FILE OPS] Modo {(isTakeOwn ? "takeown" : "unlock")} ativado: {targetPath}");
                base.OnStartup(e);
                // IPC ainda precisa escutar mesmo no cold-start
                KitLugia.GUI.Services.UnlockIpcServer.Start();
                _ = Task.Run(() => KitLugia.Core.SystemTweaks.RefreshContextMenuPathsIfNeeded());
                // Cria MainWindow mas já navega pra aba correta antes de Show
                var mwEarly = new MainWindow();
                if (!StartMinimized) mwEarly.Show();
                // Aguarda a janela carregar e navega
                _ = Task.Run(async () =>
                {
                    await Task.Delay(600);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (isTakeOwn) mwEarly.NavigateToTakeOwn(targetPath);
                        else mwEarly.NavigateToUnlock(targetPath);
                    });
                });
                return;
            }

            // Modo --kitstore: abre só o KitStore standalone (pasta dedicada KitStore, sem precisar do Kit)
            if (e.Args.Contains("--kitstore") || e.Args.Contains("--store"))
            {
                base.OnStartup(e);
                KitLugia.Core.Logger.Log("[STORE] Modo --kitstore ativado — abrindo KitStore standalone");
                KitLugia.GUI.Services.UnlockIpcServer.Start();
                var sw = new Windows.KitStore.KitStoreWindow();
                sw.Show();
                return;
            }

            base.OnStartup(e);

            // Start IPC server to receive --unlock/--takeown commands from other instances
            KitLugia.GUI.Services.UnlockIpcServer.Start();

            // Sempre verifica .NET Desktop Runtime e oferece instalação direta (sem abrir site) — prompt inline
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!KitLugia.GUI.Services.DotNetDirectInstaller.IsDesktopRuntimeInstalled("10") &&
                        !KitLugia.GUI.Services.DotNetDirectInstaller.IsDesktopRuntimeInstalled("8"))
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            await KitLugia.GUI.Services.DotNetDirectInstaller.PromptAndInstallDirectAsync(Current.MainWindow);
                        });
                    }
                }
                catch { }
            });

            // Always run startup method check in background so the window appears first
            _ = Task.Run(() => KitLugia.Core.StartupManager.CheckAndFixStartupMethods());
            _ = Task.Run(() => KitLugia.Core.SystemTweaks.RefreshContextMenuPathsIfNeeded());

            var mainWindow = new MainWindow();
            
            // Só exibe a janela principal se não tiver o argumento --tray
            if (!StartMinimized)
            {
                mainWindow.Show();
            }
        }

        private async void OpenForceStopUnlock(string path)
        {
            // Wait for MainWindow to initialize
            await Task.Delay(500);

            // Navigate within the Kit to ForceStopUnlock page with path
            if (Current.MainWindow is KitLugia.GUI.MainWindow mw)
            {
                mw.NavigateToUnlock(path);
            }
        }

        private async Task RunAutoUpdateAsync()
        {
            try
            {
                KitLugia.Core.Logger.Log("🔄 Modo auto-update ativado");

                // Verifica se há atualização
                var hasUpdate = await KitLugia.Core.GitHubUpdater.CheckForUpdatesAsync();
                if (!hasUpdate)
                {
                    KitLugia.Core.Logger.Log("✅ KitLugia já está atualizado!");
                    Current.Shutdown();
                    return;
                }

                KitLugia.Core.Logger.Log("🔄 Baixando e instalando atualização...");
                var success = await KitLugia.Core.GitHubUpdater.DownloadAndInstallUpdateAsync(visible: true);
                if (success)
                {
                    KitLugia.Core.Logger.Log("🚀 Updater lançado! Fechando...");
                    await Task.Delay(2000);
                }
                else
                {
                    KitLugia.Core.Logger.Log("❌ Falha na atualização");
                    await Task.Delay(5000);
                }
            }
            catch (Exception ex)
            {
                KitLugia.Core.Logger.Log($"❌ Erro: {ex.Message}");
            }
            Current.Shutdown();
        }
    }
}