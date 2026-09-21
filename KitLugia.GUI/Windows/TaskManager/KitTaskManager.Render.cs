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

        private void HookFrameEngine()
        {
            if (_frameHooked) return;
            // Harness/profiling: desliga o motor por variável de ambiente (diagnóstico de stalls)
            try { if (Environment.GetEnvironmentVariable("KL_TM_NO_RENDER") == "1") { _frameHooked = true; return; } } catch { }
            _frameHooked = true;
            _lastFrameUtc = DateTime.UtcNow;
            _fpsWindowStartUtc = _lastFrameUtc;
            CompositionTarget.Rendering += OnCompositionFrame;
            Closed += (_, __) =>
            {
                try { CompositionTarget.Rendering -= OnCompositionFrame; } catch { }
                _frameHooked = false;
            };
        }

        /// <summary>A aba atual tem lista/gráfico vivo?</summary>
        private bool CurrentTabAnimates()
        {
            if (_isClosed || !IsVisible) return false;
            return TabProcesses.Visibility == Visibility.Visible
                || TabSummary.Visibility == Visibility.Visible;
        }

        private void OnCompositionFrame(object? sender, EventArgs e)
        {
            if (!CurrentTabAnimates())
            {
                // Aba parada: nada de cálculo. Também reinicia o relógio para o
                // primeiro quadro ao voltar não aplicar um dt gigante.
                _lastFrameUtc = DateTime.UtcNow;
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

            EaseStep(dt);
            ReapGhostRows();
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

        private void EaseStep(double dt)
        {
            var rows = _groupedLive;
            if (rows == null || rows.Count == 0) return;

            // MEDIDO (instrumentação do próprio motor): animar TEXTO ou COR de brush
            // por quadro consome 1000-2400 ms/s de UI (100% da thread) — o WPF empurra
            // cada mudança para a render thread. Modelo TMOG real: números e cores das
            // células atualizam 1x/s (no refresh, via PropertyChanged/brush congelado)
            // e o motor de 60fps anima SOMENTE o brilho verde/vermelho (Opacity —
            // mutação comprovadamente barata) e o reap das fantasmas.
            double fadeStep = dt / HighlightFadeSeconds;

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.HighlightLevel > 0)
                    r.SetHighlightLevel(Math.Max(0, r.HighlightLevel - fadeStep));
            }
        }

        /// <summary>
        /// Remove as linhas fantasma (processo encerrado) quando o vermelho já sumiu.
        /// Elas ficam no lugar onde o processo estava, como no TMOG.
        /// </summary>
        private void ReapGhostRows()
        {
            try
            {
                for (int i = _groupedLive.Count - 1; i >= 0; i--)
                {
                    var r = _groupedLive[i];
                    if (r.IsGhost && r.HighlightLevel <= 0) _groupedLive.RemoveAt(i);
                }
            }
            catch { }
        }

        /// <summary>
        /// Converte a linha de um processo que desapareceu em FANTASMA vermelha:
        /// mantém nome/PID/memória onde estavam, zera o que era "vivo" e inicia o fade.
        /// </summary>
        private static void MakeGhost(ProcessRow r)
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
        }

        /// <summary>
        /// Um processo RENASCEU com a mesma chave (ex.: smartscreen.exe morre e renasce
        /// a cada prompt do UAC). Em vez de adicionar uma linha nova — o que duplicava
        /// a GroupKey e fazia o ToDictionary do refresh seguinte explodir — o FANTASMA
        /// (que já está na posição certa, esmaecendo) volta a ser a linha viva.
        /// </summary>
        private static void ReviveGhost(ProcessRow ghost, ProcessRow fresh)
        {
            ghost.IsGhost = false;
            ghost.UpdateFrom(fresh);
            ghost.SetHighlightColor(ColorRowNew);
            ghost.SetHighlightLevel(0.60);
        }

        /// <summary>Marca um processo recém-nascido: fundo verde que esmaece.</summary>
        private static void MarkRowNew(ProcessRow r)
        {
            if (r.IsGhost) return;
            r.SetHighlightColor(ColorRowNew);
            r.SetHighlightLevel(0.60);
        }
    }
}
