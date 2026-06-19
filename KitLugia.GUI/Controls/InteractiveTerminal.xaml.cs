using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KitLugia.Core;

// === CORREÇÃO DE CONFLITOS ===
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace KitLugia.GUI.Controls
{
    public partial class InteractiveTerminal : UserControl
    {
        public event EventHandler? RequestClose;

        public InteractiveTerminal()
        {
            InitializeComponent();

            // 1. Inicializa o VirtualTerminal (Liga o código à tela)
            VirtualTerminal.Initialize(TxtOutput, ConsoleScroller, TxtInput);

            // 2. Inicia o Loop do Console em outra thread
            Task.Run(() => StartLegacySession());
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string text = TxtInput.Text;
                TxtInput.Text = "";
                // Envia o texto para o VirtualTerminal processar
                VirtualTerminal.SubmitInput(text);
            }
        }

        // =================================================================
        // LÓGICA DO CONSOLE LEGACY (SEU CÓDIGO ANTIGO ADAPTADO)
        // =================================================================
        private async Task StartLegacySession()
        {
            // Boot Fictício
            VirtualTerminal.Clear();
            VirtualTerminal.WriteLine("Inicializando Kit Lugia Core v2.0...");
            await Task.Delay(300);
            VirtualTerminal.WriteLine("Carregando módulos de kernel...");
            await Task.Delay(300);
            VirtualTerminal.WriteLine("Acesso de Administrador: CONCEDIDO.");
            VirtualTerminal.WriteLine("----------------------------------------------------------------");
            VirtualTerminal.WriteLine("");

            bool running = true;
            while (running)
            {
                // MENU PRINCIPAL
                VirtualTerminal.WriteLine("    ==================================================================");
                VirtualTerminal.WriteLine("    ==          KIT LUGIA - LEGACY CONSOLE MODE (v20)               ==");
                VirtualTerminal.WriteLine("    ==================================================================");
                VirtualTerminal.WriteLine("");
                VirtualTerminal.WriteLine("    [OTI] OTIMIZAR PERFORMANCE ESSENCIAL (1-Clique)");
                VirtualTerminal.WriteLine("    [IMP] TWEAKS DE ALTO IMPACTO");
                VirtualTerminal.WriteLine("    [SER] GERENCIADOR DE PROCESSOS");
                VirtualTerminal.WriteLine("");
                VirtualTerminal.WriteLine("    TWEAKS DE REGISTRO:");
                VirtualTerminal.WriteLine("      [1] Aplicar Cache de CPU (L2/L3)");
                VirtualTerminal.WriteLine("      [2] Tweak 'LastActiveClick'");
                VirtualTerminal.WriteLine("      [3] Desativar Bing no Menu Iniciar");
                VirtualTerminal.WriteLine("      [4] Restaurar Menu Clássico (Win 11)");
                VirtualTerminal.WriteLine("");
                VirtualTerminal.WriteLine("    FERRAMENTAS:");
                VirtualTerminal.WriteLine("      [L] Limpeza de Disco");
                VirtualTerminal.WriteLine("      [R] Resetar Rede");
                VirtualTerminal.WriteLine("      [P] Planos de Energia");
                VirtualTerminal.WriteLine("      [D] Diagnóstico (SFC/DISM)");
                VirtualTerminal.WriteLine("");
                VirtualTerminal.WriteLine("      [0] Sair / Fechar Terminal");
                VirtualTerminal.WriteLine("");
                VirtualTerminal.Write("root@lugia:~$ ");

                // AWAIT: O código para aqui e espera você digitar na GUI
                string input = await VirtualTerminal.ReadLineAsync();
                input = input.Trim().ToUpper();
                VirtualTerminal.WriteLine("");

                try
                {
                    switch (input)
                    {
                        case "OTI":
                            VirtualTerminal.WriteLine(">> Iniciando otimização automática...");
                            SystemTweaks.ApplyAutoCacheTweak();
                            SystemTweaks.ApplyGamingOptimizations();
                            SystemTweaks.ApplyVerboseStatus();
                            Toolbox.ImportAndActivateBitsumPlan();
                            SystemTweaks.ApplyAutomaticVramTweak();
                            VirtualTerminal.WriteLine(">> [OK] Otimização concluída com sucesso.");
                            break;

                        case "IMP":
                            VirtualTerminal.WriteLine(">> Alternando VBS (Virtualization Based Security)...");
                            var vbsRes = SystemTweaks.ToggleVbs();
                            VirtualTerminal.WriteLine($">> {vbsRes.Message}");

                            VirtualTerminal.WriteLine(">> Alternando MPO (Multi-Plane Overlay)...");
                            var mpoRes = SystemTweaks.ToggleMpo();
                            VirtualTerminal.WriteLine($">> {mpoRes.Message}");
                            break;

                        case "SER":
                            VirtualTerminal.WriteLine(">> Aplicando preset GAMER nos serviços...");
                            BackgroundProcessManager.ApplyServicePreset("Gamer");
                            VirtualTerminal.WriteLine(">> [OK] Serviços otimizados.");
                            break;

                        case "1":
                            SystemTweaks.ApplyAutoCacheTweak();
                            VirtualTerminal.WriteLine(">> [OK] Cache L2/L3 ajustado.");
                            break;
                        case "2":
                            SystemTweaks.ApplyLastClickTweak();
                            VirtualTerminal.WriteLine(">> [OK] LastActiveClick ativado.");
                            break;
                        case "3":
                            SystemTweaks.ApplyBingTweak();
                            VirtualTerminal.WriteLine(">> [OK] Bing desativado.");
                            break;
                        case "4":
                            SystemTweaks.ApplyWin10ContextTweak(true);
                            VirtualTerminal.WriteLine(">> [OK] Menu Clássico ativado. Reinicie o Explorer.");
                            break;

                        case "L":
                            VirtualTerminal.WriteLine(">> Limpando arquivos temporários...");
                            var cleanRes = Toolbox.CleanTemporaryFiles();
                            VirtualTerminal.WriteLine($">> Liberado: {cleanRes.TotalBytesFreed / 1024 / 1024} MB");
                            break;

                        case "R":
                            VirtualTerminal.WriteLine(">> Resetando pilha de rede...");
                            Toolbox.ResetNetworkStack();
                            VirtualTerminal.WriteLine(">> [OK] Winsock/IP resetados. Reinicie o PC.");
                            break;

                        case "P":
                            Toolbox.UnlockAndActivateUltimatePerformance();
                            VirtualTerminal.WriteLine(">> [OK] Plano Desempenho Máximo ativado.");
                            break;

                        case "D":
                            VirtualTerminal.WriteLine(">> Iniciando SFC /Scannow em janela externa...");
                            Toolbox.RepairSystemComponentsSFC();
                            break;

                        case "0":
                        case "EXIT":
                            running = false;
                            break;

                        default:
                            VirtualTerminal.WriteLine("Comando inválido.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    VirtualTerminal.WriteLine($"ERRO: {ex.Message}");
                }

                if (running)
                {
                    VirtualTerminal.WriteLine("\nPressione ENTER para continuar...");
                    await VirtualTerminal.ReadLineAsync();
                    VirtualTerminal.Clear();
                }
            }

            // Fecha o controle visualmente
            Dispatcher.Invoke(() =>
            {
                this.Visibility = Visibility.Collapsed;
                RequestClose?.Invoke(this, EventArgs.Empty);
            });
        }
    }
}