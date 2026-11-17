using System;
using System.Windows;
using System.Windows.Media;

namespace KitLugia.GUI.Windows.TaskManager
{
    /// <summary>
    /// MOTOR DE RENDER 60 FPS (paridade visual com o Task Manager TMOG).
    ///
    /// Por que existe: o Kit coleta as métricas pesadas 1x por segundo (a enumeração
    /// nativa de ~400 processos custa ~100 ms — não dá para repetir 60x/s). O TMOG
    /// parece "liso" porque os NÚMEROS exibidos são interpolados entre as amostras.
    /// É exatamente o que este motor faz:
    ///
    ///   1. EASING — a cada quadro (~16,6 ms) cada valor exibido caminha uma fração do
    ///      caminho até o valor real da última amostra (curva exponencial que se
    ///      completa em ~0,3 s). O usuário vê barras e números deslizando, não pulando.
    ///   2. VERDE QUE ESMAECE — processo que ACABOU de aparecer ganha um fundo verde
    ///      que desaparece em ~2,5 s ("isto é novo").
    ///   3. VERMELHO QUE ESMAECE — processo que FECHOU não some na hora: vira uma
    ///      linha fantasma vermelha no lugar onde estava, esmaece e só então sai
    ///      ("isto acabou de fechar").
    ///
    /// Roda no quadro da própria UI (CompositionTarget.Rendering) e SÓ nas abas
    /// Processos/Resumo — em outra aba o custo é zero (o handler sai na 1ª linha).
    ///
    /// Detalhe de desempenho importante: o brilho verde/vermelho é animado mudando a
    /// propriedade Opacity de um SolidColorBrush por linha (objeto Freezable do WPF,
    /// que re-renderiza sozinho). Isso evita disparar PropertyChanged milhares de
    /// vezes por segundo só para o fundo.
    /// </summary>
    public partial class KitTaskManagerWindow
    {
        private bool _frameHooked;
        private DateTime _lastFrameUtc = DateTime.UtcNow;
        private long _framesThisSecond;
        private DateTime _fpsWindowStartUtc = DateTime.UtcNow;

        /// <summary>FPS efetivo medido pelo próprio motor (mostrado no rodapé da lista).</summary>
        private double _renderFps;

        internal static readonly System.Windows.Media.Color ColorRowNew =
            System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32);   // verde "nasceu"
        internal static readonly System.Windows.Media.Color ColorRowDead =
            System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28);   // vermelho "fechou"

        /// <summary>Tempo do fade do brilho de processo novo/morto (segundos).</summary>
        private const double HighlightFadeSeconds = 2.5;

        /// <summary>O motor pode ser ligado (o KL_TM_NO_RENDER desliga; harness de stalls).</summary>
        private bool _frameEngineAvailable;

        /// <summary>
        /// Prepara o motor de animação das linhas — mas NÃO o liga.
        ///
        /// ANTES: assinava CompositionTarget.Rendering no construtor e ficava 60x/s para
        /// sempre, inclusive no Resumo (aba que abre, e que não tem linha nenhuma para
        /// animar) e com as linhas todas em repouso. O laço por quadro varria centenas de
        /// linhas só para checar "não tenho nada a fazer" — trabalho puro de UI thread em
        /// cima de um app que já é pesado.
        ///
        /// AGORA: liga quando uma linha começa a esmaecer (RequestFrameAnimation) e DESLIGA
        /// sozinho quando não sobrou nada a animar (ReleaseFrameAnimation). Em repouso o
        /// custo é exatamente zero — sem hook, sem laço, sem medição de FPS.
        /// </summary>
        private void InitFrameEngine()
        {
            try { if (Environment.GetEnvironmentVariable("KL_TM_NO_RENDER") == "1") return; } catch { }
            _frameEngineAvailable = true;
            // Segurança: se a janela fechar com o motor ligado, desassina.
            Closed += (_, __) => ReleaseFrameAnimation();
        }

        /// <summary>Liga o motor de quadros (idempotente). Chamado quando algo começa a esmaecer.</summary>
        private void RequestFrameAnimation()
        {
            if (!_frameEngineAvailable || _frameHooked || _isClosed) return;
            // Fora da aba Processos não existe linha visível: não liga agora (o retorno à aba
            // religa pelo ResumeFrameAnimationsIfNeeded). Sem isso, cada processo nascendo/morrendo
            // com outra aba aberta assinava e desassinava o hook em quadros alternados —
            // exatamente o vaivém que a abertura instantânea não quer pagar.
            if (!CurrentTabAnimates()) return;
            _frameHooked = true;
            _lastFrameUtc = DateTime.UtcNow;
            _fpsWindowStartUtc = _lastFrameUtc;
            _framesThisSecond = 0;
            CompositionTarget.Rendering += OnCompositionFrame;
        }

        /// <summary>Desliga o motor de quadros e limpa o rótulo de FPS (que senão fica congelado mentindo).</summary>
        private void ReleaseFrameAnimation()
        {
            if (!_frameHooked) return;
            try { CompositionTarget.Rendering -= OnCompositionFrame; } catch { }
            _frameHooked = false;
            _renderFps = 0;
            UpdateRenderFpsLabel();
        }

        /// <summary>
        /// A aba Processos foi aberta e pode ter esmaecimentos que ficaram congelados enquanto
        /// o motor esteve desligado (trocar de aba não apaga o brilho de uma linha). Se ainda
        /// houver algo a animar — ou fantasma esperando o reap — religa o motor.
        /// </summary>
        private void ResumeFrameAnimationsIfNeeded()
        {
            try
            {
                if (!CurrentTabAnimates()) return;
                for (int i = 0; i < _groupedLive.Count; i++)
                {
                    var r = _groupedLive[i];
                    if (r.HighlightLevel > 0 || r.IsGhost) { RequestFrameAnimation(); return; }
                }
            }
            catch { }
        }

        /// <summary>
        /// A aba atual tem linha viva para animar? Só a aba PROCESSOS tem linhas de processo
        /// (o Resumo mostra cartões e gráficos alimentados pelo tick de 1s) — antes o Resumo
        /// entrava aqui e pagava o motor de quadros inteiro para não animar nada.
        /// </summary>
        private bool CurrentTabAnimates()
        {
            if (_isClosed || !IsVisible) return false;
            return TabProcesses.Visibility == Visibility.Visible;
        }

        private void OnCompositionFrame(object? sender, EventArgs e)
        {
            // Fora da aba Processos não há o que esmaecer: desassina em vez de rodar à toa.
            if (!CurrentTabAnimates())
            {
                ReleaseFrameAnimation();
                return;
            }

            var now = DateTime.UtcNow;
            double dt = (now - _lastFrameUtc).TotalSeconds;
            _lastFrameUtc = now;
            if (dt <= 0) return;
            if (dt > 0.25) dt = 0.25;   // voltou de suspensão/minimizado: não "teleporta"

            _framesThisSecond++;
            if ((now - _fpsWindowStartUtc).TotalSeconds >= 1.0)
            {
                _renderFps = _framesThisSecond / (now - _fpsWindowStartUtc).TotalSeconds;
                _framesThisSecond = 0;
                _fpsWindowStartUtc = now;
                UpdateRenderFpsLabel();
            }

            bool easing = EaseStep(dt);
            bool stillFading = ReapGhostRows();

            // Acabou o esmaecimento e não sobra fantasma: desliga. O próximo esmaecer religa.
            if (!easing && !stillFading) ReleaseFrameAnimation();
        }

        private void UpdateRenderFpsLabel()
        {
            try
            {
                if (TxtRenderFps == null) return;
                TxtRenderFps.Text = _renderFps > 2 ? $"{_renderFps:F0} fps" : "";
            }
            catch { }
        }

        // ── Easing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fração do caminho percorrida no quadro: k = 1 - exp(-dt/tau).
        /// tau = 0,09 s dá ~95% do caminho em 0,27 s (mesmo ritmo do TMOG).
        /// </summary>
        private static double EaseK(double dt) => 1.0 - Math.Exp(-dt / 0.09);

        /// <summary>Retorna true se ANIMOU alguma linha neste quadro (o motor ainda é necessário).</summary>
        private bool EaseStep(double dt)
        {
            var rows = _groupedLive;
            if (rows == null || rows.Count == 0) return false;

            // MEDIDO (instrumentação do próprio motor): animar TEXTO ou COR de brush
            // por quadro consome 1000-2400 ms/s de UI (100% da thread) — o WPF empurra
            // cada mudança para a render thread. Modelo TMOG real: números e cores das
            // células atualizam 1x/s (no refresh, via PropertyChanged/brush congelado)
            // e o motor de 60fps anima SOMENTE o brilho verde/vermelho (Opacity —
            // mutação comprovadamente barata) e o reap das fantasmas.
            double fadeStep = dt / HighlightFadeSeconds;

            bool animated = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.HighlightLevel > 0)
                {
                    r.SetHighlightLevel(Math.Max(0, r.HighlightLevel - fadeStep));
                    animated = true;
                }
            }
            return animated;
        }

        /// <summary>
        /// Remove as linhas fantasma (processo encerrado) quando o vermelho já sumiu.
        /// Elas ficam no lugar onde o processo estava, como no TMOG.
        /// </summary>
        /// <summary>Remove fantasmas já apagados. Retorna true se AINDA resta fantasma esperando o reap.</summary>
        private bool ReapGhostRows()
        {
            bool remaining = false;
            try
            {
                for (int i = _groupedLive.Count - 1; i >= 0; i--)
                {
                    var r = _groupedLive[i];
                    if (!r.IsGhost) continue;
                    if (r.HighlightLevel <= 0) _groupedLive.RemoveAt(i);
                    else remaining = true;
                }
            }
            catch { }
            return remaining;
        }

        /// <summary>
        /// Converte a linha de um processo que desapareceu em FANTASMA vermelha:
        /// mantém nome/PID/memória onde estavam, zera o que era "vivo" e inicia o fade.
        /// </summary>
        private void MakeGhost(ProcessRow r)
        {
            if (r.IsGhost) return;
            r.IsGhost = true;
            r.Status = "encerrado";
            r.CpuTarget = 0; r.CpuValue = 0; r.Cpu = "0%";
            r.Disk = "—"; r.DiskTarget = 0;
            r.Network = "—"; r.NetTarget = 0;
            r.GpuTarget = 0;
            r.SetHighlightColor(ColorRowDead);
            r.SetHighlightLevel(0.60);
            RequestFrameAnimation();   // o esmaecimento do vermelho precisa do motor ligado
        }

        /// <summary>
        /// Um processo RENASCEU com a mesma chave (ex.: smartscreen.exe morre e renasce
        /// a cada prompt do UAC). Em vez de adicionar uma linha nova — o que duplicava
        /// a GroupKey e fazia o ToDictionary do refresh seguinte explodir — o FANTASMA
        /// (que já está na posição certa, esmaecendo) volta a ser a linha viva.
        /// </summary>
        private void ReviveGhost(ProcessRow ghost, ProcessRow fresh)
        {
            ghost.IsGhost = false;
            ghost.UpdateFrom(fresh);
            ghost.SetHighlightColor(ColorRowNew);
            ghost.SetHighlightLevel(0.60);
            RequestFrameAnimation();
        }

        /// <summary>Marca um processo recém-nascido: fundo verde que esmaece.</summary>
        private void MarkRowNew(ProcessRow r)
        {
            if (r.IsGhost) return;
            r.SetHighlightColor(ColorRowNew);
            r.SetHighlightLevel(0.60);
            RequestFrameAnimation();
        }
    }
}
