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

            // Modo --unlock: abre a janela Force Stop Unlock diretamente (via context menu)
            string? unlockPath = KitLugia.GUI.Program.UnlockPath;
            if (!string.IsNullOrEmpty(unlockPath))
            {
                KitLugia.Core.Logger.Log($"[FORCE STOP] Modo unlock ativado: {unlockPath}");
                base.OnStartup(e);
                OpenForceStopUnlock(unlockPath);
                return;
            }

            base.OnStartup(e);

            // Start IPC server to receive --unlock commands from other instances
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