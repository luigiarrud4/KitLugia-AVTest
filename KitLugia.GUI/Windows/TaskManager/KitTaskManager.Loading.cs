using System;
using System.Windows;
using KitLugia.Core;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KitLugia.GUI.Windows.TaskManager
{
    /// <summary>
    /// VÉU DA 1ª CARGA — o "sinal de que está carregando" que faltava.
    ///
    /// Por que existe: a janela abre instantânea (o Loaded é fire-and-forget) e a primeira
    /// coleta real — enumeração nativa dos ~400 processos + contadores de desempenho + GPU
    /// via PDH — leva de ~200 ms (máquina boa, cache quente) a vários segundos (perfil
    /// recém-logado, perflib frio, disco lento, Defender mexendo em tudo). Nesse intervalo a
    /// tela ficava com a ESTRUTURA pronta e VAZIA, sem nada indicando trabalho em andamento.
    /// O relato foi literal: "abre a interface mas os valores não aparecem por um bom tempo
    /// e não aparece nenhum sinal de que esteja carregando".
    ///
    /// O que faz:
    ///   1. aparece JUNTO com a janela (já visível no XAML — não há um quadro sem feedback);
    ///   2. avança as 4 etapas nos MESMOS eventos que produzem os dados (lista pintada,
    ///      contadores prontos, primeira amostra de desempenho na tela) — nada de barra
    ///      fake que enche sozinha;
    ///   3. mostra o tempo decorrido: em máquina lenta o número honesto vale mais que um
    ///      spinner girando sem prazo;
    ///   4. sai com fade assim que a lista E os gráficos existem, e tem TETO DE SEGURANÇA
    ///      (15 s) para nunca ficar preso cobrindo a tela se uma coleta falhar.
    ///
    /// Custo: um DispatcherTimer de 100 ms enquanto o véu existe (giro do spinner + texto).
    /// Ele é parado no mesmo instante em que o véu sai (e no fechamento da janela).
    /// </summary>
    public partial class KitTaskManagerWindow
    {
        private const int LoadingHardCapMs = 15000;

        private DispatcherTimer? _loadTimer;
        private readonly System.Diagnostics.Stopwatch _loadWatch = new();
        private int _loadStage;
        private bool _loadOverlayStarted;
        private bool _loadFinished;
        private long _msToFirstRows = -1;
        private long _msToFirstGraphics = -1;

        // Totalmente qualificado: "Brush" é ambíguo neste projeto (System.Drawing × Media).
        private static readonly System.Windows.Media.Brush LoadStepOn =
            Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7)));
        private static readonly System.Windows.Media.Brush LoadStepOff =
            Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0x30, 0x40)));

        /// <summary>Liga o véu. Chamado UMA vez, do Loaded (primeira exibição da janela).</summary>
        private void BeginLoadingOverlay()
        {
            if (_loadOverlayStarted || _loadFinished) return;
            _loadOverlayStarted = true;

            try { _loadWatch.Restart(); } catch { }
            SetLoadStage(1, "Enumerando os processos");

            // O mesmo timer gira o spinner e escreve o tempo decorrido. Prioridade Render:
            // ele nunca atrasa a pintura do que já está pronto na tela.
            try
            {
                _loadTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                _loadTimer.Tick += LoadTimer_Tick;
                _loadTimer.Start();
            }
            catch { }
        }

        private void LoadTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_loadFinished) { StopLoadingTimer(); return; }

                long ms = _loadWatch.ElapsedMilliseconds;

                // Spinner: 36°/tick ≈ uma volta por segundo, sem animação WPF (nada de
                // storyboard para parar/soltar depois — o timer é dono do giro).
                if (LoadSpinnerRotate != null)
                    LoadSpinnerRotate.Angle = (LoadSpinnerRotate.Angle + 36.0) % 360.0;

                if (LoadElapsed != null)
                {
                    string dica = ms > 5000 ? " — a primeira leitura é a mais lenta; as próximas são instantâneas"
                                            : "";
                    LoadElapsed.Text = $"{ms / 1000.0:F1} s{dica}";
                }

                // Rede de segurança: nenhuma combinação de falhas pode deixar o véu na tela.
                if (ms > LoadingHardCapMs) FinishLoadingOverlay("limite de tempo");
            }
            catch { }
        }

        /// <summary>Acende a etapa <paramref name="stage"/> (1..4) e escreve o texto dela. Nunca regride.</summary>
        private void SetLoadStage(int stage, string texto)
        {
            if (_loadFinished) return;
            if (stage <= _loadStage) return;
            _loadStage = stage;
            try
            {
                if (LoadStage != null) LoadStage.Text = texto;
                if (LoadTitle != null) LoadTitle.Text = stage >= 4 ? "Quase pronto" : "Carregando o gerenciador";
                if (LoadStep1 != null) LoadStep1.Background = stage >= 1 ? LoadStepOn : LoadStepOff;
                if (LoadStep2 != null) LoadStep2.Background = stage >= 2 ? LoadStepOn : LoadStepOff;
                if (LoadStep3 != null) LoadStep3.Background = stage >= 3 ? LoadStepOn : LoadStepOff;
                if (LoadStep4 != null) LoadStep4.Background = stage >= 4 ? LoadStepOn : LoadStepOff;
            }
            catch { }
        }

        /// <summary>Contadores de desempenho (PerformanceCounter/perflib) prontos.</summary>
        private void NotifyLoadCountersReady()
        {
            if (_loadFinished || !_loadOverlayStarted) return;
            SetLoadStage(3, "Coletando desempenho (CPU, memória, GPU, disco e rede)");
        }

        /// <summary>Primeira lista de processos pintada na tela.</summary>
        private void NotifyLoadRowsPainted()
        {
            if (_loadFinished || !_loadOverlayStarted) return;
            if (_msToFirstRows < 0) _msToFirstRows = _loadWatch.ElapsedMilliseconds;
            SetLoadStage(2, "Lendo os contadores do sistema");
            MaybeFinishLoading();
        }

        /// <summary>Primeira amostra de desempenho desenhada (gráficos + Resumo).</summary>
        private void NotifyLoadGraphicsRendered()
        {
            if (_loadFinished || !_loadOverlayStarted) return;
            if (_msToFirstGraphics < 0) _msToFirstGraphics = _loadWatch.ElapsedMilliseconds;
            SetLoadStage(4, "Montando a tela");
            MaybeFinishLoading();
        }

        /// <summary>A lista e os gráficos existem: a janela deixou de ser "uma casca vazia".</summary>
        private void MaybeFinishLoading()
        {
            if (_msToFirstRows >= 0 && _msToFirstGraphics >= 0) FinishLoadingOverlay("dados na tela");
        }

        /// <summary>Tira o véu (fade curto) e loga o tempo REAL da primeira carga.</summary>
        private void FinishLoadingOverlay(string motivo)
        {
            if (_loadFinished) return;
            _loadFinished = true;
            StopLoadingTimer();

            long ms = _loadWatch.ElapsedMilliseconds;
            try
            {
                Logger.Log($"[KIT TASK MANAGER] 1ª carga em {ms} ms ({motivo}) — " +
                           $"lista em {_msToFirstRows} ms · gráficos em {_msToFirstGraphics} ms.");
            }
            catch { }

            try
            {
                if (LoadOverlay == null) return;
                var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                fade.Completed += (_, __) =>
                {
                    try { LoadOverlay.Visibility = Visibility.Collapsed; } catch { }
                };
                LoadOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch
            {
                try { LoadOverlay.Visibility = Visibility.Collapsed; } catch { }
            }
        }

        private void StopLoadingTimer()
        {
            try
            {
                if (_loadTimer != null)
                {
                    _loadTimer.Stop();
                    _loadTimer.Tick -= LoadTimer_Tick;
                    _loadTimer = null;
                }
            }
            catch { _loadTimer = null; }
        }

        /// <summary>Fechamento da janela durante a carga: o timer não pode sobreviver à janela.</summary>
        private void StopLoadingOverlay()
        {
            _loadFinished = true;
            StopLoadingTimer();
        }
    }
}
