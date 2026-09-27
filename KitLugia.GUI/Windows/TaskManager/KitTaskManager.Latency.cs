using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KitLugia.Core.TaskManager;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace KitLugia.GUI.Windows.TaskManager
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Partial: aba LATÊNCIA — estilo LatencyMon, mas SEM driver de kernel.
    //  O Core (LatencyMonitor) escuta 4 fontes nativas:
    //    1. DPC/ISR por núcleo (NtQSI classe 8);
    //    2. sessão ETW do "NT Kernel Logger" (atribui o DPC/ISR ao .sys — exige admin);
    //    3. hard page faults por processo (NtQSI classe 5);
    //    4. janelas "Não respondendo" (IsHungAppWindow) + jitter do próprio loop.
    //  Aqui só apresentamos isso de forma didática: conclusão em português, medidores,
    //  4 tabelas (eventos/drivers/núcleos/processos) e relatório p/ colar numa IA.
    // ══════════════════════════════════════════════════════════════════════════
    public partial class KitTaskManagerWindow
    {
        private readonly LatencyMonitor _lat = LatencyMonitor.Instance;
        private DispatcherTimer? _latTimer;
        private bool _latBuilt;
        private string _latView = "Events";

        private readonly ObservableCollection<LatEventRow> _latEventRows = new();
        private readonly ObservableCollection<LatDriverRow> _latDriverRows = new();
        private readonly ObservableCollection<LatCoreRow> _latCoreRows = new();
        private readonly ObservableCollection<LatProcRow> _latProcRows = new();
        private bool _latIsAdmin;
        private readonly HashSet<string> _latSeenEvents = new();
        private readonly Dictionary<string, LatDriverRow> _latDriverIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, LatCoreRow> _latCoreIndex = new();
        private readonly Dictionary<int, LatProcRow> _latProcIndex = new();

        private static readonly SolidColorBrush LatOk = LatBrush(0x4C, 0xAF, 0x50);
        private static readonly SolidColorBrush LatWarn = LatBrush(0xFF, 0xB7, 0x4D);
        private static readonly SolidColorBrush LatBad = LatBrush(0xF4, 0x43, 0x36);
        private static readonly SolidColorBrush LatIdle = LatBrush(0x77, 0x77, 0x77);

        private static SolidColorBrush LatBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // ══════════════ inicialização (chamada pelo SwitchTab) ══════════════

        private void EnsureLatencyBuilt()
        {
            if (_latBuilt) return;
            _latBuilt = true;

            DgLatEvents.ItemsSource = _latEventRows;
            DgLatDrivers.ItemsSource = _latDriverRows;
            DgLatCores.ItemsSource = _latCoreRows;
            DgLatProcs.ItemsSource = _latProcRows;

            SetLatView("Events");

            // Botão de admin só faz sentido quando NÃO somos admin (ETW do kernel exige elevação)
            try
            {
                _latIsAdmin = KitLugia.Core.SystemUtils.IsRunningAsAdministrator();
                // Já somos admin? então NADA de "abra como admin" na tela — o que faltar
                // tem outra causa (sessão de kernel em uso) e o texto explica isso.
                BtnLatElevate.Visibility = _latIsAdmin ? Visibility.Collapsed : Visibility.Visible;
            }
            catch { }

            // Ganchos do monitor de áudio (captura em loopback + injeção do "quem" está tocando)
            // e liga a escuta automaticamente: esta aba é feita para pegar travadas de áudio.
            AutoStartAudioListen();

            _latTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _latTimer.Tick += (_, __) => { RefreshLatencyUi(); RenderAudioCard(); };
            _latTimer.Start();
            Closed += (_, __) => { try { _latTimer?.Stop(); } catch { } };

            RefreshLatencyUi();
            RenderAudioCard();
        }

        // ══════════════ ações ══════════════

        private void BtnLatToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lat.IsRunning)
                {
                    _lat.Stop();
                    TxtStatus.Text = "⏹ Latência: monitoramento parado.";
                }
                else
                {
                    _lat.Start();
                    TxtStatus.Text = "🎧 Latência: escutando o sistema — deixe rodar enquanto o problema acontece.";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro ao alternar o monitor de latência: {ex.Message}";
            }
            UpdateLatState();
            RefreshLatencyUi();
        }

        private void BtnLatClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _lat.Clear();
                AudioGlitchMonitor.Instance.Clear();
                _latEventRows.Clear();
                _latDriverRows.Clear();
                _latCoreRows.Clear();
                _latProcRows.Clear();
                _latSeenEvents.Clear();
                _latDriverIndex.Clear();
                _latCoreIndex.Clear();
                _latProcIndex.Clear();
                TxtStatus.Text = "🧹 Latência: eventos e estatísticas zerados.";
                RefreshLatencyUi();
            }
            catch { }
        }

        private void BtnLatCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Relatório COMBINADO: travadas/DPC + estalos de áudio no mesmo documento — quem
                // recebe (usuário, suporte ou IA) precisa das duas coisas juntas para cruzar.
                string rep = BuildCombinedLatencyReport();
                Clipboard.SetText(rep);
                TxtStatus.Text = $"📋 Relatório copiado ({rep.Length:N0} caracteres: latência + áudio) — cole na IA ou no suporte.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro ao copiar o relatório de latência: {ex.Message}";
            }
        }

        private void BtnLatElevate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = Environment.ProcessPath ?? typeof(KitTaskManagerWindow).Assembly.Location;
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas", // UAC — sem ele o ETW do kernel não abre e não dá p/ dizer qual .sys
                    Arguments = "--tray",
                });
                TxtStatus.Text = "🛡️ Abrindo uma cópia elevada do Kit (aceite o UAC) — com admin o Kit identifica o driver exato.";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"UAC recusado ou falhou: {ex.Message}";
            }
        }

        private void LatView_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag) SetLatView(tag);
        }

        private void SetLatView(string view)
        {
            _latView = view;
            DgLatEvents.Visibility = view == "Events" ? Visibility.Visible : Visibility.Collapsed;
            DgLatDrivers.Visibility = view == "Drivers" ? Visibility.Visible : Visibility.Collapsed;
            DgLatCores.Visibility = view == "Cores" ? Visibility.Visible : Visibility.Collapsed;
            DgLatProcs.Visibility = view == "Procs" ? Visibility.Visible : Visibility.Collapsed;

            foreach (var (btn, tag) in new[] { (BtnLatViewEvents, "Events"), (BtnLatViewDrivers, "Drivers"), (BtnLatViewCores, "Cores"), (BtnLatViewProcs, "Procs") })
            {
                bool on = tag == view;
                btn.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
                btn.Opacity = on ? 1.0 : 0.62;
            }

            LatViewHint.Text = view switch
            {
                "Events" => "o que aconteceu, do mais recente para o mais antigo — passe o mouse para ler o porquê",
                "Drivers" => "quem consumiu DPC/ISR · o nome do .sys exige um driver de kernel (limitação do Windows)",
                "Cores" => "DPC = trabalho de driver · ISR = interrupção de hardware · resolução de 15,6 ms (tick do relógio)",
                _ => "hard page fault = busca no DISCO porque faltou RAM · coluna 'Agora' = falhas por segundo",
            };
        }

        // ══════════════ refresh da interface (1x/s) ══════════════

        private void UpdateLatState()
        {
            bool running = _lat.IsRunning;
            LatState.Text = running ? "MONITORANDO" : "PARADO";
            LatStateBadge.Background = running
                ? new SolidColorBrush(Color.FromRgb(0x16, 0x35, 0x1B))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            LatState.Foreground = running ? LatOk : LatIdle;
            BtnLatToggle.Content = running ? "■ PARAR" : "▶ INICIAR";
            if (!running) LatEtwStatus.Text = "";
        }

        private void RefreshLatencyUi()
        {
            if (_isClosed || !_latBuilt) return;
            try
            {
                var s = _lat.GetSnapshot();

                // ── faixa de métricas ──
                LatTime.Text = s.Elapsed.ToString(@"hh\:mm\:ss");
                LatEvents.Text = $"{_latEventRows.Count} evento(s) na lista";

                LatDpcPct.Text = $"{s.MaxCoreDpcPercent1Min:F1}%";
                SetLatTip(LatDpcPct,
                    "📌 DPC (1 min)",
                    $"Valor agora: {s.MaxCoreDpcPercent1Min:F1}% do núcleo mais ocupado (pico dos últimos 60 s).",
                    "DPC é o trabalho que um DRIVER faz antes de qualquer programa rodar. Quando um núcleo gasta muito " +
                    "tempo nisso, o que estava rodando nele espera — é a causa clássica de estalo no áudio e micro-freezes.",
                    s.MaxCoreDpcPercent1Min >= 25
                        ? "O QUE FAZER: está alto. Abra a tabela Drivers para achar o .sys culpado e atualize esse driver " +
                          "pelo site do fabricante."
                        : s.MaxCoreDpcPercent1Min >= 8
                            ? "O QUE FAZER: moderado. Só se preocupe se você ouvir estalos no áudio ou sentir travadinhas."
                            : "O QUE FAZER: nada — os drivers estão se comportando bem.");

                SetLatTip(LatTime,
                    "📌 Tempo de análise",
                    $"Monitorando há {s.Elapsed:hh\\:mm\\:ss} · {s.Events.Count} evento(s) registrado(s).",
                    "Quanto mais tempo rodando, mais fácil fica achar o padrão: um travamento isolado pode ser acaso, " +
                    "mas o mesmo evento sempre no mesmo horário/situação aponta a causa.",
                    "Dica: deixe o Kit rodando enquanto o problema acontece e depois clique em 'Copiar relatório (IA)' " +
                    "para analisar tudo de uma vez.");

                SetLatTip(LatEvents,
                    "📌 Eventos registrados",
                    $"{s.Events.Count} evento(s) na lista (os 60 mais recentes aparecem acima).",
                    "O Kit só registra quando algo sai da linha — sistema saudável quase não gera evento.",
                    "Clique em qualquer linha da tabela Eventos para ver o relato completo, com o que aconteceu, " +
                    "quem causou e o que fazer.");
                LatDpcPct.Foreground = s.MaxCoreDpcPercent1Min >= 25 ? LatBad : s.MaxCoreDpcPercent1Min >= 8 ? LatWarn : LatOk;

                LatDpcMax.Text = s.MaxDpcUsSession > 0 ? $"{s.MaxDpcUsSession / 1000.0:F2} ms" : "—";
                var worstDpc = s.Drivers.Where(d => d.DpcCount > 0).OrderByDescending(d => d.MaxDpcUs).FirstOrDefault();
                LatDpcMaxDrv.Text = worstDpc != null
                    ? worstDpc.Name
                    : s.EtwActive
                        ? "aguardando DPC…"
                        : s.EtwProviderDeniedByWindows
                            ? "o Windows não libera o nome do driver p/ processos comuns"
                            : "driver não identificado";

                LatIsrMax.Text = s.MaxIsrUsSession > 0 ? $"{s.MaxIsrUsSession / 1000.0:F2} ms" : "—";

                // ── Balões dos "picos de uso" (hover curto + espera) ──
                SetLatTip(LatDpcMax,
                    "📌 Maior execução de DPC da sessão",
                    s.MaxDpcUsSession > 0
                        ? $"Pior caso registrado: {s.MaxDpcUsSession / 1000.0:F2} ms" +
                          (string.IsNullOrEmpty(s.WorstDriver) ? "." : $" — driver: {s.WorstDriver}.")
                        : "Nenhum DPC relevante registrado até agora.",
                    "Um único DPC que demora mais de ~1 ms já pode ser sentido como engasgo; acima de 5 ms o áudio " +
                    "costuma estalar. O LatencyMon mede o mesmo tipo de valor.",
                    s.MaxDpcUsSession >= 5000
                        ? "O QUE FAZER: grave o relatório (botão Copiar relatório) e atualize o driver citado — este é " +
                          "o pico que mais atrapalha o seu sistema agora."
                        : "O QUE FAZER: nada urgente enquanto ficar abaixo de ~1 ms.");

                SetLatTip(LatIsrMax,
                    "📌 Maior execução de ISR da sessão",
                    s.MaxIsrUsSession > 0
                        ? $"Pior atendimento de interrupção: {s.MaxIsrUsSession / 1000.0:F2} ms."
                        : "Nenhuma interrupção longa registrada até agora.",
                    "ISR é o atendimento imediato do hardware (placa de rede, disco, USB). Um ISR longo bloqueia o " +
                    "núcleo naquele instante — aparece como travadinha curta e seca.",
                    "O QUE FAZER: se este número subir sempre, o driver do dispositivo citado na tabela Drivers está " +
                    "lento ou com conflito (comum em Wi‑Fi, chipset e USB). Atualize-o.");

                SetLatTip(LatHf,
                    "📌 Paginação do disco",
                    $"Agora: {s.HardFaultsPerSec:F0} falhas duras/s · {s.TotalHardFaults:N0} no total.",
                    "Falha dura = o dado que o programa pediu não estava na RAM e o Windows teve de buscar no DISCO. " +
                    "O programa (e às vezes o áudio) congela enquanto isso.",
                    s.HardFaultsPerSec >= 100
                        ? GetPagHintAction(s.HardFaultsPerSec)
                        : "O QUE FAZER: nada — o nível está saudável.");

                SetLatTip(LatStall,
                    "📌 Sistema travou",
                    $"O sistema inteiro parou {s.JitterEvents}x · último tick {s.LastTickMs} ms, pior {s.MaxTickMs} ms.",
                    "O Kit pede 1 s de CPU para atualizar. Quando esse pedido demora muito, significa que NADA no PC " +
                    "estava rodando — nem ele. É uma aproximação honesta de 'interrupt to process latency' sem instalar " +
                    "driver de kernel.",
                    s.MaxTickMs >= 5000
                        ? "O QUE FAZER: travadas de segundos apontam energia (plano de energia/BIOS), superaquecimento ou " +
                          "espera extrema por disco. Veja temperatura e frequência na aba Resumo."
                        : "O QUE FAZER: nada — o sistema está respondendo dentro do esperado.");

                LatHf.Text = $"{s.HardFaultsPerSec:F0}/s";
                LatHfTotal.Text = $"{s.TotalHardFaults:N0} no total";
                // Cor calibrada pela RAM DISPONÍVEL: paginação com memória de sobra é verde
                // (tráfego de dados frios), só amarela/vermelha quando a RAM está acabando.
                double availNow;
                try { availNow = NativeMetricsHelper.GetAvailableRamMb(); } catch { availNow = -1; }
                bool ramOk = availNow < 0 || availNow >= 1024;
                LatHf.Foreground = !ramOk && s.HardFaultsPerSec >= 500 ? LatBad
                    : !ramOk && s.HardFaultsPerSec >= 100 ? LatWarn
                    : s.HardFaultsPerSec >= 2000 ? LatWarn : LatOk;

                LatStall.Text = $"{s.JitterEvents}x";
                LatStallWorst.Text = s.MaxTickMs > 0 ? $"pior: {s.MaxTickMs / 1000.0:F1}s" : "pior: —";
                LatStall.Foreground = s.MaxTickMs >= 5000 ? LatBad : s.JitterEvents > 0 ? LatWarn : LatOk;

                LatEtwStatus.Text = s.Running
                    ? (s.EtwActive && s.EtwEventsSeen > 0
                        ? "🛡️ ETW ativo — driver identificado"
                        : s.EtwActive && s.EtwSessionTotalEvents > 0
                            ? "⚠ sessão fluindo, mas sem eventos de DPC/ISR — o Windows não entrega esse provedor para processos comuns (ver aba Drivers)"
                            : s.EtwActive && s.EtwUnconfirmed
                                ? "⏳ confirmando fluxo da sessão (~12s)…"
                                : s.EtwProviderDeniedByWindows
                                    ? "provedor de kernel indisponível para programas comuns — DPC/ISR por núcleo segue válido (ver aba Drivers)"
                                    : $"sem identificação de driver ({s.EtwStatus})")
                    : "";

                // ── medidores ── (largura EASADA pelo motor fluido; cor/rotulo no tick 1s)
                SetLatBar(LatBarDpc, LatBarDpcVal, s.MaxDpcUsSession / 1000.0, 5.0, "ms");
                SetLatBar(LatBarIsr, LatBarIsrVal, s.MaxIsrUsSession / 1000.0, 5.0, "ms");
                SetLatBar(LatBarStall, LatBarStallVal, s.MaxTickMs / 1000.0, 10.0, "s");
                SetLatBar(LatBarHf, LatBarHfVal, s.HardFaultsPerSec, 2000.0, "/s");

                // ── conclusão em português ──
                var (level, text) = _lat.BuildConclusion();
                LatConclusionText.Text = text;
                var brush = level >= 2 ? LatBad : level == 1 ? LatWarn : LatOk;
                LatConclusionCard.BorderBrush = brush;
                LatConclusionTitle.Foreground = brush;
                LatConclusionTitle.Text = level >= 2
                    ? "⚠ PROBLEMA DETECTADO"
                    : level == 1 ? "ATENÇÃO" : "Tudo bem por aqui";

                // ── tabelas ──
                SyncLatEvents(s.Events);
                SyncLatDrivers(s.Drivers);
                SyncLatCores(s.PerCore);
                SyncLatProcs(s.TopHardFaults);
                UpdateLatEmptyHint(s);
            }
            catch { /* a aba nunca derruba o gerenciador */ }
        }

        /// <summary>Tabela vazia nunca fica muda: explica POR QUE está vazia e o que fazer.</summary>
        /// <summary>Ação recomendada para paginação alta, calibrada pela RAM DISPONÍVEL (não % em uso): com memória de sobra, página dura é tráfego de dados frios (normal, sobretudo em NVMe) — só virá alerta real perto de 1 GB.</summary>
        private static string GetPagHintAction(double hardFaultsPerSec)
        {
            double availMb;
            try { availMb = NativeMetricsHelper.GetAvailableRamMb(); } catch { availMb = -1; }
            if (availMb >= 0 && availMb < 1024)
                return "O QUE FAZER: a RAM está acabando AGORA (" + availMb + " MB disponíveis). Feche programas/abas e veja a aba " +
                       "Processos ordenada por Memória para saber quem come a RAM.";
            return "O QUE FAZER: nada urgente — há RAM disponível (" + (availMb >= 0 ? availMb + " MB" : "?") +
                   "), então isso é tráfego normal de dados frios, não pressão de memória. Se começar a engasgar áudio " +
                   "JUNTO com picos aqui, feche o programa que aparece nos eventos no mesmo horário.";
        }

        private void UpdateLatEmptyHint(LatencyMonitor.Snapshot s)
        {
            int count = _latView switch
            {
                "Events" => _latEventRows.Count,
                "Drivers" => _latDriverRows.Count,
                "Cores" => _latCoreRows.Count,
                _ => _latProcRows.Count,
            };
            if (count > 0) { LatEmptyHint.Visibility = Visibility.Collapsed; return; }

            LatEmptyHint.Text = BuildEmptyHintText(s);
            LatEmptyHint.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Texto da "tabela vazia" — escrito em fluxo imperativo (if/else) de propósito:
        /// a primeira versão era um ternário aninhado gigante, que o compilador Lia
        /// mal e deixava o diagnóstico ilegível.
        /// </summary>
        private string BuildEmptyHintText(LatencyMonitor.Snapshot s)
        {
            switch (_latView)
            {
                case "Events":
                    return "Nenhum evento ainda — isso é NORMAL num sistema saudável.\n" +
                           "O Kit só registra quando algo realmente sai da linha: DPC alto, paginação pesada, " +
                           "janela travada ou o sistema inteiro parando.";

                case "Cores":
                    if (!_lat.IsRunning)
                        return "Monitoramento parado.\nClique em ▶ INICIAR para ver o DPC/ISR de cada núcleo lógico.";
                    return "Sem amostra de DPC/ISR por núcleo nesta janela (contadores não suportados neste Windows).";

                case "Drivers":
                    if (!_lat.IsRunning)
                        return "Monitoramento parado.\nClique em ▶ INICIAR para o Kit começar a escutar os drivers.";

                    // Diagnosticado em bancada (Windows 28000, 4 execuções elevadas): o bloqueio
                    // vem do PRÓPRIO Windows, não de falta de permissão. Antes o Kit pedia admin
                    // de novo — o usuário (corretamente) via isso como bug.
                    if (s.EtwProviderDeniedByWindows && _latIsAdmin)
                    {
                        string legenda =
                            "O Windows não entrega os eventos de DPC/ISR para esta sessão — e NÃO é falta de permissão.\n\n" +
                            "O Kit já está elevado, habilitou a permissão específica exigida (" + s.EtwPrivilegeStatus + ") e até " +
                            "criou uma sessão de kernel dedicada — ainda assim nenhum evento de DPC/ISR chegou.\n\n" +
                            "Motivo: desde o Windows 8/10, esses eventos só fluem para sessões criadas por um DRIVER de kernel. " +
                            "Programas como o LatencyMon conseguem o nome do driver porque instalam um driver próprio " +
                            "(rspLLL64.sys). O Kit não instala driver de kernel — escolha de segurança.\n\n" +
                            "O QUE VOCÊ PERDE: só o nome do arquivo .sys.\n" +
                            "O QUE VOCÊ MANTÉM: DPC/ISR de cada núcleo, pico e tempo máximo por núcleo, " +
                            "paginação pesada por processo e janelas travadas. Na aba NÚCLEOS dá para ver " +
                            "qual núcleo está sofrendo — na prática, é isso que importa.";
                        return legenda;
                    }

                    if (s.EtwActive && s.EtwEventsSeen == 0 && s.Elapsed.TotalSeconds > 15)
                        return "A sessão de kernel está anexada, mas NÃO entrega eventos de DPC/ISR — " +
                               "sinal de sessão ÓRFÃ (restou de uma execução anterior) ou criada por outro " +
                               "programa sem as flags certas.\n\n" +
                               "O Kit detecta isso sozinho e recria a sessão após ~10 segundos. Se a lista " +
                               "continuar vazia depois de 1 minuto, feche outros programas de profiling " +
                               "(LatencyMon, xperf) e clique PARAR → INICIAR; persistindo, reinicie o PC.\n\n" +
                               "As demais medições (DPC/ISR por núcleo, paginação, janelas travadas) " +
                               "continuam válidas.";

                    if (s.EtwActive)
                        return "A sessão de kernel está ativa, mas ainda não chegou DPC/ISR com endereço " +
                               "conhecido.\nIsso costuma ser normal nos primeiros segundos — a lista se " +
                               "preenche sozinha.";

                    if (_latIsAdmin)
                        return "O Kit está elevado, mas a sessão de kernel não pôde ser usada.\n" +
                               "O Windows respondeu: " + s.EtwStatus + "\n\n" +
                               "A sessão 'NT Kernel Logger' só existe UMA por inicialização do Windows. " +
                               "Se outro programa a estiver usando (LatencyMon, xperf, Process Monitor) ou " +
                               "se ela ficou órfã de um programa que travou, feche o outro programa e clique " +
                               "em PARAR → INICIAR; se persistir, reinicie o PC.";

                    return "O Windows só entrega o NOME do driver (.sys) para programas elevados.\n" +
                           "Clique no botão 🛡️ Admin (aceite o UAC) — o Kit reabre elevado e tenta de novo.\n" +
                           "Mesmo sem isso o Kit continua medindo DPC/ISR por núcleo, que é o essencial.";

                default:
                    return "Nenhum processo com hard page fault até agora — a RAM está dando conta do recado.";
            }
        }

        /// <summary>
        /// Aplica um balão de informação (formato "o que é / por que / o que fazer") a um
        /// elemento, com atraso: o mouse precisa PARAR sobre o pico para o texto aparecer.
        /// </summary>
        private static void SetLatTip(FrameworkElement el, string titulo, string agora, string porQue, string fazer)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(titulo);
                sb.AppendLine(agora);
                sb.AppendLine();
                sb.AppendLine("POR QUE IMPORTA:");
                sb.AppendLine(porQue);
                sb.AppendLine();
                sb.AppendLine(fazer);
                el.ToolTip = sb.ToString().TrimEnd();
                ToolTipService.SetInitialShowDelay(el, 700);
                ToolTipService.SetShowDuration(el, 60000);
                ToolTipService.SetBetweenShowDelay(el, 250);
            }
            catch { }
        }

        private void SetLatBar(System.Windows.Controls.Border fill, System.Windows.Controls.TextBlock label, double value, double scaleMax, string unit)
        {
            double frac = scaleMax <= 0 ? 0 : Math.Max(0, Math.Min(1.0, value / scaleMax));
            // Largura escrita direto no tick (~1s), como era antes (pílula 3..258px).
            fill.Width = 3 + frac * 255;
            fill.Background = frac >= 0.7 ? LatBad : frac >= 0.4 ? LatWarn : LatOk;
            label.Text = value <= 0 ? "—" : $"{value:F2} {unit}";
        }

        private void SyncLatEvents(List<LatencyMonitor.LatencyEvent> events)
        {
            int added = 0;
            foreach (var e in events) // ordem cronológica no Core → inserimos no topo
            {
                string key = $"{e.When.Ticks}|{e.Title}";
                if (!_latSeenEvents.Add(key)) continue;
                _latEventRows.Insert(0, new LatEventRow
                {
                    Hora = e.When.ToString("HH:mm:ss"),
                    Tipo = e.Kind,
                    Gravidade = e.Severity switch { "alert" => "CRÍTICO", "warn" => "ATENÇÃO", _ => "info" },
                    Titulo = e.Title,
                    Resumo = e.Explanation,
                    Tooltip = BuildEventTip(e),
                    GravidadeCor = e.Severity switch
                    {
                        "alert" => LatBad,
                        "warn" => LatWarn,
                        _ => LatIdle,
                    },
                });
                added++;
            }
            if (added > 0)
                while (_latEventRows.Count > 400) _latEventRows.RemoveAt(_latEventRows.Count - 1);
        }

        /// <summary>
        /// Balão de um evento (hover com espera): o quê, QUEM causou, por que importa e
        /// o que fazer — em português simples, como os "i" de informação do TweaksPage.
        /// </summary>
        private static string BuildEventTip(LatencyMonitor.LatencyEvent e)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("📌 " + e.Title);
            sb.AppendLine($"QUANDO: {e.When:HH:mm:ss}   ·   TIPO: {e.Kind}   ·   GRAVIDADE: {e.StatusLabel}");
            sb.AppendLine();
            sb.AppendLine("QUEM / ONDE: " + EventCulprit(e));
            sb.AppendLine();
            sb.AppendLine("O QUE ACONTECEU:");
            sb.AppendLine(e.Explanation);
            sb.AppendLine();
            sb.AppendLine("O QUE FAZER:");
            sb.AppendLine(EventAdvice(e));
            if (!string.IsNullOrWhiteSpace(e.TechDetail))
            {
                sb.AppendLine();
                sb.AppendLine("DETALHE TÉCNICO (para copiar e mandar a uma IA):");
                sb.AppendLine(e.TechDetail);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Extrai "quem causou" do título do evento (processo/driver citado).</summary>
        private static string EventCulprit(LatencyMonitor.LatencyEvent e)
        {
            string t = e.Title ?? "";
            int colon = t.IndexOf(':');
            string quem = colon >= 0 && colon + 1 < t.Length ? t[(colon + 1)..].Trim() : t.Trim();
            // Remove o sufixo de taxa "(302/s)" para sobrar só o nome
            int paren = quem.IndexOf('(');
            if (paren > 0) quem = quem[..paren].Trim();
            return string.IsNullOrWhiteSpace(quem) ? "(não identificado nesta janela)" : quem;
        }

        /// <summary>Recomendação prática por tipo de evento (o Kit explica, não só mede).</summary>
        private static string EventAdvice(LatencyMonitor.LatencyEvent e)
        {
            switch (e.Kind)
            {
                case "Memória":
                    return "Um programa pediu memória e o Windows teve de buscar no DISCO (hard page fault) — " +
                           "enquanto isso o programa congela e o áudio pode estalar. Abra o Gerenciador de Tarefas " +
                           "(este Kit, aba Processos), ordene por Memória e feche o que você não está usando. " +
                           "Se isso se repete com frequência, veja a aba Resumo → MEMÓRIA: a RAM está no limite.";
                case "DPC":
                case "ISR":
                    return "Esse tempo foi gasto por um DRIVER antes de o Windows atender seus programas. " +
                           "A causa mais comum é driver desatualizado (placa de rede, áudio, disco, antivírus ou " +
                           "o driver do fabricante do notebook). Atualize pelo site do fabricante — não pelo " +
                           "Windows Update — e reinicie. Veja a tabela Drivers para o arquivo .sys exato.";
                case "Janela":
                    return "O programa indicado parou de responder por alguns segundos. Se acontecer SEMPRE com o " +
                           "mesmo programa, o problema é dele. Se alternar entre programas, o problema é do sistema " +
                           "(RAM no limite, disco lento ou driver) — confira os eventos de Memória e DPC no mesmo horário.";
                case "Sistema":
                    return "Durante esse intervalo NADA no PC rodou de verdade — nem o próprio Kit conseguiu usar a CPU " +
                           "no tempo pedido. Costuma indicar plano de energia/BIOS limitando a CPU, superaquecimento " +
                           "ou espera extrema por disco. Veja a aba Resumo (temperatura e frequência) e reduza o que " +
                           "estiver rodando em segundo plano.";
                default:
                    return e.Severity == "info"
                        ? "Ainda não é problema: o Kit registrou só para você ter o histórico do que aconteceu e quando."
                        : "Observe se o evento se repete sempre na mesma situação (jogo, música, cópia de arquivos) — " +
                          "o padrão é o que aponta a causa.";
            }
        }

        private void SyncLatDrivers(List<LatencyMonitor.DriverStats> drivers)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drivers)
            {
                seen.Add(d.Name);
                if (!_latDriverIndex.TryGetValue(d.Name, out var row))
                {
                    row = new LatDriverRow { Nome = d.Name };
                    _latDriverIndex[d.Name] = row;
                    _latDriverRows.Add(row);
                }
                row.Descricao = string.IsNullOrWhiteSpace(d.Description) ? "—" : d.Description;
                row.DpcCount = d.DpcCount.ToString("N0");
                row.IsrCount = d.IsrCount.ToString("N0");
                row.MaxDpc = d.MaxDpcUs > 0 ? $"{d.MaxDpcUs / 1000.0:F2} ms" : "—";
                row.MaxIsr = d.MaxIsrUs > 0 ? $"{d.MaxIsrUs / 1000.0:F2} ms" : "—";
                row.Total = d.TotalUs > 0 ? $"{d.TotalUs / 1000.0:F1} ms" : "—";
                row.Tooltip = $"📌 Driver: {d.Name}\nQUEM: {row.Descricao}\n\n" +
                              $"O QUE ELE FEZ: {row.DpcCount} DPC (maior {row.MaxDpc}) e {row.IsrCount} ISR " +
                              $"(maior {row.MaxIsr}) · {row.Total} no total.\n\n" +
                              "POR QUE IMPORTA: DPC/ISR é trabalho de driver que roda ANTES de qualquer programa. " +
                              "Picos aqui são a causa clássica de áudio estalando, mouse travando e micro-freezes.\n\n" +
                              "O QUE FAZER: se este driver estiver no topo, atualize-o pelo site do fabricante " +
                              "(não pelo Windows Update) e reinicie. Se for de antivírus, teste desativá-lo por " +
                              "alguns minutos para confirmar.";
            }
            for (int i = _latDriverRows.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(_latDriverRows[i].Nome)) continue;
                _latDriverIndex.Remove(_latDriverRows[i].Nome);
                _latDriverRows.RemoveAt(i);
            }
        }

        private void SyncLatCores(List<LatencyMonitor.CoreStats> cores)
        {
            var seen = new HashSet<int>();
            foreach (var c in cores)
            {
                seen.Add(c.Index);
                if (!_latCoreIndex.TryGetValue(c.Index, out var row))
                {
                    row = new LatCoreRow { Nome = $"CPU {c.Index}" };
                    _latCoreIndex[c.Index] = row;
                    _latCoreRows.Add(row);
                }
                row.DpcPct = $"{c.DpcPercent:F2}%";
                row.IsrPct = $"{c.IsrPercent:F2}%";
                // Teto honesto: quando a janela atravessa um stall, o delta NÃO é uma rotina só.
                // O Core descarta esses deltas do pico (teto 250ms) — nunca mais "max 3781 ms".
                row.MaxDpc = c.MaxDpcUs > 0 ? $"{c.MaxDpcUs / 1000.0:F2} ms" : (c.StallFlag ? ">250 ms (janela c/ stall)" : "—");
                row.MaxIsr = c.MaxIsrUs > 0 ? $"{c.MaxIsrUs / 1000.0:F2} ms" : (c.StallFlag ? ">250 ms (janela c/ stall)" : "—");
                row.Ints = c.InterruptCount.ToString("N0");
                row.TotalDpc = $"{c.TotalDpcUs / 1000.0:F0} ms";
                row.DpcCor = c.DpcPercent >= 25 ? LatBad : c.DpcPercent >= 8 ? LatWarn : LatOk;
                row.Tooltip = $"📌 Núcleo lógico {c.Index}\nQUEM: os drivers que o sistema escolheu executar neste núcleo\n\n" +
                              $"O QUE ACONTECEU: {row.DpcPct} do núcleo em DPC na última janela (pico {row.MaxDpc}) " +
                              $"e {row.IsrPct} em ISR (pico {row.MaxIsr}). Interrupções atendidas: {row.Ints}. " +
                              $"Tempo total em DPC nesta sessão: {row.TotalDpc}.\n\n" +
                              (c.DpcPercent >= 25 || c.MaxDpcUs >= 1000
                                ? "POR QUE IMPORTA: acima de 25% (ou picos de 1 ms+) este núcleo está atrasando os " +
                                  "programas que caíram nele — é aí que aparece travadinha e estalo de áudio.\n"
                                : "POR QUE IMPORTA: dentro do normal; abaixo de 8% nenhum programa perde tempo aqui.\n") +
                              "O QUE FAZER: se um núcleo se destaca sempre, olhe a tabela Drivers — o driver que " +
                              "está queimando este núcleo é o culpado.\n\n" +
                              (c.StallFlag
                                ? "NOTA: uma ou mais janelas de medida atravessaram uma pausa do sistema (stall); " +
                                  "nesses períodos o delta acumula MUITAS rotinas e não representa uma execução só — " +
                                  "o pico mostrado é o melhor valor confiável (teto 250 ms por rotina).\n"
                                : "") +
                              "Nota técnica: os valores por núcleo são quantizados pelo tick do relógio do sistema " +
                              "(15,6 ms), por isso aparecem em múltiplos desse valor — mesma limitação do LatencyMon.";
            }
            for (int i = _latCoreRows.Count - 1; i >= 0; i--)
            {
                int idx = CoreIndexOf(_latCoreRows[i]);
                if (seen.Contains(idx)) continue;
                if (idx >= 0) _latCoreIndex.Remove(idx);
                _latCoreRows.RemoveAt(i);
            }
        }

        private static int CoreIndexOf(LatCoreRow row)
            => int.TryParse(row.Nome.Replace("CPU ", ""), out int v) ? v : -1;

        private void SyncLatProcs(List<LatencyMonitor.ProcessHardFault> procs)
        {
            var seen = new HashSet<int>();
            foreach (var p in procs)
            {
                seen.Add(p.Pid);
                if (!_latProcIndex.TryGetValue(p.Pid, out var row))
                {
                    row = new LatProcRow { Pid = p.Pid };
                    _latProcIndex[p.Pid] = row;
                    _latProcRows.Add(row);
                }
                row.Nome = string.IsNullOrEmpty(p.Name) ? $"({p.Pid})" : p.Name;
                row.Duras = p.Total.ToString("N0");
                row.Agora = $"{p.PerSec:F0}/s";
                row.Soft = p.SoftFaults > 0 ? p.SoftFaults.ToString("N0") : "—";
                row.Ws = $"{p.WorkingSetMB:F1} MB";
                row.AgoraCor = p.PerSec >= 500 ? LatBad : p.PerSec >= 50 ? LatWarn : LatOk;
                row.Tooltip = $"📌 {row.Nome} (PID {p.Pid})\nQUEM: este processo\n\n" +
                              $"O QUE ACONTECEU: {row.Duras} falhas DURAS de página nesta sessão ({row.Agora} agora) e " +
                              $"{row.Soft} falhas de página no total. Memória em uso: {row.Ws}.\n\n" +
                              "POR QUE IMPORTA: falha dura = a RAM não tinha o dado e o Windows foi buscar no DISCO. " +
                              "Enquanto isso o programa — e às vezes o áudio do sistema inteiro — congela.\n\n" +
                              (p.PerSec >= 50
                                ? "O QUE FAZER: neste momento ele está paginando pesado. Feche-o se puder, ou libere RAM " +
                                  "fechando abas/programas. Se ele é essencial, o caminho real é aumentar a RAM.\n"
                                : "O QUE FAZER: nada urgente agora — o total acumulado serve para identificar quem mais " +
                                  "pesa na sua RAM ao longo do uso.\n") +
                              "Dica: clique em Limpar e deixe rodando; o acúmulo mostra o padrão real de consumo.";
            }
            for (int i = _latProcRows.Count - 1; i >= 0; i--)
            {
                if (seen.Contains(_latProcRows[i].Pid)) continue;
                _latProcIndex.Remove(_latProcRows[i].Pid);
                _latProcRows.RemoveAt(i);
            }
        }

        // ══════════════ linhas (INotifyPropertyChanged p/ atualizar sem piscar) ══════════════

        private abstract class LatRow : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            protected void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            protected bool Set<T>(ref T f, T v, string n)
            {
                if (EqualityComparer<T>.Default.Equals(f, v)) return false;
                f = v; Raise(n); return true;
            }
            private string _tooltip = "";
            public string Tooltip { get => _tooltip; set => Set(ref _tooltip, value, nameof(Tooltip)); }
        }

        private sealed class LatEventRow : LatRow
        {
            private string _hora = "", _tipo = "", _gravidade = "", _titulo = "", _resumo = "";
            private SolidColorBrush _cor = Brushes.Gray;
            public string Hora { get => _hora; set => Set(ref _hora, value, nameof(Hora)); }
            public string Tipo { get => _tipo; set => Set(ref _tipo, value, nameof(Tipo)); }
            public string Gravidade { get => _gravidade; set => Set(ref _gravidade, value, nameof(Gravidade)); }
            public string Titulo { get => _titulo; set => Set(ref _titulo, value, nameof(Titulo)); }
            public string Resumo { get => _resumo; set => Set(ref _resumo, value, nameof(Resumo)); }
            public SolidColorBrush GravidadeCor { get => _cor; set => Set(ref _cor, value, nameof(GravidadeCor)); }
        }

        private sealed class LatDriverRow : LatRow
        {
            private string _nome = "", _descricao = "", _dpc = "0", _isr = "0", _maxDpc = "—", _maxIsr = "—", _total = "—";
            public string Nome { get => _nome; set => Set(ref _nome, value, nameof(Nome)); }
            public string Descricao { get => _descricao; set => Set(ref _descricao, value, nameof(Descricao)); }
            public string DpcCount { get => _dpc; set => Set(ref _dpc, value, nameof(DpcCount)); }
            public string IsrCount { get => _isr; set => Set(ref _isr, value, nameof(IsrCount)); }
            public string MaxDpc { get => _maxDpc; set => Set(ref _maxDpc, value, nameof(MaxDpc)); }
            public string MaxIsr { get => _maxIsr; set => Set(ref _maxIsr, value, nameof(MaxIsr)); }
            public string Total { get => _total; set => Set(ref _total, value, nameof(Total)); }
        }

        private sealed class LatCoreRow : LatRow
        {
            private string _nome = "", _dpcPct = "0%", _isrPct = "0%", _maxDpc = "—", _maxIsr = "—", _ints = "0", _totalDpc = "0 ms";
            private SolidColorBrush _cor = Brushes.Gray;
            public string Nome { get => _nome; set => Set(ref _nome, value, nameof(Nome)); }
            public string DpcPct { get => _dpcPct; set => Set(ref _dpcPct, value, nameof(DpcPct)); }
            public string IsrPct { get => _isrPct; set => Set(ref _isrPct, value, nameof(IsrPct)); }
            public string MaxDpc { get => _maxDpc; set => Set(ref _maxDpc, value, nameof(MaxDpc)); }
            public string MaxIsr { get => _maxIsr; set => Set(ref _maxIsr, value, nameof(MaxIsr)); }
            public string Ints { get => _ints; set => Set(ref _ints, value, nameof(Ints)); }
            public string TotalDpc { get => _totalDpc; set => Set(ref _totalDpc, value, nameof(TotalDpc)); }
            public SolidColorBrush DpcCor { get => _cor; set => Set(ref _cor, value, nameof(DpcCor)); }
        }

        private sealed class LatProcRow : LatRow
        {
            private string _nome = "", _duras = "0", _agora = "0/s", _soft = "—", _ws = "0 MB";
            private int _pid;
            private SolidColorBrush _cor = Brushes.Gray;
            public string Nome { get => _nome; set => Set(ref _nome, value, nameof(Nome)); }
            public int Pid { get => _pid; set => Set(ref _pid, value, nameof(Pid)); }
            public string Duras { get => _duras; set => Set(ref _duras, value, nameof(Duras)); }
            public string Agora { get => _agora; set => Set(ref _agora, value, nameof(Agora)); }
            public string Soft { get => _soft; set => Set(ref _soft, value, nameof(Soft)); }
            public string Ws { get => _ws; set => Set(ref _ws, value, nameof(Ws)); }
            public SolidColorBrush AgoraCor { get => _cor; set => Set(ref _cor, value, nameof(AgoraCor)); }
        }
    }
}
