using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KitLugia.Core;
using Application = System.Windows.Application;

#pragma warning disable CS4014 // Chamadas async não aguardadas são intencionais para operações em background

namespace KitLugia.GUI.Pages
{
    public partial class GamesPage : Page
    {
        private CancellationTokenSource? _cts;
        private bool _isGameOperation;

        public GamesPage()
        {
            InitializeComponent();
            LoadStats();

            this.Unloaded += GamesPage_Unloaded;
        }


        public void Cleanup()
        {
            // Cancela tasks em background
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            this.Unloaded -= GamesPage_Unloaded;


            this.DataContext = null;



        }

        private void GamesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Cleanup();
        }

        private async Task LoadStats()
        {
            double totalRam = SystemUtils.GetTotalSystemRamGB();
            TxtTotalRam.Text = $"{totalRam:F1} GB";

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                await Task.Run(() =>
                {
                    bool gameMode = SystemTweaks.IsGamingOptimized();
                    bool dvrEnabled = SystemTweaks.IsGameDvrEnabled();
                    if (!token.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            ChkGameMode.IsChecked = gameMode;
                            ChkDvr.IsChecked = !dvrEnabled;
                        });
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {

            }
        }

        // --- RAM BOOSTER ---
        private async void BtnBoostRam_Click(object sender, RoutedEventArgs e)
        {
            if (_isGameOperation) return;
            _isGameOperation = true;
            try
            {
                if (!(Application.Current.MainWindow is MainWindow mw)) return;
                mw.ShowInfo("AGUARDE", "Otimizando Memória RAM...");

                var result = await Task.Run(() => SystemTweaks.OptimizeMemory());
                mw.ShowSuccess("RAM BOOSTER", $"Memória limpa com sucesso!\n{result.Message}");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnBoostRam_Click", ex.Message);
            }
            finally
            {
                _isGameOperation = false;
            }
        }

        // --- TWEAKS ---
        private void ChkGameMode_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;
            if (ChkGameMode.IsChecked == true)
            {
                SystemTweaks.ApplyGamingOptimizations();
                mw.ShowSuccess("MODO JOGO", "Prioridade de Jogo definida para ALTA.");
            }
            else
            {
                mw.ShowInfo("AVISO", "Use o backup do registro para reverter completamente esta otimização.");
            }
        }

        private void ChkDvr_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            bool turnOff = ChkDvr.IsChecked == true;
            SystemTweaks.ToggleGameDvr(!turnOff);

            string status = turnOff ? "DESATIVADO (Otimizado)" : "ATIVADO (Padrão)";
            mw.ShowSuccess("XBOX DVR", $"Game DVR do Xbox foi {status}.\nReinicie o computador para garantir o efeito.");
        }

        // --- ATALHOS ---
        private async void BtnClearShaders_Click(object sender, RoutedEventArgs e)
        {
            if (_isGameOperation) return;
            _isGameOperation = true;
            try
            {
                if (!(Application.Current.MainWindow is MainWindow mw)) return;
                mw.ShowInfo("AGUARDE", "Limpando caches de shaders...");

                var res = await Task.Run(() => Toolbox.CleanShaderCaches());
                mw.ShowSuccess("SUCESSO", $"Caches de shaders limpos.\nLiberado: {res.TotalBytesFreed / 1024 / 1024} MB");
            }
            catch (Exception ex)
            {
                Logger.LogError("BtnClearShaders_Click", ex.Message);
            }
            finally
            {
                _isGameOperation = false;
            }
        }

        private void BtnHighPerf_Click(object sender, RoutedEventArgs e)
        {
            if (!(Application.Current.MainWindow is MainWindow mw)) return;

            var result = Toolbox.ImportAndActivateBitsumPlan();
            if (result.Success)
            {
                mw.ShowSuccess("ENERGIA", $"Plano de energia 'Bitsum Highest Performance' foi ativado com sucesso!");
            }
            else
            {
                mw.ShowError("ERRO", result.Message);
            }
        }
    }
}