using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace KitLugia.Bootstrap
{
    /// <summary>
    /// Janela que hospeda a tela "Componentes Adicionais Necesarios" e conduz
    /// o fluxo de download + instalacao dos componentes oficiais.
    /// </summary>
    public partial class DependencyDialogWindow : Window
    {
        private readonly List<RequiredComponent> _components;

        public DependencyDialogWindow(List<RequiredComponent> components)
        {
            InitializeComponent();

            _components = components ?? new List<RequiredComponent>();
            View.SetComponents(_components);

            View.CancelRequested += (s, e) =>
            {
                DialogResult = false;
                Close();
            };

            View.CloseRequested += (s, e) =>
            {
                DialogResult = false;
                Close();
            };

            View.InstallRequested += async (s, e) => await RunInstallAsync();
        }

        private async Task RunInstallAsync()
        {
            View.SetBusy(true);
            View.ShowError("");

            foreach (var component in _components)
            {
                // 1) DOWNLOAD
                View.SetBusy(true, "Baixando...");
                View.SetProgress(0, "Baixando " + component.DisplayName + "...");
                var progress = new Progress<InstallProgress>(p =>
                {
                    View.SetProgress(p.Percent, p.Status);
                });

                bool downloaded = await ComponentInstaller.DownloadAsync(component, progress, BootstrapLog.Write);
                if (!downloaded)
                {
                    View.ShowError("Não foi possível baixar " + component.DisplayName +
                                   ". Verifique a conexão com a internet e tente novamente.");
                    View.SetBusy(false);
                    return;
                }

                // 2) INSTALACAO (o instalador oficial pede o UAC sozinho)
                View.SetBusy(true, "Instalando...");
                View.SetProgress(100, "Instalando " + component.DisplayName + "...");

                var outcome = ComponentInstaller.Install(component, BootstrapLog.Write);
                if (outcome != InstallOutcome.Success)
                {
                    // Estados espelhados do LuaTools (Velopack Runtimes API):
                    // cancelado pelo usuario / instalado mas precisa reiniciar.
                    if (outcome == InstallOutcome.UserCancelled)
                    {
                        View.ShowError("Cancelado pelo usuário. " + component.DisplayName +
                                       " não foi instalado — o KitLugia precisa dele para abrir.");
                        View.SetBusy(false);
                        return;
                    }
                    if (outcome == InstallOutcome.RestartRequired)
                    {
                        View.SetDone(true, "Instalado. O Windows precisa reiniciar antes de o KitLugia abrir.");
                        await Task.Delay(2500);
                        DialogResult = false;
                        Close();
                        return;
                    }

                    View.ShowError("A instalação de " + component.DisplayName +
                                   " não foi concluída. Verifique a internet e tente novamente.");
                    View.SetBusy(false);
                    return;
                }
            }

            // 3) VERIFICACAO FINAL
            var stillMissing = ComponentDetector.GetMissingComponents();
            if (stillMissing.Count > 0)
            {
                View.ShowError("A instalação terminou, mas ainda faltam: " +
                               string.Join(", ", stillMissing.ConvertAll(c => c.DisplayName).ToArray()) + ".");
                View.SetBusy(false);
                return;
            }

            View.SetDone(true, "Instalação concluída. Abrindo o KitLugia...");
            await Task.Delay(1200);

            DialogResult = true;
            Close();
        }
    }
}
