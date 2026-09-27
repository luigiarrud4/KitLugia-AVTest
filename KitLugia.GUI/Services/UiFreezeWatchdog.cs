using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Logger = KitLugia.Core.Logger;

namespace KitLugia.GUI.Services
{
    /// <summary>
    /// Vigia a responsividade da thread de UI e registra no log quando ela fica travada.
    ///
    /// POR QUE (relato do usuario): "enquanto o sistema estava sobrecarregado o kit simplesmente
    /// nao respondia, clicava e nada acontecia ate aparecer o app nao esta respondendo".
    /// O Windows mostra "(Nao Respondendo)" quando a janela fica ~5 s sem bombear mensagens —
    /// ou seja, alguem bloqueou a thread de UI. Isso pode vir de varias origens (tick de timer
    /// fazendo enumeracao de processos, PerformanceCounter, I/O de disco, GC bloqueante,
    /// Dispatcher.Invoke sincrono de uma thread de background...). Sem medicao, cada relato vira
    /// adivinhacao.
    ///
    /// COMO MEDE: uma thread dedicada (fora do thread pool, para nao sofrer com starvation)
    /// posta um probe em <see cref="DispatcherPriority.Send"/> — a prioridade MAXIMA do dispatcher —
    /// e mede quanto tempo levou ate ele ser executado. Se a UI estivesse apenas ocupada com
    /// trabalho de prioridade baixa (Background/Render), o probe furaria a fila e o atraso seria
    /// proximo de zero; atraso alto = a thread de UI esta REALMENTE bloqueada.
    ///
    /// Custo: 1 BeginInvoke a cada <see cref="ProbeIntervalMs"/> ms. Irrelevante.
    /// Nao derruba nada: qualquer excecao encerra o loop e fica logada.
    /// </summary>
    public static class UiFreezeWatchdog
    {
        /// <summary>A partir deste atraso consideramos a UI travada (ms).</summary>
        public static int StallThresholdMs { get; set; } = 1000;

        /// <summary>Intervalo entre probes (ms).</summary>
        public static int ProbeIntervalMs { get; set; } = 500;

        /// <summary>Maior travamento observado na sessao (ms).</summary>
        public static long WorstStallMs { get; private set; }

        /// <summary>Quantos travamentos acima do limiar ocorreram na sessao.</summary>
        public static int StallCount { get; private set; }

        private static readonly object _sync = new();
        private static bool _started;

        /// <summary>Inicia o watchdog (idempotente). Chame uma vez, na thread de UI.</summary>
        public static void Start()
        {
            lock (_sync)
            {
                if (_started) return;
                _started = true;
            }

            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "KitLugia.UIWatchdog",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();

            Logger.Log($"[UI] Watchdog de responsividade ativo (limiar {StallThresholdMs} ms).");
        }

        private static void Run()
        {
            DateTime lastReport = DateTime.MinValue;

            while (true)
            {
                try
                {
                    Thread.Sleep(ProbeIntervalMs);

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

                    long delay = MeasureDispatcherDelay(dispatcher);

                    if (delay >= StallThresholdMs)
                    {
                        if (delay > WorstStallMs) WorstStallMs = delay;
                        StallCount++;

                        // Anti-flood: no maximo 1 log a cada 5 s (uma travada longa gera varios probes)
                        if ((DateTime.Now - lastReport).TotalSeconds >= 5)
                        {
                            lastReport = DateTime.Now;
                            long pending = 0;
                            try { pending = ThreadPool.PendingWorkItemCount; } catch { }
                            Logger.Log($"[UI-FREEZE] Thread de UI travada por ~{delay} ms (maior da sessao: {WorstStallMs} ms, ocorrencias: {StallCount}, itens pendentes no pool: {pending}).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[UI] Watchdog encerrado: {ex.Message}");
                    return;
                }
            }
        }

        /// <summary>Retorna quantos ms o dispatcher levou para executar um probe de prioridade Send.</summary>
        private static long MeasureDispatcherDelay(Dispatcher dispatcher)
        {
            var done = new ManualResetEventSlim(false);
            var sw = Stopwatch.StartNew();

            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    sw.Stop();
                    done.Set();
                }));
            }
            catch
            {
                return 0; // dispatcher em shutdown: nao e travamento
            }

            // Espera generosa: se estourar, a UI esta travada de verdade (o probe ainda roda depois)
            bool executed = done.Wait(TimeSpan.FromSeconds(20));
            return executed ? sw.ElapsedMilliseconds : 20000;
        }
    }
}
