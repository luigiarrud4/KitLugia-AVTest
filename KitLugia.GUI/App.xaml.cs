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

            base.OnStartup(e);


            // Deferred in tray mode so tray icon appears faster
            if (StartMinimized)
                _ = Task.Run(() => KitLugia.Core.StartupManager.CheckAndFixStartupMethods());
            else
                KitLugia.Core.StartupManager.CheckAndFixStartupMethods();

            var mainWindow = new MainWindow();
            
            // Só exibe a janela principal se não tiver o argumento --tray
            if (!StartMinimized)
            {
                mainWindow.Show();
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